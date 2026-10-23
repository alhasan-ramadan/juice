# Sichert oder schreibt die VIA-Belegung der Keychron K8 Pro (per USB-Kabel, Schalter auf "Cable").
#   Sichern:        .\via-belegung.ps1 -Sichern belegung.json
#   Zurückschreiben: .\via-belegung.ps1 -Zurueck belegung.json [-NurLicht]
# Gesichert werden Tastenbelegung aller Ebenen, Makros, Layout-Optionen und RGB-Einstellungen.
# -NurLicht schreibt nur die RGB-Einstellungen zurück, z. B. nach einem Firmware-Wechsel auf
# eine andere Tastencode-Nummerierung, wenn die Tasten ohnehin im Werkszustand waren.
# Verwendet nur Standard-VIA-Befehle (Protokoll 0x000C), keine Keychron-Sonderbefehle.
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Sichern')][string]$Sichern,
    [Parameter(Mandatory, ParameterSetName = 'Zurueck')][string]$Zurueck,
    [Parameter(ParameterSetName = 'Zurueck')][switch]$NurLicht,
    [int]$Rows = 6,
    [int]$Cols = 17
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public sealed class ViaDevice : IDisposable {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern SafeFileHandle CreateFile(string n, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
  readonly FileStream fs;
  public ViaDevice(string path) {
    var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
    if (h.IsInvalid) throw new IOException("Raw-HID nicht zu öffnen, Fehler " + Marshal.GetLastWin32Error());
    fs = new FileStream(h, FileAccess.ReadWrite, 0, true);
  }
  // Schickt einen Befehl (max. 32 Byte) und liefert die 32-Byte-Antwort mit gleichem Befehlsbyte.
  public byte[] Send(byte[] payload) {
    var report = new byte[33];
    Array.Copy(payload, 0, report, 1, payload.Length);
    fs.Write(report, 0, report.Length);
    var deadline = DateTime.UtcNow.AddMilliseconds(1000);
    var reply = new byte[33];
    while (DateTime.UtcNow < deadline) {
      using (var cts = new CancellationTokenSource(deadline - DateTime.UtcNow)) {
        int n;
        try { n = fs.ReadAsync(reply, 0, reply.Length, cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { break; }
        if (n >= 2 && (reply[1] == payload[0] || reply[1] == 0xFF)) {
          var data = new byte[32];
          Array.Copy(reply, 1, data, 0, 32);
          return data;
        }
      }
    }
    throw new IOException("Keine Antwort auf Befehl 0x" + payload[0].ToString("X2"));
  }
  public void Dispose() { fs.Dispose(); }
}
"@

function Find-RawHid {
    $guid = '{4d1e55b2-f16f-11cf-88cb-001111000030}'
    $dev = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'HID\VID_3434&PID_0281&MI_01\*' } | Select-Object -First 1
    if (-not $dev) { throw 'Keychron K8 Pro nicht per Kabel gefunden. Schalter auf "Cable" und USB-Kabel anstecken.' }
    '\\?\' + $dev.InstanceId.Replace('\', '#') + '#' + $guid
}

function Invoke-Via([ViaDevice]$via, [byte[]]$payload) {
    $reply = $via.Send($payload)
    if ($reply[0] -eq 0xFF) { throw ('Befehl 0x{0:X2} von der Tastatur nicht unterstützt' -f $payload[0]) }
    $reply
}

function Read-Buffer([ViaDevice]$via, [byte]$cmd, [int]$length) {
    $result = [byte[]]::new($length)
    for ($offset = 0; $offset -lt $length; $offset += 28) {
        $size = [Math]::Min(28, $length - $offset)
        $reply = Invoke-Via $via ([byte[]]@($cmd, ($offset -shr 8), ($offset -band 0xFF), $size))
        [Array]::Copy($reply, 4, $result, $offset, $size)
    }
    $result
}

function Write-Buffer([ViaDevice]$via, [byte]$cmd, [byte[]]$data) {
    for ($offset = 0; $offset -lt $data.Length; $offset += 28) {
        $size = [Math]::Min(28, $data.Length - $offset)
        $payload = [byte[]]::new(4 + $size)
        $payload[0] = $cmd; $payload[1] = $offset -shr 8; $payload[2] = $offset -band 0xFF; $payload[3] = $size
        [Array]::Copy($data, $offset, $payload, 4, $size)
        [void](Invoke-Via $via $payload)
    }
}

# RGB-Matrix-Werte. Ab Protokoll 0x000C über Kanal 3 (Id 1..4), davor direkt über Ids 0x80..0x83.
$rgbValues = @(
    @{ Name = 'Helligkeit'; Id = 1; OldId = 0x80; Len = 1 },
    @{ Name = 'Effekt'; Id = 2; OldId = 0x81; Len = 1 },
    @{ Name = 'Geschwindigkeit'; Id = 3; OldId = 0x82; Len = 1 },
    @{ Name = 'Farbe'; Id = 4; OldId = 0x83; Len = 2 })

function Get-RgbValue([ViaDevice]$via, [int]$version, $v) {
    if ($version -ge 0x000C) {
        $reply = Invoke-Via $via ([byte[]]@(0x08, 3, $v.Id))
        return [Convert]::ToHexString($reply, 3, $v.Len)
    }
    $reply = Invoke-Via $via ([byte[]]@(0x08, $v.OldId))
    [Convert]::ToHexString($reply, 2, $v.Len)
}

function Set-RgbValue([ViaDevice]$via, [int]$version, $v, [string]$hex) {
    $value = [Convert]::FromHexString($hex)
    if ($version -ge 0x000C) { [void](Invoke-Via $via ([byte[]](@(0x07, 3, $v.Id) + $value))) }
    else { [void](Invoke-Via $via ([byte[]](@(0x07, $v.OldId) + $value))) }
}

$via = [ViaDevice]::new((Find-RawHid))
try {
    $proto = Invoke-Via $via ([byte[]]@(0x01))
    $version = ([int]$proto[1] -shl 8) -bor $proto[2]
    if ($version -lt 0x0009 -or $version -gt 0x000C) { throw ('Unerwartete VIA-Protokollversion 0x{0:X4}' -f $version) }
    $layers = (Invoke-Via $via ([byte[]]@(0x11)))[1]
    $keymapLength = $layers * $Rows * $Cols * 2

    if ($PSCmdlet.ParameterSetName -eq 'Sichern') {
        $macroCount = (Invoke-Via $via ([byte[]]@(0x0C)))[1]
        $sizeReply = Invoke-Via $via ([byte[]]@(0x0D))
        $macroLength = ([int]$sizeReply[1] -shl 8) -bor $sizeReply[2]
        $layout = Invoke-Via $via ([byte[]]@(0x02, 0x02))
        $rgb = [ordered]@{}
        foreach ($v in $rgbValues) { $rgb[$v.Name] = Get-RgbValue $via $version $v }
        $backup = [ordered]@{
            Geraet         = 'Keychron K8 Pro ISO RGB (3434:0281)'
            Datum          = (Get-Date).ToString('s')
            Protokoll      = '0x{0:X4}' -f $version
            Ebenen         = $layers
            Zeilen         = $Rows
            Spalten        = $Cols
            Belegung       = [Convert]::ToHexString((Read-Buffer $via 0x12 $keymapLength))
            MakroAnzahl    = $macroCount
            Makros         = [Convert]::ToHexString((Read-Buffer $via 0x0E $macroLength))
            LayoutOptionen = [Convert]::ToHexString($layout, 2, 4)
            Rgb            = $rgb
        }
        $backup | ConvertTo-Json | Set-Content -Path $Sichern -Encoding utf8
        "Gesichert: $layers Ebenen, $macroCount Makros ($macroLength Byte), RGB und Layout -> $Sichern"
    }
    else {
        $backup = Get-Content -Path $Zurueck -Raw | ConvertFrom-Json
        if ($NurLicht) {
            foreach ($v in $rgbValues) { Set-RgbValue $via $version $v $backup.Rgb.($v.Name) }
            [void](Invoke-Via $via ([byte[]]$(if ($version -ge 0x000C) { @(0x09, 3) } else { @(0x09) })))
            $now = [ordered]@{}
            foreach ($v in $rgbValues) { $now[$v.Name] = Get-RgbValue $via $version $v }
            "Licht zurückgeschrieben: " + ($now | ConvertTo-Json -Compress)
            return
        }
        if ($backup.Ebenen -ne $layers) { throw "Sicherung hat $($backup.Ebenen) Ebenen, Tastatur $layers" }
        # Vor Protokoll 0x000B galten andere Tastencode-Nummern (QMK 0.19). Rohwerte passen dann nicht.
        $backupVersion = [Convert]::ToInt32($backup.Protokoll, 16)
        if (($backupVersion -ge 0x000B) -ne ($version -ge 0x000B)) {
            throw ('Sicherung (0x{0:X4}) und Tastatur (0x{1:X4}) nutzen verschiedene Tastencode-Nummern; erst übersetzen' -f $backupVersion, $version)
        }
        $keymap = [Convert]::FromHexString($backup.Belegung)
        if ($keymap.Length -ne $keymapLength) { throw 'Belegung passt nicht zur Tastatur' }

        Write-Buffer $via 0x13 $keymap
        Write-Buffer $via 0x0F ([Convert]::FromHexString($backup.Makros))
        [void](Invoke-Via $via ([byte[]](@(0x03, 0x02) + [Convert]::FromHexString($backup.LayoutOptionen))))
        foreach ($v in $rgbValues) { Set-RgbValue $via $version $v $backup.Rgb.($v.Name) }
        [void](Invoke-Via $via ([byte[]]$(if ($version -ge 0x000C) { @(0x09, 3) } else { @(0x09) })))

        $check = Read-Buffer $via 0x12 $keymapLength
        if ([Convert]::ToHexString($check) -ne $backup.Belegung) { throw 'Kontrolle fehlgeschlagen: Belegung weicht ab' }
        "Zurückgeschrieben und geprüft: $layers Ebenen, Makros, Layout, RGB aus $Zurueck"
    }
}
finally {
    $via.Dispose()
}
