using System.Net;
using System.Security.Authentication;
using System.Text;

namespace GitPersona.Core.Tests;

public class TokenValidatorTests
{
    const string Token = "tok-SECRET-123";

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    static (TokenValidator, FakeHandler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new TokenValidator(new HttpClient(handler)), handler);
    }

    // ---- GitHub ----

    [Fact]
    public async Task GitHub_classic_token_returns_profile_scopes_and_expiry()
    {
        var (v, handler) = Make(_ => Json("""{"login":"octo","name":"Octo Cat","email":"octo@example.com"}""",
            headers: [("X-OAuth-Scopes", "repo, read:org"), ("github-authentication-token-expiration", "2026-11-01 10:00:00 UTC")]));

        var r = await v.ValidateAsync(Provider.GitHub, "https://github.com", Token);

        Assert.True(r.IsValid);
        Assert.Equal("octo", r.Username);
        Assert.Equal("Octo Cat", r.Name);
        Assert.Equal("octo@example.com", r.Email);
        Assert.Equal(["repo", "read:org"], r.Scopes);
        Assert.Equal(new DateOnly(2026, 11, 1), r.ExpiresOn);
        Assert.Empty(r.Warnings);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/user", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Token, req.Headers.Authorization.Parameter);
        Assert.DoesNotContain(Token, req.RequestUri.ToString());
    }

    [Fact]
    public async Task GitHub_classic_token_without_repo_scope_warns()
    {
        var (v, _) = Make(_ => Json("""{"login":"octo"}""", headers: [("X-OAuth-Scopes", "read:user")]));
        var r = await v.ValidateAsync(Provider.GitHub, "https://github.com", Token);

        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("'repo'"));
        Assert.Null(r.Email); // private email stays empty
        Assert.Null(r.Name);
    }

    [Fact]
    public async Task GitHub_fine_grained_token_gets_an_informational_warning_not_a_scope_error()
    {
        var (v, _) = Make(_ => Json("""{"login":"octo"}"""));
        var r = await v.ValidateAsync(Provider.GitHub, "https://github.com", Token);

        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("fine-grained"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("'repo'"));
    }

    [Fact]
    public async Task GitHub_enterprise_uses_the_api_v3_path_on_its_own_host()
    {
        var (v, handler) = Make(_ => Json("""{"login":"octo"}""", headers: [("X-OAuth-Scopes", "repo")]));
        await v.ValidateAsync(Provider.GitHub, "https://ghe.company.com", Token);
        Assert.Equal("https://ghe.company.com/api/v3/user", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task GitHub_bad_token_is_rejected_without_echoing_it()
    {
        var (v, _) = Make(_ => Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized));
        var r = await v.ValidateAsync(Provider.GitHub, "https://github.com", Token);

        Assert.Equal(ValidationOutcome.Rejected, r.Outcome);
        Assert.False(r.CanSaveUnvalidated);
        Assert.DoesNotContain(Token, r.Message);
    }

    // ---- GitLab ----

    static HttpResponseMessage GitLab(HttpRequestMessage req, string scopes = """["read_repository","write_repository"]""", string expires = "\"2026-12-31\"")
    {
        var path = req.RequestUri!.AbsolutePath;
        return path switch
        {
            "/api/v4/user" => Json("""{"username":"muneel-work","name":"Muneel Work","email":"muneel@company.com"}"""),
            "/api/v4/personal_access_tokens/self" => Json($$"""{"scopes":{{scopes}},"expires_at":{{expires}},"active":true}"""),
            _ => Json("{}", HttpStatusCode.NotFound),
        };
    }

    [Fact]
    public async Task GitLab_returns_profile_scopes_and_expiry_from_both_endpoints()
    {
        var (v, handler) = Make(req => GitLab(req));

        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.company.com", Token);

        Assert.True(r.IsValid);
        Assert.Equal("muneel-work", r.Username);
        Assert.Equal("Muneel Work", r.Name);
        Assert.Equal("muneel@company.com", r.Email);
        Assert.Equal(new DateOnly(2026, 12, 31), r.ExpiresOn);
        Assert.Equal(["read_repository", "write_repository"], r.Scopes);
        Assert.Empty(r.Warnings);

        Assert.Equal(
            ["https://gitlab.company.com/api/v4/user", "https://gitlab.company.com/api/v4/personal_access_tokens/self"],
            handler.Requests.Select(x => x.RequestUri!.ToString()));
        Assert.All(handler.Requests, x =>
        {
            Assert.Equal(Token, x.Headers.GetValues("PRIVATE-TOKEN").Single());
            Assert.DoesNotContain(Token, x.RequestUri!.ToString());
            Assert.Null(x.Headers.Authorization);
        });
    }

    [Theory]
    [InlineData("""["read_repository"]""", "write_repository")]
    [InlineData("""["write_repository"]""", "read_repository")]
    [InlineData("""["read_user"]""", "read_repository and write_repository")]
    public async Task GitLab_warns_about_missing_repository_scopes(string scopes, string expectedInWarning)
    {
        var (v, _) = Make(req => GitLab(req, scopes));
        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token);

        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains(expectedInWarning));
    }

    [Fact]
    public async Task GitLab_api_scope_is_enough()
    {
        var (v, _) = Make(req => GitLab(req, """["api"]"""));
        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task GitLab_token_without_expiry_has_no_expiry_date()
    {
        var (v, _) = Make(req => GitLab(req, expires: "null"));
        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token);
        Assert.Null(r.ExpiresOn);
    }

    [Fact]
    public async Task GitLab_without_the_self_endpoint_is_still_valid_with_a_note()
    {
        var (v, _) = Make(req => req.RequestUri!.AbsolutePath == "/api/v4/user"
            ? Json("""{"username":"u","name":"U","email":"u@x.example"}""")
            : Json("{}", HttpStatusCode.NotFound));

        var r = await v.ValidateAsync(Provider.GitLab, "https://old.gitlab.example", Token);

        Assert.True(r.IsValid);
        Assert.Null(r.ExpiresOn);
        Assert.Contains(r.Warnings, w => w.Contains("scopes or expiry"));
    }

    [Fact]
    public async Task GitLab_falls_back_to_the_public_email()
    {
        var (v, _) = Make(req => req.RequestUri!.AbsolutePath == "/api/v4/user"
            ? Json("""{"username":"u","name":"U","public_email":"pub@x.example"}""")
            : Json("{}", HttpStatusCode.NotFound));
        Assert.Equal("pub@x.example", (await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token)).Email);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GitLab_rejected_tokens_are_reported(HttpStatusCode code)
    {
        var (v, _) = Make(_ => Json("""{"message":"401 Unauthorized"}""", code));
        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token);
        Assert.Equal(ValidationOutcome.Rejected, r.Outcome);
    }

    // ---- failures ----

    [Fact]
    public async Task Tls_failures_are_shown_plainly_and_allow_saving_without_validation()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")));
        var v = new TokenValidator(new HttpClient(handler));

        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.internal.example", Token);

        Assert.Equal(ValidationOutcome.TlsError, r.Outcome);
        Assert.True(r.CanSaveUnvalidated);
        Assert.Contains("UntrustedRoot", r.Message);
        Assert.Contains("never turns certificate checks off", r.Message);
        Assert.DoesNotContain(Token, r.Message);
    }

    [Fact]
    public async Task Unreachable_hosts_allow_saving_without_validation()
    {
        var v = new TokenValidator(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("No such host is known."))));
        var r = await v.ValidateAsync(Provider.GitLab, "https://nowhere.example", Token);
        Assert.Equal(ValidationOutcome.NetworkError, r.Outcome);
        Assert.True(r.CanSaveUnvalidated);
    }

    [Fact]
    public async Task Redirects_are_not_followed_and_are_reported()
    {
        var (v, handler) = Make(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example/steal");
            return r;
        });

        var result = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token);

        Assert.Equal(ValidationOutcome.NetworkError, result.Outcome);
        Assert.Contains("redirected", result.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://gitlab.company.com")]
    [InlineData("ftp://gitlab.company.com")]
    [InlineData("gitlab.company.com")]
    public async Task Non_https_hosts_never_reach_the_network(string host)
    {
        var (v, handler) = Make(_ => Json("{}"));
        var r = await v.ValidateAsync(Provider.GitLab, host, Token);

        Assert.False(r.IsValid);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Other_providers_cannot_be_validated_and_make_no_requests()
    {
        var (v, handler) = Make(_ => Json("{}"));
        var r = await v.ValidateAsync(Provider.Other, "https://git.example.com", Token);

        Assert.Equal(ValidationOutcome.NotSupported, r.Outcome);
        Assert.True(r.CanSaveUnvalidated);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Tokens_with_line_breaks_are_refused_before_any_request()
    {
        var (v, handler) = Make(_ => Json("{}"));
        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", "abc\r\nX-Evil: 1");
        Assert.False(r.IsValid);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Non_json_answers_are_reported_as_wrong_server()
    {
        var (v, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        var r = await v.ValidateAsync(Provider.GitLab, "https://gitlab.com", Token);
        Assert.Equal(ValidationOutcome.NetworkError, r.Outcome);
        Assert.Contains("GitLab", r.Message);
    }
}
