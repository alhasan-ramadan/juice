using System.Text.RegularExpressions;

namespace Juice;

/// <summary>
/// Akkustände gekoppelter Bluetooth-Geräte, so wie Windows sie in den Einstellungen zeigt.
/// Windows legt den Wert als Geräteeigenschaft an einem der Bluetooth-Knoten des Geräts ab.
/// </summary>
static partial class BluetoothBattery
{
    static readonly string[] Enumerators = ["BTHENUM", "BTHLE", "BTHLEDEVICE"];

    static readonly Native.DevPropKey BatteryLevel = new(new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    static readonly Native.DevPropKey IsConnected = new(new Guid("83da6326-97a6-4088-9453-a1923f573b29"), 15);
    static readonly Native.DevPropKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static readonly Native.DevPropKey ClassOfDevice = new(new Guid("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), 4);

    // Bluetooth-Adresse in der Instanz-ID, aber nicht der letzte Block einer GUID.
    [GeneratedRegex(@"(?<![0-9A-F-])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    public static List<DeviceReading> Read()
    {
        var rootNodes = new Dictionary<string, string>();   // Adresse -> Instanz-ID von BTHENUM\DEV_ bzw. BTHLE\DEV_
        var levels = new Dictionary<string, int>();         // Adresse -> Akkustand in %

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in Native.PresentDeviceIds(enumerator))
            {
                var match = AddressPattern().Match(deviceId);
                if (!match.Success)
                    continue;
                string address = match.Groups[1].Value.ToUpperInvariant();

                if (deviceId.StartsWith(enumerator + @"\DEV_", StringComparison.OrdinalIgnoreCase))
                    rootNodes[address] = deviceId;

                if (Level(deviceId) is int level)
                    levels[address] = level;
            }
        }

        var readings = new List<DeviceReading>();
        foreach (var (address, rootId) in rootNodes)
        {
            var connected = Native.DeviceProperty(rootId, IsConnected, out uint type);
            if (type != Native.DevPropTypeBoolean || connected is not [not 0, ..])
                continue;

            string name = Native.DeviceStringProperty(rootId, FriendlyName) ?? address;
            string id = $"bt-{address}";
            var kind = KindOf(rootId);
            if (levels.TryGetValue(address, out int percent))
                readings.Add(new DeviceReading(id, name, "Bluetooth", percent, ChargeState.Unknown, Kind: kind));
            else if (KeychronCodes.TryGet(address, out int coded, out bool charging, out var measuredAt))
                readings.Add(new DeviceReading(id, name, "Bluetooth", coded,
                    charging ? ChargeState.Charging : ChargeState.Discharging, MeasuredAt: measuredAt, Kind: DeviceKind.Keyboard));
            else
                readings.Add(new DeviceReading(id, name, "Bluetooth", null, ChargeState.Unknown, "meldet keinen Akkustand", Kind: kind));
        }
        return readings;
    }

    /// <summary>Geräteart aus der Bluetooth-Geräteklasse (Class of Device), soweit Windows sie kennt.</summary>
    static DeviceKind KindOf(string rootId)
    {
        var data = Native.DeviceProperty(rootId, ClassOfDevice, out _);
        if (data is not { Length: >= 4 })
            return DeviceKind.Other;
        int cod = BitConverter.ToInt32(data, 0);
        int major = (cod >> 8) & 0x1F;
        int minor = cod & 0xFC;
        return major switch
        {
            0x04 => DeviceKind.Headset,                              // Audio/Video
            0x05 when (minor & 0x40) != 0 => DeviceKind.Keyboard,    // Peripherie: Tastatur (auch Kombi)
            0x05 when (minor & 0x80) != 0 => DeviceKind.Mouse,       // Peripherie: Zeigegerät
            0x05 when (minor & 0x3C) is 0x04 or 0x08 => DeviceKind.Gamepad,
            _ => DeviceKind.Other,
        };
    }

    static int? Level(string deviceId)
    {
        var data = Native.DeviceProperty(deviceId, BatteryLevel, out _);
        if (data is null || data.Length == 0)
            return null;
        int value = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : data[0];
        return value is >= 0 and <= 100 ? value : null;
    }
}
