using System.Text;

namespace GitSona.Core;

/// <summary>Helpers for the few places where gitconfig text is written by hand.</summary>
public static class GitConfigText
{
    /// <summary>Git accepts forward slashes everywhere on Windows, and backslashes in values are escapes.</summary>
    public static string ToGitPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    /// <summary>Quotes a value only when git would otherwise misread it (comment chars, escapes, edge whitespace).</summary>
    public static string QuoteValue(string value)
    {
        if (value.Any(c => c is '\n' or '\r' or '\0'))
            throw new ArgumentException("Config values cannot contain line breaks.", nameof(value));

        var needsQuotes = value.Length == 0
            || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])
            || value.AsSpan().IndexOfAny("\"\\#;") >= 0;
        if (!needsQuotes) return value;

        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
