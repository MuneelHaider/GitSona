using System.Text.RegularExpressions;

namespace GitPersona.Core.Git;

public static partial class GitLocator
{
    /// <summary>Minimum git version for <c>includeIf "hasconfig:remote.*.url:..."</c>.</summary>
    public static readonly Version HasConfigRemoteMinimum = new(2, 36, 0);

    public static string? Find()
    {
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";

        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in WindowsFallbacks())
                if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static IEnumerable<string> WindowsFallbacks()
    {
        foreach (var folder in new[]
        {
            System.Environment.SpecialFolder.ProgramFiles,
            System.Environment.SpecialFolder.ProgramFilesX86,
        })
        {
            var root = System.Environment.GetFolderPath(folder);
            if (root.Length > 0) yield return Path.Combine(root, "Git", "cmd", "git.exe");
        }
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) yield return Path.Combine(local, "Programs", "Git", "cmd", "git.exe");
    }

    public static Version? ParseVersion(string output)
    {
        var m = VersionRegex().Match(output);
        return m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;
    }

    public static bool SupportsHasConfigRemote(Version gitVersion) => gitVersion >= HasConfigRemoteMinimum;

    [GeneratedRegex(@"git version (\d+)\.(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();
}
