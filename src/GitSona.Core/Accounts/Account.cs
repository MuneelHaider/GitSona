using System.Security.Cryptography;

namespace GitSona.Core;

public enum Provider { GitHub, GitLab, Other }

public enum AuthType { PersonalAccessToken, Browser }

/// <summary>Account metadata. There is deliberately no token field: secrets live only in git's credential helper.</summary>
public sealed record Account
{
    public string Id { get; init; } = NewId();
    public string Label { get; init; } = "";
    public Provider Provider { get; init; } = Provider.Other;
    public string HostUrl { get; init; } = "";
    public string Username { get; init; } = "";
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public AuthType AuthType { get; init; } = AuthType.PersonalAccessToken;
    public DateOnly? TokenExpiresOn { get; init; }

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
}
