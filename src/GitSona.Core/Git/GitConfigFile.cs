namespace GitSona.Core.Git;

/// <summary>Reads and writes one gitconfig file through <c>git config --file</c>, so git does the parsing and escaping.</summary>
public sealed class GitConfigFile(GitRunner git, string path)
{
    public string Path { get; } = path;

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        EnsureDirectory();
        // The value follows the key, so a leading '-' in it is never parsed as an option.
        (await git.RunAsync(["config", "--file", Path, key, value], cancellationToken: ct)).EnsureSuccess($"git config {key}");
    }

    public async Task ReplaceAllAsync(string key, string value, CancellationToken ct = default)
    {
        EnsureDirectory();
        (await git.RunAsync(["config", "--file", Path, "--replace-all", key, value], cancellationToken: ct)).EnsureSuccess($"git config {key}");
    }

    public async Task AddAsync(string key, string value, CancellationToken ct = default)
    {
        EnsureDirectory();
        (await git.RunAsync(["config", "--file", Path, "--add", key, value], cancellationToken: ct)).EnsureSuccess($"git config {key}");
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var result = await git.RunAsync(["config", "--file", Path, "--get", key], cancellationToken: ct);
        if (result.ExitCode == 1) return null;
        return result.EnsureSuccess($"git config {key}").StdOut.TrimEnd('\r', '\n');
    }

    public async Task<IReadOnlyList<string>> GetAllAsync(string key, CancellationToken ct = default)
    {
        var result = await git.RunAsync(["config", "--file", Path, "--get-all", key], cancellationToken: ct);
        if (result.ExitCode == 1) return [];
        return result.EnsureSuccess($"git config {key}").StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
    }

    void EnsureDirectory()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
