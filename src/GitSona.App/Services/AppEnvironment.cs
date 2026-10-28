using System.Reflection;
using GitSona.Core;

namespace GitSona.App.Services;

internal static class AppEnvironment
{
    public const string PlaceholderRepo = "/OWNER/";

    /// <summary>Normally the per-user app-data folder; <c>GITSONA_DATA_DIR</c> overrides it (portable use, testing).</summary>
    public static AppPaths ResolvePaths() =>
        Environment.GetEnvironmentVariable("GITSONA_DATA_DIR") is { Length: > 0 } dir ? new AppPaths(dir) : AppPaths.Default();

    public static string Version
    {
        get
        {
            var info = typeof(AppEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return "0.0.0";
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }

    public static string RepositoryUrl =>
        typeof(AppEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "";

    public static bool RepositoryConfigured => RepositoryUrl.Length > 0 && !RepositoryUrl.Contains(PlaceholderRepo);

    public static string StartupLabel => OperatingSystem.IsWindows() ? "Start with Windows" : "Start at login";

    public static string TrayName => OperatingSystem.IsMacOS() ? "menu bar" : OperatingSystem.IsWindows() ? "system tray" : "tray";

    /// <summary>The executable that startup entries should launch, or null when running under <c>dotnet run</c>.</summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }
}
