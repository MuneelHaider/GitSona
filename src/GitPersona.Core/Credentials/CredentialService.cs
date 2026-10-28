using System.Text;
using GitPersona.Core.Git;

namespace GitPersona.Core;

/// <summary>
/// Stores and removes tokens through <c>git credential approve|reject</c>. The token travels only on
/// stdin — never in arguments or URLs — and is never written to disk or logs by this app.
/// </summary>
public sealed class CredentialService(GitRunner git)
{
    public async Task ApproveAsync(string hostUrl, string username, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("Token is empty.", nameof(token));
        var input = BuildInput(hostUrl, username, token);
        (await git.RunAsync(["credential", "approve"], stdin: input, cancellationToken: ct)).EnsureSuccess("git credential approve");
    }

    public async Task RejectAsync(string hostUrl, string username, CancellationToken ct = default)
    {
        var input = BuildInput(hostUrl, username, password: null);
        (await git.RunAsync(["credential", "reject"], stdin: input, cancellationToken: ct)).EnsureSuccess("git credential reject");
    }

    /// <summary>For the "test login" button: reports only whether a credential exists. The secret is discarded.</summary>
    public async Task<bool> HasCredentialAsync(string hostUrl, string username, CancellationToken ct = default)
    {
        var input = BuildInput(hostUrl, username, password: null);
        var result = await git.RunAsync(["credential", "fill"], stdin: input, cancellationToken: ct);
        return result.Success && ParsePassword(result.StdOut) is { Length: > 0 };
    }

    /// <summary>
    /// Interactive sign-in through the user's credential helper: <c>git credential fill</c> with the helper's own
    /// UI enabled. With Git Credential Manager this opens the browser for GitHub's OAuth flow and returns the
    /// signed-in login plus a token. Nothing is stored here; the caller decides, and the token is never logged.
    /// </summary>
    public async Task<SignInCredential> SignInAsync(string hostUrl, string? usernameHint = null, CancellationToken ct = default)
    {
        var input = BuildInput(hostUrl, usernameHint, password: null);
        var result = await git.RunAsync(["credential", "fill"], stdin: input,
            timeout: TimeSpan.FromMinutes(5), cancellationToken: ct, allowCredentialUi: true);

        if (!result.Success)
        {
            var err = result.StdErr.Trim();
            throw new GitException(err.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
                ? "No credential helper handled the sign-in. Browser sign-in needs Git Credential Manager."
                : "Sign-in did not complete" + (err.Length > 0 ? ": " + string.Join(" ", err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)) : "."));
        }

        string? username = null, password = null;
        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("username=", StringComparison.Ordinal)) username = line["username=".Length..];
            else if (line.StartsWith("password=", StringComparison.Ordinal)) password = line["password=".Length..];
        }
        username ??= usernameHint;
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(username))
            throw new GitException("The credential helper did not return a login. Try again, or use a personal access token.");
        return new SignInCredential(username, password);
    }

    /// <summary>Test seam: lets tests verify which secret git hands back. Not part of the public surface.</summary>
    internal async Task<string?> FillPasswordAsync(string hostUrl, string? username, CancellationToken ct = default)
    {
        var input = BuildInput(hostUrl, username, password: null);
        var result = await git.RunAsync(["credential", "fill"], stdin: input, cancellationToken: ct);
        return result.Success ? ParsePassword(result.StdOut) : null;
    }

    internal static string BuildInput(string hostUrl, string? username, string? password)
    {
        if (!AccountValidator.TryNormalizeHost(hostUrl, out var normalized, out var error))
            throw new ArgumentException(error, nameof(hostUrl));

        // A newline in any value would let the caller inject extra credential-protocol keys.
        if (username is not null && (username.Length == 0 || AccountValidator.HasControlCharacters(username)))
            throw new ArgumentException("Username is empty or contains control characters.", nameof(username));
        if (password is not null && AccountValidator.HasControlCharacters(password))
            throw new ArgumentException("Token contains control characters.", nameof(password));

        var sb = new StringBuilder();
        sb.Append("protocol=https\n");
        sb.Append("host=").Append(AccountValidator.CredentialHost(normalized)).Append('\n');
        if (username is not null) sb.Append("username=").Append(username).Append('\n');
        if (password is not null) sb.Append("password=").Append(password).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    static string? ParsePassword(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("password=", StringComparison.Ordinal)) return trimmed["password=".Length..];
        }
        return null;
    }
}

/// <summary>A login obtained interactively. <c>ToString</c> never prints the token.</summary>
public sealed record SignInCredential(string Username, string Token)
{
    public override string ToString() => $"SignInCredential({Username})";
}
