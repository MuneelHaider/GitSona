using System.Text.Json;

namespace GitSona.Core.Tests;

public class AccountTests
{
    [Theory]
    [InlineData("https://gitlab.company.com", "https://gitlab.company.com")]
    [InlineData("https://GitLab.Company.com/", "https://gitlab.company.com")]
    [InlineData("  https://github.com  ", "https://github.com")]
    [InlineData("https://gitlab.company.com:443", "https://gitlab.company.com")]
    [InlineData("https://git.example.com:8443/", "https://git.example.com:8443")]
    public void Normalizes_hosts(string input, string expected)
    {
        Assert.True(AccountValidator.TryNormalizeHost(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://gitlab.company.com")]
    [InlineData("git@gitlab.company.com:group/repo.git")]
    [InlineData("ssh://git@gitlab.company.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pw@gitlab.company.com")]
    [InlineData("https://gitlab.company.com/group/repo")]
    [InlineData("https://gitlab.company.com?x=1")]
    [InlineData("https://gitlab.company.com#frag")]
    public void Rejects_hosts_that_are_not_plain_https_origins(string input) =>
        Assert.False(AccountValidator.TryNormalizeHost(input, out _, out var error) || error is null);

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    public void Account_ids_cannot_escape_the_profiles_directory(string id)
    {
        Assert.False(AccountValidator.IsValidId(id));
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gp-unused"));
        Assert.Throws<ArgumentException>(() => paths.ProfileConfig(id));
    }

    [Fact]
    public void Generated_ids_are_valid_and_unique()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Account.NewId()).ToList();
        Assert.All(ids, id => Assert.True(AccountValidator.IsValidId(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Validation_reports_every_problem()
    {
        var account = new Account { Id = "x", HostUrl = "http://nope", Email = "no-at-sign" };
        var errors = AccountValidator.Validate(account);
        Assert.Contains(errors, e => e.Contains("Label"));
        Assert.Contains(errors, e => e.Contains("Username"));
        Assert.Contains(errors, e => e.Contains("Commit name"));
        Assert.Contains(errors, e => e.Contains("'@'"));
        Assert.Contains(errors, e => e.Contains("https"));
    }

    [Fact]
    public void Browser_sign_in_is_github_only()
    {
        var gitlab = TestEnv.MakeAccount() with { AuthType = AuthType.Browser };
        Assert.Contains(AccountValidator.Validate(gitlab), e => e.Contains("GitHub"));

        var github = TestEnv.MakeAccount(host: "https://github.com", provider: Provider.GitHub) with { AuthType = AuthType.Browser };
        Assert.Empty(AccountValidator.Validate(github));
    }

    [Fact]
    public async Task Metadata_round_trips_through_accounts_json()
    {
        using var env = new TestEnv();
        var account = TestEnv.MakeAccount() with { TokenExpiresOn = new DateOnly(2027, 3, 14) };
        await env.Manager.AddAccountAsync(account, token: null);
        await env.Manager.SetActiveAsync("work");

        var reopened = new AccountStore(env.Paths);
        Assert.Equal(account, reopened.Find("work"));
        Assert.Equal("work", reopened.ActiveAccountId);

        var json = JsonDocument.Parse(File.ReadAllText(env.Paths.AccountsFile));
        Assert.Equal("GitLab", json.RootElement.GetProperty("accounts")[0].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task The_token_is_never_written_to_any_file_the_app_owns()
    {
        using var env = new TestEnv();
        const string token = "glpat-VERY-SECRET-0123456789";

        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(TestEnv.MakeAccount(), token);
        await env.Manager.SetActiveAsync("work");
        await env.Manager.UpdateAccountAsync(TestEnv.MakeAccount(name: "Renamed"), newToken: null);
        Assert.True(await env.Manager.Credentials.HasCredentialAsync("https://gitlab.company.com", "muneel-work"));

        // Everything under the app-data dir and the global gitconfig — the credential helper's own file is the only place it may live.
        var files = Directory.EnumerateFiles(env.AppDataDir, "*", SearchOption.AllDirectories).Append(env.GlobalConfig);
        foreach (var file in files)
            Assert.DoesNotContain(token, File.ReadAllText(file));
    }

    [Fact]
    public async Task Corrupt_accounts_json_is_reported_not_overwritten()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(env.AppDataDir);
        File.WriteAllText(env.Paths.AccountsFile, "{ this is not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => env.Manager.AddAccountAsync(TestEnv.MakeAccount(), null));
        Assert.Equal("{ this is not json", File.ReadAllText(env.Paths.AccountsFile));
    }

    [Fact]
    public async Task A_hand_edited_traversal_id_in_accounts_json_is_refused()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(env.AppDataDir);
        File.WriteAllText(env.Paths.AccountsFile, """{"version":1,"accounts":[{"id":"../../evil"}]}""");

        Assert.Throws<InvalidDataException>(() => new AccountStore(env.Paths).All());
    }
}
