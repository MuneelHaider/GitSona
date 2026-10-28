namespace GitSona.Core.Tests;

public class ManagedBlockTests
{
    const string Active = "C:\\Users\\Jo\\AppData\\Roaming\\GitSona\\active.gitconfig";
    const string Rules = "C:\\Users\\Jo\\AppData\\Roaming\\GitSona\\rules.gitconfig";

    static string Block(string nl = "\n") => ManagedBlock.Render(Active, Rules, nl);

    [Fact]
    public void Renders_active_before_rules_with_forward_slashes()
    {
        var block = Block();
        var lines = block.Split('\n');
        Assert.Equal("# >>> GitSona managed — do not edit between markers >>>", lines[0]);
        Assert.Equal("[include]", lines[1]);
        Assert.Contains("active.gitconfig", lines[2]);
        Assert.Contains("rules.gitconfig", lines[3]);
        Assert.Equal("# <<< GitSona managed <<<", lines[4]);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain('\\', block);
    }

    [Fact]
    public void Quotes_paths_git_would_misread()
    {
        var block = ManagedBlock.Render("/home/a#b/active.gitconfig", "/home/a;b/rules.gitconfig");
        Assert.Contains("path = \"/home/a#b/active.gitconfig\"", block);
        Assert.Contains("path = \"/home/a;b/rules.gitconfig\"", block);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[user]\n\tname = A\n")]
    [InlineData("[user]\n\tname = A")]
    [InlineData("\n")]
    [InlineData("[user]\r\n\tname = A\r\n")]
    public void Apply_twice_equals_apply_once(string original)
    {
        var nl = ManagedBlock.DetectNewline(original);
        var once = ManagedBlock.Apply(original, Block(nl));
        var twice = ManagedBlock.Apply(once, Block(nl));
        Assert.Equal(once, twice);
        Assert.Equal(1, CountOccurrences(once, ManagedBlock.BeginPrefix));
    }

    [Fact]
    public void Never_duplicates_the_block()
    {
        var text = "[user]\n\tname = A\n";
        for (var i = 0; i < 5; i++) text = ManagedBlock.Apply(text, Block());
        Assert.Equal(1, CountOccurrences(text, ManagedBlock.BeginPrefix));
        Assert.Equal(1, CountOccurrences(text, ManagedBlock.EndMarker));
    }

    [Fact]
    public void Block_is_the_last_thing_in_the_file_even_after_the_user_edits_below_it()
    {
        var text = ManagedBlock.Apply("[user]\n\tname = A\n", Block());
        text += "[alias]\n\tco = checkout\n";

        var fixedUp = ManagedBlock.Apply(text, Block());

        Assert.EndsWith(ManagedBlock.EndMarker + "\n", fixedUp);
        Assert.Contains("[alias]\n\tco = checkout\n", fixedUp);
        Assert.Equal(1, CountOccurrences(fixedUp, ManagedBlock.BeginPrefix));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[user]\n\tname = A\n")]
    [InlineData("[user]\n\tname = A\n\n")]
    [InlineData("\n")]
    [InlineData("[user]\r\n\tname = A\r\n")]
    [InlineData("# comment only\n")]
    public void Remove_undoes_apply_exactly(string original)
    {
        var applied = ManagedBlock.Apply(original, Block(ManagedBlock.DetectNewline(original)));
        Assert.Equal(original, ManagedBlock.Remove(applied));
    }

    [Fact]
    public void Remove_keeps_content_the_user_added_after_the_block()
    {
        var applied = ManagedBlock.Apply("[user]\n\tname = A\n", Block());
        var edited = applied + "[alias]\n\tco = checkout\n";
        Assert.Equal("[user]\n\tname = A\n[alias]\n\tco = checkout\n", ManagedBlock.Remove(edited));
    }

    [Fact]
    public void A_start_marker_without_an_end_marker_is_refused_not_guessed_at()
    {
        var broken = "[user]\n\tname = A\n" + ManagedBlock.BeginMarker + "\n[include]\n";
        Assert.Throws<InvalidDataException>(() => ManagedBlock.Apply(broken, Block()));
    }

    [Fact]
    public void Markers_quoted_mid_line_are_ignored()
    {
        var text = "[alias]\n\tx = !echo '# >>> GitSona managed'\n";
        Assert.False(ManagedBlock.Contains(text));
    }

    static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
