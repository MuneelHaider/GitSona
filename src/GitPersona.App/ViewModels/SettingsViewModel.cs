using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitPersona.App.Services;
using GitPersona.Core;
using GitPersona.Core.Git;
using GitPersona.Core.Platform;

namespace GitPersona.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppHost host;
    bool loading;

    public SettingsViewModel(AppHost host)
    {
        this.host = host;
    }

    public string StartupLabel => AppEnvironment.StartupLabel;
    public string Version => AppEnvironment.Version;
    public string DataFolder => host.Paths.Root;
    public string GitInfo => host.Git.IsAvailable ? $"Git {host.Git.Version!.ToString(3)} at {host.Git.GitPath}" : host.Git.Problem ?? "Git not found";
    public bool IsStartupAvailable => AppEnvironment.ExecutablePath is not null;
    public string StartupNote => IsStartupAvailable ? "" : "Not available while running from the .NET SDK; use the published app.";
    public bool UpdatesSupported => host.Updates.IsSupported;
    public string UpdateNote => host.Updates.IsSupported ? "" : host.Updates.UnavailableReason;

    [ObservableProperty] bool startAtLogin;
    [ObservableProperty] bool autoUpdate = true;
    [ObservableProperty] string helperText = "";
    [ObservableProperty] bool helperCanFix;
    [ObservableProperty] string? helperFixLabel;
    [ObservableProperty] string? updateStatus;
    [ObservableProperty] bool updateReady;
    [ObservableProperty] string? status;
    [ObservableProperty] bool isInstalled;

    // Uninstall / restore options
    [ObservableProperty] bool restoreBackup;
    [ObservableProperty] bool removeLogins;
    [ObservableProperty] bool deleteData;
    [ObservableProperty] string? backupInfo;
    [ObservableProperty] string? uninstallStatus;

    string? recommendedHelper;

    public async Task RefreshAsync()
    {
        loading = true;
        try
        {
            StartAtLogin = host.StartupRegistration.IsEnabled();
            AutoUpdate = host.Settings.Load().AutoUpdateCheck;
            OnPropertyChanged(nameof(GitInfo));
            OnPropertyChanged(nameof(UpdatesSupported));
            OnPropertyChanged(nameof(UpdateNote));

            if (host.Manager is { } manager)
            {
                IsInstalled = manager.Installer.IsInstalled();
                var path = manager.Installer.ResolveGlobalConfigPath();
                BackupInfo = $"Your global Git config is {path}. A backup was made before the first change; \"Restore\" puts that backup back and discards edits you made to the file since.";
            }
            if (host.HelperService is { } helpers)
            {
                var status = await helpers.CheckAsync();
                recommendedHelper = status.Recommended;
                HelperText = status.Explanation;
                HelperCanFix = status.State != HelperState.Good && status.Recommended is not null;
                HelperFixLabel = status.Recommended is null ? null : $"Set credential.helper to '{status.Recommended}'";
            }
            UpdateReady = host.Updates.Available is not null;
            if (UpdateReady) UpdateStatus = $"Version {host.Updates.AvailableVersion} is available.";
        }
        finally
        {
            loading = false;
        }
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (loading) return;
        ApplyStartup(value);
    }

    /// <summary>Used by both this page and the tray menu.</summary>
    public void ApplyStartup(bool enable)
    {
        try
        {
            if (enable)
            {
                var exe = AppEnvironment.ExecutablePath ?? throw new InvalidOperationException("The app's executable path is unknown.");
                host.StartupRegistration.Enable(exe);
            }
            else host.StartupRegistration.Disable();
            Status = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = "Could not change startup: " + ex.Message;
        }
        loading = true;
        StartAtLogin = host.StartupRegistration.IsEnabled();
        loading = false;
        host.NotifyChanged();
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        if (loading) return;
        host.Settings.Update(s => s with { AutoUpdateCheck = value });
    }

    [RelayCommand]
    async Task FixHelperAsync()
    {
        if (host.HelperService is not { } helpers || recommendedHelper is null) return;
        var ok = await Dialogs.ConfirmAsync("Change Git's credential helper?",
            $"This runs:\n\ngit config --global credential.helper {recommendedHelper}\n\nso Git can store your tokens in the system credential store.",
            "Change");
        if (!ok) return;
        try
        {
            await helpers.SetGlobalHelperAsync(recommendedHelper);
            await RefreshAsync();
            host.NotifyChanged();
        }
        catch (Exception ex) when (ex is GitException or ArgumentException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    async Task InstallAsync()
    {
        if (host.Manager is not { } manager) return;
        try
        {
            var result = await manager.InstallAsync();
            Status = result.BackupCreated
                ? $"Done. Your original config was backed up to {result.BackupPath}."
                : result.Changed ? "Done. The GitPersona block was refreshed." : "GitPersona is already set up in your Git config.";
            await RefreshAsync();
            host.NotifyChanged();
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = "Could not update your Git config: " + ex.Message;
        }
    }

    [RelayCommand]
    async Task CheckUpdatesAsync()
    {
        if (!host.Updates.IsSupported) return;
        UpdateStatus = "Checking…";
        var info = await host.Updates.CheckAsync(force: true);
        UpdateReady = info is not null;
        UpdateStatus = info is null ? "You are up to date." : $"Version {host.Updates.AvailableVersion} is available.";
        host.NotifyChanged();
    }

    [RelayCommand]
    async Task InstallUpdateAsync()
    {
        UpdateStatus = "Downloading the update… GitPersona will restart.";
        await host.Updates.ApplyAsync();
    }

    [RelayCommand]
    async Task OpenDataFolderAsync() => await Dialogs.OpenFolderAsync(host.Paths.Root);

    [RelayCommand]
    async Task UninstallAsync()
    {
        if (host.Manager is not { } manager) return;

        var details = "This removes the GitPersona block from your global Git config" +
                      (RestoreBackup ? ", and restores the backup taken before the first change (discarding later edits to that file)" : "") +
                      (RemoveLogins ? ", and removes the logins GitPersona stored in your credential helper" : "") +
                      (DeleteData ? ", and deletes GitPersona's own data (accounts and rules)" : "") +
                      (host.StartupRegistration.IsEnabled() ? ". It also turns off start-at-login" : "") + ".";
        if (!await Dialogs.ConfirmAsync("Remove GitPersona from Git?", details, "Remove", destructive: true)) return;

        try
        {
            var report = await manager.UninstallAsync(new UninstallOptions(RestoreBackup, RemoveLogins, DeleteData));
            host.StartupRegistration.Disable();
            UninstallStatus = "Done. Git no longer uses GitPersona." +
                              (report.Warnings.Count > 0 ? " " + string.Join(" ", report.Warnings) : "") +
                              " You can now delete the app.";
            await RefreshAsync();
            host.NotifyChanged();
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            UninstallStatus = "Could not finish: " + ex.Message;
        }
    }
}
