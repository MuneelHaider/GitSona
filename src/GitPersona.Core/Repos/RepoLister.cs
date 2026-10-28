using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GitPersona.Core;

/// <summary>A repository an account can see on its host. <see cref="IsPrivate"/> is true for anything that is not public.</summary>
public sealed record RemoteRepo(string FullName, string CloneUrl, bool IsPrivate, string? Description)
{
    /// <summary>Lets a ComboBox jump to an item by typing its name.</summary>
    public override string ToString() => FullName;
}

public sealed record RepoListResult
{
    public IReadOnlyList<RemoteRepo> Repos { get; init; } = [];
    public string? Error { get; init; }
    /// <summary>True when the account has more repositories than we are willing to fetch.</summary>
    public bool Truncated { get; init; }

    public bool Succeeded => Error is null;
}

/// <summary>
/// Lists every repository (public and private) the account's token can access, from the account's own host.
/// Like <see cref="TokenValidator"/>, the token goes only in a request header to that host, redirects are not
/// followed, and certificate validation is never disabled. Pages are requested by number, never by following
/// a server-supplied URL, so the token can only ever be sent to the account's host.
/// </summary>
public sealed class RepoLister
{
    const int PageSize = 100;
    const int MaxPages = 30; // 3,000 repositories; beyond that the dropdown is useless and the URL box still works.

    readonly HttpClient http;

    public RepoLister(HttpClient? client = null)
    {
        http = client ?? TokenValidator.CreateDefaultClient();
    }

    public async Task<RepoListResult> ListAsync(Provider provider, string hostUrl, string token, CancellationToken ct = default)
    {
        if (!AccountValidator.TryNormalizeHost(hostUrl, out var host, out var error)) return Fail(error);
        if (string.IsNullOrWhiteSpace(token) || AccountValidator.HasControlCharacters(token)) return Fail("There is no usable token for this account.");

        return provider switch
        {
            Provider.GitHub => await ListAsync(host, token.Trim(), GitHubRequest, ParseGitHub, ct),
            Provider.GitLab => await ListAsync(host, token.Trim(), GitLabRequest, ParseGitLab, ct),
            _ => Fail("Listing repositories is not available for this kind of host."),
        };
    }

    // ---- GitHub ----

    static HttpRequestMessage GitHubRequest(string host, string token, int page)
    {
        var apiBase = host == "https://github.com" ? "https://api.github.com" : host + "/api/v3";
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBase}/user/repos?per_page={PageSize}&page={page}&sort=pushed&affiliation=owner,collaborator,organization_member");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("GitPersona");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    static RemoteRepo? ParseGitHub(JsonElement item) =>
        Build(Str(item, "full_name"), Str(item, "clone_url"), item.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True, Str(item, "description"));

    // ---- GitLab ----

    static HttpRequestMessage GitLabRequest(string host, string token, int page)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{host}/api/v4/projects?membership=true&archived=false&per_page={PageSize}&page={page}&order_by=last_activity_at");
        request.Headers.Add("PRIVATE-TOKEN", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("GitPersona");
        return request;
    }

    static RemoteRepo? ParseGitLab(JsonElement item) =>
        Build(Str(item, "path_with_namespace"), Str(item, "http_url_to_repo"), Str(item, "visibility") is not "public", Str(item, "description"));

    // ---- shared ----

    static RemoteRepo? Build(string? fullName, string? cloneUrl, bool isPrivate, string? description) =>
        fullName is null || cloneUrl is null || !cloneUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? null
            : new RemoteRepo(fullName, cloneUrl, isPrivate, description);

    async Task<RepoListResult> ListAsync(string host, string token,
        Func<string, string, int, HttpRequestMessage> buildRequest, Func<JsonElement, RemoteRepo?> parse, CancellationToken ct)
    {
        var repos = new List<RemoteRepo>();
        for (var page = 1; page <= MaxPages; page++)
        {
            HttpResponseMessage response;
            using var request = buildRequest(host, token, page);
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (TokenValidator.IsTlsFailure(ex))
            {
                return Fail($"The secure connection to {host} failed: {TokenValidator.FirstTlsMessage(ex)} If this server uses an internal certificate authority, install that CA in your operating system's trust store.");
            }
            catch (HttpRequestException ex)
            {
                return Fail($"Could not reach {host}: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail($"{host} did not respond in time.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode) return Fail(DescribeFailure(response, host));

                JsonDocument doc;
                try
                {
                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (JsonException)
                {
                    return Fail($"{host} did not return a list of repositories. Is the host's type set correctly?");
                }

                using (doc)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return Fail($"{host} did not return a list of repositories. Is the host's type set correctly?");

                    var count = 0;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        count++;
                        if (item.ValueKind == JsonValueKind.Object && parse(item) is { } repo) repos.Add(repo);
                    }
                    if (count < PageSize) return new RepoListResult { Repos = repos };
                }
            }
        }
        return new RepoListResult { Repos = repos, Truncated = true };
    }

    static string DescribeFailure(HttpResponseMessage response, string host)
    {
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400)
            return $"{host} redirected the request. Check the host URL (it must be the server's real address).";
        if (response.StatusCode is HttpStatusCode.Unauthorized)
            return $"{host} rejected the saved token. It may have expired or been revoked; update the account's token.";
        if (response.StatusCode is HttpStatusCode.Forbidden)
            return $"{host} refused to list repositories with this token (missing permission, or a rate limit). A GitHub classic token needs the 'repo' scope; a GitLab token needs 'read_api' or 'api'.";
        return $"{host} answered with HTTP {code}.";
    }

    static RepoListResult Fail(string? message) => new() { Error = message ?? "Could not list repositories." };

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()
            : null;
}
