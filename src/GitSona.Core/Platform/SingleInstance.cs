using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace GitSona.Core.Platform;

/// <summary>
/// Only one GitSona per user. The first instance holds an exclusive lock on a file in the app-data folder
/// (released by the OS if the process dies, so it is never stale) and listens on a named pipe. A second launch
/// tells the first to show its window and then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string ShowMessage = "SHOW";

    readonly FileStream lockStream;
    readonly CancellationTokenSource cts = new();
    Task? listener;

    public string PipeName { get; }

    public event Action? ActivationRequested;

    SingleInstance(FileStream lockStream, string pipeName)
    {
        this.lockStream = lockStream;
        PipeName = pipeName;
    }

    /// <summary>Returns null when another instance already holds the lock.</summary>
    public static SingleInstance? TryAcquire(string lockDirectory)
    {
        Directory.CreateDirectory(lockDirectory);
        try
        {
            var stream = new FileStream(Path.Combine(lockDirectory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var instance = new SingleInstance(stream, PipeNameFor(lockDirectory));
            instance.StartListening();
            return instance;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string PipeNameFor(string lockDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(lockDirectory).ToLowerInvariant()));
        return "GitSona-" + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Asks the running instance to show itself. False if nobody answered in time.</summary>
    public static async Task<bool> SignalExistingAsync(string lockDirectory, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeNameFor(lockDirectory), PipeDirection.Out);
            await client.ConnectAsync((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(ShowMessage + "\n");
            await client.WriteAsync(bytes);
            await client.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    void StartListening() => listener = Task.Run(ListenAsync);

    async Task ListenAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cts.Token);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cts.Token);
                if (line == ShowMessage) ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // client went away mid-message; keep listening
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { listener?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        lockStream.Dispose();
        cts.Dispose();
    }
}
