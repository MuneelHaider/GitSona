using Avalonia;
using GitPersona.Core;
using GitPersona.Core.Git;
using GitPersona.Core.Platform;
using GitPersona.App.Services;
using Velopack;

namespace GitPersona.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles the installer/updater/uninstaller launching this exe with special arguments.
        var velopack = VelopackApp.Build();
        // Uninstall hooks exist on Windows only; elsewhere the Settings page does the same job on request.
        if (OperatingSystem.IsWindows()) velopack.OnBeforeUninstallFastCallback(_ => UninstallHook.Run());
        velopack.Run();

        var paths = AppEnvironment.ResolvePaths();
        var instance = SingleInstance.TryAcquire(paths.Root);
        if (instance is null)
        {
            // Another GitPersona is running: ask it to show its window, then leave.
            SingleInstance.SignalExistingAsync(paths.Root, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            return 0;
        }

        using (instance)
        {
            Startup = new StartupContext(paths, instance, args.Contains(StartupRegistration.MinimizedArgument));
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static StartupContext Startup { get; private set; } = null!;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

internal sealed record StartupContext(AppPaths Paths, SingleInstance Instance, bool Minimized);

/// <summary>Runs when the Velopack uninstaller starts: take our block out of the user's gitconfig and drop the startup entry.</summary>
internal static class UninstallHook
{
    public static void Run()
    {
        try
        {
            var git = GitLocator.Find() is { } path ? new GitRunner(path) : new GitRunner("git");
            var manager = new PersonaManager(git, AppEnvironment.ResolvePaths());
            // Non-interactive: remove the managed block but keep the user's saved logins and data.
            manager.Installer.UninstallAsync(restoreBackup: false).GetAwaiter().GetResult();
        }
        catch
        {
            // An uninstaller must never fail because of us.
        }

        try { StartupRegistration.ForCurrentOS().Disable(); }
        catch { }
    }
}
