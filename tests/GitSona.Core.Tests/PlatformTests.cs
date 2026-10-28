using GitSona.Core.Platform;

namespace GitSona.Core.Tests;

public class StartupTests
{
    static string Exe => OperatingSystem.IsWindows() ? @"C:\Apps\GitSona\GitSona.exe" : "/opt/gitsona/GitSona";
    static string OtherExe => OperatingSystem.IsWindows() ? @"D:\Moved\GitSona.exe" : "/home/me/Downloads/GitSona";

    static string TempDir() => Path.Combine(Path.GetTempPath(), "gp-startup-" + Guid.NewGuid().ToString("N")[..8]);

    // "/opt/x" is only absolute on Unix; resolve it against the current OS so these tests run everywhere.
    static string Abs(string unixStyle) => Path.GetFullPath(unixStyle);

    // ---- macOS and Linux file formats are pure file generation, so they are tested on every OS ----

    [Fact]
    public void Linux_autostart_entry_runs_minimized_and_round_trips_the_path()
    {
        var dir = TempDir();
        try
        {
            var startup = new LinuxAutostartStartup(dir);
            Assert.False(startup.IsEnabled());

            var exe = Abs("/opt/My Apps/Git Sona/GitSona");
            startup.Enable(exe);

            Assert.True(startup.IsEnabled());
            var text = File.ReadAllText(startup.DesktopFilePath);
            Assert.Contains("Exec=\"", text);
            Assert.Contains("\" --minimized", text);
            Assert.Equal(exe, startup.RegisteredPath());

            startup.Disable();
            Assert.False(startup.IsEnabled());
            Assert.Null(startup.RegisteredPath());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Linux_exec_quoting_escapes_shell_metacharacters()
    {
        var dir = TempDir();
        try
        {
            var startup = new LinuxAutostartStartup(dir);
            var path = OperatingSystem.IsWindows() ? @"C:\x\a$b`c\GitSona" : "/x/a$b`c/GitSona";
            startup.Enable(path);
            Assert.Equal(path, startup.RegisteredPath());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mac_launch_agent_is_valid_xml_with_minimized_argument()
    {
        var dir = TempDir();
        try
        {
            var startup = new MacLaunchAgentStartup(dir);
            var exe = Abs("/Applications/GitSona.app/Contents/MacOS/GitSona");
            startup.Enable(exe);

            var doc = System.Xml.Linq.XDocument.Load(startup.PlistPath); // throws if malformed
            var strings = doc.Descendants("string").Select(s => s.Value).ToList();
            Assert.Contains("--minimized", strings);
            Assert.Contains("true", doc.Descendants().Select(e => e.Name.LocalName));
            Assert.Equal(exe, startup.RegisteredPath());

            startup.Disable();
            Assert.False(startup.IsEnabled());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mac_plist_escapes_xml_in_paths()
    {
        var dir = TempDir();
        try
        {
            var startup = new MacLaunchAgentStartup(dir);
            var path = OperatingSystem.IsWindows() ? @"C:\R&D\<x>\GitSona" : "/Apps/R&D/<x>/GitSona";
            startup.Enable(path);
            Assert.Equal(path, startup.RegisteredPath());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- Windows registry (isolated under a throwaway key, never the real Run key) ----

    [Fact]
    public void Windows_run_key_value_quotes_the_path_and_adds_minimized()
    {
        Assert.Equal("\"C:\\Program Files\\GitSona\\GitSona.exe\" --minimized",
            WindowsRunKeyStartup.BuildCommand(@"C:\Program Files\GitSona\GitSona.exe"));
    }

    [Fact]
    public void Windows_registry_enable_disable_round_trip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = @"Software\GitSonaTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var startup = new WindowsRunKeyStartup(key);
            Assert.False(startup.IsEnabled());

            startup.Enable(@"C:\Program Files\GitSona\GitSona.exe");

            Assert.True(startup.IsEnabled());
            Assert.Equal(@"C:\Program Files\GitSona\GitSona.exe", startup.RegisteredPath());
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Program Files\\GitSona\\GitSona.exe\" --minimized", k!.GetValue("GitSona"));

            startup.Disable();
            Assert.False(startup.IsEnabled());
            Assert.Null(startup.RegisteredPath());
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GitSonaTests", throwOnMissingSubKey: false);
        }
    }

    // ---- shared behaviour ----

    [Fact]
    public void Moved_portable_exe_repoints_the_entry_but_only_when_enabled()
    {
        var dir = TempDir();
        try
        {
            var startup = new LinuxAutostartStartup(dir);

            Assert.False(StartupRegistration.RefreshIfMoved(startup, OtherExe)); // disabled: leave alone
            Assert.False(startup.IsEnabled());

            startup.Enable(Exe);
            Assert.False(StartupRegistration.RefreshIfMoved(startup, Exe)); // same path: no change

            Assert.True(StartupRegistration.RefreshIfMoved(startup, OtherExe));
            Assert.Equal(OtherExe, startup.RegisteredPath());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("relative/GitSona")]
    [InlineData("")]
    public void Unsafe_executable_paths_are_refused(string path)
    {
        var dir = TempDir();
        Assert.Throws<ArgumentException>(() => new LinuxAutostartStartup(dir).Enable(path));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Paths_with_quotes_or_line_breaks_are_refused()
    {
        var dir = TempDir();
        var root = OperatingSystem.IsWindows() ? @"C:\x" : "/x";
        Assert.Throws<ArgumentException>(() => new LinuxAutostartStartup(dir).Enable(root + "/a\"b"));
        Assert.Throws<ArgumentException>(() => new LinuxAutostartStartup(dir).Enable(root + "/a\nExec=evil"));
    }

    [Fact]
    public async Task Uninstall_removing_startup_is_idempotent()
    {
        var dir = TempDir();
        var startup = new LinuxAutostartStartup(dir);
        startup.Disable();
        startup.Disable();
        await Task.CompletedTask;
        Assert.False(startup.IsEnabled());
    }
}

public class SingleInstanceTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "gp-single-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void Only_one_instance_can_hold_the_lock_and_it_is_released_on_dispose()
    {
        var dir = TempDir();
        try
        {
            var first = SingleInstance.TryAcquire(dir);
            Assert.NotNull(first);

            Assert.Null(SingleInstance.TryAcquire(dir));

            first.Dispose();
            using var third = SingleInstance.TryAcquire(dir);
            Assert.NotNull(third);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Different_data_folders_do_not_conflict()
    {
        var a = TempDir();
        var b = TempDir();
        try
        {
            using var one = SingleInstance.TryAcquire(a);
            using var two = SingleInstance.TryAcquire(b);
            Assert.NotNull(one);
            Assert.NotNull(two);
            Assert.NotEqual(one.PipeName, two.PipeName);
        }
        finally
        {
            foreach (var d in new[] { a, b }) try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_second_launch_wakes_the_first()
    {
        var dir = TempDir();
        try
        {
            using var first = SingleInstance.TryAcquire(dir)!;
            var shown = new TaskCompletionSource();
            first.ActivationRequested += () => shown.TrySetResult();

            Assert.True(await SingleInstance.SignalExistingAsync(dir, TimeSpan.FromSeconds(5)));

            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task It_can_be_woken_repeatedly()
    {
        var dir = TempDir();
        try
        {
            using var first = SingleInstance.TryAcquire(dir)!;
            var count = 0;
            var three = new TaskCompletionSource();
            first.ActivationRequested += () => { if (Interlocked.Increment(ref count) == 3) three.TrySetResult(); };

            for (var i = 0; i < 3; i++) Assert.True(await SingleInstance.SignalExistingAsync(dir, TimeSpan.FromSeconds(5)));

            await three.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Signalling_nobody_returns_false()
    {
        var dir = TempDir();
        Assert.False(await SingleInstance.SignalExistingAsync(dir, TimeSpan.FromMilliseconds(300)));
    }
}

public class ExpiryAndSettingsTests
{
    static readonly DateOnly Today = new(2026, 10, 28);

    static Account WithExpiry(DateOnly? on) => TestEnv.MakeAccount() with { TokenExpiresOn = on };

    [Theory]
    [InlineData(30, ExpiryState.Ok)]
    [InlineData(8, ExpiryState.Ok)]
    [InlineData(7, ExpiryState.ExpiringSoon)]
    [InlineData(1, ExpiryState.ExpiringSoon)]
    [InlineData(0, ExpiryState.ExpiringSoon)]
    [InlineData(-1, ExpiryState.Expired)]
    [InlineData(-40, ExpiryState.Expired)]
    public void Warns_seven_days_before_expiry(int daysFromNow, ExpiryState expected)
    {
        var notice = ExpiryChecker.Check(WithExpiry(Today.AddDays(daysFromNow)), Today);
        Assert.Equal(expected, notice!.State);
        Assert.Equal(daysFromNow, notice.DaysLeft);
    }

    [Fact]
    public void Accounts_without_an_expiry_never_warn() =>
        Assert.Null(ExpiryChecker.Check(WithExpiry(null), Today));

    [Fact]
    public void Attention_lists_only_problem_accounts_soonest_first()
    {
        var accounts = new[]
        {
            TestEnv.MakeAccount("fine") with { TokenExpiresOn = Today.AddDays(90) },
            TestEnv.MakeAccount("soon") with { TokenExpiresOn = Today.AddDays(3) },
            TestEnv.MakeAccount("gone") with { TokenExpiresOn = Today.AddDays(-2) },
            TestEnv.MakeAccount("none"),
        };

        var attention = ExpiryChecker.Attention(accounts, Today);

        Assert.Equal(["gone", "soon"], attention.Select(n => n.Account.Id));
        Assert.Contains("expired 2 days ago", attention[0].Describe());
        Assert.Contains("expires in 3 days", attention[1].Describe());
    }

    [Fact]
    public void Settings_default_load_save_and_survive_a_corrupt_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gp-settings-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new SettingsStore(new AppPaths(dir));
            Assert.False(store.Load().FirstRunCompleted);
            Assert.True(store.Load().AutoUpdateCheck);

            store.Update(s => s with { FirstRunCompleted = true, AutoUpdateCheck = false });
            var reloaded = new SettingsStore(new AppPaths(dir)).Load();
            Assert.True(reloaded.FirstRunCompleted);
            Assert.False(reloaded.AutoUpdateCheck);

            File.WriteAllText(new AppPaths(dir).SettingsFile, "not json");
            Assert.False(store.Load().FirstRunCompleted);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class CredentialHelperTests
{
    [Fact]
    public async Task The_test_sandbox_helper_is_reported_as_weak_not_secure()
    {
        using var env = new TestEnv();
        var status = await new CredentialHelperService(env.Git).CheckAsync();

        Assert.StartsWith("store", status.Current);
        Assert.Equal(HelperState.Weak, status.State);
        Assert.Contains("plain-text", status.Explanation);
    }

    [Fact]
    public async Task A_missing_helper_is_detected_and_explained()
    {
        using var env = new TestEnv(extraConfig: [("credential.helper", "")]); // empty value resets the list
        var status = await new CredentialHelperService(env.Git).CheckAsync();

        Assert.Equal(HelperState.Missing, status.State);
        Assert.Contains("cannot save tokens", status.Explanation);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("osxkeychain")]
    [InlineData("libsecret")]
    [InlineData("/usr/lib/git-core/git-credential-libsecret")]
    public async Task Secure_helpers_are_recognised(string helper)
    {
        using var env = new TestEnv(extraConfig: [("credential.helper", ""), ("credential.helper", helper)]);
        var status = await new CredentialHelperService(env.Git).CheckAsync();
        Assert.Equal(HelperState.Good, status.State);
    }

    [Fact]
    public async Task Setting_the_helper_writes_global_config_only_for_known_names()
    {
        using var env = new TestEnv("[user]\n\tname = Keep\n");
        var service = new CredentialHelperService(env.Git);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetGlobalHelperAsync("!rm -rf /"));
        Assert.DoesNotContain("credential", env.ReadGlobal());

        await service.SetGlobalHelperAsync("libsecret");
        Assert.Contains("helper = libsecret", env.ReadGlobal());
        Assert.Contains("name = Keep", env.ReadGlobal());
    }
}

public class GitStatusTests
{
    [Fact]
    public async Task Detects_the_installed_git()
    {
        var status = await Git.GitStatus.DetectAsync();
        Assert.True(status.IsAvailable);
        Assert.Null(status.Problem);
        Assert.Equal(status.Version >= new Version(2, 36), status.RemoteRulesSupported);
    }
}

public class GcmDetectionTests
{
    [Theory]
    [InlineData("manager", true)]
    [InlineData("manager-core", true)]
    [InlineData("osxkeychain", false)]
    [InlineData("libsecret", false)]
    public async Task Detects_whether_git_credential_manager_is_the_helper(string helper, bool expected)
    {
        using var env = new TestEnv(extraConfig: [("credential.helper", ""), ("credential.helper", helper)]);
        var status = await new Core.Platform.CredentialHelperService(env.Git).CheckAsync();
        Assert.Equal(expected, status.UsesGitCredentialManager);
    }
}
