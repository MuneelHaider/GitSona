using System.Text;

namespace GitSona.Core;

/// <summary>Turns rules into the <c>includeIf</c> condition strings Git understands.</summary>
public static class RuleConditions
{
    /// <summary><c>gitdir/i</c> on Windows and macOS (case-insensitive file systems), <c>gitdir</c> on Linux.</summary>
    public static string GitDirKeyword => OperatingSystem.IsLinux() ? "gitdir" : "gitdir/i";

    public static IReadOnlyList<string> For(Rule rule) => rule.Type switch
    {
        RuleType.Folder => [$"{GitDirKeyword}:{EscapeGlob(rule.Folder!)}"],
        RuleType.Remote => RemoteConditions(rule),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    // The clone helper embeds the login in the remote (https://user@host/...), so both shapes must match.
    static IReadOnlyList<string> RemoteConditions(Rule rule)
    {
        var host = EscapeGlob(rule.RemoteHostUrl!["https://".Length..]);
        var path = string.IsNullOrEmpty(rule.RemotePath) ? "" : EscapeGlob(rule.RemotePath) + "/";
        return
        [
            $"hasconfig:remote.*.url:https://{host}/{path}**",
            $"hasconfig:remote.*.url:https://*@{host}/{path}**",
        ];
    }

    /// <summary>Backslash-escapes characters that are wildcards in Git's pattern matching.</summary>
    public static string EscapeGlob(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '*' or '?' or '[' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
