using GitPersona.Core.Git;

namespace GitPersona.Core.Tests;

public class GitRunnerTests
{
    [Fact]
    public async Task Reports_version()
    {
        using var env = new TestEnv();
        var version = await env.Git.GetVersionAsync();
        Assert.True(version.Major >= 2);
    }

    [Theory]
    [InlineData("git version 2.55.0.windows.4", 2, 55, 0)]
    [InlineData("git version 2.36.1", 2, 36, 1)]
    [InlineData("git version 2.39.5 (Apple Git-154)", 2, 39, 5)]
    public void Parses_version_strings(string output, int major, int minor, int patch) =>
        Assert.Equal(new Version(major, minor, patch), GitLocator.ParseVersion(output));

    [Fact]
    public void Remote_rules_need_2_36()
    {
        Assert.False(GitLocator.SupportsHasConfigRemote(new Version(2, 35, 9)));
        Assert.True(GitLocator.SupportsHasConfigRemote(new Version(2, 36, 0)));
    }

    [Fact]
    public async Task Passes_stdin_and_arguments_without_shell_interpretation()
    {
        using var env = new TestEnv();
        // hash-object hashes exactly what arrives on stdin; the argument contains shell metacharacters.
        var result = await env.Git.RunAsync(["hash-object", "--stdin"], stdin: "hello\n");
        Assert.True(result.Success);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", result.StdOut.Trim());

        var bad = await env.Git.RunAsync("config", "--get", "a.b & echo pwned");
        Assert.False(bad.Success); // treated as one (invalid) key name, never run through a shell
        Assert.DoesNotContain("pwned", bad.StdOut);
    }

    [Fact]
    public async Task Never_waits_for_input_when_stdin_is_not_supplied()
    {
        using var env = new TestEnv();
        var result = await env.Git.RunAsync(["hash-object", "--stdin"], timeout: TimeSpan.FromSeconds(10));
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Times_out_and_kills_slow_commands()
    {
        using var env = new TestEnv();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<GitTimeoutException>(() =>
            env.Git.RunAsync(["-c", "alias.slow=!sleep 20", "slow"], timeout: TimeSpan.FromMilliseconds(700)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Missing_git_is_a_clear_error()
    {
        var runner = new GitRunner(Path.Combine(Path.GetTempPath(), "definitely-not-git.exe"));
        await Assert.ThrowsAsync<GitNotFoundException>(() => runner.RunAsync("--version"));
    }

    [Fact]
    public void Result_text_never_includes_output()
    {
        var result = new GitResult(0, "password=hunter2", "");
        Assert.DoesNotContain("hunter2", result.ToString());
    }
}
