using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;

namespace GitPersona.Core;

public enum ValidationOutcome
{
    /// <summary>The token was accepted by the provider.</summary>
    Valid,
    /// <summary>The provider rejected the token (401/403).</summary>
    Rejected,
    /// <summary>TLS handshake or certificate failure; the user may choose to save without validation.</summary>
    TlsError,
    /// <summary>Host unreachable, timed out, or returned something unexpected.</summary>
    NetworkError,
    /// <summary>This provider has no API we can check (Provider.Other).</summary>
    NotSupported,
}

public sealed record TokenValidationResult
{
    public required ValidationOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public string? Username { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsValid => Outcome == ValidationOutcome.Valid;

    /// <summary>True when saving without validation is a reasonable choice (self-hosted with an internal CA, offline).</summary>
    public bool CanSaveUnvalidated => Outcome is ValidationOutcome.TlsError or ValidationOutcome.NetworkError or ValidationOutcome.NotSupported;
}

/// <summary>
/// Checks a token against the account's own provider API. The token goes only in a request header to that
/// host — never in a URL, never logged, and redirects are not followed so it cannot be forwarded elsewhere.
/// Certificate validation is never disabled: the OS trust store decides.
/// </summary>
public sealed class TokenValidator
{
    readonly HttpClient http;

    public TokenValidator(HttpClient? client = null)
    {
        http = client ?? CreateDefaultClient();
    }

    public static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public Task<TokenValidationResult> ValidateAsync(Provider provider, string hostUrl, string token, CancellationToken ct = default)
    {
        if (!AccountValidator.TryNormalizeHost(hostUrl, out var host, out var error))
            return Task.FromResult(Failure(ValidationOutcome.NetworkError, error));
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(Failure(ValidationOutcome.Rejected, "Enter a token."));
        if (AccountValidator.HasControlCharacters(token))
            return Task.FromResult(Failure(ValidationOutcome.Rejected, "The token contains characters that are not allowed."));

        return provider switch
        {
            Provider.GitHub => ValidateGitHubAsync(host, token.Trim(), ct),
            Provider.GitLab => ValidateGitLabAsync(host, token.Trim(), ct),
            _ => Task.FromResult(Failure(ValidationOutcome.NotSupported, "Tokens for other hosts cannot be checked automatically. It will be saved as entered.")),
        };
    }

    // ---- GitHub ----

    async Task<TokenValidationResult> ValidateGitHubAsync(string host, string token, CancellationToken ct)
    {
        var apiBase = host == "https://github.com" ? "https://api.github.com" : host + "/api/v3";
        using var request = new HttpRequestMessage(HttpMethod.Get, apiBase + "/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("GitPersona");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var (response, failure) = await SendAsync(request, host, ct);
        if (response is null) return failure!;
        using (response)
        {
            if (Classify(response, host) is { } bad) return bad;

            var json = await ReadJsonAsync(response, ct);
            if (json is null) return Failure(ValidationOutcome.NetworkError, $"{host} did not return a GitHub user. Is this really a GitHub server?");

            var warnings = new List<string>();
            var scopes = new List<string>();
            var classic = response.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues);
            if (classic)
            {
                scopes.AddRange(scopeValues!.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                if (!scopes.Contains("repo"))
                    warnings.Add("This classic token does not have the 'repo' scope, so pushing to private repositories will fail.");
            }
            else
            {
                warnings.Add("This looks like a fine-grained token, so its repository permissions could not be checked. It needs Contents: read and write on the repositories you use.");
            }

            return new TokenValidationResult
            {
                Outcome = ValidationOutcome.Valid,
                Username = GetString(json.Value, "login"),
                Name = GetString(json.Value, "name"),
                Email = GetString(json.Value, "email"), // null unless the user made it public
                Scopes = scopes,
                ExpiresOn = ParseGitHubExpiry(response),
                Warnings = warnings,
            };
        }
    }

    // "2026-11-01 10:00:00 UTC"
    static DateOnly? ParseGitHubExpiry(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("github-authentication-token-expiration", out var values)) return null;
        var text = values.FirstOrDefault()?.Replace("UTC", "").Trim();
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;
    }

    // ---- GitLab ----

    async Task<TokenValidationResult> ValidateGitLabAsync(string host, string token, CancellationToken ct)
    {
        using var userRequest = GitLabRequest(host + "/api/v4/user", token);
        var (userResponse, failure) = await SendAsync(userRequest, host, ct);
        if (userResponse is null) return failure!;

        JsonElement user;
        using (userResponse)
        {
            if (Classify(userResponse, host) is { } bad) return bad;
            var json = await ReadJsonAsync(userResponse, ct);
            if (json is null) return Failure(ValidationOutcome.NetworkError, $"{host} did not return a GitLab user. Is this really a GitLab server?");
            user = json.Value;
        }

        var warnings = new List<string>();
        var scopes = new List<string>();
        DateOnly? expires = null;

        using var selfRequest = GitLabRequest(host + "/api/v4/personal_access_tokens/self", token);
        var (selfResponse, selfFailure) = await SendAsync(selfRequest, host, ct);
        if (selfResponse is null)
        {
            warnings.Add("The token is valid, but its scopes and expiry could not be read: " + selfFailure!.Message);
        }
        else
        {
            using (selfResponse)
            {
                if (selfResponse.IsSuccessStatusCode && await ReadJsonAsync(selfResponse, ct) is { } self)
                {
                    if (self.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array)
                        scopes.AddRange(s.EnumerateArray().Select(e => e.GetString()).OfType<string>());
                    if (GetString(self, "expires_at") is { } at && DateOnly.TryParseExact(at, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        expires = d;

                    // `api` grants everything `read_repository`/`write_repository` do.
                    if (!scopes.Contains("api"))
                    {
                        var missing = new[] { "read_repository", "write_repository" }.Where(x => !scopes.Contains(x)).ToList();
                        if (missing.Count > 0)
                            warnings.Add($"This token is missing the {string.Join(" and ", missing)} scope{(missing.Count > 1 ? "s" : "")}, so Git pushes and pulls will fail.");
                    }
                }
                else
                {
                    warnings.Add("The token is valid, but this GitLab server did not report its scopes or expiry (it may be a project/group token or an older GitLab).");
                }
            }
        }

        return new TokenValidationResult
        {
            Outcome = ValidationOutcome.Valid,
            Username = GetString(user, "username"),
            Name = GetString(user, "name"),
            Email = GetString(user, "email") ?? GetString(user, "public_email"),
            Scopes = scopes,
            ExpiresOn = expires,
            Warnings = warnings,
        };
    }

    static HttpRequestMessage GitLabRequest(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("PRIVATE-TOKEN", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("GitPersona");
        return request;
    }

    // ---- shared ----

    async Task<(HttpResponseMessage? Response, TokenValidationResult? Failure)> SendAsync(HttpRequestMessage request, string host, CancellationToken ct)
    {
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return (response, null);
        }
        catch (HttpRequestException ex) when (IsTlsFailure(ex))
        {
            return (null, Failure(ValidationOutcome.TlsError,
                $"The secure connection to {host} failed: {FirstTlsMessage(ex)} " +
                "If this server uses an internal certificate authority, install that CA in your operating system's trust store. " +
                "You can still save the account without checking the token; GitPersona never turns certificate checks off."));
        }
        catch (HttpRequestException ex)
        {
            return (null, Failure(ValidationOutcome.NetworkError, $"Could not reach {host}: {ex.Message}"));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, Failure(ValidationOutcome.NetworkError, $"{host} did not respond in time."));
        }
    }

    static TokenValidationResult? Classify(HttpResponseMessage response, string host)
    {
        var code = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return null;
        if (code is >= 300 and < 400)
            return Failure(ValidationOutcome.NetworkError, $"{host} redirected the request. Check the host URL (it must be the server's real address).");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Failure(ValidationOutcome.Rejected, $"{host} rejected this token. It may be wrong, expired, or revoked.");
        return Failure(ValidationOutcome.NetworkError, $"{host} answered with HTTP {code}.");
    }

    internal static bool IsTlsFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is AuthenticationException) return true;
        return false;
    }

    internal static string FirstTlsMessage(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is AuthenticationException) return e.Message.TrimEnd('.') + ".";
        return ex.Message;
    }

    static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()
            : null;

    static TokenValidationResult Failure(ValidationOutcome outcome, string? message) =>
        new() { Outcome = outcome, Message = message };
}
