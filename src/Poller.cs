using System.Diagnostics;

namespace Juice;

/// <summary>Fragt alle Quellen einmal ab. Läuft im Hintergrund-Thread.</summary>
static class Poller
{
    // Solange eines dieser Programme läuft, bleibt die Corsair-Schnittstelle ihm überlassen.
    static readonly (string Process, string Label)[] CorsairOwners = [("iCUE", "iCUE"), ("SignalRgb", "SignalRGB")];

    public static PollResult Poll()
    {
        string? blockedBy = CorsairOwners.FirstOrDefault(o => IsRunning(o.Process)).Label;

        var readings = new List<DeviceReading>();
        Safe("Corsair-Meldungen", () =>
        {
            CorsairBragi.RefreshListeners();
            return [];
        });
        // Läuft iCUE oder SignalRGB, nur deren Meldungen mitlesen statt selbst zu fragen.
        readings.AddRange(blockedBy is null ? Safe("Corsair", CorsairBragi.Read) : CorsairBragi.FromNotifications());
        Safe("Keychron", () =>
        {
            KeychronCodes.Refresh();
            return [];
        });
        readings.AddRange(Safe("Bluetooth", BluetoothBattery.Read));
        return new PollResult(readings, blockedBy);
    }

    static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    }

    static List<DeviceReading> Safe(string source, Func<List<DeviceReading>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Write($"{source}: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }
}
