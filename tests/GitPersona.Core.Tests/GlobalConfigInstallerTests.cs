using System.Text;

namespace GitPersona.Core.Tests;

public class GlobalConfigInstallerTests
{
    const string UserConfig = "[user]\n\tname = Someone Else\n\temail = old@example.com\n[alias]\n\tco = checkout\n";

    [Fact]
    public async Task Install_adds_one_block_at_the_end_and_leaves_the_rest_alone()
    {
        using var env = new TestEnv(UserConfig);

        var result = await env.Manager.InstallAsync();

        Assert.True(result.Changed);
        var text = env.ReadGlobal();
        Assert.StartsWith(UserConfig, text);
        Assert.EndsWith(ManagedBlock.EndMarker + "\n", text);
        Assert.Contains(env.Paths.ActiveConfig.Replace('\\', '/'), text);
        Assert.Contains(env.Paths.RulesConfig.Replace('\\', '/'), text);
        Assert.True(text.IndexOf("active.gitconfig", StringComparison.Ordinal) < text.IndexOf("rules.gitconfig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_is_idempotent_and_does_not_rewrite_an_up_to_date_file()
    {
        using var env = new TestEnv(UserConfig);
        await env.Manager.InstallAsync();
        var after1 = File.ReadAllBytes(env.GlobalConfig);
        File.SetLastWriteTimeUtc(env.GlobalConfig, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var second = await env.Manager.InstallAsync();
        var third = await env.Manager.InstallAsync();

        Assert.False(second.Changed);
        Assert.False(third.Changed);
        Assert.Equal(after1, File.ReadAllBytes(env.GlobalConfig));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(env.GlobalConfig));
    }

    [Fact]
    public async Task Backup_is_created_once_and_holds_the_original_bytes()
    {
        using var env = new TestEnv(UserConfig);
        var original = File.ReadAllBytes(env.GlobalConfig);

        var first = await env.Manager.InstallAsync();
        // The user keeps editing; later installs must not take a second backup of an already-modified file.
        File.AppendAllText(env.GlobalConfig, "[core]\n\tautocrlf = false\n");
        var second = await env.Manager.InstallAsync();
        var third = await env.Manager.InstallAsync();

        Assert.True(first.BackupCreated);
        Assert.False(second.BackupCreated);
        Assert.False(third.BackupCreated);
        Assert.Equal(first.BackupPath, second.BackupPath);

        var backups = Directory.GetFiles(Path.GetDirectoryName(env.GlobalConfig)!, ".gitconfig.gitpersona-backup-*");
        Assert.Single(backups);
        Assert.Equal(original, File.ReadAllBytes(backups[0]));
    }

    [Fact]
    public async Task Install_without_an_existing_global_config_creates_it_and_needs_no_backup()
    {
        using var env = new TestEnv();
        Assert.False(File.Exists(env.GlobalConfig));

        var result = await env.Manager.InstallAsync();

        Assert.False(result.BackupCreated);
        Assert.Null(result.BackupPath);
        Assert.True(ManagedBlock.Contains(env.ReadGlobal()));
    }

    [Fact]
    public async Task Block_moves_back_to_the_end_after_the_user_edits_below_it()
    {
        using var env = new TestEnv(UserConfig);
        await env.Manager.InstallAsync();
        File.AppendAllText(env.GlobalConfig, "[user]\n\temail = sneaky@example.com\n");

        var result = await env.Manager.InstallAsync();

        Assert.Single(result.OverridesFoundBefore);
        Assert.Contains("user.email", result.OverridesFoundBefore[0].Message);
        Assert.EndsWith(ManagedBlock.EndMarker + "\n", env.ReadGlobal());
        Assert.Empty(env.Manager.Installer.DetectOverrides());
    }

    [Fact]
    public async Task Detects_overrides_placed_after_the_block()
    {
        using var env = new TestEnv(UserConfig);
        await env.Manager.InstallAsync();
        File.AppendAllText(env.GlobalConfig,
            "[user]\n\temail = a@b.c\n\tname = X\n[credential \"https://gitlab.company.com\"]\n\tusername = someone\n[alias]\n\tx = y\n");

        var warnings = env.Manager.Installer.DetectOverrides();

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("user.email"));
        Assert.Contains(warnings, w => w.Message.Contains("user.name"));
        Assert.Contains(warnings, w => w.Message.Contains("credential username"));
    }

    [Fact]
    public async Task Settings_before_the_block_are_not_reported_as_overrides()
    {
        using var env = new TestEnv(UserConfig);
        var result = await env.Manager.InstallAsync();
        Assert.Empty(result.OverridesFoundBefore);
        Assert.Empty(env.Manager.Installer.DetectOverrides());
    }

    [Fact]
    public async Task Uninstall_removes_the_block_and_restores_the_original_byte_for_byte()
    {
        using var env = new TestEnv(UserConfig);
        var original = File.ReadAllBytes(env.GlobalConfig);
        await env.Manager.InstallAsync();

        var report = await env.Manager.UninstallAsync(new UninstallOptions());

        Assert.False(report.Config.RestoredFromBackup);
        Assert.Equal(original, File.ReadAllBytes(env.GlobalConfig));
    }

    [Fact]
    public async Task Uninstall_with_restore_copies_the_backup_back_byte_for_byte()
    {
        using var env = new TestEnv(UserConfig);
        var original = File.ReadAllBytes(env.GlobalConfig);
        await env.Manager.InstallAsync();
        File.AppendAllText(env.GlobalConfig, "[core]\n\teditor = vim\n");

        var report = await env.Manager.UninstallAsync(new UninstallOptions(RestoreBackup: true));

        Assert.True(report.Config.RestoredFromBackup);
        Assert.Equal(original, File.ReadAllBytes(env.GlobalConfig));
    }

    [Fact]
    public async Task Uninstall_deletes_a_global_config_the_app_created()
    {
        using var env = new TestEnv();
        await env.Manager.InstallAsync();

        await env.Manager.UninstallAsync(new UninstallOptions());

        Assert.False(File.Exists(env.GlobalConfig));
    }

    [Fact]
    public async Task Round_trip_preserves_bytes_that_are_not_valid_utf8_and_a_bom_and_crlf()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.Latin1.GetBytes("[user]\r\n\tname = José Ångström\r\n"))
            .ToArray();
        using var env = new TestEnv(initialGlobalConfigBytes: original);

        await env.Manager.InstallAsync();
        var installed = File.ReadAllBytes(env.GlobalConfig);
        Assert.StartsWith(Encoding.Latin1.GetString(original), Encoding.Latin1.GetString(installed));
        Assert.Contains("\r\n", Encoding.Latin1.GetString(installed)[original.Length..]);

        await env.Manager.UninstallAsync(new UninstallOptions());
        Assert.Equal(original, File.ReadAllBytes(env.GlobalConfig));
    }

    [Fact]
    public async Task Reinstall_after_uninstall_takes_a_fresh_backup()
    {
        using var env = new TestEnv(UserConfig);
        await env.Manager.InstallAsync();
        await env.Manager.UninstallAsync(new UninstallOptions());
        File.AppendAllText(env.GlobalConfig, "[core]\n\teditor = vim\n");

        var again = await env.Manager.InstallAsync();

        Assert.True(again.BackupCreated);
        Assert.Contains("editor = vim", File.ReadAllText(again.BackupPath!));
    }

    [Fact]
    public async Task Git_follows_the_managed_includes_and_the_global_config_still_parses()
    {
        using var env = new TestEnv(UserConfig);
        var account = TestEnv.MakeAccount();
        await env.Manager.InstallAsync();
        await env.Manager.AddAccountAsync(account, token: null);
        await env.Manager.SetActiveAsync(account.Id);
        var repo = env.CreateRepo();

        Assert.Equal("muneel@company.com", await env.EffectiveAsync(repo, "user.email"));
        Assert.Equal("Muneel Work", await env.EffectiveAsync(repo, "user.name"));
        Assert.Equal("checkout", await env.EffectiveAsync(repo, "alias.co"));
    }
}
