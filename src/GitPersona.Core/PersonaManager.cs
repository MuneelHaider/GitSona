using GitPersona.Core.Platform;
using GitPersona.Core.Git;

namespace GitPersona.Core;

public sealed record UninstallOptions(bool RestoreBackup = false, bool RejectCredentials = false, bool DeleteAppData = false);

public sealed record UninstallReport(UninstallResult Config, IReadOnlyList<string> Warnings);

/// <summary>The entry point the UI talks to: keeps accounts.json, the generated gitconfig files and the credential store in step.</summary>
public sealed class PersonaManager
{
    readonly SemaphoreSlim gate = new(1, 1);

    public GitRunner Git { get; }
    public AppPaths Paths { get; }
    public AccountStore Accounts { get; }
    public ProfileWriter Profiles { get; }
    public CredentialService Credentials { get; }
    public GlobalConfigInstaller Installer { get; }
    public RuleManager Rules { get; }
    public FolderChecker Checker { get; }
    public RepoService Repos { get; }
    public SettingsStore Settings { get; }

    public PersonaManager(GitRunner git, AppPaths paths, TimeProvider? clock = null)
    {
        Git = git;
        Paths = paths;
        Accounts = new AccountStore(paths);
        Profiles = new ProfileWriter(git, paths);
        Credentials = new CredentialService(git);
        Installer = new GlobalConfigInstaller(git, paths, clock);
        Rules = new RuleManager(git, paths, Accounts);
        Checker = new FolderChecker(git, paths, Accounts, Rules.Store);
        Repos = new RepoService(git, Accounts, Rules.Store);
        Settings = new SettingsStore(paths);
    }

    /// <summary>Creates the app's files and adds (or refreshes) the managed block in the global gitconfig.</summary>
    public Task<InstallResult> InstallAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        Profiles.EnsureBaseFiles();
        await Rules.RegenerateAsync(ct);
        await Profiles.WriteActiveAsync(Accounts.ActiveAccountId, ct);
        return await Installer.InstallAsync(ct);
    }, ct);

    /// <summary>
    /// Adds an account. When <paramref name="token"/> is given it is handed to git's credential helper
    /// and nothing else; it is not stored by the app.
    /// </summary>
    public Task<Account> AddAccountAsync(Account account, string? token, CancellationToken ct = default) => LockedAsync(async () =>
    {
        account = Normalize(account);
        AccountValidator.EnsureValid(account);
        EnsureNoConflict(account);

        if (token is not null) await Credentials.ApproveAsync(account.HostUrl, account.Username, token, ct);
        try
        {
            await Profiles.WriteProfileAsync(account, ct);
            Accounts.Upsert(account);
        }
        catch
        {
            if (token is not null) await TryRejectAsync(account);
            throw;
        }
        return account;
    }, ct);

    public Task<Account> UpdateAccountAsync(Account account, string? newToken, CancellationToken ct = default) => LockedAsync(async () =>
    {
        var existing = Accounts.Find(account.Id) ?? throw new ArgumentException($"No account with id '{account.Id}'.", nameof(account));
        account = Normalize(account);
        AccountValidator.EnsureValid(account);
        EnsureNoConflict(account);

        var loginMoved = existing.HostUrl != account.HostUrl || existing.Username != account.Username;
        if (newToken is not null) await Credentials.ApproveAsync(account.HostUrl, account.Username, newToken, ct);
        if (loginMoved) await TryRejectAsync(existing);

        await Profiles.WriteProfileAsync(account, ct);
        Accounts.Upsert(account);
        return account;
    }, ct);

    /// <summary>Removes the account's fragment, its metadata and its stored login. Returns non-fatal problems.</summary>
    public Task<IReadOnlyList<string>> DeleteAccountAsync(string id, CancellationToken ct = default) => LockedAsync<IReadOnlyList<string>>(async () =>
    {
        var account = Accounts.Find(id) ?? throw new ArgumentException($"No account with id '{id}'.", nameof(id));
        var warnings = new List<string>();

        if (Accounts.ActiveAccountId == id)
        {
            await Profiles.WriteActiveAsync(null, ct);
            Accounts.SetActive(null);
        }

        if (await TryRejectAsync(account) is { } problem) warnings.Add(problem);
        await Rules.RemoveForAccountAsync(id, ct);
        Profiles.DeleteProfile(id);
        Accounts.Remove(id);
        return warnings;
    }, ct);

    /// <summary>Sets the default account used by repos that match no rule. <c>null</c> clears it.</summary>
    public Task SetActiveAsync(string? accountId, CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (accountId is not null && Accounts.Find(accountId) is null)
            throw new ArgumentException($"No account with id '{accountId}'.", nameof(accountId));

        await Profiles.WriteActiveAsync(accountId, ct);
        Accounts.SetActive(accountId);
        return 0;
    }, ct);

    public Task<UninstallReport> UninstallAsync(UninstallOptions options, CancellationToken ct = default) => LockedAsync(async () =>
    {
        var warnings = new List<string>();

        if (options.RejectCredentials)
        {
            foreach (var account in Accounts.All())
                if (account.AuthType == AuthType.PersonalAccessToken || account.Provider == Provider.GitHub)
                    if (await TryRejectAsync(account) is { } problem) warnings.Add(problem);
        }

        var config = await Installer.UninstallAsync(options.RestoreBackup, ct);
        warnings.AddRange(config.Warnings);

        if (options.DeleteAppData) DeleteAppData();
        return new UninstallReport(config, warnings);
    }, ct);

    static Account Normalize(Account account) =>
        AccountValidator.TryNormalizeHost(account.HostUrl, out var host, out _) ? account with { HostUrl = host } : account;

    void EnsureNoConflict(Account account)
    {
        var clash = Accounts.All().FirstOrDefault(a =>
            a.Id != account.Id
            && a.HostUrl == account.HostUrl
            && string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            throw new AccountValidationException([$"Account '{clash.Label}' already uses {account.Username} on {account.HostUrl}."]);
    }

    async Task<string?> TryRejectAsync(Account account)
    {
        try
        {
            await Credentials.RejectAsync(account.HostUrl, account.Username);
            return null;
        }
        catch (GitException ex)
        {
            return $"Could not remove the stored login for {account.Username} on {account.HostUrl}: {ex.Message}";
        }
    }

    void DeleteAppData()
    {
        foreach (var file in new[] { Paths.AccountsFile, Paths.StateFile, Paths.ActiveConfig, Paths.RulesConfig, Paths.RulesFile, Paths.SettingsFile })
            File.Delete(file);
        if (Directory.Exists(Paths.ProfilesDir)) Directory.Delete(Paths.ProfilesDir, recursive: true);
        if (Directory.Exists(Paths.Root) && !Directory.EnumerateFileSystemEntries(Paths.Root).Any())
            Directory.Delete(Paths.Root);
    }

    async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }
}
