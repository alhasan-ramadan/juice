using System.Text.Json;
using Microsoft.Win32;

namespace Juice;

/// <summary>
/// Ein Taskleisten-Symbol pro Gerät mit Akkustand. Alle Symbole teilen sich ein Menü.
/// Gibt es kein sichtbares Gerät, hält ein Platzhalter-Symbol das Menü erreichbar.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    const int LowThreshold = 20;
    const int CriticalThreshold = 10;
    const int RearmAbove = 25;
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    readonly Settings settings = Settings.Load();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = (int)Interval.TotalMilliseconds };
    readonly System.Windows.Forms.Timer listenerTimer = new() { Interval = 15_000 };
    readonly NotifyIcon placeholder;
    readonly Dictionary<string, Slot> slots = [];
    List<DeviceReading> withoutBattery = [];
    string? corsairBlockedBy;
    string savedState = "";
    bool polling;

    static readonly string StateFile = Path.Combine(Settings.Folder, "letzte-werte.json");

    /// <summary>Letzter Stand eines Geräts, damit es nach einem Neustart gleich (grau) wieder da ist.</summary>
    sealed record SavedReading(string Id, string Name, string Source, int Percent, ChargeState State, DateTime MeasuredAt,
        DeviceKind Kind = DeviceKind.Other);

    sealed class Slot(NotifyIcon icon, DeviceReading reading)
    {
        public NotifyIcon Icon { get; } = icon;
        public DeviceReading Reading { get; set; } = reading;
        public DateTime Updated { get; set; } = DateTime.Now;
        public string? StaleNote { get; set; }
        public string? RenderedKey { get; set; }
        public bool WarnedLow { get; set; }
        public bool WarnedCritical { get; set; }
    }

    public TrayApp()
    {
        if (!settings.FirstRunDone)
        {
            Autostart.Set(true);
            settings.FirstRunDone = true;
            settings.Save();
        }
        else
        {
            Autostart.MigrateFromRunKey();
        }

        menu.Opening += (_, _) => BuildMenu();

        placeholder = NewIcon();
        placeholder.Icon = IconRenderer.Render(null, IconTone.Stale, DeviceKind.Other);
        placeholder.Text = "JUICE: suche Geräte …";
        placeholder.Visible = true;
        RestoreSlots();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();

        // Keychron und Corsair melden sich von selbst: neue Werte sofort anzeigen und
        // frisch verbundene Geräte nach Sekunden statt Minuten finden.
        var ui = SynchronizationContext.Current;
        KeychronCodes.Changed += () => ui?.Post(async _ => await RefreshAsync(), null);
        CorsairBragi.Changed += () => ui?.Post(async _ => await RefreshAsync(), null);
        listenerTimer.Tick += (_, _) => Task.Run(() =>
        {
            try
            {
                KeychronCodes.Refresh();
                CorsairBragi.RefreshListeners();
            }
            catch (Exception ex)
            {
                Log.Write($"Lauscher: {ex.GetType().Name}: {ex.Message}");
            }
        });
        listenerTimer.Start();

        var firstPoll = new System.Windows.Forms.Timer { Interval = 300 };
        firstPoll.Tick += async (_, _) =>
        {
            firstPoll.Dispose();
            await RefreshAsync();
        };
        firstPoll.Start();
    }

    NotifyIcon NewIcon()
    {
        var icon = new NotifyIcon { ContextMenuStrip = menu };
        icon.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await RefreshAsync();
        };
        return icon;
    }

    async Task RefreshAsync()
    {
        if (polling)
            return;
        polling = true;
        try
        {
            Apply(await Task.Run(Poller.Poll));
        }
        catch (Exception ex)
        {
            Log.Write($"Abfrage fehlgeschlagen: {ex}");
        }
        finally
        {
            polling = false;
        }
    }

    void Apply(PollResult result)
    {
        if (result.CorsairBlockedBy != corsairBlockedBy)
            Log.Write(result.CorsairBlockedBy is null ? "Corsair-Abfrage aktiv" : $"Corsair pausiert: {result.CorsairBlockedBy} läuft");
        corsairBlockedBy = result.CorsairBlockedBy;

        var seen = new HashSet<string>();
        withoutBattery = [];
        foreach (var reading in result.Readings)
        {
            if (reading.Percent is null)
            {
                if (slots.TryGetValue(reading.Id, out var known))
                {
                    known.StaleNote = reading.Note;
                    seen.Add(reading.Id);
                }
                else
                {
                    withoutBattery.Add(reading);
                }
                continue;
            }

            seen.Add(reading.Id);
            if (!slots.TryGetValue(reading.Id, out var slot))
            {
                slot = new Slot(NewIcon(), reading);
                slots[reading.Id] = slot;
                Log.Write($"Gerät gefunden: {reading.Name} ({reading.Source}), {reading.Percent} % {reading.StateText}");
            }
            else if (slot.Reading.Percent != reading.Percent || slot.Reading.State != reading.State)
            {
                Log.Write($"{reading.Name}: {reading.Percent} % {reading.StateText}");
            }
            slot.Reading = reading;
            slot.Updated = reading.MeasuredAt ?? DateTime.Now;
            slot.StaleNote = null;
            CheckWarnings(slot);
        }

        foreach (var (id, slot) in slots)
        {
            if (seen.Contains(id))
                continue;
            slot.StaleNote = id.StartsWith("corsair-") && corsairBlockedBy is not null
                ? $"pausiert, solange {corsairBlockedBy} läuft"
                : "nicht verbunden";
        }

        foreach (var slot in slots.Values)
            Draw(slot);
        UpdatePlaceholder();
        SaveSlots();
    }

    void Draw(Slot slot, bool force = false)
    {
        var reading = slot.Reading;
        var tone = slot.StaleNote is not null ? IconTone.Stale
            : reading.State is ChargeState.Charging or ChargeState.Full ? IconTone.Charging
            : reading.Percent <= LowThreshold ? IconTone.Low
            : IconTone.Normal;

        // Symbol nur neu zeichnen, wenn sich Zahl oder Farbe geändert hat.
        string key = $"{reading.Percent}|{tone}|{reading.Kind}";
        if (force || slot.RenderedKey != key)
        {
            var previous = slot.Icon.Icon;
            slot.Icon.Icon = IconRenderer.Render(reading.Percent, tone, reading.Kind);
            previous?.Dispose();
            slot.RenderedKey = key;
        }
        slot.Icon.Text = Truncate(Describe(slot), 127);
        slot.Icon.Visible = !settings.Hidden.Contains(reading.Id);
    }

    void RestoreSlots()
    {
        try
        {
            if (!File.Exists(StateFile) || JsonSerializer.Deserialize<List<SavedReading>>(File.ReadAllText(StateFile)) is not { } saved)
                return;
            foreach (var s in saved)
            {
                var slot = new Slot(NewIcon(), new DeviceReading(s.Id, s.Name, s.Source, s.Percent, s.State, Kind: s.Kind))
                {
                    Updated = s.MeasuredAt,
                    StaleNote = "noch keine neue Messung",
                };
                slots[s.Id] = slot;
                Draw(slot);
            }
            UpdatePlaceholder();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Letzte Werte nicht lesbar: {ex.Message}");
        }
    }

    void SaveSlots()
    {
        var state = JsonSerializer.Serialize(slots.Values
            .Where(s => s.Reading.Percent is not null)
            .Select(s => new SavedReading(s.Reading.Id, s.Reading.Name, s.Reading.Source, s.Reading.Percent!.Value, s.Reading.State, s.Updated, s.Reading.Kind))
            .OrderBy(s => s.Id));
        if (state == savedState)
            return;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(StateFile, state);
            savedState = state;
        }
        catch (IOException ex)
        {
            Log.Write($"Letzte Werte nicht speicherbar: {ex.Message}");
        }
    }

    static string Describe(Slot slot)
    {
        var reading = slot.Reading;
        string state = reading.StateText.Length > 0 ? $", {reading.StateText}" : "";
        string status = slot.StaleNote is null
            ? $"Stand {slot.Updated:HH:mm}"
            : $"{slot.StaleNote}, zuletzt {slot.Updated:HH:mm}";
        return $"{reading.Name}\n{reading.Percent} %{state}\n{status}";
    }

    void UpdatePlaceholder()
    {
        placeholder.Visible = !slots.Values.Any(s => s.Icon.Visible);
        placeholder.Text = Truncate(
            slots.Count > 0 ? "JUICE: alle Geräte ausgeblendet"
            : corsairBlockedBy is not null ? $"JUICE: Corsair pausiert, solange {corsairBlockedBy} läuft"
            : "JUICE: kein Gerät mit Akkustand gefunden", 127);
    }

    void CheckWarnings(Slot slot)
    {
        var reading = slot.Reading;
        if (reading.Percent is not int percent)
            return;

        bool charging = reading.State is ChargeState.Charging or ChargeState.Full;
        if (charging || percent > RearmAbove)
            slot.WarnedLow = slot.WarnedCritical = false;
        if (charging)
            return;

        if (percent <= CriticalThreshold && !slot.WarnedCritical)
        {
            slot.WarnedCritical = slot.WarnedLow = true;
            Notify(slot, $"{reading.Name}: nur noch {percent} %", "Bitte bald aufladen.", ToolTipIcon.Warning);
        }
        else if (percent <= LowThreshold && !slot.WarnedLow)
        {
            slot.WarnedLow = true;
            Notify(slot, $"{reading.Name}: Akku bei {percent} %", "Der Akku wird schwach.", ToolTipIcon.Info);
        }
    }

    void Notify(Slot slot, string title, string text, ToolTipIcon icon)
    {
        // Die Meldung braucht ein sichtbares Symbol; ausgeblendete Geräte melden sich über ein anderes.
        var target = slot.Icon.Visible ? slot.Icon
            : slots.Values.FirstOrDefault(s => s.Icon.Visible)?.Icon ?? placeholder;
        if (!target.Visible)
            target = placeholder;
        placeholder.Visible |= target == placeholder;
        target.ShowBalloonTip(10_000, title, text, icon);
        Log.Write($"Warnung: {title}");
    }

    void BuildMenu()
    {
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToList())
            item.Dispose();
        menu.Items.Clear();

        foreach (var slot in slots.Values.OrderBy(s => s.Reading.Name))
        {
            var reading = slot.Reading;
            string state = reading.StateText.Length > 0 ? $" ({reading.StateText})" : "";
            string note = slot.StaleNote is null ? "" : $", {slot.StaleNote}";
            var item = new ToolStripMenuItem($"{reading.Name}: {reading.Percent} %{state}{note}")
            {
                Checked = !settings.Hidden.Contains(reading.Id),
                ToolTipText = "Symbol in der Taskleiste ein- oder ausblenden",
            };
            item.Click += (_, _) => ToggleHidden(reading.Id);
            menu.Items.Add(item);
        }
        foreach (var reading in withoutBattery.OrderBy(r => r.Name))
            menu.Items.Add(new ToolStripMenuItem($"{reading.Name}: {reading.Note}") { Enabled = false });
        if (slots.Count == 0 && withoutBattery.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("Kein Gerät gefunden") { Enabled = false });
        if (corsairBlockedBy is not null)
            menu.Items.Add(new ToolStripMenuItem($"Corsair pausiert, solange {corsairBlockedBy} läuft") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Jetzt aktualisieren", null, async (_, _) => await RefreshAsync());
        var autostart = new ToolStripMenuItem("Mit Windows starten") { Checked = Autostart.IsEnabled };
        autostart.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled);
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitThread());
    }

    void ToggleHidden(string id)
    {
        if (!settings.Hidden.Remove(id))
            settings.Hidden.Add(id);
        settings.Save();
        if (slots.TryGetValue(id, out var slot))
            slot.Icon.Visible = !settings.Hidden.Contains(id);
        UpdatePlaceholder();
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Hell/Dunkel der Taskleiste geändert: Symbole neu zeichnen.
        if (e.Category == UserPreferenceCategory.General)
            foreach (var slot in slots.Values)
                Draw(slot, force: true);
    }

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    protected override void ExitThreadCore()
    {
        timer.Stop();
        listenerTimer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        foreach (var slot in slots.Values)
        {
            slot.Icon.Visible = false;
            slot.Icon.Icon?.Dispose();
            slot.Icon.Dispose();
        }
        placeholder.Visible = false;
        placeholder.Icon?.Dispose();
        placeholder.Dispose();
        Log.Write("Beendet");
        base.ExitThreadCore();
    }
}
