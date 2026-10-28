using System.Text;
using System.Xml.Linq;

namespace GitPersona.Core.Platform;

/// <summary>
/// "Start at login", per user and without admin rights: the registry Run key on Windows, a LaunchAgent on macOS,
/// an XDG autostart entry on Linux. No Task Scheduler, no services.
/// </summary>
public interface IStartupRegistration
{
    bool IsEnabled();

    /// <summary>The executable the entry currently launches, or null if disabled.</summary>
    string? RegisteredPath();

    void Enable(string exePath);

    void Disable();
}

public static class StartupRegistration
{
    public const string MinimizedArgument = "--minimized";
    public const string AppName = "GitPersona";

    public static IStartupRegistration ForCurrentOS() =>
        OperatingSystem.IsWindows() ? new WindowsRunKeyStartup()
        : OperatingSystem.IsMacOS() ? new MacLaunchAgentStartup()
        : new LinuxAutostartStartup();

    /// <summary>
    /// A portable exe can be moved. If startup is enabled but points somewhere else, repoint it at the running exe.
    /// Returns true when the entry was changed.
    /// </summary>
    public static bool RefreshIfMoved(IStartupRegistration registration, string currentExePath)
    {
        if (!registration.IsEnabled()) return false;
        var registered = registration.RegisteredPath();
        if (registered is not null && SamePath(registered, currentExePath)) return false;
        registration.Enable(currentExePath);
        return true;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static void EnsureUsable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath))
            throw new ArgumentException("The executable path must be absolute.", nameof(exePath));
        if (AccountValidator.HasControlCharacters(exePath))
            throw new ArgumentException("The executable path contains control characters.", nameof(exePath));
        if (exePath.Contains('"'))
            throw new ArgumentException("The executable path contains a quote character.", nameof(exePath));
    }
}

public sealed class WindowsRunKeyStartup(string? keyPath = null) : IStartupRegistration
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly string keyPath = keyPath ?? DefaultKeyPath;

    public static string BuildCommand(string exePath) => $"\"{exePath}\" {StartupRegistration.MinimizedArgument}";

    public bool IsEnabled() => ReadValue() is not null;

    public string? RegisteredPath()
    {
        var value = ReadValue();
        if (value is null) return null;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : null;
        }
        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }

    public void Enable(string exePath)
    {
        StartupRegistration.EnsureUsable(exePath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(StartupRegistration.AppName, BuildCommand(exePath), Microsoft.Win32.RegistryValueKind.String);
    }

    public void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(StartupRegistration.AppName, throwOnMissingValue: false);
    }

    string? ReadValue()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(StartupRegistration.AppName) as string;
    }
}

public sealed class MacLaunchAgentStartup(string? directory = null) : IStartupRegistration
{
    public const string Label = "io.github.gitpersona";

    readonly string directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public string PlistPath => Path.Combine(directory, Label + ".plist");

    public bool IsEnabled() => File.Exists(PlistPath);

    public string? RegisteredPath()
    {
        if (!File.Exists(PlistPath)) return null;
        try
        {
            var array = XDocument.Load(PlistPath).Descendants("key")
                .FirstOrDefault(k => k.Value == "ProgramArguments")?.ElementsAfterSelf("array").FirstOrDefault();
            return array?.Elements("string").FirstOrDefault()?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public void Enable(string exePath)
    {
        StartupRegistration.EnsureUsable(exePath);
        Directory.CreateDirectory(directory);

        var doc = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", new XElement("string", exePath), new XElement("string", StartupRegistration.MinimizedArgument)),
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    new XElement("key", "ProcessType"), new XElement("string", "Interactive"))));
        File.WriteAllText(PlistPath, doc.Declaration is null ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + doc : doc.ToString(), new UTF8Encoding(false));
    }

    public void Disable()
    {
        if (File.Exists(PlistPath)) File.Delete(PlistPath);
    }
}

public sealed class LinuxAutostartStartup(string? directory = null) : IStartupRegistration
{
    readonly string directory = directory ?? Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart");

    public string DesktopFilePath => Path.Combine(directory, "gitpersona.desktop");

    public bool IsEnabled() => File.Exists(DesktopFilePath);

    public string? RegisteredPath()
    {
        if (!File.Exists(DesktopFilePath)) return null;
        var exec = File.ReadLines(DesktopFilePath).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        if (exec is null) return null;

        var value = exec["Exec=".Length..];
        if (!value.StartsWith('"')) return value.Split(' ')[0];

        var sb = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) sb.Append(value[++i]);
            else if (value[i] == '"') break;
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    public void Enable(string exePath)
    {
        StartupRegistration.EnsureUsable(exePath);
        Directory.CreateDirectory(directory);
        var content =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=GitPersona\n" +
            "Comment=Switch between Git accounts\n" +
            $"Exec={QuoteExec(exePath)} {StartupRegistration.MinimizedArgument}\n" +
            "Terminal=false\n" +
            "X-GNOME-Autostart-enabled=true\n";
        File.WriteAllText(DesktopFilePath, content, new UTF8Encoding(false));
    }

    public void Disable()
    {
        if (File.Exists(DesktopFilePath)) File.Delete(DesktopFilePath);
    }

    // Desktop Entry spec: quote the program, and backslash-escape " ` $ \ inside the quotes.
    static string QuoteExec(string path)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
