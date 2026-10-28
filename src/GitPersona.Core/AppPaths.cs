namespace GitPersona.Core;

/// <summary>Where the app keeps its own files. Holds metadata only — never secrets.</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string AccountsFile => Path.Combine(Root, "accounts.json");
    public string StateFile => Path.Combine(Root, "state.json");
    public string ProfilesDir => Path.Combine(Root, "profiles");
    public string ActiveConfig => Path.Combine(Root, "active.gitconfig");
    public string RulesConfig => Path.Combine(Root, "rules.gitconfig");
    public string RulesFile => Path.Combine(Root, "rules.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string ProfileConfig(string accountId)
    {
        AccountValidator.EnsureValidId(accountId);
        return Path.Combine(ProfilesDir, accountId + ".gitconfig");
    }

    /// <summary>Path of a profile relative to <see cref="ActiveConfig"/>, as written into its include directive.</summary>
    public static string RelativeProfilePath(string accountId)
    {
        AccountValidator.EnsureValidId(accountId);
        return $"profiles/{accountId}.gitconfig";
    }

    public static AppPaths Default()
    {
        string baseDir;
        if (OperatingSystem.IsMacOS())
        {
            baseDir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        }
        else
        {
            baseDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        }
        return new AppPaths(Path.Combine(baseDir, "GitPersona"));
    }
}
