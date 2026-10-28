namespace GitSona.Core.Tests;

public class PersonaManagerTests
{
    [Fact]
    public async Task Adding_an_account_with_a_token_stores_it_in_the_credential_helper()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), "glpat-abc");

        Assert.Equal("glpat-abc", await env.Manager.Credentials.FillPasswordAsync("https://gitlab.company.com", "muneel-work"));
        Assert.True(File.Exists(env.Paths.ProfileConfig("work")));
    }

    [Fact]
    public async Task Host_is_normalised_on_add()
    {
        using var env = new TestEnv();
        var added = await env.Manager.AddAccountAsync(TestEnv.MakeAccount(host: "https://GitLab.Company.com/"), null);
        Assert.Equal("https://gitlab.company.com", added.HostUrl);
    }

    [Fact]
    public async Task Invalid_accounts_change_nothing()
    {
        using var env = new TestEnv();
        var bad = TestEnv.MakeAccount(host: "http://insecure.example.com");

        await Assert.ThrowsAsync<AccountValidationException>(() => env.Manager.AddAccountAsync(bad, "tok"));

        Assert.Empty(env.Manager.Accounts.All());
        Assert.False(File.Exists(env.CredentialFile));
    }

    [Fact]
    public async Task The_same_login_on_the_same_host_cannot_be_added_twice()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount("one"), null);

        var ex = await Assert.ThrowsAsync<AccountValidationException>(() =>
            env.Manager.AddAccountAsync(TestEnv.MakeAccount("two", username: "MUNEEL-WORK"), null));
        Assert.Contains("already uses", ex.Message);
    }

    [Fact]
    public async Task Update_rewrites_the_fragment_and_keeps_the_stored_token_when_none_is_supplied()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), "glpat-abc");
        await env.Manager.SetActiveAsync("work");

        await env.Manager.UpdateAccountAsync(TestEnv.MakeAccount(email: "new@company.com"), newToken: null);

        Assert.Equal("new@company.com", await env.EffectiveAsync(env.CreateRepo(), "user.email"));
        Assert.True(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "muneel-work"));
    }

    [Fact]
    public async Task Changing_the_username_removes_the_old_login_and_stores_the_new_one()
    {
        using var env = new TestEnv();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), "old-token");

        await env.Manager.UpdateAccountAsync(TestEnv.MakeAccount(username: "renamed"), "new-token");

        Assert.False(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "muneel-work"));
        Assert.Equal("new-token", await env.Manager.Credentials.FillPasswordAsync("https://gitlab.company.com", "renamed"));
    }

    [Fact]
    public async Task Delete_removes_fragment_metadata_and_stored_login()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), "glpat-abc");
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount("other", username: "someone-else"), "tok2");

        var warnings = await env.Manager.DeleteAccountAsync("work");

        Assert.Empty(warnings);
        Assert.False(File.Exists(env.Paths.ProfileConfig("work")));
        Assert.Null(env.Manager.Accounts.Find("work"));
        Assert.False(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "muneel-work"));
        Assert.True(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "someone-else"));
    }

    [Fact]
    public async Task Deleting_the_active_account_clears_the_default()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), null);
        await env.Manager.SetActiveAsync("work");
        var repo = env.CreateRepo();

        await env.Manager.DeleteAccountAsync("work");

        Assert.Null(env.Manager.Accounts.ActiveAccountId);
        Assert.Null(await env.EffectiveAsync(repo, "user.email"));
    }

    [Fact]
    public async Task Setting_an_unknown_account_active_is_refused()
    {
        using var env = new TestEnv();
        await Assert.ThrowsAsync<ArgumentException>(() => env.Manager.SetActiveAsync("nope"));
    }

    [Fact]
    public async Task Uninstall_can_also_reject_stored_logins_and_delete_app_data()
    {
        using var env = new TestEnv("[user]\n\tname = Keep\n");
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), "glpat-abc");
        await env.Manager.SetActiveAsync("work");

        var report = await env.Manager.UninstallAsync(new UninstallOptions(RejectCredentials: true, DeleteAppData: true));

        Assert.Empty(report.Warnings);
        Assert.Equal("[user]\n\tname = Keep\n", env.ReadGlobal());
        Assert.False(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "muneel-work"));
        Assert.False(Directory.Exists(env.AppDataDir));
    }

    [Fact]
    public async Task Uninstall_keeps_app_data_and_logins_by_default()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), "glpat-abc");

        await env.Manager.UninstallAsync(new UninstallOptions());

        Assert.True(File.Exists(env.Paths.AccountsFile));
        Assert.True(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "muneel-work"));
    }
}
