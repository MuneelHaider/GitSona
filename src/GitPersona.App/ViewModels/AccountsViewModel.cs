using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitPersona.App.Services;
using GitPersona.Core;
using GitPersona.Core.Git;
using GitPersona.Core.Platform;

namespace GitPersona.App.ViewModels;

public sealed partial class AccountsViewModel(AppHost host) : ObservableObject
{
    public ObservableCollection<AccountItemViewModel> Items { get; } = [];

    [ObservableProperty] bool isEmpty = true;
    [ObservableProperty] string? status;

    public void Refresh()
    {
        Items.Clear();
        if (host.Manager is null) return;

        var activeId = host.Manager.Accounts.ActiveAccountId;
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var account in host.Manager.Accounts.All())
            Items.Add(new AccountItemViewModel(this, account, account.Id == activeId, ExpiryChecker.Check(account, today)));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    async Task AddAsync()
    {
        if (host.Manager is not { } manager) return;
        var editor = new AccountEditorViewModel(host, null);
        if (!await Dialogs.ShowAccountEditorAsync(editor)) return;

        // The first account becomes the default so GitPersona does something straight away.
        if (manager.Accounts.ActiveAccountId is null && editor.SavedAccountId is { } id)
        {
            await manager.SetActiveAsync(id);
            Status = "Added, and set as your default account.";
        }
        else Status = "Account added.";
        host.NotifyChanged();
    }

    internal async Task EditAsync(AccountItemViewModel item)
    {
        if (!await Dialogs.ShowAccountEditorAsync(new AccountEditorViewModel(host, item.Account))) return;
        Status = "Account updated.";
        host.NotifyChanged();
    }

    internal async Task MakeDefaultAsync(AccountItemViewModel item)
    {
        if (host.Manager is not { } manager) return;
        await Run(async () =>
        {
            await manager.SetActiveAsync(item.Account.Id);
            Status = $"\"{item.Account.Label}\" is now your default account (used where no rule matches).";
            host.NotifyChanged();
        });
    }

    internal async Task TestLoginAsync(AccountItemViewModel item)
    {
        if (host.Manager is not { } manager) return;
        await Run(async () =>
        {
            var found = await manager.Credentials.HasCredentialAsync(item.Account.HostUrl, item.Account.Username);
            Status = found
                ? $"A stored login for {item.Account.Username} on {item.Account.HostUrl} was found."
                : item.Account.AuthType == AuthType.Browser
                    ? $"No stored login yet. That is normal for browser sign-in: your first push to {item.Account.HostUrl} will open the browser."
                    : $"No stored token for {item.Account.Username} on {item.Account.HostUrl}. Edit the account and enter the token again.";
        });
    }

    internal async Task DeleteAsync(AccountItemViewModel item)
    {
        if (host.Manager is not { } manager) return;
        var rules = manager.Rules.ForAccount(item.Account.Id);
        var message = $"Delete \"{item.Account.Label}\"?\n\nThis removes its generated Git settings and the token stored in your credential helper.";
        if (rules.Count > 0)
            message += $"\n\nIt also deletes {rules.Count} rule{(rules.Count == 1 ? "" : "s")} that point to this account.";
        if (!await Dialogs.ConfirmAsync("Delete account", message, "Delete", destructive: true)) return;

        await Run(async () =>
        {
            var warnings = await manager.DeleteAccountAsync(item.Account.Id);
            Status = warnings.Count == 0 ? "Account deleted." : "Account deleted. " + string.Join(" ", warnings);
            host.NotifyChanged();
        });
    }

    async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is GitException or AccountValidationException or ArgumentException or InvalidDataException or IOException)
        {
            Status = "Something went wrong: " + ex.Message;
        }
    }
}

public sealed partial class AccountItemViewModel(AccountsViewModel owner, Account account, bool isActive, ExpiryNotice? expiry) : ObservableObject
{
    public Account Account { get; } = account;
    public bool IsActive { get; } = isActive;

    public string Title => Account.Label;
    public string Subtitle => $"{Account.Provider} · {Account.HostUrl}";
    public string LoginText => $"Login: {Account.Username}" + (Account.AuthType == AuthType.Browser ? " (browser sign-in)" : "");
    public string IdentityText => $"Commits as {Account.Name} <{Account.Email}>";

    public bool HasExpiry => expiry is not null;
    public string ExpiryText => expiry is null ? "" : expiry.State switch
    {
        ExpiryState.Expired => $"Token expired {Account.TokenExpiresOn:yyyy-MM-dd}",
        _ => $"Token expires {Account.TokenExpiresOn:yyyy-MM-dd} ({expiry.DaysLeft} day{(expiry.DaysLeft == 1 ? "" : "s")})",
    };
    public bool ExpiryIsBad => expiry?.State == ExpiryState.Expired;
    public bool ExpiryIsWarn => expiry?.State == ExpiryState.ExpiringSoon;

    [RelayCommand] Task MakeDefault() => owner.MakeDefaultAsync(this);
    [RelayCommand] Task Edit() => owner.EditAsync(this);
    [RelayCommand] Task TestLogin() => owner.TestLoginAsync(this);
    [RelayCommand] Task Delete() => owner.DeleteAsync(this);
}
