using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitPersona.App.Services;
using GitPersona.Core;
using GitPersona.Core.Git;

namespace GitPersona.App.ViewModels;

public sealed partial class CheckerViewModel(AppHost host) : ObservableObject
{
    CheckResult? last;

    public ObservableCollection<CheckLine> Issues { get; } = [];
    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty] string folderPath = "";
    [ObservableProperty] bool isBusy;
    [ObservableProperty] bool hasResult;
    [ObservableProperty] string? error;

    [ObservableProperty] string repoText = "";
    [ObservableProperty] string emailText = "";
    [ObservableProperty] string emailSource = "";
    [ObservableProperty] string nameText = "";
    [ObservableProperty] string nameSource = "";
    [ObservableProperty] string loginText = "";
    [ObservableProperty] string loginSource = "";
    [ObservableProperty] string remoteText = "";
    [ObservableProperty] string rulesText = "";
    [ObservableProperty] bool hasMismatch;

    [ObservableProperty] Account? fixAccount;
    [ObservableProperty] bool canFix;
    [ObservableProperty] string? fixStatus;

    public void RefreshAccounts()
    {
        Accounts.Clear();
        if (host.Manager is null) return;
        foreach (var a in host.Manager.Accounts.All()) Accounts.Add(a);
        FixAccount = Accounts.FirstOrDefault(a => a.Id == FixAccount?.Id) ?? Accounts.FirstOrDefault();
    }

    /// <summary>Entry point for the tray's "Check a folder…" item.</summary>
    public async Task CheckAsync(string folder)
    {
        FolderPath = folder;
        await CheckAsync();
    }

    [RelayCommand]
    async Task BrowseAsync()
    {
        if (await Dialogs.PickFolderAsync("Choose a folder or repository to check") is { } path)
        {
            FolderPath = path;
            await CheckAsync();
        }
    }

    [RelayCommand]
    async Task CheckAsync()
    {
        if (host.Manager is not { } manager) return;
        Error = null;
        FixStatus = null;
        if (string.IsNullOrWhiteSpace(FolderPath)) { Error = "Choose a folder first."; return; }

        IsBusy = true;
        try
        {
            last = await manager.Checker.CheckAsync(FolderPath.Trim());
            Show(last);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or GitException or IOException)
        {
            HasResult = false;
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    void Show(CheckResult r)
    {
        RepoText = r.IsRepository ? $"Git repository at {r.RepoRoot}" : "Not a Git repository (showing what a new repository here would get)";
        EmailText = r.Email.Value ?? "(not set)";
        EmailSource = Source(r.Email, r.IdentityAccount);
        NameText = r.Name.Value ?? "(not set)";
        NameSource = Source(r.Name, r.IdentityAccount);
        RemoteText = r.RemoteUrl ?? "(no origin remote)";
        LoginText = r.Username.Value ?? (r.RemoteIsSsh ? "(SSH remote — ignored)" : "(none configured)");
        LoginSource = r.Username.Value is null ? "" : Source(r.Username, r.LoginAccount);
        RulesText = r.MatchingRules.Count == 0
            ? "No rule matches; the default account applies."
            : string.Join("\n", r.MatchingRules.Select((rule, i) =>
                $"{rule.Describe()}  →  {host.Manager!.Accounts.Find(rule.AccountId)?.Label ?? "?"}" + (i == r.MatchingRules.Count - 1 ? "   (last match wins)" : "")));

        Issues.Clear();
        foreach (var issue in r.Issues)
            Issues.Add(new CheckLine(issue.Severity switch { Severity.Error => Level.Error, Severity.Warning => Level.Warning, _ => Level.Info }, issue.Message));
        if (r.Issues.Count == 0) Issues.Add(new CheckLine(Level.Ok, "Everything lines up: the commit identity and the login belong to the same account."));

        HasMismatch = r.HasMismatch;
        CanFix = r.IsRepository && r.RemoteHostUrl is not null && !r.RemoteIsSsh;
        FixAccount = Accounts.FirstOrDefault(a => a.HostUrl == r.RemoteHostUrl) ?? FixAccount ?? Accounts.FirstOrDefault();
        HasResult = true;
    }

    string Source(ConfigValue value, Account? account)
    {
        if (value.OriginFile is { } file)
        {
            var friendly = account is not null && IsProfileFile(file) ? $"GitPersona account \"{account.Label}\" — " : "";
            return $"set by {friendly}{file}";
        }
        return value.OriginText is { } text ? $"set by {text}" : "";
    }

    bool IsProfileFile(string file) =>
        file.Replace('\\', '/').StartsWith(host.Paths.ProfilesDir.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    async Task FixRemoteAsync()
    {
        if (host.Manager is not { } manager || last is null || FixAccount is null) return;
        try
        {
            var preview = await manager.Repos.PreviewRemoteChangeAsync(last.Folder, FixAccount.Id);
            var ok = await Dialogs.ConfirmAsync("Change this repository's remote?",
                $"Account: {FixAccount.Label}\n\nCurrent:\n{preview.CurrentUrl}\n\nNew:\n{preview.ProposedUrl}\n\n" +
                "Only the login part of the URL changes. Your commits and history are not touched.",
                "Change remote");
            if (!ok) return;

            await manager.Repos.SetRemoteAccountAsync(last.Folder, FixAccount.Id);
            FixStatus = "Remote updated.";
            await CheckAsync();
        }
        catch (Exception ex) when (ex is RepoException or GitException)
        {
            FixStatus = ex.Message;
        }
    }
}
