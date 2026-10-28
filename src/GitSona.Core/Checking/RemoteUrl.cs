using System.Text.RegularExpressions;

namespace GitSona.Core;

/// <summary>A git remote URL taken apart. Passwords are detected but never kept.</summary>
public sealed partial record RemoteUrl
{
    public bool IsHttps { get; init; }
    public bool IsSsh { get; init; }
    public bool HasPassword { get; init; }
    public string? Username { get; init; }
    public string? HostUrl { get; init; }
    public string Path { get; init; } = "";

    /// <summary>The URL with any password removed, safe to display.</summary>
    public string Redacted { get; init; } = "";

    /// <summary>URL for <c>git config --get-urlmatch</c> (login removed).</summary>
    public string? UrlForMatching => HostUrl is null ? null : HostUrl + Path;

    public static RemoteUrl Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return new RemoteUrl();
        url = url.Trim();

        // scp-like syntax: git@host:path
        if (ScpRegex().IsMatch(url) && !url.Contains("://"))
            return new RemoteUrl { IsSsh = true, Redacted = url };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new RemoteUrl { Redacted = url };

        if (uri.Scheme is "ssh" or "git+ssh" or "ssh+git")
            return new RemoteUrl { IsSsh = true, Redacted = RedactUserInfo(uri) };

        if (uri.Scheme != "https")
            return new RemoteUrl { Redacted = url.Contains('@') ? RedactUserInfo(uri) : url };

        string? user = null;
        var hasPassword = false;
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            hasPassword = colon >= 0;
            user = Uri.UnescapeDataString(colon >= 0 ? uri.UserInfo[..colon] : uri.UserInfo);
        }

        var origin = "https://" + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        return new RemoteUrl
        {
            IsHttps = true,
            HasPassword = hasPassword,
            Username = string.IsNullOrEmpty(user) ? null : user,
            HostUrl = origin,
            Path = uri.AbsolutePath,
            Redacted = (user is null ? origin : "https://" + Uri.EscapeDataString(user) + "@" + origin["https://".Length..]) + uri.AbsolutePath,
        };
    }

    static string RedactUserInfo(Uri uri)
    {
        var colon = uri.UserInfo.IndexOf(':');
        var user = colon >= 0 ? uri.UserInfo[..colon] : uri.UserInfo;
        return $"{uri.Scheme}://{(user.Length > 0 ? user + "@" : "")}{uri.Authority.Split('@').Last()}{uri.PathAndQuery}";
    }

    [GeneratedRegex(@"^[\w.-]+@[\w.-]+:")]
    private static partial Regex ScpRegex();
}
