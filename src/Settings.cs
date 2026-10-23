using System.Text.Json;
using Microsoft.Win32;

namespace Juice;

/// <summary>Einstellungen in %LOCALAPPDATA%\Juice\settings.json.</summary>
sealed class Settings
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Juice");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");

    /// <summary>Geräte-IDs, deren Symbol ausgeblendet ist.</summary>
    public HashSet<string> Hidden { get; set; } = [];

    /// <summary>Beim ersten Start wird der Autostart einmal eingeschaltet.</summary>
    public bool FirstRunDone { get; set; }

    public static Settings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex)
        {
            Log.Write($"Einstellungen nicht lesbar: {ex.Message}");
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Autostart über HKCU\...\Run, also nur für den eigenen Benutzer und ohne Admin-Rechte.</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Juice";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value == Command;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write($"Autostart {(enabled ? "an" : "aus")}: {Command}");
    }
}

static class Log
{
    static readonly string FilePath = Path.Combine(Settings.Folder, "juice.log");
    const long MaxBytes = 256 * 1024;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var lines = File.ReadAllLines(FilePath);
                File.WriteAllLines(FilePath, lines[(lines.Length / 2)..]);
            }
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Protokoll ist nur zur Fehlersuche da.
        }
    }
}
