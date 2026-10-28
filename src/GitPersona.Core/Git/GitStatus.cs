namespace GitPersona.Core.Git;

/// <summary>What the first-run check learns about the machine's git.</summary>
public sealed record GitStatus(string? GitPath, Version? Version, bool RemoteRulesSupported, string? Problem)
{
    public bool IsAvailable => GitPath is not null && Version is not null;

    public static async Task<GitStatus> DetectAsync(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var path = GitLocator.Find();
        if (path is null)
            return new GitStatus(null, null, false,
                OperatingSystem.IsWindows()
                    ? "Git was not found. Install Git for Windows from https://git-scm.com/download/win."
                    : "Git was not found. Install it with your package manager, or from https://git-scm.com.");
        try
        {
            var runner = new GitRunner(path, environment);
            var version = await runner.GetVersionAsync();
            return new GitStatus(path, version, GitLocator.SupportsHasConfigRemote(version), null);
        }
        catch (GitException ex)
        {
            return new GitStatus(path, null, false, $"Git was found at {path} but could not be run: {ex.Message}");
        }
    }
}
