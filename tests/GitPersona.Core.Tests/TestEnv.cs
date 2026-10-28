using System.Text;
using GitPersona.Core;
using GitPersona.Core.Git;

namespace GitPersona.Core.Tests;

/// <summary>
/// A sandbox for one test: temp global gitconfig, no system config, temp app-data dir, and a
/// file-backed credential helper so the real OS credential store is never touched.
/// </summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; }
    public string GlobalConfig { get; }
    public string AppDataDir { get; }
    public string CredentialFile { get; }
    public GitRunner Git { get; }
    public AppPaths Paths { get; }
    public PersonaManager Manager { get; }

    public TestEnv(string? initialGlobalConfig = null, byte[]? initialGlobalConfigBytes = null, IReadOnlyList<(string Key, string Value)>? extraConfig = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "gitpersona-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);

        GlobalConfig = Path.Combine(Root, "home", ".gitconfig");
        AppDataDir = Path.Combine(Root, "appdata", "GitPersona");
        CredentialFile = Path.Combine(Root, "credentials");
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalConfig)!);

        if (initialGlobalConfigBytes is not null) File.WriteAllBytes(GlobalConfig, initialGlobalConfigBytes);
        else if (initialGlobalConfig is not null) File.WriteAllText(GlobalConfig, initialGlobalConfig, new UTF8Encoding(false));

        var home = Path.GetDirectoryName(GlobalConfig)!;
        var injected = new List<(string Key, string Value)>
        {
            ("credential.helper", $"store --file='{CredentialFile.Replace('\\', '/')}'"),
        };
        if (extraConfig is not null) injected.AddRange(extraConfig);

        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = GlobalConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
            ["GIT_AUTHOR_NAME"] = null,
            ["GIT_AUTHOR_EMAIL"] = null,
            ["GIT_COMMITTER_NAME"] = null,
            ["GIT_COMMITTER_EMAIL"] = null,
            ["EMAIL"] = null,
            // Injected via the environment so the global file stays exactly what the test wrote.
            ["GIT_CONFIG_COUNT"] = injected.Count.ToString(),
        };
        for (var i = 0; i < injected.Count; i++)
        {
            environment[$"GIT_CONFIG_KEY_{i}"] = injected[i].Key;
            environment[$"GIT_CONFIG_VALUE_{i}"] = injected[i].Value;
        }
        Git = new GitRunner(GitLocator.Find() ?? throw new InvalidOperationException("git is required to run these tests"), environment);

        Paths = new AppPaths(AppDataDir);
        Manager = new PersonaManager(Git, Paths);
    }

    public static Account MakeAccount(
        string id = "work", string host = "https://gitlab.company.com", string username = "muneel-work",
        string name = "Muneel Work", string email = "muneel@company.com", Provider provider = Provider.GitLab) => new()
    {
        Id = id,
        Label = id,
        Provider = provider,
        HostUrl = host,
        Username = username,
        Name = name,
        Email = email,
    };

    public string CreateRepo(string name = "repo", string? remoteUrl = null)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        Git.RunAsync(["init", "-q", dir]).GetAwaiter().GetResult().EnsureSuccess();
        if (remoteUrl is not null)
            Git.RunAsync(["-C", dir, "remote", "add", "origin", remoteUrl]).GetAwaiter().GetResult().EnsureSuccess();
        return dir;
    }

    /// <summary>The value git would use inside <paramref name="repoDir"/>, resolved with the real include semantics.</summary>
    public async Task<string?> EffectiveAsync(string repoDir, string key)
    {
        var r = await Git.RunAsync(["config", "--get", key], workingDirectory: repoDir);
        return r.ExitCode == 1 ? null : r.EnsureSuccess().StdOut.TrimEnd('\r', '\n');
    }

    public string ReadGlobal() => File.ReadAllText(GlobalConfig);

    public void Dispose()
    {
        DeleteTree(Root);
    }

    /// <summary>Deletes a tree even when it holds read-only files (git object files are read-only on Windows).</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
