using GitPersona.Core.Git;

namespace GitPersona.Core;

public enum Severity { Info, Warning, Error }

public sealed record CheckIssue(Severity Severity, string Message);

/// <summary>A config value and the file that set it, as reported by <c>git config --show-origin</c>.</summary>
public sealed record ConfigValue(string? Value, string? OriginFile, string? OriginText)
{
    public static readonly ConfigValue None = new(null, null, null);
}

public sealed record CheckResult
{
    public required string Folder { get; init; }
    public bool IsRepository { get; init; }
    public string? RepoRoot { get; init; }
    public ConfigValue Email { get; init; } = ConfigValue.None;
    public ConfigValue Name { get; init; } = ConfigValue.None;
    public string? RemoteUrl { get; init; }
    public string? RemoteHostUrl { get; init; }
    public bool RemoteIsSsh { get; init; }
    public ConfigValue Username { get; init; } = ConfigValue.None;

    /// <summary>The account whose fragment supplied user.email (or whose email matches).</summary>
    public Account? IdentityAccount { get; init; }

    /// <summary>The account the push login belongs to.</summary>
    public Account? LoginAccount { get; init; }

    /// <summary>Rules that match this repo, in file order. The last one wins.</summary>
    public IReadOnlyList<Rule> MatchingRules { get; init; } = [];

    public IReadOnlyList<CheckIssue> Issues { get; init; } = [];

    public bool HasMismatch => Issues.Any(i => i.Severity == Severity.Error);
}

public sealed class FolderChecker(GitRunner git, AppPaths paths, AccountStore accounts, RuleStore rules)
{
    public async Task<CheckResult> CheckAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"'{folder}' is not a folder.");
        var issues = new List<CheckIssue>();

        var top = await git.RunAsync(["rev-parse", "--show-toplevel"], workingDirectory: folder, cancellationToken: ct);
        var isRepo = top.Success;
        var repoRoot = isRepo ? top.StdOut.Trim() : null;
        if (!isRepo)
            issues.Add(new(Severity.Info, "This folder is not a Git repository yet, so folder and remote rules cannot apply. Showing what a new repository here would get from your global settings."));

        var email = await ReadWithOriginAsync(["--get", "user.email"], folder, ct);
        var name = await ReadWithOriginAsync(["--get", "user.name"], folder, ct);

        // Remote
        string? remoteUrl = null;
        var remote = await git.RunAsync(["remote", "get-url", "origin"], workingDirectory: folder, cancellationToken: ct);
        if (remote.Success) remoteUrl = remote.StdOut.Trim();

        var parsed = remoteUrl is null ? RemoteUrl.Parse(null) : RemoteUrl.Parse(remoteUrl);
        string? displayUrl = remoteUrl is null ? null : parsed.Redacted;
        if (isRepo && remoteUrl is null)
            issues.Add(new(Severity.Info, "This repository has no 'origin' remote, so no host-specific login can be determined."));
        if (parsed.IsSsh)
            issues.Add(new(Severity.Warning, "The remote uses SSH. GitPersona manages HTTPS only and ignores SSH remotes."));
        if (parsed.HasPassword)
            issues.Add(new(Severity.Warning, "The remote URL contains a password or token. Remove it with \"Set this repo's remote to use account…\" and keep the token in the credential helper."));

        // Login: a username in the remote URL beats credential.<url>.username.
        var username = ConfigValue.None;
        if (parsed.Username is not null)
        {
            username = new ConfigValue(parsed.Username, null, "the remote URL");
        }
        else if (parsed.IsHttps && remoteUrl is not null)
        {
            username = await ReadUrlMatchedUsernameAsync(parsed, folder, ct);
        }

        var all = accounts.All();
        var identityAccount = FindIdentityAccount(all, email);
        var loginAccount = parsed.HostUrl is null || username.Value is null
            ? null
            : all.FirstOrDefault(a => a.HostUrl == parsed.HostUrl && string.Equals(a.Username, username.Value, StringComparison.OrdinalIgnoreCase));

        var matching = isRepo ? await MatchingRulesAsync(repoRoot!, folder, ct) : [];

        AddIdentityIssues(issues, email, name, identityAccount, loginAccount, username, parsed, matching);

        return new CheckResult
        {
            Folder = folder,
            IsRepository = isRepo,
            RepoRoot = repoRoot,
            Email = email,
            Name = name,
            RemoteUrl = displayUrl,
            RemoteHostUrl = parsed.HostUrl,
            RemoteIsSsh = parsed.IsSsh,
            Username = username,
            IdentityAccount = identityAccount,
            LoginAccount = loginAccount,
            MatchingRules = matching,
            Issues = issues,
        };
    }

    void AddIdentityIssues(
        List<CheckIssue> issues, ConfigValue email, ConfigValue name, Account? identity, Account? login,
        ConfigValue username, RemoteUrl remote, IReadOnlyList<Rule> matching)
    {
        if (email.Value is null)
            issues.Add(new(Severity.Warning, "No commit email is set. Git will refuse to commit, or guess one from your machine name."));
        if (name.Value is null)
            issues.Add(new(Severity.Warning, "No commit name is set."));

        if (email.Value is not null && identity is null)
            issues.Add(new(Severity.Info, $"The commit email {email.Value} comes from {Describe(email)}, which is not one of your GitPersona accounts."));

        if (identity is not null && login is not null && identity.Id != login.Id)
        {
            issues.Add(new(Severity.Error,
                $"MISMATCH: commits will be authored as \"{identity.Label}\" ({identity.Email}) but pushes will log in as \"{login.Label}\" ({login.Username} on {login.HostUrl})."));
        }
        else if (identity is not null && remote.HostUrl is not null && login is null)
        {
            if (identity.HostUrl != remote.HostUrl)
                issues.Add(new(Severity.Error,
                    $"MISMATCH: commits will be authored as \"{identity.Label}\", an account on {identity.HostUrl}, but this repo's remote is on {remote.HostUrl}."));
            else if (username.Value is null)
                issues.Add(new(Severity.Warning,
                    $"No login is configured for {remote.HostUrl}. The credential helper may pick the wrong one when several accounts share the host."));
            else
                issues.Add(new(Severity.Error,
                    $"MISMATCH: commits will be authored as \"{identity.Label}\" but pushes will log in as \"{username.Value}\", which is not that account's login ({identity.Username})."));
        }
        else if (identity is null && login is not null)
        {
            issues.Add(new(Severity.Warning,
                $"Pushes will log in as \"{login.Label}\" but the commit identity does not come from that account."));
        }

        // The rules say one thing, the effective config says another: something overrides the app.
        if (matching.Count > 0 && identity is not null)
        {
            var expected = matching[^1].AccountId;
            if (expected != identity.Id)
                issues.Add(new(Severity.Warning,
                    $"The last matching rule ({matching[^1].Describe()}) points at another account, but the commit identity comes from \"{identity.Label}\". A repo-level or later config is overriding the rule."));
        }
    }

    Account? FindIdentityAccount(IReadOnlyList<Account> all, ConfigValue email)
    {
        if (email.OriginFile is not null)
        {
            var dir = Normalize(paths.ProfilesDir) + "/";
            var file = Normalize(email.OriginFile);
            if (file.StartsWith(dir, PathComparison) && file.EndsWith(".gitconfig", PathComparison))
            {
                var id = file[dir.Length..^".gitconfig".Length];
                var fromFragment = all.FirstOrDefault(a => a.Id == id);
                if (fromFragment is not null) return fromFragment;
            }
        }
        // The email was set elsewhere (repo config, global) but may still equal one of the accounts' emails.
        return email.Value is null ? null : all.FirstOrDefault(a => string.Equals(a.Email, email.Value, StringComparison.OrdinalIgnoreCase));
    }

    async Task<IReadOnlyList<Rule>> MatchingRulesAsync(string repoRoot, string folder, CancellationToken ct)
    {
        var urls = new List<string>();
        var remotes = await git.RunAsync(["config", "--get-regexp", @"^remote\..*\.url$"], workingDirectory: folder, cancellationToken: ct);
        if (remotes.Success)
            foreach (var line in remotes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = line.IndexOf(' ');
                if (space > 0) urls.Add(line[(space + 1)..].Trim());
            }

        return rules.All().Where(r => RuleMatcher.Matches(r, repoRoot, urls)).ToList();
    }

    async Task<ConfigValue> ReadWithOriginAsync(string[] getArgs, string folder, CancellationToken ct)
    {
        string[] args = ["config", "--show-origin", .. getArgs];
        var result = await git.RunAsync(args, workingDirectory: folder, cancellationToken: ct);
        if (!result.Success || result.StdOut.Trim().Length == 0) return ConfigValue.None;
        return ParseOrigin(result.StdOut.TrimEnd('\r', '\n'));
    }

    // --get-urlmatch cannot report origins, so ask it for the value and then find the file that set it.
    async Task<ConfigValue> ReadUrlMatchedUsernameAsync(RemoteUrl remote, string folder, CancellationToken ct)
    {
        var match = await git.RunAsync(["config", "--get-urlmatch", "credential.username", remote.UrlForMatching!], workingDirectory: folder, cancellationToken: ct);
        if (!match.Success) return ConfigValue.None;
        var value = match.StdOut.TrimEnd('\r', '\n');
        if (value.Length == 0) return ConfigValue.None;

        var listing = await git.RunAsync(["config", "--show-origin", "--get-regexp", @"^credential\..+\.username$"], workingDirectory: folder, cancellationToken: ct);
        if (listing.Success)
        {
            // Last entry for this host with the winning value is the one git used.
            foreach (var line in listing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                var origin = ParseOrigin(line.TrimEnd('\r'));
                var entry = origin.Value ?? "";
                var space = entry.IndexOf(' ');
                if (space < 0) continue;
                var key = entry[..space];
                var entryValue = entry[(space + 1)..];
                var url = key["credential.".Length..^".username".Length];
                if (entryValue == value && remote.UrlForMatching!.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                    return new ConfigValue(value, origin.OriginFile, origin.OriginText);
            }
        }
        return new ConfigValue(value, null, null);
    }

    /// <summary>Parses <c>file:/path/to/config&lt;TAB&gt;value</c> (also <c>command line:</c> and friends).</summary>
    internal static ConfigValue ParseOrigin(string line)
    {
        var tab = line.IndexOf('\t');
        if (tab < 0) return new ConfigValue(line, null, null);

        var origin = line[..tab];
        var value = line[(tab + 1)..];
        var file = origin.StartsWith("file:", StringComparison.Ordinal) ? origin["file:".Length..] : null;
        return new ConfigValue(value, file, origin);
    }

    string Describe(ConfigValue v) => v.OriginFile ?? v.OriginText ?? "an unknown source";

    static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>Our own evaluation of a rule, used only to explain results. Git itself is always the authority.</summary>
public static class RuleMatcher
{
    public static bool Matches(Rule rule, string repoRoot, IEnumerable<string> remoteUrls)
    {
        if (rule.Type == RuleType.Folder)
        {
            var root = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return root.StartsWith(rule.Folder!, cmp);
        }

        foreach (var url in remoteUrls)
        {
            var parsed = RemoteUrl.Parse(url);
            if (!parsed.IsHttps || parsed.HostUrl != rule.RemoteHostUrl) continue;
            if (string.IsNullOrEmpty(rule.RemotePath)) return true;
            if (parsed.Path.StartsWith("/" + rule.RemotePath + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
