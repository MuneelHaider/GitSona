using GitPersona.Core.Git;

namespace GitPersona.Core.Tests;

public class ProfileAndSwitchTests
{
    [Fact]
    public async Task Fragment_contains_identity_and_per_host_username()
    {
        using var env = new TestEnv();
        var account = TestEnv.MakeAccount();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(account, token: null);

        var file = new GitConfigFile(env.Git, env.Paths.ProfileConfig("work"));
        Assert.Equal("Muneel Work", await file.GetAsync("user.name"));
        Assert.Equal("muneel@company.com", await file.GetAsync("user.email"));
        Assert.Equal("muneel-work", await file.GetAsync("credential.https://gitlab.company.com.username"));
    }

    [Theory]
    [InlineData("Dr. \"Quote\" O'Brien")]
    [InlineData("back\\slash # not a comment ; either")]
    [InlineData("  padded  ")]
    [InlineData("-leading-dash")]
    [InlineData("Zoë Łukasz 日本語")]
    public async Task Awkward_values_survive_because_git_does_the_escaping(string name)
    {
        using var env = new TestEnv();
        var account = TestEnv.MakeAccount(name: name);
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(account, token: null);
        await env.Manager.SetActiveAsync(account.Id);

        Assert.Equal(name, await env.EffectiveAsync(env.CreateRepo(), "user.name"));
    }

    [Fact]
    public async Task Switching_the_active_account_changes_what_git_uses()
    {
        using var env = new TestEnv();
        var personal = TestEnv.MakeAccount("personal", "https://github.com", "me-personal", "Me Personal", "me@home.example", Provider.GitHub);
        var work = TestEnv.MakeAccount("work");
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(personal, token: null);
        await env.Manager.AddAccountAsync(work, token: null);
        var repo = env.CreateRepo();

        await env.Manager.SetActiveAsync("personal");
        Assert.Equal("me@home.example", await env.EffectiveAsync(repo, "user.email"));

        await env.Manager.SetActiveAsync("work");
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(repo, "user.email"));
        Assert.Equal("work", env.Manager.Accounts.ActiveAccountId);

        await env.Manager.SetActiveAsync(null);
        Assert.Null(await env.EffectiveAsync(repo, "user.email"));
    }

    [Fact]
    public async Task Active_file_is_a_single_relative_include()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), token: null);
        await env.Manager.SetActiveAsync("work");

        var includes = await new GitConfigFile(env.Git, env.Paths.ActiveConfig).GetAllAsync("include.path");
        Assert.Equal(["profiles/work.gitconfig"], includes);
    }

    [Fact]
    public async Task Repo_local_identity_still_beats_the_app()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), token: null);
        await env.Manager.SetActiveAsync("work");
        var repo = env.CreateRepo();
        (await env.Git.RunAsync(["-C", repo, "config", "user.email", "local@repo.example"])).EnsureSuccess();

        Assert.Equal("local@repo.example", await env.EffectiveAsync(repo, "user.email"));
    }

    [Fact]
    public async Task Same_host_accounts_pick_their_own_login_via_the_credential_username()
    {
        using var env = new TestEnv();
        var a = TestEnv.MakeAccount("a", "https://gitlab.com", "alice-work", email: "a@x.example");
        var b = TestEnv.MakeAccount("b", "https://gitlab.com", "alice-side", email: "b@x.example");
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(a, "token-for-alice-work");
        await env.Manager.AddAccountAsync(b, "token-for-alice-side");
        var repo = env.CreateRepo();

        await env.Manager.SetActiveAsync("a");
        Assert.Equal("alice-work", await env.EffectiveAsync(repo, "credential.https://gitlab.com.username"));
        Assert.Equal("token-for-alice-work", await FillInAsync(env, repo));

        await env.Manager.SetActiveAsync("b");
        Assert.Equal("alice-side", await env.EffectiveAsync(repo, "credential.https://gitlab.com.username"));
        Assert.Equal("token-for-alice-side", await FillInAsync(env, repo));
    }

    // `git credential fill` applies credential.<url>.username from config, exactly like a real push would.
    static async Task<string?> FillInAsync(TestEnv env, string repo)
    {
        var r = await env.Git.RunAsync(["credential", "fill"], stdin: "protocol=https\nhost=gitlab.com\n\n", workingDirectory: repo);
        return r.Success ? r.StdOut.Split('\n').First(l => l.StartsWith("password=")).TrimEnd('\r')["password=".Length..] : null;
    }
}
