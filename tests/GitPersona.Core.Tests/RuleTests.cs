using GitPersona.Core.Git;

namespace GitPersona.Core.Tests;

public class RuleTests
{
    static async Task<TestEnv> TwoAccountsAsync(string? globalConfig = null)
    {
        var env = new TestEnv(globalConfig);
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(
            TestEnv.MakeAccount("personal", "https://github.com", "me-personal", "Me Personal", "me@home.example", Provider.GitHub), null);
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount("work"), null);
        await env.Manager.SetActiveAsync("personal");
        return env;
    }

    static string Sub(TestEnv env, string name)
    {
        var dir = Path.Combine(env.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Folder_rule_applies_inside_the_folder_and_not_outside()
    {
        using var env = await TwoAccountsAsync();
        var workDir = Sub(env, "work");
        await env.Manager.Rules.AddFolderRuleAsync("work", workDir);

        var inside = env.CreateRepo(Path.Combine("work", "proj"));
        var outside = env.CreateRepo("hobby");

        Assert.Equal("muneel@company.com", await env.EffectiveAsync(inside, "user.email"));
        Assert.Equal("me@home.example", await env.EffectiveAsync(outside, "user.email"));
    }

    [Fact]
    public async Task Folder_rule_is_written_with_forward_slashes_and_a_trailing_slash()
    {
        using var env = await TwoAccountsAsync();
        var rule = await env.Manager.Rules.AddFolderRuleAsync("work", Sub(env, "work"));

        Assert.EndsWith("/", rule.Folder);
        Assert.DoesNotContain('\\', rule.Folder!);

        var text = File.ReadAllText(env.Paths.RulesConfig);
        var keyword = OperatingSystem.IsLinux() ? "gitdir:" : "gitdir/i:";
        Assert.Contains($"[includeIf \"{keyword}", text);
        Assert.Contains("path = profiles/work.gitconfig", text);
    }

    [Fact]
    public void Windows_style_paths_are_normalised()
    {
        if (!OperatingSystem.IsWindows()) return; // C:\ paths are only absolute on Windows

        var rule = RuleFactory.CreateFolder("work", @"C:\Work\Client Projects");
        Assert.Equal("C:/Work/Client Projects/", rule.Folder);
        Assert.Equal(["gitdir/i:C:/Work/Client Projects/"], RuleConditions.For(rule));

        Assert.Equal("C:/work/", RuleFactory.CreateFolder("work", @"C:\work\").Folder);
    }

    [Fact]
    public async Task Windows_drive_letter_rules_match_case_insensitively()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var env = await TwoAccountsAsync();
        var workDir = Sub(env, "Work");
        await env.Manager.Rules.AddFolderRuleAsync("work", workDir.ToLowerInvariant());

        var repo = env.CreateRepo(Path.Combine("Work", "proj"));
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(repo, "user.email"));
    }

    [Fact]
    public async Task Folder_names_with_glob_characters_are_matched_literally()
    {
        if (OperatingSystem.IsWindows()) return; // [ ] * ? are not legal/typical in Windows folder names
        using var env = await TwoAccountsAsync();
        var odd = Sub(env, "w[or]k*");
        await env.Manager.Rules.AddFolderRuleAsync("work", odd);

        var inside = env.CreateRepo(Path.Combine("w[or]k*", "proj"));
        var lookalike = env.CreateRepo(Path.Combine("wo", "proj"));
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(inside, "user.email"));
        Assert.Equal("me@home.example", await env.EffectiveAsync(lookalike, "user.email"));
    }

    [Fact]
    public async Task Last_matching_rule_wins_and_reordering_flips_the_result()
    {
        using var env = await TwoAccountsAsync();
        await env.Manager.AddAccountAsync(
            TestEnv.MakeAccount("client", "https://gitlab.client.example", "me-client", "Me Client", "me@client.example"), null);
        var parent = Sub(env, "code");
        var child = Sub(env, Path.Combine("code", "client"));

        var broad = await env.Manager.Rules.AddFolderRuleAsync("work", parent);
        var narrow = await env.Manager.Rules.AddFolderRuleAsync("client", child);
        var repo = env.CreateRepo(Path.Combine("code", "client", "app"));

        // Both rules match; "client" is listed last, so it wins.
        Assert.Equal("me@client.example", await env.EffectiveAsync(repo, "user.email"));

        await env.Manager.Rules.MoveAsync(narrow.Id, 0);
        Assert.Equal(["client", "work"], env.Manager.Rules.All().Select(r => r.AccountId));
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(repo, "user.email"));

        await env.Manager.Rules.MoveAsync(narrow.Id, 1);
        Assert.Equal("me@client.example", await env.EffectiveAsync(repo, "user.email"));
        Assert.NotNull(broad);
    }

    [Fact]
    public async Task Rules_override_the_active_default_but_the_default_covers_everything_else()
    {
        using var env = await TwoAccountsAsync();
        await env.Manager.Rules.AddFolderRuleAsync("work", Sub(env, "work"));
        var inside = env.CreateRepo(Path.Combine("work", "p"));
        var outside = env.CreateRepo("other");

        await env.Manager.SetActiveAsync("work");
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(outside, "user.email"));

        await env.Manager.SetActiveAsync("personal");
        Assert.Equal("me@home.example", await env.EffectiveAsync(outside, "user.email"));
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(inside, "user.email"));
    }

    [Fact]
    public async Task Remote_rule_matches_by_host_and_group_including_a_login_in_the_url()
    {
        using var env = await TwoAccountsAsync();
        if (!await env.Manager.Rules.RemoteRulesSupportedAsync()) return;
        await env.Manager.Rules.AddRemoteRuleAsync("work", "https://gitlab.company.com", "mycompany");

        var plain = env.CreateRepo("a", "https://gitlab.company.com/mycompany/app.git");
        var withLogin = env.CreateRepo("b", "https://muneel-work@gitlab.company.com/mycompany/team/app.git");
        var otherGroup = env.CreateRepo("c", "https://gitlab.company.com/someone-else/app.git");
        var otherHost = env.CreateRepo("d", "https://github.com/mycompany/app.git");
        var ssh = env.CreateRepo("e", "git@gitlab.company.com:mycompany/app.git");

        Assert.Equal("muneel@company.com", await env.EffectiveAsync(plain, "user.email"));
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(withLogin, "user.email"));
        Assert.Equal("me@home.example", await env.EffectiveAsync(otherGroup, "user.email"));
        Assert.Equal("me@home.example", await env.EffectiveAsync(otherHost, "user.email"));
        Assert.Equal("me@home.example", await env.EffectiveAsync(ssh, "user.email"));
    }

    [Fact]
    public async Task Host_only_remote_rule_covers_every_group()
    {
        using var env = await TwoAccountsAsync();
        if (!await env.Manager.Rules.RemoteRulesSupportedAsync()) return;
        await env.Manager.Rules.AddRemoteRuleAsync("work", "https://gitlab.company.com", null);

        var repo = env.CreateRepo("a", "https://gitlab.company.com/any/deep/group/app.git");
        Assert.Equal("muneel@company.com", await env.EffectiveAsync(repo, "user.email"));
    }

    [Fact]
    public async Task Remote_rules_are_refused_on_git_older_than_2_36()
    {
        using var env = await TwoAccountsAsync();
        env.Manager.Rules.GitVersionOverride = new Version(2, 35, 0);

        Assert.False(await env.Manager.Rules.RemoteRulesSupportedAsync());
        var ex = await Assert.ThrowsAsync<RuleNotSupportedException>(() =>
            env.Manager.Rules.AddRemoteRuleAsync("work", "https://gitlab.company.com", null));
        Assert.Contains("2.36", ex.Message);
        Assert.Empty(env.Manager.Rules.All());
    }

    [Fact]
    public async Task Existing_remote_rules_are_left_out_of_the_generated_file_on_old_git()
    {
        using var env = await TwoAccountsAsync();
        if (!await env.Manager.Rules.RemoteRulesSupportedAsync()) return;
        await env.Manager.Rules.AddRemoteRuleAsync("work", "https://gitlab.company.com", null);
        await env.Manager.Rules.AddFolderRuleAsync("work", Sub(env, "work"));

        env.Manager.Rules.GitVersionOverride = new Version(2, 30, 0);
        await env.Manager.Rules.RegenerateAsync();

        var text = File.ReadAllText(env.Paths.RulesConfig);
        Assert.DoesNotContain("hasconfig", text);
        Assert.Contains("gitdir", text);
        Assert.Equal(2, env.Manager.Rules.All().Count); // still remembered
    }

    [Theory]
    [InlineData("mycompany/../x")]
    [InlineData("a b")]
    [InlineData("a/*")]
    [InlineData("a//b")]
    public void Bad_group_paths_are_rejected(string path) =>
        Assert.Throws<RuleException>(() => RuleFactory.CreateRemote("work", "https://gitlab.company.com", path));

    [Fact]
    public void Remote_rules_need_https_hosts() =>
        Assert.Throws<RuleException>(() => RuleFactory.CreateRemote("work", "http://gitlab.company.com", null));

    [Fact]
    public async Task Duplicate_rules_are_rejected()
    {
        using var env = await TwoAccountsAsync();
        var dir = Sub(env, "work");
        await env.Manager.Rules.AddFolderRuleAsync("work", dir);

        await Assert.ThrowsAsync<RuleException>(() => env.Manager.Rules.AddFolderRuleAsync("personal", dir));
        Assert.Single(env.Manager.Rules.All());
    }

    [Fact]
    public async Task Rules_for_unknown_accounts_are_rejected()
    {
        using var env = await TwoAccountsAsync();
        await Assert.ThrowsAsync<RuleException>(() => env.Manager.Rules.AddFolderRuleAsync("ghost", Sub(env, "x")));
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_rules_and_the_generated_sections()
    {
        using var env = await TwoAccountsAsync();
        await env.Manager.Rules.AddFolderRuleAsync("work", Sub(env, "work"));
        await env.Manager.Rules.AddFolderRuleAsync("personal", Sub(env, "play"));

        await env.Manager.DeleteAccountAsync("work");

        Assert.Equal(["personal"], env.Manager.Rules.All().Select(r => r.AccountId));
        Assert.DoesNotContain("work.gitconfig", File.ReadAllText(env.Paths.RulesConfig));
        // And git still parses everything.
        Assert.Equal("me@home.example", await env.EffectiveAsync(env.CreateRepo(Path.Combine("work", "p")), "user.email"));
    }

    [Fact]
    public async Task Rules_survive_a_restart()
    {
        using var env = await TwoAccountsAsync();
        await env.Manager.Rules.AddFolderRuleAsync("work", Sub(env, "work"));

        var reopened = new PersonaManager(env.Git, env.Paths);
        Assert.Single(reopened.Rules.All());
        Assert.Equal(RuleType.Folder, reopened.Rules.All()[0].Type);
    }

    [Fact]
    public async Task The_managed_block_loads_rules_after_the_default_so_rules_win()
    {
        using var env = await TwoAccountsAsync();
        var text = env.ReadGlobal();
        Assert.True(text.IndexOf("active.gitconfig", StringComparison.Ordinal) < text.IndexOf("rules.gitconfig", StringComparison.Ordinal));
    }

    [Fact]
    public void Matcher_agrees_with_the_generated_conditions()
    {
        var folder = RuleFactory.CreateFolder("w", Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gp-work")));
        var root = folder.Folder!.TrimEnd('/');
        Assert.True(RuleMatcher.Matches(folder, root + "/app", []));
        Assert.False(RuleMatcher.Matches(folder, root + "-other/app", []));

        var remote = RuleFactory.CreateRemote("w", "https://gitlab.company.com", "mycompany");
        Assert.True(RuleMatcher.Matches(remote, "/x", ["https://muneel@gitlab.company.com/mycompany/a.git"]));
        Assert.False(RuleMatcher.Matches(remote, "/x", ["https://gitlab.company.com/mycompanyx/a.git"]));
        Assert.False(RuleMatcher.Matches(remote, "/x", ["git@gitlab.company.com:mycompany/a.git"]));
    }
}
