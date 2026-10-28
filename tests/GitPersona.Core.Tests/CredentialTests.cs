namespace GitPersona.Core.Tests;

public class CredentialTests
{
    const string Host = "https://gitlab.company.com";

    [Fact]
    public async Task Approve_then_fill_then_reject_round_trip()
    {
        using var env = new TestEnv();
        var creds = env.Manager.Credentials;

        Assert.False(await creds.HasCredentialAsync(Host, "muneel-work"));

        await creds.ApproveAsync(Host, "muneel-work", "glpat-secret-123");
        Assert.True(await creds.HasCredentialAsync(Host, "muneel-work"));
        Assert.Equal("glpat-secret-123", await creds.FillPasswordAsync(Host, "muneel-work"));

        await creds.RejectAsync(Host, "muneel-work");
        Assert.False(await creds.HasCredentialAsync(Host, "muneel-work"));
    }

    [Fact]
    public async Task Two_logins_on_one_host_are_independent()
    {
        using var env = new TestEnv();
        var creds = env.Manager.Credentials;
        await creds.ApproveAsync(Host, "one", "token-one");
        await creds.ApproveAsync(Host, "two", "token-two");

        Assert.Equal("token-one", await creds.FillPasswordAsync(Host, "one"));
        Assert.Equal("token-two", await creds.FillPasswordAsync(Host, "two"));

        await creds.RejectAsync(Host, "one");
        Assert.False(await creds.HasCredentialAsync(Host, "one"));
        Assert.True(await creds.HasCredentialAsync(Host, "two"));
    }

    [Fact]
    public async Task Ports_are_part_of_the_host()
    {
        using var env = new TestEnv();
        var creds = env.Manager.Credentials;
        await creds.ApproveAsync("https://git.example.com:8443", "u", "tok");

        Assert.True(await creds.HasCredentialAsync("https://git.example.com:8443", "u"));
        Assert.False(await creds.HasCredentialAsync("https://git.example.com", "u"));
    }

    [Theory]
    [InlineData("http://gitlab.company.com")]
    [InlineData("ssh://gitlab.company.com")]
    [InlineData("gitlab.company.com")]
    [InlineData("https://user:pw@gitlab.company.com")]
    [InlineData("https://gitlab.company.com/group")]
    public async Task Non_https_or_decorated_hosts_are_rejected(string host)
    {
        using var env = new TestEnv();
        await Assert.ThrowsAsync<ArgumentException>(() => env.Manager.Credentials.ApproveAsync(host, "u", "tok"));
    }

    [Theory]
    [InlineData("tok\nhost=evil.example.com")]
    [InlineData("tok\r\nusername=evil")]
    [InlineData("tok\0")]
    public async Task Control_characters_cannot_smuggle_extra_protocol_keys(string token)
    {
        using var env = new TestEnv();
        await Assert.ThrowsAsync<ArgumentException>(() => env.Manager.Credentials.ApproveAsync(Host, "u", token));
        Assert.False(File.Exists(env.CredentialFile));
    }

    [Fact]
    public async Task Username_with_a_newline_is_rejected()
    {
        using var env = new TestEnv();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            env.Manager.Credentials.ApproveAsync(Host, "u\npassword=x", "tok"));
    }

    [Fact]
    public void Protocol_input_is_stdin_formatted_with_a_terminating_blank_line()
    {
        var input = CredentialService.BuildInput(Host, "muneel-work", "pat");
        Assert.Equal("protocol=https\nhost=gitlab.company.com\nusername=muneel-work\npassword=pat\n\n", input);
    }


    [Fact]
    public async Task Sign_in_returns_the_login_and_token_the_helper_provides()
    {
        using var env = new TestEnv();
        await env.Manager.Credentials.ApproveAsync("https://github.com", "octocat", "gho_token-from-oauth");

        var credential = await env.Manager.Credentials.SignInAsync("https://github.com");

        Assert.Equal("octocat", credential.Username);
        Assert.Equal("gho_token-from-oauth", credential.Token);
        Assert.DoesNotContain("gho_token", credential.ToString());
    }

    [Fact]
    public async Task Sign_in_with_a_username_hint_picks_that_login()
    {
        using var env = new TestEnv();
        await env.Manager.Credentials.ApproveAsync("https://github.com", "first", "tok-1");
        await env.Manager.Credentials.ApproveAsync("https://github.com", "second", "tok-2");

        var credential = await env.Manager.Credentials.SignInAsync("https://github.com", "second");

        Assert.Equal(("second", "tok-2"), (credential.Username, credential.Token));
    }

    [Fact]
    public async Task Sign_in_without_any_working_helper_explains_what_is_missing()
    {
        using var env = new TestEnv(extraConfig: [("credential.helper", "")]); // no helper at all

        var ex = await Assert.ThrowsAsync<GitPersona.Core.Git.GitException>(() =>
            env.Manager.Credentials.SignInAsync("https://github.com"));

        Assert.Contains("Git Credential Manager", ex.Message);
    }

    [Fact]
    public async Task Sign_in_refuses_non_https_hosts()
    {
        using var env = new TestEnv();
        await Assert.ThrowsAsync<ArgumentException>(() => env.Manager.Credentials.SignInAsync("http://github.com"));
    }
    [Fact]
    public async Task Passwords_containing_equals_signs_round_trip()
    {
        using var env = new TestEnv();
        await env.Manager.Credentials.ApproveAsync(Host, "u", "a=b=c==");
        Assert.Equal("a=b=c==", await env.Manager.Credentials.FillPasswordAsync(Host, "u"));
    }
}
