namespace GitPersona.Core.Tests;

public class RepoTests
{
    static readonly Account Work = TestEnv.MakeAccount();

    // ---- URL building ----

    [Theory]
    [InlineData("https://gitlab.company.com/mycompany/app.git", "https://muneel-work@gitlab.company.com/mycompany/app.git")]
    [InlineData("https://gitlab.company.com/mycompany/team/app", "https://muneel-work@gitlab.company.com/mycompany/team/app")]
    [InlineData("  https://GitLab.Company.com/g/app.git  ", "https://muneel-work@gitlab.company.com/g/app.git")]
    [InlineData("https://someone@gitlab.company.com/g/app.git", "https://muneel-work@gitlab.company.com/g/app.git")]
    public void Puts_the_accounts_username_into_the_url(string pasted, string expected) =>
        Assert.Equal(expected, RepoService.BuildUrlWithUsername(pasted, Work));

    [Fact]
    public void Usernames_that_are_emails_are_percent_encoded()
    {
        var account = Work with { Username = "me@company.com" };
        Assert.Equal("https://me%40company.com@gitlab.company.com/g/app.git",
            RepoService.BuildUrlWithUsername("https://gitlab.company.com/g/app.git", account));
    }

    [Theory]
    [InlineData("git@gitlab.company.com:g/app.git", "SSH")]
    [InlineData("ssh://git@gitlab.company.com/g/app.git", "SSH")]
    [InlineData("http://gitlab.company.com/g/app.git", "https://")]
    [InlineData("https://github.com/g/app.git", "github.com")]
    [InlineData("https://user:glpat-SECRET@gitlab.company.com/g/app.git", "password or token")]
    [InlineData("https://gitlab.company.com", "no repository path")]
    [InlineData("not a url", "https://")]
    public void Refuses_urls_that_would_pick_the_wrong_account_or_leak_secrets(string pasted, string expectedInMessage)
    {
        var ex = Assert.Throws<RepoException>(() => RepoService.BuildUrlWithUsername(pasted, Work));
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    [Theory]
    [InlineData("https://gitlab.company.com/g/app.git", "app")]
    [InlineData("https://gitlab.company.com/g/sub/my.repo.git", "my.repo")]
    [InlineData("https://gitlab.company.com/g/app/", "app")]
    [InlineData("https://gitlab.company.com/g/app", "app")]
    public void Derives_the_folder_name(string url, string expected) =>
        Assert.Equal(expected, RepoService.RepoNameFromUrl(url));

    // ---- clone (a local directory stands in for the server via url.<base>.insteadOf) ----

    static async Task<(TestEnv Env, string RemoteDir)> WithFakeServerAsync()
    {
        var remoteRoot = Path.Combine(Path.GetTempPath(), "gp-remote-" + Guid.NewGuid().ToString("N")[..8]);
        var bare = Path.Combine(remoteRoot, "mycompany", "app.git");
        Directory.CreateDirectory(bare);

        var baseUrl = new Uri(remoteRoot + Path.DirectorySeparatorChar).AbsoluteUri; // file:///...
        var env = new TestEnv(extraConfig:
        [
            ($"url.{baseUrl}.insteadOf", "https://muneel-work@gitlab.company.com/"),
            ($"url.{baseUrl}.insteadOf", "https://gitlab.company.com/"),
        ]);

        (await env.Git.RunAsync(["init", "-q", "--bare", bare])).EnsureSuccess();
        // seed one commit
        var seed = Path.Combine(env.Root, "seed");
        (await env.Git.RunAsync(["init", "-q", seed])).EnsureSuccess();
        File.WriteAllText(Path.Combine(seed, "README.md"), "hi");
        (await env.Git.RunAsync(["-C", seed, "add", "."])).EnsureSuccess();
        (await env.Git.RunAsync(["-C", seed, "-c", "user.name=T", "-c", "user.email=t@t.example", "commit", "-q", "-m", "init"])).EnsureSuccess();
        (await env.Git.RunAsync(["-C", seed, "push", "-q", bare, "HEAD:refs/heads/main"])).EnsureSuccess();
        (await env.Git.RunAsync(["-C", bare, "symbolic-ref", "HEAD", "refs/heads/main"])).EnsureSuccess();
        return (env, remoteRoot);
    }

    [Fact]
    public async Task Clone_embeds_the_username_and_the_account_rules_apply_straight_away()
    {
        var (env, remoteRoot) = await WithFakeServerAsync();
        using (env)
        {
            try
            {
                await env.Manager.InstallAsync();
                await env.Manager.AddAccountAsync(
                    TestEnv.MakeAccount("personal", "https://github.com", "me-personal", "Me", "me@home.example", Provider.GitHub), null);
                await env.Manager.AddAccountAsync(Work, null);
                await env.Manager.SetActiveAsync("personal");
                var workFolder = Path.Combine(env.Root, "work");
                Directory.CreateDirectory(workFolder);
                await env.Manager.Rules.AddFolderRuleAsync("work", workFolder);

                Assert.Equal(workFolder.Replace('\\', '/') + "/", env.Manager.Repos.SuggestDestination(Work));

                var target = await env.Manager.Repos.CloneAsync("https://gitlab.company.com/mycompany/app.git", "work", workFolder);

                Assert.Equal(Path.Combine(workFolder, "app"), target);
                Assert.True(File.Exists(Path.Combine(target, "README.md")));
                // The stored remote carries the login...
                Assert.Equal("https://muneel-work@gitlab.company.com/mycompany/app.git", await env.EffectiveAsync(target, "remote.origin.url"));
                // ...and the folder rule picked the right identity for the new repo.
                Assert.Equal("muneel@company.com", await env.EffectiveAsync(target, "user.email"));

                var check = await env.Manager.Checker.CheckAsync(target);
                Assert.False(check.HasMismatch);
                Assert.Equal("work", check.IdentityAccount?.Id);
                Assert.True(check.IsRepository);
            }
            finally { TestEnv.DeleteTree(remoteRoot); }
        }
    }

    [Fact]
    public async Task Clone_refuses_to_overwrite_a_non_empty_folder()
    {
        var (env, remoteRoot) = await WithFakeServerAsync();
        using (env)
        {
            try
            {
                await env.Manager.AddAccountAsync(Work, null);
                var parent = Path.Combine(env.Root, "dest");
                Directory.CreateDirectory(Path.Combine(parent, "app"));
                File.WriteAllText(Path.Combine(parent, "app", "keep.txt"), "x");

                await Assert.ThrowsAsync<RepoException>(() =>
                    env.Manager.Repos.CloneAsync("https://gitlab.company.com/mycompany/app.git", "work", parent));
                Assert.True(File.Exists(Path.Combine(parent, "app", "keep.txt")));
            }
            finally { TestEnv.DeleteTree(remoteRoot); }
        }
    }

    [Fact]
    public async Task Clone_for_a_different_host_is_refused_before_git_runs()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(Work, null);

        // The URL names a different host than the account, so this is caught without starting git.
        var ex = await Assert.ThrowsAsync<RepoException>(() =>
            env.Manager.Repos.CloneAsync("https://gitlab.company.com.invalid/g/app.git", "work", Path.Combine(env.Root, "d")));
        Assert.Contains("account", ex.Message, StringComparison.OrdinalIgnoreCase); // host mismatch is caught before git runs
    }

    [Fact]
    public async Task Suggests_nothing_when_the_account_has_no_folder_rule()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(Work, null);
        Assert.Null(env.Manager.Repos.SuggestDestination(Work));
    }

    // ---- fix existing repo ----

    [Fact]
    public async Task Fix_remote_previews_then_rewrites_origin_with_the_username()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(Work, null);
        var repo = env.CreateRepo("r", "https://gitlab.company.com/mycompany/app.git");

        var preview = await env.Manager.Repos.PreviewRemoteChangeAsync(repo, "work");
        Assert.Equal("https://gitlab.company.com/mycompany/app.git", preview.CurrentUrl);
        Assert.Equal("https://muneel-work@gitlab.company.com/mycompany/app.git", preview.ProposedUrl);
        // preview alone changes nothing
        Assert.Equal("https://gitlab.company.com/mycompany/app.git", await env.EffectiveAsync(repo, "remote.origin.url"));

        await env.Manager.Repos.SetRemoteAccountAsync(repo, "work");
        Assert.Equal("https://muneel-work@gitlab.company.com/mycompany/app.git", await env.EffectiveAsync(repo, "remote.origin.url"));
    }

    [Fact]
    public async Task Fix_remote_replaces_another_login_and_drops_any_embedded_password()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(Work, null);
        var repo = env.CreateRepo("r", "https://old-login:glpat-SECRET@gitlab.company.com/g/app.git");

        var preview = await env.Manager.Repos.PreviewRemoteChangeAsync(repo, "work");
        Assert.DoesNotContain("SECRET", preview.CurrentUrl);
        Assert.DoesNotContain("SECRET", preview.ProposedUrl);

        await env.Manager.Repos.SetRemoteAccountAsync(repo, "work");
        var stored = await env.EffectiveAsync(repo, "remote.origin.url");
        Assert.Equal("https://muneel-work@gitlab.company.com/g/app.git", stored);
    }

    [Fact]
    public async Task Fix_remote_refuses_wrong_host_ssh_and_missing_remote()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(Work, null);

        var otherHost = env.CreateRepo("a", "https://github.com/me/app.git");
        await Assert.ThrowsAsync<RepoException>(() => env.Manager.Repos.SetRemoteAccountAsync(otherHost, "work"));
        Assert.Equal("https://github.com/me/app.git", await env.EffectiveAsync(otherHost, "remote.origin.url"));

        var ssh = env.CreateRepo("b", "git@gitlab.company.com:g/app.git");
        await Assert.ThrowsAsync<RepoException>(() => env.Manager.Repos.SetRemoteAccountAsync(ssh, "work"));

        var none = env.CreateRepo("c");
        await Assert.ThrowsAsync<RepoException>(() => env.Manager.Repos.SetRemoteAccountAsync(none, "work"));
    }
}
