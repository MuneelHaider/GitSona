using System.Text.Json;

namespace GitPersona.Core.Platform;

/// <summary>Preferences only — no secrets. Stored in <c>settings.json</c>.</summary>
public sealed record AppSettings
{
    public bool FirstRunCompleted { get; init; }
    public bool TrayNoticeShown { get; init; }

    /// <summary>Check GitHub Releases for updates (installed builds only, at most once a day).</summary>
    public bool AutoUpdateCheck { get; init; } = true;

    public DateTimeOffset? LastUpdateCheck { get; init; }
}

public sealed class SettingsStore(AppPaths paths)
{
    public AppSettings Load()
    {
        if (!File.Exists(paths.SettingsFile)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), AccountStore.JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings(); // preferences are disposable; don't block startup on a bad file
        }
    }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        var updated = change(Load());
        Directory.CreateDirectory(paths.Root);
        var tmp = paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(updated, AccountStore.JsonOptions));
        File.Move(tmp, paths.SettingsFile, overwrite: true);
        return updated;
    }
}
