using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GitPersona.Core;

public enum RuleType { Folder, Remote }

/// <summary>
/// An automatic rule. Folder rules become <c>includeIf "gitdir:..."</c>, remote rules become
/// <c>includeIf "hasconfig:remote.*.url:..."</c>. The list order is the order written to rules.gitconfig,
/// and when several rules match, Git applies them in that order so the last one wins.
/// </summary>
public sealed record Rule
{
    public string Id { get; init; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
    public RuleType Type { get; init; }
    public string AccountId { get; init; } = "";

    /// <summary>Folder rules: absolute path, forward slashes, trailing slash.</summary>
    public string? Folder { get; init; }

    /// <summary>Remote rules: <c>https://host[:port]</c>.</summary>
    public string? RemoteHostUrl { get; init; }

    /// <summary>Remote rules: optional group/org path such as <c>mycompany</c> or <c>mycompany/team</c>.</summary>
    public string? RemotePath { get; init; }

    public string Describe() => Type == RuleType.Folder
        ? $"Folder {Folder}"
        : $"Remote {RemoteHostUrl}/{(string.IsNullOrEmpty(RemotePath) ? "" : RemotePath + "/")}**";
}

public sealed class RuleException(string message) : Exception(message);

public sealed class RuleNotSupportedException(string message) : Exception(message);

public static partial class RuleFactory
{
    public static Rule CreateFolder(string accountId, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new RuleException("Choose a folder.");
        if (AccountValidator.HasControlCharacters(folder)) throw new RuleException("The folder path contains control characters.");
        if (!Path.IsPathFullyQualified(folder)) throw new RuleException("The folder must be an absolute path.");

        var full = GitConfigText.ToGitPath(Path.GetFullPath(folder.Trim()));
        if (!full.EndsWith('/')) full += "/";
        return new Rule { Type = RuleType.Folder, AccountId = accountId, Folder = full };
    }

    public static Rule CreateRemote(string accountId, string hostUrl, string? groupPath)
    {
        if (!AccountValidator.TryNormalizeHost(hostUrl, out var host, out var error))
            throw new RuleException(error);

        var path = (groupPath ?? "").Trim().Trim('/');
        if (path.Length > 0)
        {
            if (!PathRegex().IsMatch(path))
                throw new RuleException("The group path may contain only letters, digits, '.', '_', '-' and '/' between segments.");
            if (path.Split('/').Any(s => s is "." or ".."))
                throw new RuleException("The group path must not contain '.' or '..' segments.");
        }
        return new Rule { Type = RuleType.Remote, AccountId = accountId, RemoteHostUrl = host, RemotePath = path };
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex PathRegex();
}
