using GitSona.Core.Git;

namespace GitSona.Core.Tests;

public class CheckerTests
{
    static async Task<TestEnv> SetupAsync(string? globalConfig = null)
    {
        var env = new TestEnv(globalConfig);
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(
            TestEnv.MakeAccount("personal", "https://github.com", "me-personal", "Me Personal", "me@home.example", Provider.GitHub), null);
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount("work"), null);
        await env.Manager.SetActiveAsync("personal");
        return env;
    }

    [Fact]
    public async Task Reports_identity_login_and_where_each_value_came_from()
    {
        using var env = await SetupAsync();
        var repo = env.CreateRepo("r", "https://gitlab.company.com/mycompany/app.git");
        await env.Manager.SetActiveAsync("work");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.True(result.IsRepository);
        Assert.Equal("muneel@company.com", result.Email.Value);
        Assert.Equal("Muneel Work", result.Name.Value);
        Assert.Equal("muneel-work", result.Username.Value);
        Assert.EndsWith("work.gitconfig", result.Email.OriginFile!.Replace('\\', '/'));
        Assert.Equal("work", result.IdentityAccount?.Id);
        Assert.Equal("work", result.LoginAccount?.Id);
        Assert.Equal("https://gitlab.company.com", result.RemoteHostUrl);
        Assert.False(result.HasMismatch);
        Assert.DoesNotContain(result.Issues, i => i.Severity != Severity.Info);
    }

    [Fact]
    public async Task Names_the_matching_folder_rule()
    {
        using var env = await SetupAsync();
        var work = Path.Combine(env.Root, "work");
        Directory.CreateDirectory(work);
        var rule = await env.Manager.Rules.AddFolderRuleAsync("work", work);
        var repo = env.CreateRepo(Path.Combine("work", "app"), "https://gitlab.company.com/g/app.git");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.Equal(rule.Id, Assert.Single(result.MatchingRules).Id);
        Assert.Equal("work", result.IdentityAccount?.Id);
        Assert.False(result.HasMismatch);
    }

    [Fact]
    public async Task Flags_a_red_mismatch_when_the_identity_is_one_account_and_the_login_another()
    {
        // Two accounts on the same host. The identity comes from "a" (active), but the repo's URL pins "b"'s login.
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount("a", "https://gitlab.com", "alice-a", email: "a@x.example"), null);
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount("b", "https://gitlab.com", "alice-b", email: "b@x.example"), null);
        await env.Manager.SetActiveAsync("a");
        var repo = env.CreateRepo("r", "https://alice-b@gitlab.com/g/app.git");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.True(result.HasMismatch);
        var issue = Assert.Single(result.Issues, i => i.Severity == Severity.Error);
        Assert.Contains("MISMATCH", issue.Message);
        Assert.Equal("a", result.IdentityAccount?.Id);
        Assert.Equal("b", result.LoginAccount?.Id);
    }

    [Fact]
    public async Task Flags_a_mismatch_when_the_active_identity_is_on_a_different_host_than_the_remote()
    {
        using var env = await SetupAsync(); // active = personal (github)
        var repo = env.CreateRepo("r", "https://gitlab.company.com/mycompany/app.git");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.True(result.HasMismatch);
        Assert.Contains(result.Issues, i => i.Severity == Severity.Error && i.Message.Contains("gitlab.company.com"));
    }

    [Fact]
    public async Task A_login_in_the_remote_url_is_the_effective_username()
    {
        using var env = await SetupAsync();
        await env.Manager.SetActiveAsync("work");
        var repo = env.CreateRepo("r", "https://someone-else@gitlab.company.com/g/app.git");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.Equal("someone-else", result.Username.Value);
        Assert.Equal("the remote URL", result.Username.OriginText);
        Assert.True(result.HasMismatch);
    }

    [Fact]
    public async Task Passwords_in_remote_urls_are_flagged_and_never_echoed()
    {
        using var env = await SetupAsync();
        var repo = env.CreateRepo("r", "https://muneel-work:glpat-SECRET@gitlab.company.com/g/app.git");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.Contains(result.Issues, i => i.Message.Contains("password or token"));
        Assert.DoesNotContain("SECRET", result.RemoteUrl);
        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("SECRET"));
        Assert.Equal("muneel-work", result.Username.Value);
    }

    [Theory]
    [InlineData("git@gitlab.company.com:mycompany/app.git")]
    [InlineData("ssh://git@gitlab.company.com/mycompany/app.git")]
    public async Task Ssh_remotes_are_flagged_as_ignored(string url)
    {
        using var env = await SetupAsync();
        var repo = env.CreateRepo("r", url);

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.True(result.RemoteIsSsh);
        Assert.Contains(result.Issues, i => i.Message.Contains("SSH"));
        Assert.Null(result.Username.Value);
    }

    [Fact]
    public async Task A_repo_level_email_that_beats_the_app_is_reported_with_its_file()
    {
        using var env = await SetupAsync();
        await env.Manager.SetActiveAsync("work");
        var repo = env.CreateRepo("r", "https://gitlab.company.com/g/app.git");
        (await env.Git.RunAsync(["-C", repo, "config", "user.email", "stray@elsewhere.example"])).EnsureSuccess();

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.Equal("stray@elsewhere.example", result.Email.Value);
        Assert.EndsWith("config", result.Email.OriginFile!.Replace('\\', '/'));
        Assert.Contains(".git", result.Email.OriginFile.Replace('\\', '/'));
        Assert.Null(result.IdentityAccount);
        Assert.Contains(result.Issues, i => i.Message.Contains("stray@elsewhere.example"));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repo_is_explained_not_failed()
    {
        using var env = await SetupAsync();
        var folder = Path.Combine(env.Root, "plain");
        Directory.CreateDirectory(folder);

        var result = await env.Manager.Checker.CheckAsync(folder);

        Assert.False(result.IsRepository);
        Assert.Contains(result.Issues, i => i.Message.Contains("not a Git repository"));
        Assert.Equal("me@home.example", result.Email.Value);
    }

    [Fact]
    public async Task Missing_folder_is_an_error()
    {
        using var env = await SetupAsync();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => env.Manager.Checker.CheckAsync(Path.Combine(env.Root, "nope")));
    }

    [Fact]
    public async Task No_identity_at_all_is_a_warning()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        var repo = env.CreateRepo("r");

        var result = await env.Manager.Checker.CheckAsync(repo);

        Assert.Null(result.Email.Value);
        Assert.Contains(result.Issues, i => i.Severity == Severity.Warning && i.Message.Contains("email"));
    }

    [Theory]
    [InlineData("file:C:/Users/x/.gitconfig\tfoo@bar.example", "foo@bar.example", "C:/Users/x/.gitconfig")]
    [InlineData("file:/home/x/.gitconfig\ta\tb", "a\tb", "/home/x/.gitconfig")]
    [InlineData("command line:\tvalue", "value", null)]
    public void Parses_show_origin_output(string line, string value, string? file)
    {
        var parsed = FolderChecker.ParseOrigin(line);
        Assert.Equal(value, parsed.Value);
        Assert.Equal(file, parsed.OriginFile);
    }

    [Theory]
    [InlineData("https://gitlab.com/g/r.git", "https://gitlab.com", null, false)]
    [InlineData("https://bob@gitlab.com/g/r.git", "https://gitlab.com", "bob", false)]
    [InlineData("https://bob%40x.com@gitlab.com/g/r.git", "https://gitlab.com", "bob@x.com", false)]
    [InlineData("https://bob:pw@gitlab.com:8443/g/r.git", "https://gitlab.com:8443", "bob", true)]
    public void Parses_https_remotes(string url, string host, string? user, bool hasPassword)
    {
        var parsed = RemoteUrl.Parse(url);
        Assert.True(parsed.IsHttps);
        Assert.Equal(host, parsed.HostUrl);
        Assert.Equal(user, parsed.Username);
        Assert.Equal(hasPassword, parsed.HasPassword);
        Assert.DoesNotContain("pw", parsed.Redacted);
    }
}
