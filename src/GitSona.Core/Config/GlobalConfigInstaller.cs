using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitSona.Core.Git;

namespace GitSona.Core;

public sealed record ConfigWarning(string Message, int Line);

public sealed record InstallResult(
    string GlobalConfigPath,
    string? BackupPath,
    bool BackupCreated,
    bool Changed,
    IReadOnlyList<ConfigWarning> OverridesFoundBefore);

public sealed record UninstallResult(
    string GlobalConfigPath,
    bool RestoredFromBackup,
    string? BackupPath,
    IReadOnlyList<string> Warnings);

/// <summary>What we remember about the user's config from before our first modification.</summary>
public sealed record InstallState(string GlobalConfigPath, string? BackupPath, bool GlobalConfigExisted);

/// <summary>
/// Owns the single managed block in the user's global gitconfig: backup before the first change,
/// idempotent install, removal, and detection of user settings that would override the app.
/// </summary>
public sealed partial class GlobalConfigInstaller(GitRunner git, AppPaths paths, TimeProvider? clock = null)
{
    // The user's file may not be valid UTF-8. Latin-1 maps bytes 1:1 to chars, so unrelated content
    // survives a read/write round trip byte for byte. Our own text is converted through UTF-8 first.
    static readonly Encoding Bytes = Encoding.Latin1;
    static readonly UTF8Encoding Utf8 = new(false);

    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public string ResolveGlobalConfigPath()
    {
        if (git.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") is { Length: > 0 } explicitPath)
            return Path.GetFullPath(explicitPath);

        var home = FirstNonEmpty(
            git.GetEnvironmentVariable("HOME"),
            git.GetEnvironmentVariable("USERPROFILE"),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile))
            ?? throw new InvalidOperationException("Cannot determine the home directory.");

        // Same precedence git uses when writing with --global.
        var primary = Path.Combine(home, ".gitconfig");
        if (File.Exists(primary)) return primary;

        var xdgHome = git.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        var xdg = Path.Combine(xdgHome, "git", "config");
        return File.Exists(xdg) ? xdg : primary;
    }

    public async Task<InstallResult> InstallAsync(CancellationToken ct = default)
    {
        var path = ResolveGlobalConfigPath();
        var existed = File.Exists(path);
        var original = existed ? await File.ReadAllBytesAsync(path, ct) : [];
        var text = Bytes.GetString(original);

        var state = LoadState();
        if (state is not null && !PathsEqual(state.GlobalConfigPath, path)) state = null;

        var hasBlock = ManagedBlock.Contains(text);
        var overrides = FindOverrides(text);

        string? backupPath = state?.BackupPath is { } known && File.Exists(known) ? known : null;
        var backupCreated = false;
        if (backupPath is null && existed && !hasBlock)
        {
            backupPath = NextBackupPath(path);
            File.Copy(path, backupPath);
            backupCreated = true;
        }

        var nl = ManagedBlock.DetectNewline(text);
        var block = Bytes.GetString(Utf8.GetBytes(ManagedBlock.Render(paths.ActiveConfig, paths.RulesConfig, nl)));
        var updated = ManagedBlock.Apply(text, block);

        var changed = !existed || updated != text;
        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written in place (not replaced) so a symlinked dotfile keeps pointing where it did.
            await File.WriteAllBytesAsync(path, Bytes.GetBytes(updated), ct);
        }

        SaveState(new InstallState(path, backupPath, state?.GlobalConfigExisted ?? existed));
        return new InstallResult(path, backupPath, backupCreated, changed, overrides);
    }

    /// <summary>
    /// Removes the managed block. With <paramref name="restoreBackup"/> the pre-install backup is copied back
    /// instead, which also discards any edits the user made to the file since.
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(bool restoreBackup, CancellationToken ct = default)
    {
        var path = ResolveGlobalConfigPath();
        var state = LoadState();
        if (state is not null && !PathsEqual(state.GlobalConfigPath, path)) state = null;

        var warnings = new List<string>();
        var restored = false;
        var backupPath = state?.BackupPath;

        if (restoreBackup && backupPath is not null && File.Exists(backupPath))
        {
            File.Copy(backupPath, path, overwrite: true);
            restored = true;
        }
        else
        {
            if (restoreBackup) warnings.Add("No backup was found, so only the managed block was removed.");

            if (File.Exists(path))
            {
                var text = Bytes.GetString(await File.ReadAllBytesAsync(path, ct));
                var updated = ManagedBlock.Remove(text);
                if (updated != text) await File.WriteAllBytesAsync(path, Bytes.GetBytes(updated), ct);
            }
        }

        // We created the file; don't leave an empty one behind.
        if (state is { GlobalConfigExisted: false } && File.Exists(path) && new FileInfo(path).Length == 0)
            File.Delete(path);

        File.Delete(paths.StateFile);
        return new UninstallResult(path, restored, backupPath, warnings);
    }

    /// <summary>
    /// User settings placed after the managed block, which silently win over the app. Re-running
    /// <see cref="InstallAsync"/> moves the block back to the end.
    /// </summary>
    public IReadOnlyList<ConfigWarning> DetectOverrides()
    {
        var path = ResolveGlobalConfigPath();
        return File.Exists(path) ? FindOverrides(Bytes.GetString(File.ReadAllBytes(path))) : [];
    }

    public bool IsInstalled()
    {
        var path = ResolveGlobalConfigPath();
        return File.Exists(path) && ManagedBlock.Contains(Bytes.GetString(File.ReadAllBytes(path)));
    }

    static IReadOnlyList<ConfigWarning> FindOverrides(string text)
    {
        var after = ManagedBlock.TextAfterBlock(text);
        if (after is null) return [];

        var firstLine = text[..^after.Length].Count(c => c == '\n') + 1;
        var warnings = new List<ConfigWarning>();
        string? section = null;
        var lineNo = firstLine - 1;

        foreach (var raw in after.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            var header = SectionRegex().Match(line);
            if (header.Success)
            {
                section = header.Groups[1].Value.ToLowerInvariant();
                line = header.Groups[2].Value.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;
            }

            var key = KeyRegex().Match(line);
            if (!key.Success || section is null) continue;

            var name = key.Groups[1].Value.ToLowerInvariant();
            if (section == "user" && name == "email")
                warnings.Add(new ConfigWarning("user.email is set after the GitSona block and overrides the active account's email.", lineNo));
            else if (section == "user" && name == "name")
                warnings.Add(new ConfigWarning("user.name is set after the GitSona block and overrides the active account's name.", lineNo));
            else if (section == "credential" && name == "username")
                warnings.Add(new ConfigWarning("A credential username is set after the GitSona block and overrides the account's login.", lineNo));
        }
        return warnings;
    }

    string NextBackupPath(string configPath)
    {
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var candidate = $"{configPath}.gitsona-backup-{stamp}";
        for (var i = 2; File.Exists(candidate); i++) candidate = $"{configPath}.gitsona-backup-{stamp}-{i}";
        return candidate;
    }

    InstallState? LoadState()
    {
        if (!File.Exists(paths.StateFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(paths.StateFile), AccountStore.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    void SaveState(InstallState state)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.StateFile, JsonSerializer.Serialize(state, AccountStore.JsonOptions));
    }

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    [GeneratedRegex(@"^\[\s*([A-Za-z0-9.-]+)(?:\s+""(?:[^""\\]|\\.)*"")?\s*\](.*)$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9-]*)\s*(?:=|$)")]
    private static partial Regex KeyRegex();
}
