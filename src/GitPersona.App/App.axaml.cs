using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GitPersona.App.Services;
using GitPersona.App.ViewModels;
using GitPersona.App.Views;
using GitPersona.Core;
using GitPersona.Core.Git;
using GitPersona.Core.Platform;

namespace GitPersona.App;

public partial class App : Application
{
    IClassicDesktopStyleApplicationLifetime? desktop;
    AppHost? host;
    MainViewModel? main;
    TrayIcon? tray;
    bool quitting;

    public MainWindow? MainWindowInstance { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            // Closing the window hides it; only the tray's Quit really exits.
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Start(lifetime);
        }
        base.OnFrameworkInitializationCompleted();
    }

    void Start(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var startup = Program.Startup;
        // Off the UI thread so the blocking wait below cannot deadlock on the UI sync context.
        host = Task.Run(() => AppHost.CreateAsync(startup.Paths)).GetAwaiter().GetResult();
        main = new MainViewModel(host);

        var window = new MainWindow { DataContext = main };
        MainWindowInstance = window;
        window.Closing += OnWindowClosing;

        SetUpTray();
        host.Changed += () => Dispatcher.UIThread.Post(RebuildTray);
        startup.Instance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);

        // A portable exe may have been moved since startup was enabled.
        if (AppEnvironment.ExecutablePath is { } exe)
        {
            try { StartupRegistration.RefreshIfMoved(host.StartupRegistration, exe); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        }

        // --minimized goes straight to the tray, unless the user still has first-run questions to answer.
        var needsAttention = !host.Git.IsAvailable || !host.Settings.Load().FirstRunCompleted;
        if (!startup.Minimized || needsAttention) window.Show();

        _ = RunSafelyAsync(async () =>
        {
            await main.InitializeAsync();
            RebuildTray();
            await host.Updates.CheckAsync();
            host.NotifyChanged();
        });

        // Expiry warnings depend on today's date; refresh the tray hourly.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        timer.Tick += (_, _) => RebuildTray();
        timer.Start();
    }

    // ---- window ----

    void ShowMainWindow()
    {
        if (MainWindowInstance is not { } window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (quitting || host is null) return;
        e.Cancel = true;
        _ = RunSafelyAsync(HideToTrayAsync);
    }

    async Task HideToTrayAsync()
    {
        if (host is null || MainWindowInstance is not { } window) return;

        if (!host.Settings.Load().TrayNoticeShown)
        {
            host.Settings.Update(s => s with { TrayNoticeShown = true });
            await Dialogs.MessageAsync("GitPersona is still running",
                $"GitPersona is still running in the {AppEnvironment.TrayName}. Use it to switch accounts, or choose Quit from its menu to exit completely.");
        }
        window.Hide();
    }

    void Quit()
    {
        quitting = true;
        if (tray is not null) tray.IsVisible = false;
        desktop?.Shutdown();
    }

    // ---- tray ----

    void SetUpTray()
    {
        tray = new TrayIcon { ToolTipText = "GitPersona", IsVisible = true };
        tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, [tray]);
        RebuildTray();
    }

    void RebuildTray()
    {
        if (tray is null || host is null) return;

        var menu = new NativeMenu();
        var manager = host.Manager;
        Account? active = null;

        if (manager is not null)
        {
            var accounts = manager.Accounts.All();
            var activeId = manager.Accounts.ActiveAccountId;
            active = accounts.FirstOrDefault(a => a.Id == activeId);

            if (host.Updates.Available is not null)
            {
                var update = new NativeMenuItem($"Update available ({host.Updates.AvailableVersion}) — click to install");
                update.Click += (_, _) => _ = RunSafelyAsync(host.Updates.ApplyAsync);
                menu.Add(update);
            }

            var warnings = ExpiryChecker.Attention(accounts, DateOnly.FromDateTime(DateTime.Today));
            foreach (var notice in warnings)
                menu.Add(new NativeMenuItem("⚠ " + notice.Describe()) { IsEnabled = false });
            if (warnings.Count > 0 || host.Updates.Available is not null) menu.Add(new NativeMenuItemSeparator());

            if (accounts.Count == 0)
            {
                menu.Add(new NativeMenuItem("No accounts yet") { IsEnabled = false });
            }
            foreach (var account in accounts)
            {
                var item = new NativeMenuItem(account.Label)
                {
                    ToggleType = NativeMenuItemToggleType.Radio,
                    IsChecked = account.Id == activeId,
                };
                var id = account.Id;
                item.Click += (_, _) => _ = RunSafelyAsync(async () =>
                {
                    await manager.SetActiveAsync(id);
                    host.NotifyChanged();
                });
                menu.Add(item);
            }
            menu.Add(new NativeMenuItemSeparator());
        }

        if (manager is not null)
        {
            var add = new NativeMenuItem("Add account…");
            add.Click += (_, _) => _ = RunSafelyAsync(async () =>
            {
                ShowMainWindow();
                main!.SelectedNav = main.NavItems[0];
                await main.Accounts.AddCommand.ExecuteAsync(null);
            });
            menu.Add(add);
        }

        var open = new NativeMenuItem("Open GitPersona");
        open.Click += (_, _) => ShowMainWindow();
        menu.Add(open);

        if (manager is not null)
        {
            var check = new NativeMenuItem("Check a folder…");
            check.Click += (_, _) => _ = RunSafelyAsync(CheckFolderFromTrayAsync);
            menu.Add(check);

            if (AppEnvironment.ExecutablePath is not null)
            {
                var startupItem = new NativeMenuItem(AppEnvironment.StartupLabel)
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = host.StartupRegistration.IsEnabled(),
                };
                startupItem.Click += (_, _) => main?.Settings.ApplyStartup(!host.StartupRegistration.IsEnabled());
                menu.Add(startupItem);
            }
        }

        menu.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Add(quit);

        tray.Menu = menu;
        tray.Icon = TrayIconRenderer.Render(active);
        tray.ToolTipText = ToolTip(active, manager);
    }

    string ToolTip(Account? active, PersonaManager? manager)
    {
        var text = active is null ? "GitPersona — no default account" : $"GitPersona — {active.Label}";
        if (manager is not null &&
            ExpiryChecker.Attention(manager.Accounts.All(), DateOnly.FromDateTime(DateTime.Today)).Count > 0)
            text += "\n⚠ A token needs attention";
        return text;
    }

    async Task CheckFolderFromTrayAsync()
    {
        ShowMainWindow();
        var folder = await Dialogs.PickFolderAsync("Choose a folder or repository to check");
        if (folder is not null) main?.ShowChecker(folder);
    }

    // ---- errors ----

    /// <summary>Fire-and-forget without ever crashing the tray app: unexpected failures are shown, not thrown.</summary>
    static async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { await Dialogs.MessageAsync("GitPersona", "Something went wrong: " + ex.Message); }
            catch { /* nothing left to do */ }
        }
    }
}
