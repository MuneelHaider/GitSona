using GitPersona.Core;
using GitPersona.Core.Git;
using GitPersona.Core.Platform;

namespace GitPersona.App.Services;

/// <summary>Everything the view models share. <see cref="Manager"/> is null when git is missing.</summary>
public sealed class AppHost
{
    public required AppPaths Paths { get; init; }
    public required GitStatus Git { get; set; }
    public PersonaManager? Manager { get; private set; }
    public CredentialHelperService? HelperService { get; private set; }
    public required SettingsStore Settings { get; init; }
    public IStartupRegistration StartupRegistration { get; } = Core.Platform.StartupRegistration.ForCurrentOS();
    public TokenValidator Validator { get; } = new();
    public RepoLister RepoLister { get; } = new();
    public required UpdateService Updates { get; init; }

    /// <summary>Raised after accounts, rules, the active account or settings change, so the tray and pages can refresh.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public static async Task<AppHost> CreateAsync(AppPaths paths)
    {
        var settings = new SettingsStore(paths);
        var host = new AppHost { Paths = paths, Git = await GitStatus.DetectAsync(), Settings = settings, Updates = new UpdateService(settings) };
        host.Bind();
        return host;
    }

    /// <summary>Re-detects git (after the user installs it) and wires up the manager.</summary>
    public async Task RedetectGitAsync()
    {
        Git = await GitStatus.DetectAsync();
        Bind();
        NotifyChanged();
    }

    void Bind()
    {
        if (Git.IsAvailable)
        {
            var runner = new GitRunner(Git.GitPath!);
            Manager = new PersonaManager(runner, Paths);
            HelperService = new CredentialHelperService(runner);
        }
        else
        {
            Manager = null;
            HelperService = null;
        }
    }
}
