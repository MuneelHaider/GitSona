using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitPersona.App.Services;
using GitPersona.Core;
using GitPersona.Core.Git;
using GitPersona.Core.Platform;

namespace GitPersona.App.ViewModels;

public sealed record NavItem(string Title, string Glyph, object Page);

public sealed partial class BannerViewModel(Level level, string message, string? actionText = null, Func<Task>? action = null) : ObservableObject
{
    public string Message { get; } = message;
    public string? ActionText { get; } = actionText;
    public bool HasAction => action is not null;
    public bool IsError { get; } = level == Level.Error;
    public bool IsWarning { get; } = level == Level.Warning;
    public bool IsInfo { get; } = level is Level.Info or Level.Ok;

    [RelayCommand]
    async Task Act()
    {
        if (action is not null) await action();
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    readonly AppHost host;

    public MainViewModel(AppHost host)
    {
        this.host = host;
        Accounts = new AccountsViewModel(host);
        Rules = new RulesViewModel(host);
        Checker = new CheckerViewModel(host);
        Clone = new CloneViewModel(host);
        Settings = new SettingsViewModel(host);

        NavItems =
        [
            new NavItem("Accounts", "👤", Accounts),
            new NavItem("Rules", "☰", Rules),
            new NavItem("Check a folder", "🔍", Checker),
            new NavItem("Clone", "⬇", Clone),
            new NavItem("Settings", "⚙", Settings),
        ];
        selectedNav = NavItems[0];
        currentPage = Accounts;

        host.Changed += () => Dispatcher.UIThread.Post(async () => await RefreshAllAsync());
    }

    public AccountsViewModel Accounts { get; }
    public RulesViewModel Rules { get; }
    public CheckerViewModel Checker { get; }
    public CloneViewModel Clone { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }
    public ObservableCollection<BannerViewModel> Banners { get; } = [];
    public string Version => "v" + AppEnvironment.Version;

    [ObservableProperty] NavItem selectedNav;
    [ObservableProperty] object currentPage;
    [ObservableProperty] object? overlay;
    [ObservableProperty] bool hasOverlay;

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value.Page;
        _ = RefreshPageAsync(value.Page);
    }

    partial void OnOverlayChanged(object? value) => HasOverlay = value is not null;

    public async Task InitializeAsync()
    {
        // Developer aid for screenshots: GITPERSONA_START_PAGE=Rules (or Accounts, Settings, ...).
        if (Environment.GetEnvironmentVariable("GITPERSONA_START_PAGE") is { Length: > 0 } startPage
            && NavItems.FirstOrDefault(n => n.Title.StartsWith(startPage, StringComparison.OrdinalIgnoreCase)) is { } startNav)
            SelectedNav = startNav;

        await RefreshAllAsync();
        if (!host.Git.IsAvailable)
            Overlay = new GitMissingViewModel(host, () => _ = RefreshAllAsync());
        else if (!host.Settings.Load().FirstRunCompleted)
            Overlay = new FirstRunViewModel(host, () => { Overlay = null; _ = RefreshAllAsync(); });
    }

    public void ShowChecker(string? folder = null)
    {
        SelectedNav = NavItems.First(n => n.Page == Checker);
        if (folder is not null) _ = Checker.CheckAsync(folder);
    }

    public async Task RefreshAllAsync()
    {
        if (!host.Git.IsAvailable && Overlay is not GitMissingViewModel)
            Overlay = new GitMissingViewModel(host, () => _ = RefreshAllAsync());
        else if (host.Git.IsAvailable && Overlay is GitMissingViewModel)
            Overlay = host.Settings.Load().FirstRunCompleted ? null : new FirstRunViewModel(host, () => { Overlay = null; _ = RefreshAllAsync(); });

        await RefreshPageAsync(CurrentPage);
        await RefreshBannersAsync();
    }

    async Task RefreshPageAsync(object page)
    {
        switch (page)
        {
            case AccountsViewModel a: a.Refresh(); break;
            case RulesViewModel r: await r.RefreshAsync(); break;
            case CheckerViewModel c: c.RefreshAccounts(); break;
            case CloneViewModel c: c.Refresh(); break;
            case SettingsViewModel s: await s.RefreshAsync(); break;
        }
    }

    async Task RefreshBannersAsync()
    {
        Banners.Clear();
        if (host.Manager is not { } manager || Overlay is not null) return;

        try
        {
            if (!manager.Installer.IsInstalled())
                Banners.Add(new BannerViewModel(Level.Warning,
                    "GitPersona is not active in your Git config yet, so nothing it manages takes effect.",
                    "Turn on", async () => { await manager.InstallAsync(); host.NotifyChanged(); }));

            var overrides = manager.Installer.DetectOverrides();
            if (overrides.Count > 0)
                Banners.Add(new BannerViewModel(Level.Warning,
                    $"Your global Git config sets {(overrides.Count == 1 ? "a value" : overrides.Count + " values")} after GitPersona's block, which overrides your accounts (line {overrides[0].Line}: {overrides[0].Message})",
                    "Move block to the end", async () => { await manager.InstallAsync(); host.NotifyChanged(); }));

            foreach (var notice in ExpiryChecker.Attention(manager.Accounts.All(), DateOnly.FromDateTime(DateTime.Today)))
                Banners.Add(new BannerViewModel(notice.State == ExpiryState.Expired ? Level.Error : Level.Warning, notice.Describe()));

            if (host.HelperService is { } helpers && (await helpers.CheckAsync()).State == HelperState.Missing)
                Banners.Add(new BannerViewModel(Level.Warning,
                    "Git has no credential helper, so tokens cannot be saved.", "Open settings",
                    () => { SelectedNav = NavItems.First(n => n.Page == Settings); return Task.CompletedTask; }));

            if (host.Updates.Available is not null)
                Banners.Add(new BannerViewModel(Level.Info, $"GitPersona {host.Updates.AvailableVersion} is available.", "Update and restart", host.Updates.ApplyAsync));
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidDataException)
        {
            Banners.Add(new BannerViewModel(Level.Error, "Could not read your Git configuration: " + ex.Message));
        }
    }
}

public sealed partial class GitMissingViewModel(AppHost host, Action onChanged) : ObservableObject
{
    public string Message => host.Git.Problem ?? "Git was not found.";
    public string DownloadUrl => "https://git-scm.com/downloads";
    [ObservableProperty] string? status;

    [RelayCommand] Task Download() => Dialogs.OpenUriAsync(DownloadUrl);

    [RelayCommand]
    async Task Recheck()
    {
        await host.RedetectGitAsync();
        Status = host.Git.IsAvailable ? null : "Still not found. Open a new terminal to check that `git --version` works, then try again.";
        OnPropertyChanged(nameof(Message));
        onChanged();
    }
}

public sealed partial class FirstRunViewModel : ObservableObject
{
    readonly AppHost host;
    readonly Action onFinished;
    string? recommendedHelper;

    public FirstRunViewModel(AppHost host, Action onFinished)
    {
        this.host = host;
        this.onFinished = onFinished;
        GitText = $"Git {host.Git.Version!.ToString(3)} found at {host.Git.GitPath}";
        StartupLabel = AppEnvironment.StartupLabel;
        CanOfferStartup = AppEnvironment.ExecutablePath is not null;
        _ = LoadAsync();
    }

    public string GitText { get; }
    public string StartupLabel { get; }
    public bool CanOfferStartup { get; }
    public string RemoteRulesText => host.Git.RemoteRulesSupported ? "" : "Your Git is older than 2.36, so “remote” rules are turned off. Folder rules work.";
    public bool HasRemoteRulesText => !host.Git.RemoteRulesSupported;

    [ObservableProperty] string helperText = "Checking…";
    [ObservableProperty] bool helperNeedsAction;
    [ObservableProperty] string helperButton = "";
    [ObservableProperty] bool configReady;
    [ObservableProperty] string configText = "";
    [ObservableProperty] bool startAtLogin; // off by default
    [ObservableProperty] string? error;

    async Task LoadAsync()
    {
        if (host.HelperService is { } helpers)
        {
            var status = await helpers.CheckAsync();
            recommendedHelper = status.Recommended;
            HelperText = status.Explanation;
            HelperNeedsAction = status.State != HelperState.Good && status.Recommended is not null;
            HelperButton = $"Set it to '{status.Recommended}'";
        }
        if (host.Manager is { } manager)
        {
            ConfigReady = manager.Installer.IsInstalled();
            ConfigText = ConfigReady ? "GitPersona is already set up in your Git config." : "";
        }
    }

    [RelayCommand]
    async Task FixHelperAsync()
    {
        if (host.HelperService is null || recommendedHelper is null) return;
        try
        {
            await host.HelperService.SetGlobalHelperAsync(recommendedHelper);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is GitException or ArgumentException) { Error = ex.Message; }
    }

    [RelayCommand]
    async Task EnableAsync()
    {
        if (host.Manager is not { } manager) return;
        try
        {
            var result = await manager.InstallAsync();
            ConfigReady = true;
            ConfigText = result.BackupCreated
                ? $"Done. Your original config is backed up at {result.BackupPath}."
                : "Done.";
            Error = null;
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error = "Could not update your Git config: " + ex.Message;
        }
    }

    [RelayCommand]
    void Finish()
    {
        if (StartAtLogin && AppEnvironment.ExecutablePath is { } exe)
        {
            try { host.StartupRegistration.Enable(exe); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Error = ex.Message; return; }
        }
        host.Settings.Update(s => s with { FirstRunCompleted = true });
        onFinished();
        host.NotifyChanged();
    }
}
