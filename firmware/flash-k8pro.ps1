# Flasht eine Firmware auf die Keychron K8 Pro über den STM32-Bootloader (dfu-util aus QMK MSYS).
# Vorher die Tastatur in den Flash-Modus bringen: Kabel dran, Schalter auf "Off",
# Esc gedrückt halten, Schalter auf "Cable", Esc loslassen.
#   .\flash-k8pro.ps1 -Firmware bin\keychron_k8_pro_iso_rgb_akku.bin -Sicherung sicherung\firmware-vorher.bin
# Mit -Sicherung wird zuerst der komplette Flash-Speicher ausgelesen (falls der Chip das erlaubt).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Firmware,
    [string]$Sicherung,
    [int]$WarteMinuten = 10,
    [string]$DfuUtil = (Join-Path $PSScriptRoot 'tools\dfu-util.exe')
)
$ErrorActionPreference = 'Stop'
$device = '0483:df11'

if (-not (Test-Path $Firmware)) { throw "Firmware nicht gefunden: $Firmware" }
if (-not (Test-Path $DfuUtil)) { throw "dfu-util nicht gefunden: $DfuUtil" }

"Warte bis zu $WarteMinuten min auf den Flash-Modus (STM32 BOOTLOADER, USB $device) ..."
$deadline = (Get-Date).AddMinutes($WarteMinuten)
while (-not (Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_0483&PID_DF11\*' })) {
    if ((Get-Date) -gt $deadline) { throw 'Flash-Modus nicht erkannt.' }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
"Flash-Modus erkannt $(Get-Date -Format 'HH:mm:ss')"
& $DfuUtil -l 2>&1 | Where-Object { $_ -match 'Found DFU' }

if ($Sicherung) {
    "--- Sicherung der vorhandenen Firmware ---"
    & $DfuUtil -a 0 -d $device -s 0x08000000:262144 -U $Sicherung 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -eq 0 -and (Test-Path $Sicherung)) {
        "Gesichert: $((Get-Item $Sicherung).Length) Byte -> $Sicherung"
    } else {
        "Auslesen nicht möglich (Exitcode $LASTEXITCODE), Chip ist vermutlich lesegeschützt. Weiter ohne Sicherung."
        Remove-Item $Sicherung -ErrorAction SilentlyContinue
    }
}

"--- Flashen: $Firmware ($((Get-Item $Firmware).Length) Byte) ---"
& $DfuUtil -a 0 -d $device -s 0x08000000:leave -D $Firmware 2>&1 | Select-Object -Last 6
$code = $LASTEXITCODE
"dfu-util Exitcode: $code"

"Warte auf den Neustart der Tastatur ..."
$deadline = (Get-Date).AddSeconds(30)
while (-not (Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_3434&PID_0281\*' })) {
    if ((Get-Date) -gt $deadline) { "Tastatur nach 30 s nicht wieder da. Schalter einmal auf Off und zurück auf Cable stellen."; exit 1 }
    Start-Sleep -Milliseconds 500
}
"Tastatur läuft wieder $(Get-Date -Format 'HH:mm:ss')"
exit $code
