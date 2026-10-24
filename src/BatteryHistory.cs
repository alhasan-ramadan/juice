using System.Text.Json;

namespace Juice;

/// <summary>
/// Merkt sich pro Gerät, bei welchem Akkustand wie viele Stunden der PC schon lief, und schätzt daraus
/// die Restlaufzeit. Gezählt wird nur Zeit, in der JUICE läuft: Ist der PC aus oder im Ruhezustand,
/// schlafen Maus und Tastatur meist auch. Kostet nichts extra, weil nur die ohnehin gemessenen Werte
/// ausgewertet werden; geschrieben wird nur, wenn sich ein Prozentwert ändert.
/// </summary>
sealed class BatteryHistory
{
    const int MinDrop = 2;
    const double MinHours = 1;
    const int MaxSamples = 100;
    // Messwerte wackeln um einen Prozentpunkt (die M75 springt etwa zwischen 42 und 43 %). Erst ein Sprung
    // um so viele Punkte nach oben gilt als Aufladen und beginnt die Messung neu.
    const int ChargeJump = 5;
    // Längere Pausen zwischen zwei Ticks (PC aus, Ruhezustand) zählen nicht als Laufzeit.
    static readonly TimeSpan MaxTickGap = TimeSpan.FromMinutes(10);
    static readonly string FilePath = Path.Combine(Settings.Folder, "verlauf.json");

    sealed record Sample(double Hours, int Percent);
    sealed record Stored(double Hours, Dictionary<string, List<Sample>> Devices);

    readonly Dictionary<string, List<Sample>> devices;
    double hours, savedHours;
    DateTime lastTick = DateTime.Now;
    bool dirty;

    BatteryHistory(double hours, Dictionary<string, List<Sample>> devices) =>
        (this.hours, savedHours, this.devices) = (hours, hours, devices);

    public static BatteryHistory Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) is { } stored)
                return new BatteryHistory(stored.Hours, stored.Devices);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Verlauf nicht lesbar: {ex.Message}");
        }
        return new BatteryHistory(0, []);
    }

    /// <summary>Zählt die Laufzeit weiter; vom Abfrage-Takt aufgerufen.</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        if (now - lastTick <= MaxTickGap)
            hours += (now - lastTick).TotalHours;
        lastTick = now;
        // Etwa viertelstündlich mitspeichern, damit ein Neustart nicht die gezählte Laufzeit verliert.
        dirty |= hours - savedHours >= 0.25;
    }

    /// <summary>
    /// Nimmt einen Messwert auf; gespeichert wird nur ein neuer Tiefstwert. Laden oder ein deutlich höherer
    /// Wert als zuletzt beginnt den Verlauf neu.
    /// </summary>
    public void Add(string id, int percent, bool charging)
    {
        Tick();
        if (charging)
        {
            dirty |= devices.Remove(id);
            return;
        }
        if (!devices.TryGetValue(id, out var list))
            devices[id] = list = [];
        if (list.Count > 0 && percent >= list[^1].Percent + ChargeJump)
            list.Clear();
        if (list.Count > 0 && percent >= list[^1].Percent)
            return;
        list.Add(new Sample(hours, percent));
        if (list.Count > MaxSamples)
            list.RemoveRange(0, list.Count - MaxSamples);
        dirty = true;
    }

    /// <summary>Restlaufzeit bei laufendem PC, null solange zu wenig gemessen ist.</summary>
    public TimeSpan? Remaining(string id, int percent)
    {
        if (!devices.TryGetValue(id, out var list) || list.Count < 2)
            return null;
        int drop = list[0].Percent - list[^1].Percent;
        double span = list[^1].Hours - list[0].Hours;
        if (drop < MinDrop || span < MinHours)
            return null;
        return TimeSpan.FromHours(percent * span / drop);
    }

    public static string Describe(TimeSpan remaining) => remaining.TotalHours switch
    {
        < 1 => "noch unter 1 Std.",
        < 48 => $"noch etwa {Math.Round(remaining.TotalHours)} Std.",
        _ => $"noch etwa {Math.Round(remaining.TotalDays)} Tage",
    };

    public void Save()
    {
        if (!dirty)
            return;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(hours, devices)));
            (dirty, savedHours) = (false, hours);
        }
        catch (IOException ex)
        {
            Log.Write($"Verlauf nicht speicherbar: {ex.Message}");
        }
    }
}
