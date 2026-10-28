using System.Net;
using System.Text;

namespace GitSona.Core.Tests;

public class RepoListerTests
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

    static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static (RepoLister, FakeHandler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new RepoLister(new HttpClient(handler)), handler);
    }

    static string GitHubItem(int i, bool isPrivate = false) =>
        $$"""{"full_name":"octo/repo{{i}}","clone_url":"https://github.com/octo/repo{{i}}.git","private":{{(isPrivate ? "true" : "false")}},"description":"d{{i}}"}""";

    static string Page(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

    // ---- GitHub ----

    [Fact]
    public async Task GitHub_lists_public_and_private_repos_with_the_token_in_a_header_only()
    {
        var (lister, handler) = Make(_ => Json(Page([GitHubItem(1), GitHubItem(2, isPrivate: true)])));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.True(r.Succeeded);
        Assert.False(r.Truncated);
        Assert.Equal(["octo/repo1", "octo/repo2"], r.Repos.Select(x => x.FullName));
        Assert.Equal(["https://github.com/octo/repo1.git", "https://github.com/octo/repo2.git"], r.Repos.Select(x => x.CloneUrl));
        Assert.Equal([false, true], r.Repos.Select(x => x.IsPrivate));

        var req = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.github.com/user/repos?", req.RequestUri!.ToString());
        Assert.Contains("affiliation=owner,collaborator,organization_member", req.RequestUri.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Token, req.Headers.Authorization.Parameter);
        Assert.DoesNotContain(Token, req.RequestUri.ToString());
    }

    [Fact]
    public async Task GitHub_follows_pages_until_a_short_page()
    {
        var (lister, handler) = Make(req => req.RequestUri!.Query.Contains("&page=1&")
            ? Json(Page(Enumerable.Range(1, 100).Select(i => GitHubItem(i))))
            : Json(Page([GitHubItem(101)])));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.True(r.Succeeded);
        Assert.Equal(101, r.Repos.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GitHub_enterprise_uses_the_v3_api_on_its_own_host()
    {
        var (lister, handler) = Make(_ => Json("[]"));

        var r = await lister.ListAsync(Provider.GitHub, "https://ghe.company.com", Token);

        Assert.True(r.Succeeded);
        Assert.Empty(r.Repos);
        Assert.StartsWith("https://ghe.company.com/api/v3/user/repos?", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task A_huge_account_is_cut_off_and_reported()
    {
        var (lister, handler) = Make(_ => Json(Page(Enumerable.Range(1, 100).Select(i => GitHubItem(i)))));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.True(r.Succeeded);
        Assert.True(r.Truncated);
        Assert.Equal(30, handler.Requests.Count);
        Assert.Equal(3000, r.Repos.Count);
    }

    // ---- GitLab ----

    [Fact]
    public async Task GitLab_lists_member_projects_and_treats_internal_as_not_public()
    {
        var (lister, handler) = Make(_ => Json("""
            [{"path_with_namespace":"team/app","http_url_to_repo":"https://gitlab.company.com/team/app.git","visibility":"private","description":null},
             {"path_with_namespace":"team/site","http_url_to_repo":"https://gitlab.company.com/team/site.git","visibility":"public"},
             {"path_with_namespace":"team/wiki","http_url_to_repo":"https://gitlab.company.com/team/wiki.git","visibility":"internal"}]
            """));

        var r = await lister.ListAsync(Provider.GitLab, "https://gitlab.company.com", Token);

        Assert.True(r.Succeeded);
        Assert.Equal(["team/app", "team/site", "team/wiki"], r.Repos.Select(x => x.FullName));
        Assert.Equal([true, false, true], r.Repos.Select(x => x.IsPrivate));

        var req = Assert.Single(handler.Requests);
        Assert.StartsWith("https://gitlab.company.com/api/v4/projects?membership=true", req.RequestUri!.ToString());
        Assert.Equal(Token, req.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.DoesNotContain(Token, req.RequestUri.ToString());
    }

    // ---- problems ----

    [Fact]
    public async Task Entries_without_an_https_clone_url_or_name_are_skipped()
    {
        var (lister, _) = Make(_ => Json("""
            [{"full_name":"a/ssh","clone_url":"git@github.com:a/ssh.git","private":false},
             {"clone_url":"https://github.com/a/noname.git","private":false},
             {"full_name":"a/ok","clone_url":"https://github.com/a/ok.git","private":false}]
            """));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.Equal(["a/ok"], r.Repos.Select(x => x.FullName));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "refused")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Http_errors_become_a_message_that_never_contains_the_token(HttpStatusCode code, string expected)
    {
        var (lister, _) = Make(_ => Json("{}", code));

        var r = await lister.ListAsync(Provider.GitLab, "https://gitlab.com", Token);

        Assert.False(r.Succeeded);
        Assert.Contains(expected, r.Error);
        Assert.DoesNotContain(Token, r.Error);
    }

    [Fact]
    public async Task Redirects_are_reported_not_followed()
    {
        var (lister, handler) = Make(_ => new HttpResponseMessage(HttpStatusCode.Found));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.False(r.Succeeded);
        Assert.Contains("redirected", r.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_non_list_response_is_reported()
    {
        var (lister, _) = Make(_ => Json("""{"message":"hello"}"""));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.False(r.Succeeded);
        Assert.Contains("did not return a list", r.Error);
    }

    [Fact]
    public async Task Network_failures_are_reported()
    {
        var (lister, _) = Make(_ => throw new HttpRequestException("boom"));

        var r = await lister.ListAsync(Provider.GitHub, "https://github.com", Token);

        Assert.False(r.Succeeded);
        Assert.Contains("Could not reach", r.Error);
    }

    [Fact]
    public async Task Other_hosts_and_missing_tokens_make_no_request()
    {
        var (lister, handler) = Make(_ => Json("[]"));

        Assert.False((await lister.ListAsync(Provider.Other, "https://git.example.com", Token)).Succeeded);
        Assert.False((await lister.ListAsync(Provider.GitHub, "https://github.com", "")).Succeeded);
        Assert.False((await lister.ListAsync(Provider.GitHub, "https://github.com", "bad\ntoken")).Succeeded);
        Assert.False((await lister.ListAsync(Provider.GitHub, "http://github.com", Token)).Succeeded);

        Assert.Empty(handler.Requests);
    }
}
