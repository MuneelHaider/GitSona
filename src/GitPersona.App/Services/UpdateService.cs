using GitPersona.Core.Platform;
using Velopack;
using Velopack.Sources;

namespace GitPersona.App.Services;

/// <summary>
/// Auto-update from GitHub Releases via Velopack. Only active in an installed build (the portable exe never
/// updates itself), at most once a day, and it applies an update only when the user asks.
/// </summary>
public sealed class UpdateService(SettingsStore settings)
{
    UpdateManager? manager;
    UpdateInfo? available;

    public UpdateInfo? Available => available;

    public string? AvailableVersion => available?.TargetFullRelease.Version.ToString();

    UpdateManager? Manager
    {
        get
        {
            if (manager is not null) return manager;
            if (!AppEnvironment.RepositoryConfigured) return null;
            try
            {
                var m = new UpdateManager(new GithubSource(AppEnvironment.RepositoryUrl, null, false));
                return m.IsInstalled ? manager = m : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsSupported => Manager is not null;

    public string UnavailableReason =>
        !AppEnvironment.RepositoryConfigured ? "Update source is not configured in this build."
        : "This is a portable copy; updates are only available in the installed version.";

    /// <summary>Checks if allowed. <paramref name="force"/> skips the once-a-day limit and the setting (manual button).</summary>
    public async Task<UpdateInfo?> CheckAsync(bool force = false)
    {
        var m = Manager;
        if (m is null) return null;

        var current = settings.Load();
        if (!force)
        {
            if (!current.AutoUpdateCheck) return null;
            if (current.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24)) return available;
        }

        try
        {
            available = await m.CheckForUpdatesAsync();
            settings.Update(s => s with { LastUpdateCheck = DateTimeOffset.UtcNow });
        }
        catch
        {
            // Offline or rate-limited: try again next time, never bother the user.
        }
        return available;
    }

    public async Task ApplyAsync()
    {
        if (Manager is not { } m || available is null) return;
        await m.DownloadUpdatesAsync(available);
        m.ApplyUpdatesAndRestart(available);
    }
}
