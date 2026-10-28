using GitPersona.Core.Git;

namespace GitPersona.Core;

/// <summary>Generates the per-account fragments and <c>active.gitconfig</c>, always via <c>git config --file</c>.</summary>
public sealed class ProfileWriter(GitRunner git, AppPaths paths)
{
    public async Task WriteProfileAsync(Account account, CancellationToken ct = default)
    {
        AccountValidator.EnsureValid(account);

        Directory.CreateDirectory(paths.ProfilesDir);
        var target = paths.ProfileConfig(account.Id);
        var tmp = target + ".tmp";
        File.Delete(tmp);

        var file = new GitConfigFile(git, tmp);
        await file.SetAsync("user.name", account.Name, ct);
        await file.SetAsync("user.email", account.Email, ct);
        // credential.<url>.username makes the credential helper pick this login on a shared host.
        await file.SetAsync($"credential.{account.HostUrl}.username", account.Username, ct);

        File.Move(tmp, target, overwrite: true);
    }

    public void DeleteProfile(string accountId) => File.Delete(paths.ProfileConfig(accountId));

    /// <summary>Points <c>active.gitconfig</c> at the account's fragment, or empties it when there is no active account.</summary>
    public async Task WriteActiveAsync(string? accountId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = paths.ActiveConfig + ".tmp";
        File.Delete(tmp);

        if (accountId is null)
        {
            await File.WriteAllTextAsync(tmp, "", ct);
        }
        else
        {
            await new GitConfigFile(git, tmp).SetAsync("include.path", AppPaths.RelativeProfilePath(accountId), ct);
        }
        File.Move(tmp, paths.ActiveConfig, overwrite: true);
    }

    /// <summary>Creates empty <c>active.gitconfig</c> / <c>rules.gitconfig</c> so the managed include never dangles.</summary>
    public void EnsureBaseFiles()
    {
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.ProfilesDir);
        if (!File.Exists(paths.ActiveConfig)) File.WriteAllText(paths.ActiveConfig, "");
        if (!File.Exists(paths.RulesConfig)) File.WriteAllText(paths.RulesConfig, "");
    }
}
