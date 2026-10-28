using Xunit.Abstractions;

namespace GitSona.Core.Tests;

/// <summary>
/// Tests that talk to the real internet. They are skipped unless GITSONA_LIVE=1, so normal runs stay offline.
///
///   GITSONA_LIVE=1                      network-only checks (real servers, dummy token, public repo clone)
///   GITSONA_LIVE_GITHUB_TOKEN=...       also checks YOUR real GitHub token
///   GITSONA_LIVE_GITLAB_TOKEN=...       also checks YOUR real GitLab token
///   GITSONA_LIVE_GITLAB_HOST=https://gitlab.company.com   (default https://gitlab.com)
///
/// Tokens are read from the environment, never written anywhere, and never printed.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(string? requiresEnv = null)
    {
        if (Environment.GetEnvironmentVariable("GITSONA_LIVE") != "1")
            Skip = "Live test: set GITSONA_LIVE=1 to run.";
        else if (requiresEnv is not null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(requiresEnv)))
            Skip = $"Live test: set {requiresEnv} to run.";
    }
}

public class LiveTests(ITestOutputHelper output)
{
    const string Bogus = "ghp_000000000000000000000000000000000000";

    static string Env(string name) => Environment.GetEnvironmentVariable(name)!;

    // ---- real servers, dummy token ----

    [LiveFact]
    public async Task Real_GitHub_rejects_a_bogus_token()
    {
        var r = await new TokenValidator().ValidateAsync(Provider.GitHub, "https://github.com", Bogus);
        output.WriteLine($"{r.Outcome}: {r.Message}");
        Assert.Equal(ValidationOutcome.Rejected, r.Outcome);
    }

    [LiveFact]
    public async Task Real_GitLab_rejects_a_bogus_token()
    {
        var r = await new TokenValidator().ValidateAsync(Provider.GitLab, "https://gitlab.com", "glpat-000000000000000000000");
        output.WriteLine($"{r.Outcome}: {r.Message}");
        Assert.Equal(ValidationOutcome.Rejected, r.Outcome);
    }

    [LiveFact]
    public async Task A_real_server_with_a_bad_certificate_is_a_tls_error_and_is_never_bypassed()
    {
        var r = await new TokenValidator().ValidateAsync(Provider.GitLab, "https://expired.badssl.com", "glpat-x");
        output.WriteLine($"{r.Outcome}: {r.Message}");
        Assert.Equal(ValidationOutcome.TlsError, r.Outcome);
        Assert.True(r.CanSaveUnvalidated);
    }

    [LiveFact]
    public async Task A_real_server_that_is_not_gitlab_is_reported_clearly()
    {
        var r = await new TokenValidator().ValidateAsync(Provider.GitLab, "https://example.com", "glpat-x");
        output.WriteLine($"{r.Outcome}: {r.Message}");
        Assert.False(r.IsValid);
        Assert.NotNull(r.Message);
    }

    [LiveFact]
    public async Task An_unreachable_real_host_is_a_network_error()
    {
        var r = await new TokenValidator().ValidateAsync(Provider.GitLab, "https://no-such-host.invalid", "glpat-x");
        output.WriteLine($"{r.Outcome}: {r.Message}");
        Assert.Equal(ValidationOutcome.NetworkError, r.Outcome);
    }

    // ---- your real tokens ----

    [LiveFact("GITSONA_LIVE_GITHUB_TOKEN")]
    public async Task Your_real_GitHub_token_validates()
    {
        var r = await new TokenValidator().ValidateAsync(Provider.GitHub, "https://github.com", Env("GITSONA_LIVE_GITHUB_TOKEN"));
        Report(r);
        Assert.True(r.IsValid, r.Message);
        Assert.False(string.IsNullOrEmpty(r.Username));
    }

    [LiveFact("GITSONA_LIVE_GITLAB_TOKEN")]
    public async Task Your_real_GitLab_token_validates()
    {
        var host = Environment.GetEnvironmentVariable("GITSONA_LIVE_GITLAB_HOST") is { Length: > 0 } h ? h : "https://gitlab.com";
        var r = await new TokenValidator().ValidateAsync(Provider.GitLab, host, Env("GITSONA_LIVE_GITLAB_TOKEN"));
        Report(r);
        Assert.True(r.IsValid, r.Message);
        Assert.False(string.IsNullOrEmpty(r.Username));
    }

    /// <summary>
    /// The full path with a real token: validate, store through git credential (into a throwaway file, not your
    /// OS store), switch accounts, and confirm git hands the same token back for the right login.
    /// </summary>
    [LiveFact("GITSONA_LIVE_GITLAB_TOKEN")]
    public async Task Your_real_GitLab_token_round_trips_through_the_credential_helper()
    {
        var host = Environment.GetEnvironmentVariable("GITSONA_LIVE_GITLAB_HOST") is { Length: > 0 } h ? h : "https://gitlab.com";
        var token = Env("GITSONA_LIVE_GITLAB_TOKEN");
        var result = await new TokenValidator().ValidateAsync(Provider.GitLab, host, token);
        Assert.True(result.IsValid, result.Message);

        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        var account = TestEnv.MakeAccount("live", host, result.Username!, result.Name ?? "Live", result.Email ?? "live@example.com") with
        {
            TokenExpiresOn = result.ExpiresOn,
        };
        await env.Manager.AddAccountAsync(account, token);
        await env.Manager.SetActiveAsync("live");

        Assert.Equal(token, await env.Manager.Credentials.FillPasswordAsync(host, result.Username));
        Assert.True(await env.Manager.Credentials.HasCredentialAsync(host, result.Username!));
        Assert.DoesNotContain(token, File.ReadAllText(env.Paths.AccountsFile));
    }

    // ---- real git over real HTTPS ----

    [LiveFact]
    public async Task Clone_with_an_account_works_against_a_real_https_server()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();
        var account = TestEnv.MakeAccount("gh", "https://github.com", "someone", "Some One", "someone@example.com", Provider.GitHub);
        await env.Manager.AddAccountAsync(account, token: null);
        await env.Manager.SetActiveAsync("gh");

        var dest = Path.Combine(env.Root, "clones");
        var target = await env.Manager.Repos.CloneAsync("https://github.com/octocat/Hello-World.git", "gh", dest);

        Assert.True(File.Exists(Path.Combine(target, "README")));
        Assert.Equal("https://someone@github.com/octocat/Hello-World.git", await env.EffectiveAsync(target, "remote.origin.url"));

        var check = await env.Manager.Checker.CheckAsync(target);
        output.WriteLine(string.Join("\n", check.Issues.Select(i => $"{i.Severity}: {i.Message}")));
        Assert.Equal("someone", check.Username.Value);
        Assert.False(check.HasMismatch);
        TestEnv.DeleteTree(dest);
    }

    void Report(TokenValidationResult r)
    {
        // Never prints the token: only what the provider said about it.
        output.WriteLine($"outcome={r.Outcome} user={r.Username} name={r.Name} email={(r.Email is null ? "(private)" : "(set)")}");
        output.WriteLine($"scopes=[{string.Join(", ", r.Scopes)}] expires={(r.ExpiresOn?.ToString("yyyy-MM-dd") ?? "none/unknown")}");
        foreach (var w in r.Warnings) output.WriteLine("warning: " + w);
    }
}
