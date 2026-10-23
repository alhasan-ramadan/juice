using System.Collections.Concurrent;
using System.Text.Json;

namespace Juice;

/// <summary>
/// Liest Akkustände von Corsair-Geräten über das Bragi-Protokoll (iCUE 4/5, SLIPSTREAM-Empfänger).
/// Protokoll wie in OpenLinkHub und im Corsair-Plugin von SignalRGB:
///   Anfrage  [0x00, 0x08 | Gerät, 0x02, Eigenschaft (LE16), 0 ...]   auf der Befehls-Schnittstelle (0xFF42/0x01)
///   Antwort  [0x00, Gerät, 0x02, Status, Wert (LE32) ...]
///   Meldung  [0x00, Gerät, 0x01, Eigenschaft (LE16), Wert (LE32)]    auf der Melde-Schnittstelle (0xFF42/0x02)
/// Gerät 0 ist der Empfänger bzw. das Kabelgerät, 1..7 sind gekoppelte Funkgeräte.
///
/// Zwei Wege:
///  - Read(): fragt aktiv ab (nur Lesebefehle). Nur wenn kein anderes Programm die Befehls-Schnittstelle nutzt.
///  - Meldungen: Die Geräte melden Akkuänderungen von selbst. Die Melde-Schnittstelle sendet nur, mitlesen
///    stört niemanden. So gibt es Werte auch, während iCUE oder SignalRGB laufen.
/// </summary>
static class CorsairBragi
{
    const ushort VendorUsagePage = 0xFF42;
    const ushort CommandUsage = 0x01;
    const ushort NotifyUsage = 0x02;
    const byte CmdNotification = 0x01;
    const byte CmdGetProperty = 0x02;

    const ushort PropBatteryLevel = 0x0F;   // 0..1000
    const ushort PropBatteryStatus = 0x10;  // 1 lädt, 2 entlädt, 3 voll
    const ushort PropProductId = 0x12;
    const ushort PropSubdevices = 0x36;     // Bitmaske verbundener Funkgeräte (Bit 1..7)

    static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(600);

    static readonly Dictionary<int, (string Name, DeviceKind Kind)> KnownDevices = new()
    {
        [0x2B08] = ("Corsair M75 Wireless", DeviceKind.Mouse),
    };

    sealed record Notified(int? Level, int? Status, DateTime At);

    // Schlüssel jeweils "Seriennummer des Empfängers:Gerät".
    static readonly ConcurrentDictionary<string, Notified> notified = new();
    static readonly ConcurrentDictionary<string, Thread> listeners = new();
    static readonly string ProductIdFile = Path.Combine(Settings.Folder, "corsair-geraete.json");
    static readonly ConcurrentDictionary<string, int> productIds = LoadProductIds();

    /// <summary>Ein Gerät hat einen neuen Akkustand gemeldet (wird im Lese-Thread ausgelöst).</summary>
    public static event Action? Changed;

    /// <summary>Aktive Abfrage aller Corsair-Geräte.</summary>
    public static List<DeviceReading> Read()
    {
        var readings = new List<DeviceReading>();
        foreach (var (path, caps) in Interfaces(CommandUsage))
        {
            if (ReceiverSerial(path) is not { } serial)
                continue;
            using var session = Session.Open(path, caps.InputReportByteLength, caps.OutputReportByteLength);
            if (session is null)
            {
                Log.Write($"Corsair: Schnittstelle nicht zu öffnen: {path}");
                continue;
            }

            if (session.Get(0, PropSubdevices) is int mask)
            {
                for (int index = 1; index < 8; index++)
                    if ((mask & (1 << index)) != 0 && ReadDevice(session, serial, index) is { } reading)
                        readings.Add(reading);
            }
            else if (ReadDevice(session, serial, 0) is { Percent: not null } wired)
            {
                readings.Add(wired);
            }
        }
        return readings;
    }

    /// <summary>Werte aus den Meldungen der Geräte, für die Zeit, in der iCUE oder SignalRGB laufen.</summary>
    public static List<DeviceReading> FromNotifications() =>
        notified.Where(n => n.Value.Level is not null)
            .Select(n => new DeviceReading(IdOf(n.Key), NameOf(n.Key), "Funk", Percent(n.Value.Level!.Value),
                StateOf(n.Value.Status), MeasuredAt: n.Value.At, Kind: KindOf(n.Key)))
            .ToList();

    /// <summary>Startet Lese-Threads für neu angeschlossene Melde-Schnittstellen.</summary>
    public static void RefreshListeners()
    {
        foreach (var (path, caps) in Interfaces(NotifyUsage))
        {
            if (listeners.TryGetValue(path, out var running) && running.IsAlive)
                continue;
            if (ReceiverSerial(path) is not { } serial)
                continue;
            var thread = new Thread(() => Listen(path, serial, caps.InputReportByteLength)) { IsBackground = true, Name = "Corsair " + serial };
            listeners[path] = thread;
            thread.Start();
        }
    }

    static void Listen(string path, string serial, int reportLength)
    {
        using var handle = Native.CreateFile(path, Native.GenericRead, Native.ShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return;
        Log.Write($"Corsair {serial}: lausche auf Meldungen");
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 0, isAsync: false);
            var report = new byte[reportLength];
            while (stream.Read(report, 0, report.Length) >= 9)
            {
                if (report[2] != CmdNotification)
                    continue;
                int index = report[1];
                int property = report[3] | (report[4] << 8);
                int value = report[5] | (report[6] << 8) | (report[7] << 16) | (report[8] << 24);
                OnNotification(serial, index, property, value);
            }
        }
        catch (IOException)
        {
            // Empfänger abgezogen; RefreshListeners startet beim Wiederkommen neu.
        }
        finally
        {
            Log.Write($"Corsair {serial}: Meldungen beendet");
        }
    }

    static void OnNotification(string serial, int index, int property, int value)
    {
        string key = $"{serial}:{index}";
        switch (property)
        {
            case PropBatteryLevel:
                notified.AddOrUpdate(key, _ => new Notified(value, null, DateTime.Now), (_, old) => old with { Level = value, At = DateTime.Now });
                Log.Write($"{NameOf(key)}: meldet {Percent(value)} %");
                Changed?.Invoke();
                break;
            case PropBatteryStatus:
                notified.AddOrUpdate(key, _ => new Notified(null, value, DateTime.Now), (_, old) => old with { Status = value, At = DateTime.Now });
                Changed?.Invoke();
                break;
            case PropSubdevices when index == 0:
                // Ein Funkgerät hat sich vom Empfänger getrennt: seinen gemeldeten Wert verwerfen.
                foreach (var gone in notified.Keys.Where(k => k.StartsWith(serial + ":") && (value & (1 << int.Parse(k[(serial.Length + 1)..]))) == 0))
                    notified.TryRemove(gone, out _);
                break;
        }
    }

    static DeviceReading? ReadDevice(Session session, string serial, int index)
    {
        if (session.Get(index, PropProductId) is not int pid)
            return null;
        string key = $"{serial}:{index}";
        if (!productIds.TryGetValue(key, out int known) || known != pid)
        {
            productIds[key] = pid;
            SaveProductIds();
        }

        if (session.Get(index, PropBatteryLevel) is not int level)
            return new DeviceReading(IdOf(key), NameOf(key), "Funk", null, ChargeState.Unknown, "antwortet nicht (schläft?)", Kind: KindOf(key));
        return new DeviceReading(IdOf(key), NameOf(key), "Funk", Percent(level), StateOf(session.Get(index, PropBatteryStatus)), Kind: KindOf(key));
    }

    static IEnumerable<(string Path, Native.HidpCaps Caps)> Interfaces(ushort usage)
    {
        foreach (var path in Native.HidInterfacePaths())
            if (path.Contains("vid_1b1c", StringComparison.OrdinalIgnoreCase)
                && Native.HidCaps(path) is { UsagePage: VendorUsagePage } caps && caps.Usage == usage)
                yield return (path, caps);
    }

    /// <summary>Seriennummer des USB-Geräts (Empfänger) über HID-Knoten -> USB-Schnittstelle -> USB-Gerät.</summary>
    static string? ReceiverSerial(string interfacePath)
    {
        var hid = Native.InstanceIdOf(interfacePath);
        var usbInterface = hid is null ? null : Native.ParentOf(hid);
        var usbDevice = usbInterface is null ? null : Native.ParentOf(usbInterface);
        return usbDevice?[(usbDevice.LastIndexOf('\\') + 1)..];
    }

    static string IdOf(string key) => "corsair-" + key.Replace(':', '-');

    static string NameOf(string key) =>
        !productIds.TryGetValue(key, out int pid) ? "Corsair-Funkgerät"
        : KnownDevices.TryGetValue(pid, out var known) ? known.Name
        : $"Corsair-Gerät {pid:X4}";

    static DeviceKind KindOf(string key) =>
        productIds.TryGetValue(key, out int pid) && KnownDevices.TryGetValue(pid, out var known) ? known.Kind : DeviceKind.Other;

    static int Percent(int level) => Math.Clamp((int)Math.Round(level / 10.0), 0, 100);

    static ChargeState StateOf(int? status) => status switch
    {
        1 => ChargeState.Charging,
        2 => ChargeState.Discharging,
        3 => ChargeState.Full,
        _ => ChargeState.Unknown,
    };

    static ConcurrentDictionary<string, int> LoadProductIds()
    {
        try
        {
            if (File.Exists(ProductIdFile) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ProductIdFile)) is { } saved)
                return new ConcurrentDictionary<string, int>(saved);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Corsair-Geräteliste nicht lesbar: {ex.Message}");
        }
        return new();
    }

    static void SaveProductIds()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(ProductIdFile, JsonSerializer.Serialize(productIds.ToDictionary(p => p.Key, p => p.Value)));
        }
        catch (IOException)
        {
            // Nur für den Gerätenamen beim nächsten Start.
        }
    }

    sealed class Session : IDisposable
    {
        readonly FileStream stream;
        readonly int inputLength;
        readonly int outputLength;

        Session(FileStream stream, int inputLength, int outputLength)
        {
            this.stream = stream;
            this.inputLength = inputLength;
            this.outputLength = outputLength;
        }

        public static Session? Open(string path, int inputLength, int outputLength)
        {
            var handle = Native.CreateFile(path, Native.GenericRead | Native.GenericWrite, Native.ShareReadWrite,
                IntPtr.Zero, Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            return new Session(new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true), inputLength, outputLength);
        }

        /// <summary>Liest eine Eigenschaft; null bei Fehlerstatus oder ohne Antwort.</summary>
        public int? Get(int index, ushort property)
        {
            var request = new byte[outputLength];
            request[1] = (byte)(0x08 | index);
            request[2] = CmdGetProperty;
            request[3] = (byte)(property & 0xFF);
            request[4] = (byte)(property >> 8);
            stream.Write(request, 0, request.Length);

            var deadline = DateTime.UtcNow + ReplyTimeout;
            var reply = new byte[inputLength];
            while (DateTime.UtcNow < deadline)
            {
                using var timeout = new CancellationTokenSource(deadline - DateTime.UtcNow);
                int read;
                try
                {
                    read = stream.ReadAsync(reply.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                // Andere Pakete (z. B. Ereignismeldungen des Geräts) überspringen.
                if (read >= 8 && reply[1] == index && reply[2] == CmdGetProperty)
                    return reply[3] == 0 ? reply[4] | (reply[5] << 8) | (reply[6] << 16) | (reply[7] << 24) : null;
            }
            return null;
        }

        public void Dispose() => stream.Dispose();
    }
}
