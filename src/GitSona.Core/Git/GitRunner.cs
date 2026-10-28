using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitSona.Core.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public GitResult EnsureSuccess(string? what = null)
    {
        if (Success) return this;
        var detail = StdErr.Trim();
        throw new GitException($"{what ?? "git"} failed (exit {ExitCode}){(detail.Length > 0 ? ": " + detail : "")}");
    }

    // StdOut may hold a secret (git credential fill), so never print it implicitly.
    public override string ToString() => $"git exited with code {ExitCode}";
}

public class GitException(string message) : Exception(message);

public sealed class GitNotFoundException(string message) : GitException(message);

public sealed class GitTimeoutException(string message) : GitException(message);

/// <summary>
/// Runs the user's real <c>git</c> executable. Arguments go through ArgumentList (no shell quoting),
/// stdin is always supplied explicitly (and closed), and git is never allowed to prompt.
/// </summary>
public sealed class GitRunner
{
    static readonly UTF8Encoding Utf8NoBom = new(false);

    public string GitPath { get; }

    /// <summary>Environment overrides applied on top of the process environment; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    public TimeSpan DefaultTimeout { get; }

    public GitRunner(string gitPath, IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? defaultTimeout = null)
    {
        GitPath = gitPath;
        Environment = environment ?? new Dictionary<string, string?>();
        DefaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Creates a runner for the git found on PATH (or a well-known install location).</summary>
    public static GitRunner Locate(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var path = GitLocator.Find()
            ?? throw new GitNotFoundException("Git was not found. Install Git (https://git-scm.com) and make sure it is on PATH.");
        return new GitRunner(path, environment);
    }

    /// <summary>Looks up a variable the way a child git process would see it.</summary>
    public string? GetEnvironmentVariable(string name)
    {
        foreach (var (key, value) in Environment)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        return System.Environment.GetEnvironmentVariable(name);
    }

    public Task<GitResult> RunAsync(params string[] args) => RunAsync(args, stdin: null);

    public async Task<GitResult> RunAsync(
        IEnumerable<string> args,
        string? stdin = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        bool allowCredentialUi = false)
    {
        var psi = new ProcessStartInfo(GitPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Credential helpers may open their own sign-in window (e.g. GitHub OAuth in the browser) only when the caller asks.
        if (!allowCredentialUi) psi.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var (key, value) in Environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new GitNotFoundException($"Could not start git at '{GitPath}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(effectiveTimeout);

        try
        {
            try
            {
                if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token);
            }
            catch (IOException)
            {
                // git exited before reading all of stdin; its exit code tells the story.
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }

            await process.WaitForExitAsync(cts.Token);
            return new GitResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new GitTimeoutException($"git {string.Join(' ', psi.ArgumentList.Take(2))} timed out after {effectiveTimeout.TotalSeconds:0.#}s.");
        }
    }

    public async Task<Version> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = (await RunAsync(["--version"], cancellationToken: cancellationToken)).EnsureSuccess("git --version");
        return GitLocator.ParseVersion(result.StdOut)
            ?? throw new GitException($"Unrecognised git version output: {result.StdOut.Trim()}");
    }
}
