using System.Runtime.InteropServices;
using System.Security;
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

/// <summary>
/// Autostart als Aufgabe „Juice“ in der Aufgabenplanung: bei der Anmeldung, nur für den eigenen
/// Benutzer, ohne Admin-Rechte. Der Run-Schlüssel reicht nicht: Windows 11 Build 26300 überspringt
/// dort den Eintrag beim Anmelden, auch mit Freigabe unter StartupApproved und ohne Meldung im Ereignisprotokoll.
/// </summary>
static class Autostart
{
    const string TaskName = "Juice";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const int CreateOrUpdate = 6, InteractiveToken = 3;

    static string ExePath => Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                dynamic task = TaskFolder().GetTask(TaskName);
                return (bool)task.Enabled && ((string)task.Xml).Contains(SecurityElement.Escape(ExePath));
            }
            catch (Exception ex) when (ex is FileNotFoundException or COMException)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        RemoveRunEntry();
        try
        {
            if (enabled)
                TaskFolder().RegisterTask(TaskName, TaskXml(), CreateOrUpdate, null, null, InteractiveToken, null);
            else
                TaskFolder().DeleteTask(TaskName, 0);
            Log.Write($"Autostart {(enabled ? "an" : "aus")}: {ExePath}");
        }
        catch (Exception ex) when (ex is FileNotFoundException or COMException)
        {
            Log.Write($"Autostart {(enabled ? "an" : "aus")} fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Version 0.2 trug sich unter Run ein. Steht der Eintrag noch da, zieht der Autostart in die Aufgabenplanung um.</summary>
    public static void MigrateFromRunKey()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(TaskName) is not null) Set(true);
    }

    static void RemoveRunEntry()
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        run.DeleteValue(TaskName, throwOnMissingValue: false);
        approved.DeleteValue(TaskName, throwOnMissingValue: false);
    }

    static dynamic TaskFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    // 10 s Verzögerung, damit die Taskleiste steht, bevor die Symbole kommen. Priorität 7 ist der
    // Standard der Aufgabenplanung (unter normal), für ein Tool im Hintergrund passend.
    static string TaskXml()
    {
        string user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>JUICE: Akkustand von Maus und Tastatur in der Taskleiste.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(ExePath)}</Command></Exec></Actions>
            </Task>
            """;
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
