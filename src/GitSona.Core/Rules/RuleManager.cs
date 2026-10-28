using GitSona.Core.Git;

namespace GitSona.Core;

/// <summary>Owns the rule list and keeps <c>rules.gitconfig</c> in step with it.</summary>
public sealed class RuleManager(GitRunner git, AppPaths paths, AccountStore accounts)
{
    readonly RuleStore store = new(paths);
    internal RuleStore Store => store;
    Version? gitVersion;

    /// <summary>Test seam: pretend to be an older git.</summary>
    internal Version? GitVersionOverride { get; set; }

    public IReadOnlyList<Rule> All() => store.All();

    public IReadOnlyList<Rule> ForAccount(string accountId) => store.All().Where(r => r.AccountId == accountId).ToList();

    public async Task<Version> GetGitVersionAsync(CancellationToken ct = default) =>
        GitVersionOverride ?? gitVersion ?? (gitVersion = await git.GetVersionAsync(ct));

    public async Task<bool> RemoteRulesSupportedAsync(CancellationToken ct = default) =>
        GitLocator.SupportsHasConfigRemote(await GetGitVersionAsync(ct));

    public async Task<Rule> AddFolderRuleAsync(string accountId, string folder, CancellationToken ct = default)
    {
        EnsureAccount(accountId);
        return await AddAsync(RuleFactory.CreateFolder(accountId, folder), ct);
    }

    public async Task<Rule> AddRemoteRuleAsync(string accountId, string hostUrl, string? groupPath, CancellationToken ct = default)
    {
        EnsureAccount(accountId);
        await EnsureRemoteRulesSupportedAsync(ct);
        return await AddAsync(RuleFactory.CreateRemote(accountId, hostUrl, groupPath), ct);
    }

    public async Task RemoveAsync(string ruleId, CancellationToken ct = default)
    {
        var rules = store.All().ToList();
        if (rules.RemoveAll(r => r.Id == ruleId) == 0) throw new RuleException("That rule no longer exists.");
        store.Replace(rules);
        await RegenerateAsync(ct);
    }

    /// <summary>Moves a rule to <paramref name="newIndex"/>. Later rules win over earlier ones.</summary>
    public async Task MoveAsync(string ruleId, int newIndex, CancellationToken ct = default)
    {
        var rules = store.All().ToList();
        var from = rules.FindIndex(r => r.Id == ruleId);
        if (from < 0) throw new RuleException("That rule no longer exists.");

        var rule = rules[from];
        rules.RemoveAt(from);
        rules.Insert(Math.Clamp(newIndex, 0, rules.Count), rule);
        store.Replace(rules);
        await RegenerateAsync(ct);
    }

    public async Task RemoveForAccountAsync(string accountId, CancellationToken ct = default)
    {
        var rules = store.All().Where(r => r.AccountId != accountId).ToList();
        if (rules.Count == store.All().Count) return;
        store.Replace(rules);
        await RegenerateAsync(ct);
    }

    /// <summary>Rewrites rules.gitconfig from the stored list. Remote rules are skipped when git is too old for them.</summary>
    public async Task RegenerateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.Root);
        var remoteOk = await RemoteRulesSupportedAsync(ct);

        var tmp = paths.RulesConfig + ".tmp";
        File.Delete(tmp);
        await File.WriteAllTextAsync(tmp, "", ct);
        var file = new GitConfigFile(git, tmp);

        foreach (var rule in store.All())
        {
            if (rule.Type == RuleType.Remote && !remoteOk) continue;
            if (accounts.Find(rule.AccountId) is null) continue;

            var include = AppPaths.RelativeProfilePath(rule.AccountId);
            foreach (var condition in RuleConditions.For(rule))
                await file.AddAsync($"includeIf.{condition}.path", include, ct);
        }
        File.Move(tmp, paths.RulesConfig, overwrite: true);
    }

    async Task<Rule> AddAsync(Rule rule, CancellationToken ct)
    {
        var rules = store.All().ToList();
        if (rules.Any(r => RuleConditions.For(r).Intersect(RuleConditions.For(rule), StringComparer.OrdinalIgnoreCase).Any()))
            throw new RuleException("A rule for that folder or remote already exists. Remove it first, or reorder the existing one.");

        rules.Add(rule);
        store.Replace(rules);
        await RegenerateAsync(ct);
        return rule;
    }

    async Task EnsureRemoteRulesSupportedAsync(CancellationToken ct)
    {
        if (await RemoteRulesSupportedAsync(ct)) return;
        var v = await GetGitVersionAsync(ct);
        throw new RuleNotSupportedException(
            $"Remote rules need Git {GitLocator.HasConfigRemoteMinimum.ToString(2)} or newer; this machine has {v.ToString(3)}. Use a folder rule or upgrade Git.");
    }

    void EnsureAccount(string accountId)
    {
        if (accounts.Find(accountId) is null) throw new RuleException("The selected account no longer exists.");
    }
}
