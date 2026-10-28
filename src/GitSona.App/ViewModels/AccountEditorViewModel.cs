using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSona.App.Services;
using GitSona.Core;
using GitSona.Core.Git;

namespace GitSona.App.ViewModels;

public enum ServiceChoice { GitHub, GitHubEnterprise, GitLab, GitLabSelfHosted, Other }

/// <summary>
/// Add/edit account. Adding is a two-step flow: pick the service, then either sign in (GitHub, through Git
/// Credential Manager's browser flow) or enter username, email and a personal access token.
/// </summary>
public sealed partial class AccountEditorViewModel : ObservableObject
{
    readonly AppHost host;
    readonly Account? existing;
    readonly Task availability;
    bool warningsAcknowledged;
    string? validatedToken;
    DateOnly? validatedExpiry;
    string? signInToken; // held in memory only until Save hands it to the credential helper

    public AccountEditorViewModel(AppHost host, Account? existing)
    {
        this.host = host;
        this.existing = existing;
        IsEdit = existing is not null;
        Title = IsEdit ? "Edit account" : "Add an account";
        availability = LoadAvailabilityAsync();

        if (existing is not null)
        {
            choice = Derive(existing);
            isChoosing = false;
            label = existing.Label;
            hostUrl = existing.HostUrl;
            selectedAuth = existing.AuthType;
            username = existing.Username;
            commitName = existing.Name;
            commitEmail = existing.Email;
            validatedExpiry = existing.TokenExpiresOn;
        }
        UpdateExpiryText();
    }

    public event Action<bool>? CloseRequested;

    public string Title { get; }
    public bool IsEdit { get; }
    public string? SavedAccountId { get; private set; }
    public ObservableCollection<CheckLine> Messages { get; } = [];

    [ObservableProperty] bool isChoosing = true;
    [ObservableProperty] ServiceChoice choice;
    [ObservableProperty] string label = "";
    [ObservableProperty] string hostUrl = "";
    [ObservableProperty] AuthType selectedAuth = AuthType.PersonalAccessToken;
    [ObservableProperty] string token = "";
    [ObservableProperty] string username = "";
    [ObservableProperty] string commitName = "";
    [ObservableProperty] string commitEmail = "";
    [ObservableProperty] string expiryText = "";
    [ObservableProperty] bool isBusy;
    [ObservableProperty] bool canSaveUnvalidated;
    [ObservableProperty] string saveButtonText = "Save";
    [ObservableProperty] bool browserSignInAvailable;
    [ObservableProperty] bool signedIn;

    // ---- derived state for the view ----

    public Provider SelectedProvider => Choice switch
    {
        ServiceChoice.GitHub or ServiceChoice.GitHubEnterprise => Provider.GitHub,
        ServiceChoice.GitLab or ServiceChoice.GitLabSelfHosted => Provider.GitLab,
        _ => Provider.Other,
    };

    public string ServiceName => Choice switch
    {
        ServiceChoice.GitHub => "GitHub",
        ServiceChoice.GitHubEnterprise => "GitHub Enterprise",
        ServiceChoice.GitLab => "GitLab.com",
        ServiceChoice.GitLabSelfHosted => "Self-hosted GitLab",
        _ => "Other Git host",
    };

    public bool IsForm => !IsChoosing;
    public bool CanGoBack => !IsEdit && !IsChoosing;
    public bool ShowHostField => Choice is ServiceChoice.GitHubEnterprise or ServiceChoice.GitLabSelfHosted or ServiceChoice.Other;
    public bool IsGitHubFamily => SelectedProvider == Provider.GitHub;
    public bool UsesToken => SelectedAuth == AuthType.PersonalAccessToken;
    public bool ShowSignIn => IsGitHubFamily && SelectedAuth == AuthType.Browser;
    public bool CanSwitchToBrowser => IsGitHubFamily && UsesToken && BrowserSignInAvailable;
    public bool CanSwitchToToken => IsGitHubFamily && !UsesToken;
    public bool ShowGcmMissingNote => IsGitHubFamily && !BrowserSignInAvailable && UsesToken;
    public bool ShowCreateToken => UsesToken && SelectedProvider != Provider.Other;
    public bool CanCheckToken => UsesToken && SelectedProvider != Provider.Other && !IsBusy;
    public string SignInButtonText => SignedIn ? "Sign in again" : "Sign in with GitHub";
    public string SignedInText => SignedIn ? $"✔  Signed in as {Username}" : "";
    public string TokenPlaceholder => IsEdit ? "Leave empty to keep the stored token" : "Paste your personal access token";

    public string TokenHelp => SelectedProvider switch
    {
        Provider.GitHub => "Needs the 'repo' scope (classic) or Contents read/write (fine-grained).",
        Provider.GitLab => "Needs the read_repository and write_repository scopes (or api). Add read_api to list your repositories when cloning.",
        _ => "Use a token or password that Git can use over HTTPS on this host.",
    };

    public string SecurityNote => "The token goes straight to Git's credential helper. GitSona never stores it.";

    public string UsernameLabel => SelectedProvider == Provider.Other ? "Username" : "Username (filled in from your token)";

    // ---- availability of browser sign-in ----

    async Task LoadAvailabilityAsync()
    {
        try
        {
            if (host.HelperService is { } helpers)
                BrowserSignInAvailable = (await helpers.CheckAsync()).UsesGitCredentialManager;
        }
        catch (GitException)
        {
            BrowserSignInAvailable = false;
        }
    }

    static ServiceChoice Derive(Account a) => a.Provider switch
    {
        Provider.GitHub => a.HostUrl == "https://github.com" ? ServiceChoice.GitHub : ServiceChoice.GitHubEnterprise,
        Provider.GitLab => a.HostUrl == "https://gitlab.com" ? ServiceChoice.GitLab : ServiceChoice.GitLabSelfHosted,
        _ => ServiceChoice.Other,
    };

    // ---- step 1: choose the service ----

    [RelayCommand] Task ChooseGitHub() => ChooseAsync(ServiceChoice.GitHub, "https://github.com");
    [RelayCommand] Task ChooseGitHubEnterprise() => ChooseAsync(ServiceChoice.GitHubEnterprise, "https://");
    [RelayCommand] Task ChooseGitLab() => ChooseAsync(ServiceChoice.GitLab, "https://gitlab.com");
    [RelayCommand] Task ChooseGitLabSelfHosted() => ChooseAsync(ServiceChoice.GitLabSelfHosted, "https://");
    [RelayCommand] Task ChooseOther() => ChooseAsync(ServiceChoice.Other, "https://");

    async Task ChooseAsync(ServiceChoice service, string defaultHost)
    {
        await availability; // so the right sign-in option is offered from the start
        Messages.Clear();
        Choice = service;
        HostUrl = defaultHost;
        SelectedAuth = SelectedProvider == Provider.GitHub && BrowserSignInAvailable ? AuthType.Browser : AuthType.PersonalAccessToken;
        IsChoosing = false;
    }

    [RelayCommand]
    void Back()
    {
        Messages.Clear();
        Token = "";
        signInToken = null;
        SignedIn = false;
        IsChoosing = true;
    }

    // ---- step 2a: GitHub browser sign-in ----

    [RelayCommand]
    async Task SignInWithGitHubAsync()
    {
        if (host.Manager is not { } manager) return;
        if (!AccountValidator.TryNormalizeHost(HostUrl, out var normalized, out var hostError))
        {
            Show(Level.Error, hostError);
            return;
        }

        IsBusy = true;
        Messages.Clear();
        Show(Level.Info, "Finish signing in to GitHub in the window or browser that just opened. This page continues automatically.");
        try
        {
            var hint = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
            var credential = await manager.Credentials.SignInAsync(normalized, hint);
            signInToken = credential.Token;
            Username = credential.Username;
            SignedIn = true;
            Messages.Clear();

            // Ask GitHub who this is, to fill in name/email and check the token's scopes.
            var result = await host.Validator.ValidateAsync(Provider.GitHub, normalized, credential.Token);
            if (result.IsValid)
            {
                if (string.IsNullOrWhiteSpace(CommitName) && result.Name is { } n) CommitName = n;
                if (string.IsNullOrWhiteSpace(CommitEmail) && result.Email is { } e) CommitEmail = e;
                if (string.IsNullOrWhiteSpace(Label)) Label = $"GitHub ({credential.Username})";
                foreach (var w in result.Warnings) Show(Level.Warning, w);
            }
            else
            {
                Show(Level.Warning, "Signed in, but GitHub's details could not be read: " + result.Message);
            }

            if (string.IsNullOrWhiteSpace(CommitName)) CommitName = credential.Username;
            if (string.IsNullOrWhiteSpace(CommitEmail))
                Show(Level.Info, "GitHub keeps your email private. Enter the email you want on your commits (for example your …@users.noreply.github.com address).");
            if (string.IsNullOrWhiteSpace(Label)) Label = $"GitHub ({credential.Username})";
        }
        catch (Exception ex) when (ex is GitException or ArgumentException)
        {
            SignedIn = false;
            signInToken = null;
            Messages.Clear();
            Show(Level.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void UseTokenInstead()
    {
        Messages.Clear();
        SelectedAuth = AuthType.PersonalAccessToken;
    }

    [RelayCommand]
    void UseBrowserInstead()
    {
        Messages.Clear();
        SelectedAuth = AuthType.Browser;
    }

    // ---- step 2b: personal access token ----

    [RelayCommand]
    async Task OpenTokenPageAsync()
    {
        if (!AccountValidator.TryNormalizeHost(HostUrl, out var h, out var error))
        {
            Show(Level.Error, error);
            return;
        }
        var url = SelectedProvider switch
        {
            Provider.GitHub => $"{h}/settings/tokens/new?scopes=repo&description=GitSona",
            Provider.GitLab => $"{h}/-/user_settings/personal_access_tokens?name=GitSona&scopes=read_repository,write_repository,read_api",
            _ => null,
        };
        if (url is not null) await Dialogs.OpenUriAsync(url);
    }

    [RelayCommand]
    async Task CheckTokenAsync() => await ValidateAsync(showSuccess: true);

    // ---- save ----

    [RelayCommand]
    async Task SaveAsync()
    {
        if (host.Manager is null) return;

        if (!IsEdit && ShowSignIn && signInToken is null)
        {
            Messages.Clear();
            Show(Level.Error, "Sign in with GitHub first, or choose \"Use a personal access token instead\".");
            return;
        }

        // Check the token first, unless we are keeping the stored one or the provider has no API to ask.
        if (UsesToken && Token.Length > 0 && SelectedProvider != Provider.Other && validatedToken != Token)
        {
            var result = await ValidateAsync(showSuccess: false);
            // Rejected, unreachable or TLS failure: the message is shown, and for the latter two the
            // "Save without checking" button appears. Nothing is saved until the user chooses.
            if (result is null || !result.IsValid) return;
        }

        // A valid token with warnings (e.g. a missing scope): show them once and ask for a second click.
        if (validatedToken == Token && Token.Length > 0 && Messages.Any(m => m.Level == Level.Warning) && !warningsAcknowledged)
        {
            warningsAcknowledged = true;
            SaveButtonText = "Save anyway";
            return;
        }
        await PersistAsync();
    }

    [RelayCommand]
    async Task SaveWithoutCheckingAsync() => await PersistAsync();

    [RelayCommand]
    void Cancel() => CloseRequested?.Invoke(false);

    async Task<TokenValidationResult?> ValidateAsync(bool showSuccess)
    {
        if (!UsesToken || SelectedProvider == Provider.Other) return null;
        if (!AccountValidator.TryNormalizeHost(HostUrl, out var normalized, out var hostError))
        {
            Show(Level.Error, hostError);
            return null;
        }
        if (Token.Length == 0)
        {
            Messages.Clear();
            Show(Level.Error, "Paste your personal access token first.");
            return null;
        }

        IsBusy = true;
        Messages.Clear();
        CanSaveUnvalidated = false;
        try
        {
            var result = await host.Validator.ValidateAsync(SelectedProvider, normalized, Token);
            if (result.IsValid)
            {
                validatedToken = Token;
                validatedExpiry = result.ExpiresOn;
                if (result.Username is { } u) Username = u;
                if (string.IsNullOrWhiteSpace(CommitName) && result.Name is { } n) CommitName = n;
                if (string.IsNullOrWhiteSpace(CommitEmail) && result.Email is { } e) CommitEmail = e;
                if (string.IsNullOrWhiteSpace(Label)) Label = $"{ServiceName} ({result.Username})";
                UpdateExpiryText();

                if (showSuccess || result.Warnings.Count == 0)
                    Show(Level.Ok, $"Token is valid for {result.Username}." + (result.Scopes.Count > 0 ? $" Scopes: {string.Join(", ", result.Scopes)}." : ""));
                foreach (var w in result.Warnings) Show(Level.Warning, w);
                if (result.Email is null && SelectedProvider == Provider.GitHub)
                    Show(Level.Info, "GitHub keeps your email private, so enter the commit email you want to use.");
            }
            else
            {
                Show(result.Outcome == ValidationOutcome.Rejected ? Level.Error : Level.Warning, result.Message ?? "The token could not be checked.");
                CanSaveUnvalidated = result.CanSaveUnvalidated && result.Outcome != ValidationOutcome.NotSupported;
            }
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    async Task PersistAsync()
    {
        if (host.Manager is not { } manager) return;

        if (!AccountValidator.TryNormalizeHost(HostUrl, out var normalized, out var hostError))
        {
            Show(Level.Error, hostError);
            return;
        }

        var token = UsesToken ? (Token.Length > 0 ? Token : null) : signInToken;
        var loginMoved = existing is not null
            && (existing.HostUrl != normalized || !string.Equals(existing.Username, Username.Trim(), StringComparison.Ordinal));
        if (loginMoved && token is null)
        {
            Messages.Clear();
            Show(Level.Error, ShowSignIn
                ? "Changing the host or username needs a new sign-in. Press \"Sign in again\"."
                : "Changing the host or username removes the stored token. Enter the token again.");
            return;
        }

        var account = new Account
        {
            Id = existing?.Id ?? Account.NewId(),
            Label = Label.Trim(),
            Provider = SelectedProvider,
            HostUrl = normalized,
            Username = Username.Trim(),
            Name = CommitName.Trim(),
            Email = CommitEmail.Trim(),
            AuthType = SelectedAuth,
            TokenExpiresOn = UsesToken ? validatedExpiry : null,
        };

        IsBusy = true;
        try
        {
            var saved = existing is null
                ? await manager.AddAccountAsync(account, token)
                : await manager.UpdateAccountAsync(account, token);
            SavedAccountId = saved.Id;
            ClearSecrets();
            CloseRequested?.Invoke(true);
        }
        catch (AccountValidationException ex)
        {
            Messages.Clear();
            foreach (var error in ex.Errors) Show(Level.Error, error);
        }
        catch (Exception ex) when (ex is GitException or ArgumentException or IOException)
        {
            Messages.Clear();
            Show(Level.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- change notifications ----

    partial void OnChoiceChanged(ServiceChoice value) => NotifyShape();
    partial void OnIsChoosingChanged(bool value) => NotifyShape();
    partial void OnBrowserSignInAvailableChanged(bool value) => NotifyShape();
    partial void OnSelectedAuthChanged(AuthType value) => NotifyShape();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCheckToken));

    partial void OnSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(SignInButtonText));
        OnPropertyChanged(nameof(SignedInText));
    }

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(SignedInText));

    partial void OnTokenChanged(string value)
    {
        warningsAcknowledged = false;
        CanSaveUnvalidated = false;
        SaveButtonText = "Save";
    }

    void NotifyShape()
    {
        foreach (var name in new[]
        {
            nameof(SelectedProvider), nameof(ServiceName), nameof(IsForm), nameof(CanGoBack), nameof(ShowHostField),
            nameof(IsGitHubFamily), nameof(UsesToken), nameof(ShowSignIn), nameof(CanSwitchToBrowser), nameof(CanSwitchToToken),
            nameof(ShowGcmMissingNote), nameof(ShowCreateToken), nameof(CanCheckToken), nameof(TokenHelp), nameof(UsernameLabel),
        })
            OnPropertyChanged(name);
    }

    void UpdateExpiryText() =>
        ExpiryText = validatedExpiry is { } d ? $"Token expires {d:yyyy-MM-dd}" : "";

    void Show(Level level, string text) => Messages.Add(new CheckLine(level, text));

    /// <summary>Called when the dialog closes, whatever the outcome.</summary>
    public void ClearSecrets()
    {
        Token = "";
        signInToken = null;
    }
}

public enum Level { Ok, Info, Warning, Error }

public sealed record CheckLine(Level Level, string Text)
{
    public bool IsOk => Level == Level.Ok;
    public bool IsInfo => Level == Level.Info;
    public bool IsWarning => Level == Level.Warning;
    public bool IsError => Level == Level.Error;
    public string Glyph => Level switch { Level.Ok => "✔", Level.Info => "ℹ", Level.Warning => "▲", _ => "✖" };
}
