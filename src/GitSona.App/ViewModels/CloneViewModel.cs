using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSona.App.Services;
using GitSona.Core;
using GitSona.Core.Git;

namespace GitSona.App.ViewModels;

public sealed partial class CloneViewModel(AppHost host) : ObservableObject
{
    string? suggestedDestination;
    CancellationTokenSource? loadCts;
    // In memory only, per account. Keyed by login so editing an account's host or username drops its stale list.
    readonly Dictionary<string, RepoListResult> repoCache = [];

    public ObservableCollection<Account> Accounts { get; } = [];
    public ObservableCollection<RemoteRepo> Repos { get; } = [];

    [ObservableProperty] string url = "";
    [ObservableProperty] Account? selectedAccount;
    [ObservableProperty] RemoteRepo? selectedRepo;
    [ObservableProperty] bool isLoadingRepos;
    [ObservableProperty] string? reposNote;
    [ObservableProperty] bool reposNoteIsError;
    [ObservableProperty] string destination = "";
    [ObservableProperty] bool isBusy;
    [ObservableProperty] string? status;
    [ObservableProperty] bool statusIsError;
    [ObservableProperty] string? clonedPath;

    public void Refresh()
    {
        Accounts.Clear();
        if (host.Manager is null) return;
        foreach (var a in host.Manager.Accounts.All()) Accounts.Add(a);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id) ?? Accounts.FirstOrDefault();
    }

    partial void OnUrlChanged(string value)
    {
        // Editing the URL by hand means the dropdown choice no longer describes it.
        if (SelectedRepo is not null && value != SelectedRepo.CloneUrl) SelectedRepo = null;

        // Pick the account that owns the URL's host, so the right login is used by default.
        var parsed = RemoteUrl.Parse(value);
        if (parsed.HostUrl is null) return;
        var match = Accounts.FirstOrDefault(a => a.HostUrl == parsed.HostUrl);
        if (match is not null && SelectedAccount?.HostUrl != parsed.HostUrl) SelectedAccount = match;
    }

    partial void OnSelectedRepoChanged(RemoteRepo? value)
    {
        if (value is not null) Url = value.CloneUrl;
    }

    partial void OnSelectedAccountChanged(Account? oldValue, Account? newValue)
    {
        // A URL that came from the previous account's dropdown belongs to that account, so drop it. (A null in
        // between is just the list being rebuilt in Refresh, which must not clear anything.)
        if (oldValue is not null && newValue is not null && oldValue.Id != newValue.Id
            && SelectedRepo is { } picked && Url == picked.CloneUrl)
            Url = "";
        _ = LoadReposAsync(newValue, force: false);

        if (newValue is null || host.Manager is null) return;
        var suggestion = host.Manager.Repos.SuggestDestination(newValue);
        // Only replace a destination we suggested ourselves, never one the user chose.
        if (string.IsNullOrWhiteSpace(Destination) || Destination == suggestedDestination)
            Destination = suggestion ?? "";
        suggestedDestination = suggestion;
    }

    public string DestinationHint =>
        suggestedDestination is not null && Destination == suggestedDestination
            ? "Suggested: this folder has an automatic rule for the account."
            : "Tip: clone inside a folder that has a rule for this account, so commits use the right identity.";

    partial void OnDestinationChanged(string value) => OnPropertyChanged(nameof(DestinationHint));

    [RelayCommand]
    Task RefreshReposAsync() => LoadReposAsync(SelectedAccount, force: true);

    async Task LoadReposAsync(Account? account, bool force)
    {
        loadCts?.Cancel();
        Repos.Clear();
        SelectedRepo = null;
        ReposNote = null;
        ReposNoteIsError = false;
        IsLoadingRepos = false;
        if (account is null || host.Manager is not { } manager) return;

        var key = $"{account.Id}|{account.HostUrl}|{account.Username}";
        if (!force && repoCache.TryGetValue(key, out var cached))
        {
            ShowRepos(cached);
            return;
        }

        var cts = loadCts = new CancellationTokenSource();
        IsLoadingRepos = true;
        try
        {
            // The token lives only in the credential helper; read it just for this one request.
            var token = await manager.Credentials.FillPasswordAsync(account.HostUrl, account.Username, cts.Token);
            RepoListResult result;
            if (token is null)
                result = new RepoListResult { Error = $"No saved login for {account.Username} was found, so the repository list can't be loaded. Sign in again from the account's settings." };
            else
                result = await host.RepoLister.ListAsync(account.Provider, account.HostUrl, token, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (result.Succeeded) repoCache[key] = result;
            ShowRepos(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException ex)
        {
            if (!cts.IsCancellationRequested) ShowRepos(new RepoListResult { Error = ex.Message });
        }
        finally
        {
            if (loadCts == cts) IsLoadingRepos = false;
        }
    }

    void ShowRepos(RepoListResult result)
    {
        if (!result.Succeeded)
        {
            ReposNote = result.Error + " You can still paste a repository URL below.";
            ReposNoteIsError = true;
            return;
        }
        foreach (var r in result.Repos) Repos.Add(r);
        ReposNote = result.Repos.Count == 0
            ? "This account has no repositories yet. You can still paste a repository URL below."
            : result.Truncated
                ? $"Showing the {result.Repos.Count:N0} most recently active repositories. For any other, paste its URL below."
                : $"{result.Repos.Count:N0} {(result.Repos.Count == 1 ? "repository" : "repositories")}, public and private.";
    }

    [RelayCommand]
    async Task BrowseAsync()
    {
        if (await Dialogs.PickFolderAsync("Choose where to clone") is { } path) Destination = path;
    }

    [RelayCommand]
    async Task CloneAsync()
    {
        if (host.Manager is not { } manager) return;
        Status = null;
        ClonedPath = null;
        if (SelectedAccount is null) { Fail("Pick an account."); return; }

        IsBusy = true;
        Status = "Cloning… (a browser or sign-in window may open the first time for GitHub browser sign-in)";
        StatusIsError = false;
        try
        {
            ClonedPath = await manager.Repos.CloneAsync(Url, SelectedAccount.Id, Destination);
            Status = $"Cloned to {ClonedPath}";
            Url = "";
        }
        catch (Exception ex) when (ex is RepoException or GitException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    async Task OpenFolderAsync()
    {
        if (ClonedPath is not null) await Dialogs.OpenFolderAsync(ClonedPath);
    }

    void Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }
}
