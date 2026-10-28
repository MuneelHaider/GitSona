using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitSona.Core;

/// <summary>Persists <c>accounts.json</c> (metadata only). Writes are atomic.</summary>
public sealed class AccountStore(AppPaths paths)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed class Document
    {
        public int Version { get; set; } = 1;
        public string? ActiveAccountId { get; set; }
        public List<Account> Accounts { get; set; } = [];
    }

    readonly Lock gate = new();
    Document? doc;

    public IReadOnlyList<Account> All()
    {
        lock (gate) return [.. Load().Accounts];
    }

    public Account? Find(string id)
    {
        lock (gate) return Load().Accounts.FirstOrDefault(a => a.Id == id);
    }

    public string? ActiveAccountId
    {
        get { lock (gate) return Load().ActiveAccountId; }
    }

    public void Upsert(Account account)
    {
        lock (gate)
        {
            var d = Load();
            var i = d.Accounts.FindIndex(a => a.Id == account.Id);
            if (i >= 0) d.Accounts[i] = account;
            else d.Accounts.Add(account);
            Save(d);
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            var d = Load();
            var removed = d.Accounts.RemoveAll(a => a.Id == id) > 0;
            if (d.ActiveAccountId == id) d.ActiveAccountId = null;
            if (removed) Save(d);
            return removed;
        }
    }

    public void SetActive(string? id)
    {
        lock (gate)
        {
            var d = Load();
            if (id is not null && d.Accounts.All(a => a.Id != id))
                throw new ArgumentException($"No account with id '{id}'.", nameof(id));
            d.ActiveAccountId = id;
            Save(d);
        }
    }

    /// <summary>Forget the in-memory copy so the next access re-reads the file.</summary>
    public void Reload()
    {
        lock (gate) doc = null;
    }

    Document Load()
    {
        if (doc is not null) return doc;
        if (!File.Exists(paths.AccountsFile)) return doc = new Document();

        Document? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Document>(File.ReadAllText(paths.AccountsFile), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{paths.AccountsFile} is not valid JSON ({ex.Message}). Fix or delete the file.", ex);
        }
        loaded ??= new Document();

        foreach (var account in loaded.Accounts)
        {
            if (!AccountValidator.IsValidId(account.Id))
                throw new InvalidDataException($"{paths.AccountsFile} contains an invalid account id '{account.Id}'.");
        }
        return doc = loaded;
    }

    void Save(Document d)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = paths.AccountsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d, JsonOptions));
        File.Move(tmp, paths.AccountsFile, overwrite: true);
        doc = d;
    }
}
