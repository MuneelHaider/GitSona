using GitPersona.Core.Git;

namespace GitPersona.Core;

public sealed class RepoException(string message) : Exception(message);

public sealed record RemoteChangePreview(string RemoteName, string CurrentUrl, string ProposedUrl);

/// <summary>Clone helper and "fix existing repo": both put the account's username into the remote URL.</summary>
public sealed class RepoService(GitRunner git, AccountStore accounts, RuleStore rules)
{
    static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);

    // ---- URL building ----

    /// <summary>
    /// Builds <c>https://&lt;username&gt;@&lt;host&gt;/&lt;path&gt;</c> from a pasted URL. The host must be the account's host.
    /// URLs that already carry a password or token are refused.
    /// </summary>
    public static string BuildUrlWithUsername(string pastedUrl, Account account)
    {
        var parsed = RemoteUrl.Parse(pastedUrl);
        if (parsed.IsSsh) throw new RepoException("SSH URLs are not supported. Paste the HTTPS URL of the repository.");
        if (!parsed.IsHttps || parsed.HostUrl is null) throw new RepoException("Paste an https:// repository URL.");
        if (parsed.HasPassword) throw new RepoException("That URL contains a password or token. Remove it; GitPersona keeps tokens in the credential helper.");
        if (parsed.HostUrl != account.HostUrl)
            throw new RepoException($"That repository is on {parsed.HostUrl}, but account \"{account.Label}\" is for {account.HostUrl}. Pick an account for the same host.");
        if (parsed.Path.Length <= 1) throw new RepoException("The URL has no repository path.");

        // Re-parse for the query part, which RemoteUrl intentionally drops.
        var uri = new Uri(pastedUrl.Trim());
        return $"https://{Uri.EscapeDataString(account.Username)}@{parsed.HostUrl["https://".Length..]}{uri.AbsolutePath}{uri.Query}";
    }

    public static string RepoNameFromUrl(string url)
    {
        var path = RemoteUrl.Parse(url).Path.TrimEnd('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new RepoException("Could not work out a folder name from that URL.");
        return name;
    }

    /// <summary>A destination parent folder that already matches one of the account's folder rules, if any.</summary>
    public string? SuggestDestination(Account account) =>
        rules.All().FirstOrDefault(r => r.AccountId == account.Id && r.Type == RuleType.Folder)?.Folder;

    // ---- clone ----

    /// <summary>
    /// Clones into <c>destinationParent/&lt;repo&gt;</c> using a URL that contains the account's username, so the
    /// right stored login is used from the very first request (folder rules cannot apply before the repo exists).
    /// </summary>
    public async Task<string> CloneAsync(string pastedUrl, string accountId, string destinationParent, CancellationToken ct = default)
    {
        var account = accounts.Find(accountId) ?? throw new RepoException("The selected account no longer exists.");
        var url = BuildUrlWithUsername(pastedUrl, account);

        if (string.IsNullOrWhiteSpace(destinationParent)) throw new RepoException("Choose a destination folder.");
        Directory.CreateDirectory(destinationParent);
        var target = Path.Combine(Path.GetFullPath(destinationParent), RepoNameFromUrl(url));
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new RepoException($"{target} already exists and is not empty.");

        // allowCredentialUi: a first GitHub sign-in may need to open the browser.
        var result = await git.RunAsync(["clone", "--", url, target], timeout: CloneTimeout, cancellationToken: ct, allowCredentialUi: true);
        if (!result.Success)
            throw new RepoException("git clone failed: " + FirstLines(result.StdErr));
        return target;
    }

    // ---- fix existing repo ----

    public async Task<RemoteChangePreview> PreviewRemoteChangeAsync(string repoFolder, string accountId, string remote = "origin", CancellationToken ct = default)
    {
        var account = accounts.Find(accountId) ?? throw new RepoException("The selected account no longer exists.");
        var current = await git.RunAsync(["remote", "get-url", remote], workingDirectory: repoFolder, cancellationToken: ct);
        if (!current.Success) throw new RepoException($"This repository has no '{remote}' remote.");

        var raw = current.StdOut.Trim();
        return new RemoteChangePreview(remote, RemoteUrl.Parse(raw).Redacted, BuildUrlWithUsername(StripUserInfo(raw), account));
    }

    /// <summary>Rewrites the remote to include the account's username. Callers must confirm with the user first.</summary>
    public async Task SetRemoteAccountAsync(string repoFolder, string accountId, string remote = "origin", CancellationToken ct = default)
    {
        var preview = await PreviewRemoteChangeAsync(repoFolder, accountId, remote, ct);
        (await git.RunAsync(["remote", "set-url", remote, preview.ProposedUrl], workingDirectory: repoFolder, cancellationToken: ct))
            .EnsureSuccess("git remote set-url");
    }

    // Drops any user[:password]@ so BuildUrlWithUsername sees a clean URL (and never carries a password forward).
    static string StripUserInfo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length == 0) return url;
        return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
    }

    static string FirstLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4);
        return string.Join(" ", lines);
    }
}
