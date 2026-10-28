using System.Text.Json;

namespace GitPersona.Core;

/// <summary>Persists the ordered rule list to <c>rules.json</c>. The order here is the order in rules.gitconfig.</summary>
public sealed class RuleStore(AppPaths paths)
{
    sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<Rule> Rules { get; set; } = [];
    }

    readonly Lock gate = new();
    Document? doc;

    string FilePath => paths.RulesFile;

    public IReadOnlyList<Rule> All()
    {
        lock (gate) return [.. Load().Rules];
    }

    public void Replace(IEnumerable<Rule> rules)
    {
        lock (gate)
        {
            var d = Load();
            d.Rules = [.. rules];
            Save(d);
        }
    }

    Document Load()
    {
        if (doc is not null) return doc;
        if (!File.Exists(FilePath)) return doc = new Document();
        try
        {
            doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), AccountStore.JsonOptions) ?? new Document();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FilePath} is not valid JSON ({ex.Message}). Fix or delete the file.", ex);
        }
        foreach (var rule in doc.Rules)
            if (!AccountValidator.IsValidId(rule.AccountId))
                throw new InvalidDataException($"{FilePath} contains a rule with an invalid account id.");
        return doc;
    }

    void Save(Document d)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d, AccountStore.JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
        doc = d;
    }
}
