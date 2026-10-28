using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitPersona.App.Services;
using GitPersona.Core;
using GitPersona.Core.Git;

namespace GitPersona.App.ViewModels;

public sealed partial class CloneViewModel(AppHost host) : ObservableObject
{
    string? suggestedDestination;

    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty] string url = "";
    [ObservableProperty] Account? selectedAccount;
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
        // Pick the account that owns the URL's host, so the right login is used by default.
        var parsed = RemoteUrl.Parse(value);
        if (parsed.HostUrl is null) return;
        var match = Accounts.FirstOrDefault(a => a.HostUrl == parsed.HostUrl);
        if (match is not null && SelectedAccount?.HostUrl != parsed.HostUrl) SelectedAccount = match;
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        if (value is null || host.Manager is null) return;
        var suggestion = host.Manager.Repos.SuggestDestination(value);
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
