using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SunChat;

// User settings, persisted as JSON under %AppData%\SunChat\settings.json.
public class Settings
{
    public string DisplayName { get; set; }
    public string Group { get; set; } = "기본";
    public string DownloadDir { get; set; }
    public bool SameGroupOnly { get; set; }
    public bool AutoStart { get; set; }
    public int TcpPort { get; set; } = 51001;   // fixed so other subnets can reach us by IP:port
    public int RetentionDays { get; set; } = 90; // chat history kept this many days (1..180)

    public const int MaxRetentionDays = 180;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SunChat";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SunChat", "settings.json");

    public static string DefaultDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SunChat_Received");

    public static Settings Load()
    {
        Settings s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
        }
        catch { /* corrupt/unreadable -> fall back to defaults */ }

        s ??= new Settings();
        s.DisplayName = string.IsNullOrWhiteSpace(s.DisplayName)
            ? $"{Environment.UserName}@{Environment.MachineName}" : s.DisplayName;
        if (string.IsNullOrWhiteSpace(s.Group)) s.Group = "기본";
        if (string.IsNullOrWhiteSpace(s.DownloadDir)) s.DownloadDir = DefaultDownloadDir;
        if (s.TcpPort is < 1 or > 65535) s.TcpPort = 51001;
        s.RetentionDays = Math.Clamp(s.RetentionDays, 1, MaxRetentionDays);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort; a failed save should not crash the app */ }
    }

    // Reflect the AutoStart flag into the HKCU Run key. Opt-in only — written when
    // the user ticks the box in settings, removed when unticked.
    public void ApplyAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (AutoStart)
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { /* registry blocked (policy, permissions) -> silently skip */ }
    }
}
