using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitPersona.Core;

public sealed class AccountValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Everything the user types is data: validate it before it reaches a file or a git process.</summary>
public static partial class AccountValidator
{
    public static bool IsValidId(string id) => IdRegex().IsMatch(id);

    public static void EnsureValidId(string id)
    {
        if (!IdRegex().IsMatch(id))
            throw new ArgumentException($"Invalid account id '{id}'.", nameof(id));
    }

    public static bool HasControlCharacters(string value) => value.Any(char.IsControl);

    /// <summary>Normalises to <c>https://host[:port]</c>. Only https origins are accepted.</summary>
    public static bool TryNormalizeHost(string? input, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Host URL is required.";
            return false;
        }
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            error = "Host must be an https:// URL.";
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = "Host URL must not contain a username or password.";
            return false;
        }
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Host URL must be just the server address (for example https://gitlab.company.com), without a path.";
            return false;
        }

        var host = uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : uri.Host;
        normalized = "https://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        error = null;
        return true;
    }

    /// <summary>The <c>host[:port]</c> form git's credential protocol expects.</summary>
    public static string CredentialHost(string normalizedHostUrl) => normalizedHostUrl["https://".Length..];

    public static IReadOnlyList<string> Validate(Account account)
    {
        var errors = new List<string>();

        if (!IdRegex().IsMatch(account.Id)) errors.Add("Account id is invalid.");
        Require(account.Label, "Label", errors);
        Require(account.Username, "Username", errors);
        Require(account.Name, "Commit name", errors);
        Require(account.Email, "Commit email", errors);

        if (account.Email.Length > 0 && !account.Email.Contains('@'))
            errors.Add("Commit email must contain '@'.");

        if (!TryNormalizeHost(account.HostUrl, out var normalized, out var hostError))
            errors.Add(hostError);
        else if (normalized != account.HostUrl)
            errors.Add($"Host URL must be normalised to '{normalized}'.");

        if (account.AuthType == AuthType.Browser && account.Provider != Provider.GitHub)
            errors.Add("Browser sign-in is only available for GitHub accounts.");

        return errors;
    }

    public static void EnsureValid(Account account)
    {
        var errors = Validate(account);
        if (errors.Count > 0) throw new AccountValidationException(errors);
    }

    static void Require(string value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{field} is required.");
        else if (HasControlCharacters(value)) errors.Add($"{field} must not contain control characters.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdRegex();
}
