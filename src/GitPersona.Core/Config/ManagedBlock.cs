namespace GitPersona.Core;

/// <summary>
/// Pure text operations on the one block GitPersona owns at the end of the user's global gitconfig.
/// Operates on strings only; the installer decides how bytes map to text.
/// </summary>
public static class ManagedBlock
{
    public const string BeginPrefix = "# >>> GitPersona managed";
    public const string BeginMarker = "# >>> GitPersona managed — do not edit between markers >>>";
    public const string EndMarker = "# <<< GitPersona managed <<<";

    /// <summary>Active default first, rules after, so rules override the default.</summary>
    public static string Render(string activeConfigPath, string rulesConfigPath, string newline = "\n") =>
        string.Join(newline,
            BeginMarker,
            "[include]",
            $"\tpath = {GitConfigText.QuoteValue(GitConfigText.ToGitPath(activeConfigPath))}",
            $"\tpath = {GitConfigText.QuoteValue(GitConfigText.ToGitPath(rulesConfigPath))}",
            EndMarker) + newline;

    public static bool Contains(string text) => TryFind(text, out _, out _);

    public static string DetectNewline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>Removes any existing block and appends <paramref name="block"/> at the very end. Idempotent.</summary>
    public static string Apply(string text, string block)
    {
        var without = Remove(text);
        if (without.Length == 0) return block;

        var nl = DetectNewline(without);
        if (!without.EndsWith('\n')) without += nl;
        // One blank separator line, which Remove knows to take back out.
        return without + nl + block;
    }

    /// <summary>Removes the block (and the separator line Apply added), leaving everything else untouched.</summary>
    public static string Remove(string text)
    {
        while (TryFind(text, out var start, out var end))
        {
            var before = text[..start];
            if (before.EndsWith("\r\n\r\n", StringComparison.Ordinal)) before = before[..^2];
            else if (before.EndsWith("\n\n", StringComparison.Ordinal)) before = before[..^1];
            text = before + text[end..];
        }
        return text;
    }

    /// <summary>Lines after the block's end marker, or null when there is no block.</summary>
    public static string? TextAfterBlock(string text) =>
        TryFind(text, out _, out var end) ? text[end..] : null;

    static bool TryFind(string text, out int start, out int end)
    {
        start = end = -1;

        var begin = IndexOfLine(text, BeginPrefix, 0);
        if (begin < 0) return false;

        var endLine = IndexOfLine(text, EndMarker, begin);
        if (endLine < 0)
            throw new InvalidDataException(
                "The GitPersona managed block in your gitconfig has a start marker but no end marker. Fix it by hand, then retry.");

        var newline = text.IndexOf('\n', endLine);
        start = begin;
        end = newline < 0 ? text.Length : newline + 1;
        return true;
    }

    static int IndexOfLine(string text, string prefix, int from)
    {
        for (var i = text.IndexOf(prefix, from, StringComparison.Ordinal); i >= 0; i = text.IndexOf(prefix, i + 1, StringComparison.Ordinal))
            if (i == 0 || text[i - 1] == '\n') return i;
        return -1;
    }
}
