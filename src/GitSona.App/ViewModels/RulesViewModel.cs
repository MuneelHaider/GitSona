using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSona.App.Services;
using GitSona.Core;
using GitSona.Core.Git;

namespace GitSona.App.ViewModels;

public sealed partial class RulesViewModel(AppHost host) : ObservableObject
{
    public ObservableCollection<RuleItemViewModel> Items { get; } = [];
    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty] Account? selectedAccount;
    [ObservableProperty] bool isFolderRule = true;
    [ObservableProperty] string folderPath = "";
    [ObservableProperty] string remoteHost = "";
    [ObservableProperty] string remoteGroup = "";
    [ObservableProperty] bool remoteSupported = true;
    [ObservableProperty] string remoteNote = "";
    [ObservableProperty] string? status;
    [ObservableProperty] bool statusIsError;
    [ObservableProperty] bool isEmpty = true;

    public bool IsRemoteRule
    {
        get => !IsFolderRule;
        set => IsFolderRule = !value;
    }

    partial void OnIsFolderRuleChanged(bool value) => OnPropertyChanged(nameof(IsRemoteRule));

    partial void OnSelectedAccountChanged(Account? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(RemoteHost)) RemoteHost = value.HostUrl;
    }

    public async Task RefreshAsync()
    {
        Items.Clear();
        Accounts.Clear();
        if (host.Manager is not { } manager) return;

        foreach (var a in manager.Accounts.All()) Accounts.Add(a);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id) ?? Accounts.FirstOrDefault();

        RemoteSupported = await manager.Rules.RemoteRulesSupportedAsync();
        RemoteNote = RemoteSupported
            ? ""
            : $"Remote rules need Git 2.36 or newer; you have {(await manager.Rules.GetGitVersionAsync()).ToString(3)}. Use a folder rule, or upgrade Git.";
        if (!RemoteSupported) IsFolderRule = true;

        var rules = manager.Rules.All();
        for (var i = 0; i < rules.Count; i++)
        {
            var account = manager.Accounts.Find(rules[i].AccountId);
            Items.Add(new RuleItemViewModel(this, rules[i], account?.Label ?? "(deleted account)", i, rules.Count));
        }
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    async Task BrowseAsync()
    {
        if (await Dialogs.PickFolderAsync("Choose the folder this rule applies to") is { } path) FolderPath = path;
    }

    [RelayCommand]
    async Task AddAsync()
    {
        if (host.Manager is not { } manager) return;
        if (SelectedAccount is null) { Fail("Add an account first."); return; }

        await Run(async () =>
        {
            if (IsFolderRule) await manager.Rules.AddFolderRuleAsync(SelectedAccount.Id, FolderPath);
            else await manager.Rules.AddRemoteRuleAsync(SelectedAccount.Id, RemoteHost, RemoteGroup);

            FolderPath = "";
            RemoteGroup = "";
            Status = "Rule added at the bottom of the list, so it wins over the rules above it if several match.";
            StatusIsError = false;
        });
    }

    internal Task MoveAsync(RuleItemViewModel item, int delta) =>
        Run(() => host.Manager!.Rules.MoveAsync(item.Rule.Id, item.Index + delta));

    internal async Task DeleteAsync(RuleItemViewModel item)
    {
        if (!await Dialogs.ConfirmAsync("Delete rule", $"Delete this rule?\n\n{item.Description}", "Delete", destructive: true)) return;
        await Run(() => host.Manager!.Rules.RemoveAsync(item.Rule.Id));
    }

    async Task Run(Func<Task> action)
    {
        try
        {
            await action();
            host.NotifyChanged();
        }
        catch (Exception ex) when (ex is RuleException or RuleNotSupportedException or GitException or IOException or InvalidDataException)
        {
            Fail(ex.Message);
        }
    }

    void Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }
}

public sealed partial class RuleItemViewModel(RulesViewModel owner, Rule rule, string accountLabel, int index, int count) : ObservableObject
{
    public Rule Rule { get; } = rule;
    public int Index { get; } = index;
    public string Position => $"#{Index + 1}";
    public string Description => Rule.Describe();
    public string AccountLabel => "→ " + accountLabel;
    public bool CanMoveUp => Index > 0;
    public bool CanMoveDown => Index < count - 1;
    public bool IsLast => Index == count - 1 && count > 1;

    [RelayCommand] Task MoveUp() => owner.MoveAsync(this, -1);
    [RelayCommand] Task MoveDown() => owner.MoveAsync(this, +1);
    [RelayCommand] Task Delete() => owner.DeleteAsync(this);
}
