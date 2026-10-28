using GitPersona.Core.Git;

namespace GitPersona.Core.Platform;

public enum HelperState
{
    /// <summary>A helper that stores tokens in the OS credential store.</summary>
    Good,
    /// <summary>Works, but tokens are not kept securely or not kept at all (store, cache).</summary>
    Weak,
    /// <summary>No helper configured: tokens cannot be saved.</summary>
    Missing,
}

public sealed record CredentialHelperStatus(string? Current, HelperState State, string? Recommended, string Explanation, bool UsesGitCredentialManager = false);

/// <summary>First-run check: tokens live in Git's credential helper, so one must be configured. Never changes anything unasked.</summary>
public sealed class CredentialHelperService(GitRunner git)
{
    static readonly string[] SecureHelpers = ["manager", "manager-core", "osxkeychain", "libsecret", "wincred", "gnome-keyring"];
    static readonly string[] SettableHelpers = ["manager", "osxkeychain", "libsecret", "wincred"];

    public async Task<CredentialHelperStatus> CheckAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(["config", "--get-all", "credential.helper"], cancellationToken: ct);
        var helpers = new List<string>();
        if (result.Success)
        {
            // Git processes helpers in order, and an empty value ("helper =") resets the list built so far.
            foreach (var line in result.StdOut.Split('\n').Select(l => l.TrimEnd('\r')).SkipLast(1))
            {
                if (line.Trim().Length == 0) helpers.Clear();
                else helpers.Add(line.Trim());
            }
        }
        var current = helpers.LastOrDefault();
        var recommended = await RecommendAsync(ct);

        if (current is null)
        {
            return new CredentialHelperStatus(null, HelperState.Missing, recommended,
                "Git has no credential helper configured, so GitPersona cannot save tokens. " +
                (recommended is null
                    ? HowToInstall()
                    : $"Setting it to '{recommended}' stores tokens in your system's credential store."));
        }

        var name = HelperName(current);
        if (helpers.Any(h => SecureHelpers.Contains(HelperName(h))))
            return new CredentialHelperStatus(current, HelperState.Good, recommended, $"Tokens are stored by the '{name}' credential helper.",
                helpers.Any(h => HelperName(h) is "manager" or "manager-core"));

        return new CredentialHelperStatus(current, HelperState.Weak, recommended,
            name == "store"
                ? "The 'store' helper keeps tokens in a plain-text file. Consider a secure helper" + (recommended is null ? "." : $" such as '{recommended}'.")
                : $"The '{name}' helper does not keep tokens permanently, so you will be asked to sign in again." +
                  (recommended is null ? "" : $" Consider '{recommended}'."));
    }

    /// <summary>Runs <c>git config --global credential.helper &lt;helper&gt;</c>. Call only after the user said yes.</summary>
    public async Task SetGlobalHelperAsync(string helper, CancellationToken ct = default)
    {
        if (!SettableHelpers.Contains(helper))
            throw new ArgumentException($"'{helper}' is not one of the supported credential helpers.", nameof(helper));
        (await git.RunAsync(["config", "--global", "credential.helper", helper], cancellationToken: ct))
            .EnsureSuccess("git config credential.helper");
    }

    async Task<string?> RecommendAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return "manager"; // Git Credential Manager ships with Git for Windows
        if (OperatingSystem.IsMacOS()) return "osxkeychain";

        // Linux: only recommend what is actually installed.
        var exec = await git.RunAsync(["--exec-path"], cancellationToken: ct);
        if (exec.Success && File.Exists(Path.Combine(exec.StdOut.Trim(), "git-credential-libsecret"))) return "libsecret";
        return OnPath("git-credential-manager") ? "manager" : null;
    }

    static string HowToInstall() => OperatingSystem.IsLinux()
        ? "Install git-credential-manager (https://github.com/git-ecosystem/git-credential-manager) or your distribution's git-credential-libsecret package, then re-check."
        : "Install Git Credential Manager (https://github.com/git-ecosystem/git-credential-manager), then re-check.";

    static string HelperName(string helper)
    {
        var first = helper.Trim().Split(' ', 2)[0];
        var name = Path.GetFileName(first.Replace('\\', '/'));
        return name.StartsWith("git-credential-", StringComparison.Ordinal) ? name["git-credential-".Length..] : name;
    }

    static bool OnPath(string program)
    {
        var exe = OperatingSystem.IsWindows() ? program + ".exe" : program;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => { try { return File.Exists(Path.Combine(dir, exe)); } catch (ArgumentException) { return false; } });
    }
}
