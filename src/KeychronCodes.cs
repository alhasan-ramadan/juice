using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Juice;

/// <summary>
/// Liest den Akkustand von Keychron-Tastaturen mit der JUICE-Firmware (siehe firmware\).
/// Die Tastatur meldet ihn über Bluetooth als unbelegte Medientaste:
///   0x300 + Prozent = Akku, 0x380 + Prozent = Kabel steckt (lädt).
/// Dafür läuft pro Medientasten-Collection ein Lese-Thread. Windows erlaubt dort geteiltes Lesen,
/// anders als bei den Tastatur-Collections.
/// </summary>
static partial class KeychronCodes
{
    const ushort ConsumerPage = 0x0C;
    const ushort ConsumerControl = 0x01;
    const int CodeBase = 0x300;
    const int CodeCharging = 0x80;

    sealed record Value(int Percent, bool Charging, DateTime MeasuredAt);

    static readonly ConcurrentDictionary<string, Value> values = Load();
    static readonly ConcurrentDictionary<string, Thread> listeners = new();
    static readonly string FilePath = Path.Combine(Settings.Folder, "keychron-akku.json");

    /// <summary>Ein neuer Wert ist angekommen (wird im Lese-Thread ausgelöst).</summary>
    public static event Action? Changed;

    [GeneratedRegex(@"(?<![0-9A-F-])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usages,
        ref uint usageLength, IntPtr preparsedData, byte[] report, uint reportLength);

    /// <summary>Letzter gemeldeter Wert für eine Bluetooth-Adresse.</summary>
    public static bool TryGet(string address, out int percent, out bool charging, out DateTime measuredAt)
    {
        if (values.TryGetValue(address, out var value))
        {
            (percent, charging, measuredAt) = (value.Percent, value.Charging, value.MeasuredAt);
            return true;
        }
        (percent, charging, measuredAt) = (0, false, default);
        return false;
    }

    /// <summary>Startet Lese-Threads für neu verbundene Keychron-Bluetooth-Tastaturen.</summary>
    public static void Refresh()
    {
        foreach (var path in Native.HidInterfacePaths())
        {
            // Bluetooth-HID (Dienst 0x1124) mit Keychron-Hersteller-ID 0x3434.
            if (!path.Contains("{00001124-0000-1000-8000-00805f9b34fb}_vid&00023434", StringComparison.OrdinalIgnoreCase))
                continue;
            if (listeners.TryGetValue(path, out var running) && running.IsAlive)
                continue;
            if (Native.HidCaps(path) is not { UsagePage: ConsumerPage, Usage: ConsumerControl } caps)
                continue;
            if (AddressOf(path) is not { } address)
                continue;

            var thread = new Thread(() => Listen(path, address, caps.InputReportByteLength)) { IsBackground = true, Name = "Keychron " + address };
            listeners[path] = thread;
            thread.Start();
        }
    }

    static void Listen(string path, string address, int reportLength)
    {
        using var handle = Native.CreateFile(path, Native.GenericRead, Native.ShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var preparsed))
            return;
        Log.Write($"Keychron {address}: lausche auf Akku-Codes");
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 0, isAsync: false);
            var report = new byte[reportLength];
            var usages = new ushort[8];
            while (stream.Read(report, 0, report.Length) > 0)
            {
                uint count = (uint)usages.Length;
                if (HidP_GetUsages(0, ConsumerPage, 0, usages, ref count, preparsed, report, (uint)report.Length) != Native.HidpStatusSuccess)
                    continue;
                for (int i = 0; i < count; i++)
                    if (usages[i] is >= CodeBase and <= CodeBase + CodeCharging + 100 && (usages[i] - CodeBase) % CodeCharging <= 100)
                        Store(address, usages[i]);
            }
        }
        catch (IOException)
        {
            // Verbindung getrennt; Refresh startet beim nächsten Verbinden neu.
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
            Log.Write($"Keychron {address}: Verbindung beendet");
        }
    }

    static void Store(string address, ushort code)
    {
        int offset = code - CodeBase;
        var value = new Value(offset % CodeCharging, offset >= CodeCharging, DateTime.Now);
        values[address] = value;
        Log.Write($"Keychron {address}: {value.Percent} %{(value.Charging ? " (lädt)" : "")}");
        Changed?.Invoke();
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(values.ToDictionary(p => p.Key, p => p.Value)));
        }
        catch (IOException)
        {
            // Nur der Merker für den nächsten Start.
        }
    }

    static ConcurrentDictionary<string, Value> Load()
    {
        try
        {
            var path = Path.Combine(Settings.Folder, "keychron-akku.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Value>>(File.ReadAllText(path)) is { } saved)
                return new ConcurrentDictionary<string, Value>(saved);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Keychron-Werte nicht lesbar: {ex.Message}");
        }
        return new();
    }

    /// <summary>Bluetooth-Adresse aus dem übergeordneten BTHENUM-Knoten der HID-Collection.</summary>
    static string? AddressOf(string interfacePath)
    {
        var instanceId = Native.InstanceIdOf(interfacePath);
        var parent = instanceId is null ? null : Native.ParentOf(instanceId);
        var match = parent is null ? Match.Empty : AddressPattern().Match(parent);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Lauscht eine Zeit lang und gibt jeden Akku-Code aus (zum Testen).</summary>
    public static void Watch(TextWriter output, TimeSpan duration)
    {
        output.WriteLine($"Lausche {duration.TotalSeconds:0} s auf Keychron-Akku-Codes …");
        var seen = new Dictionary<string, DateTime>();
        var end = DateTime.Now + duration;
        var nextRefresh = DateTime.MinValue;
        while (DateTime.Now < end)
        {
            if (DateTime.Now >= nextRefresh)
            {
                int before = listeners.Count;
                Refresh();
                if (listeners.Count != before)
                    output.WriteLine($"{DateTime.Now:HH:mm:ss} Keychron per Bluetooth gefunden, lausche");
                nextRefresh = DateTime.Now.AddSeconds(2);
            }
            foreach (var (address, value) in values)
            {
                if (seen.TryGetValue(address, out var at) && at == value.MeasuredAt)
                    continue;
                seen[address] = value.MeasuredAt;
                output.WriteLine($"{value.MeasuredAt:HH:mm:ss} {address}: {value.Percent} %{(value.Charging ? " (lädt)" : "")}");
            }
            Thread.Sleep(250);
        }
    }
}
