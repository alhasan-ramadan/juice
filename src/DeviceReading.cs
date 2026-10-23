namespace Juice;

enum ChargeState { Unknown, Discharging, Charging, Full }

enum DeviceKind { Other, Mouse, Keyboard, Headset, Gamepad }

/// <summary>
/// Ein Messwert für ein Gerät. Percent ist null, wenn das Gerät keinen Akkustand liefert.
/// MeasuredAt ist gesetzt, wenn der Wert nicht gerade eben gemessen wurde (Keychron meldet nur beim Tippen).
/// </summary>
sealed record DeviceReading(string Id, string Name, string Source, int? Percent, ChargeState State, string? Note = null,
    DateTime? MeasuredAt = null, DeviceKind Kind = DeviceKind.Other)
{
    public string StateText => State switch
    {
        ChargeState.Discharging => "entlädt",
        ChargeState.Charging => "lädt",
        ChargeState.Full => "voll",
        _ => "",
    };
}

sealed record PollResult(List<DeviceReading> Readings, string? CorsairBlockedBy);
