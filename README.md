# JUICE

**J**ust a **U**nified **I**ndicator for **C**harge & **E**nergy. Kleines Windows-11-Tool: zeigt den Akkustand kabelloser Geräte als Zahl mit Gerätesymbol in der Taskleiste, ohne dass Corsair iCUE laufen muss.

*English: tiny Windows tray tool that shows battery levels of Corsair wireless devices (read directly via the Bragi HID protocol, no iCUE needed), Bluetooth devices, and a Keychron K8 Pro with a small firmware patch.*

Inoffizielles Projekt, nicht verbunden mit Corsair oder Keychron.

## Was es anzeigt

- **Corsair-Funkgeräte** am SLIPSTREAM-Empfänger und Corsair-Kabelgeräte mit Akku (getestet: M75 Wireless). Das Tool liest die Werte direkt über das Corsair-Protokoll („Bragi“). Es schickt nur Lesebefehle und verstellt nichts an Modus, Beleuchtung oder DPI.
- **Bluetooth-Geräte**, für die Windows einen Akkustand kennt, also dieselben Werte wie unter Einstellungen › Bluetooth und Geräte.
- **Keychron K8 Pro mit eigener Firmware** (siehe unten): Die Tastatur meldet ihren Akku über Bluetooth als versteckte Medientaste, das Tool liest sie mit.
- **Geräte ohne Akkuwert** stehen nur im Menü, zum Beispiel „…: meldet keinen Akkustand“.

## Starten

Einmal bauen (siehe unten), dann `dist\Juice.exe` doppelklicken. Beim ersten Start schaltet sich der Autostart ein, das Tool startet danach mit Windows. Im Menü lässt sich das wieder abschalten.

Neue Symbole legt Windows zuerst in den Überlauf (^ neben der Uhr). Damit sie immer sichtbar sind, zieh sie in die Taskleiste oder schalte sie unter Einstellungen › Personalisierung › Taskleiste › Andere Taskleistensymbole ein.

## Bedienung

| Anzeige | Bedeutung |
| --- | --- |
| Symbol oben | Geräteart: Maus, Tastatur, Kopfhörer, Gamepad, Punkt = sonstiges Gerät |
| weiße Zahl | Akkustand in Prozent |
| rote Zahl | 20 % oder weniger |
| grüne Zahl mit Balken | lädt oder voll |
| graue Zahl | veraltet: pausiert oder Gerät nicht verbunden |

- Maus über das Symbol: Gerät, Zustand, geschätzte Restlaufzeit und Uhrzeit der letzten Messung.
- Restlaufzeit: geschätzt aus dem bisherigen Verlauf, sobald ein Gerät 3 Prozentpunkte über mindestens eine halbe Stunde verloren hat. Gezählt wird nur Zeit, in der der PC läuft; Laden beginnt die Messung neu. Die Schätzung wertet nur ohnehin gemessene Werte aus und kostet keine zusätzliche Abfrage.
- Linksklick: sofort aktualisieren.
- Rechtsklick: Menü mit allen Geräten. Ein Klick auf ein Gerät blendet sein Symbol ein oder aus. Außerdem „Jetzt aktualisieren“, „Mit Windows starten“ und „Beenden“.
- Das Tool aktualisiert alle 2 Minuten. Bei 20 % und bei 10 % kommt je einmal ein Windows-Hinweis. Nach dem Aufladen ist die Warnung wieder scharf.

## iCUE und SignalRGB

Solange iCUE oder die SignalRGB-App läuft, fragt das Tool Corsair-Geräte nicht selbst ab, damit sich die Programme nicht in die Quere kommen. Stattdessen liest es die Meldungen mit, die die Maus bei jeder Akkuänderung von selbst schickt. Diese Schnittstelle sendet nur, Mitlesen stört also niemanden. Bis die erste Meldung kommt, zeigt das Symbol den letzten bekannten Wert in Grau. Der SignalRGB-Hintergrunddienst allein stört nicht.

Wird iCUE hart beendet (etwa im Task-Manager), meldet die Maus danach weiter den Software-Modus von iCUE. Das Tool ändert daran nichts. Startet iCUE wieder, übernimmt es die Maus wie gewohnt.

## Bauen und testen

```powershell
.\build.ps1
.\dist\Juice.exe --dump | Out-Host
```

`build.ps1` braucht nur das installierte .NET-10-SDK und lädt nichts herunter. `--dump` fragt einmal ab und gibt die Werte in der Konsole aus, `--listen` lauscht drei Minuten auf Akku-Codes der Keychron.

Neue Corsair-Geräte bekommen ihren Namen über die Tabelle `KnownDevices` in `src/CorsairBragi.cs`. Unbekannte erscheinen als „Corsair-Gerät“ mit Produkt-ID.

## Dateien

- Einstellungen und Protokoll: `%LOCALAPPDATA%\Juice\`
  - `settings.json`: ausgeblendete Geräte
  - `letzte-werte.json`: letzter Stand jedes Geräts, damit es nach einem Neustart gleich (grau) da ist
  - `corsair-geraete.json`: welches Corsair-Gerät auf welchem Empfänger-Platz sitzt (für den Namen)
  - `keychron-akku.json`: letzter Akku-Code der Keychron
  - `verlauf.json`: Akkustände und Laufzeit für die Restlaufzeit, höchstens 100 Werte pro Gerät
  - `juice.log`: Protokoll, begrenzt auf 256 KB
- Autostart: Aufgabe `Juice` in der Aufgabenplanung („Bei Anmeldung“, nur dein Benutzer, normale Rechte). Der Run-Schlüssel in der Registry wäre der übliche Weg, aber Windows 11 Build 26300 hat den Eintrag dort beim Anmelden einfach übersprungen. Ein alter Run-Eintrag von Version 0.2 wird beim Start entfernt.

## Im Hintergrund

Das Tool ist ein normales Programm ohne Fenster: `Juice.exe`. Im Task-Manager steht es unter „Details“ als `Juice.exe`. Unter „Autostart-Apps“ taucht es nicht auf, weil es über die Aufgabenplanung startet; abschalten lässt es sich im eigenen Menü oder in der Aufgabenplanung. Es läuft mit deinen normalen Benutzerrechten, braucht keinen Dienst und keinen Treiber und sendet nichts ins Netz.

Typischer Verbrauch: rund 5 MB eigener Speicher (Task-Manager, Spalte „Arbeitsspeicher“), unter 1 % CPU. Dazu kommen Bibliotheken von Windows und .NET, die sich alle Programme teilen. Lese-Threads warten blockierend auf Meldungen der Geräte und kosten in der Zeit keine Rechenzeit.

## Entfernen

Im Menü „Mit Windows starten“ abschalten und „Beenden“ wählen. Danach diesen Ordner und `%LOCALAPPDATA%\Juice` löschen.

## Keychron K8 Pro: eigene Firmware

Mit Keychrons Original-Firmware meldet die K8 Pro ihren Akku nicht an den PC: Ihr Bluetooth-Modul (CKBT51) kann nur klassisches Bluetooth und hat dort kein Akkufeld. Darum läuft auf der Tastatur eine leicht erweiterte Firmware:

- `firmware/k8_pro_iso_rgb_akku/keymap.c`: Keychrons VIA-Belegung für die K8 Pro ISO RGB (Firmware-Stand `bluetooth_playground`, Ende 2023) plus etwa 25 Zeilen. Nach einem echten Tastendruck, höchstens alle 10 Minuten und nur bei bestehender Bluetooth-Verbindung, „drückt“ die Tastatur die unbelegte Medientaste `0x300 + Prozent`, bzw. `0x380 + Prozent`, wenn das Kabel steckt. Windows ignoriert diese Nummern. Das Tool liest sie über die Medientasten-Collection mit (`src/KeychronCodes.cs`).
- Der erste Wert kommt etwa eine Minute nach dem Verbinden beim nächsten Tippen. Danach aktualisiert er sich alle 10 Minuten, solange getippt wird. Den letzten Wert merkt sich das Tool über Neustarts hinweg.
- Mit Kabel steht der Wert meist auf 100 %, weil die Ladespannung über der Voll-Spannung liegt.

**Flashen auf eigene Gefahr.** Der Hauptchip (STM32L432) hat einen festen Bootloader, die Tastatur lässt sich also immer wieder zurückflashen. Das Bluetooth-Modul wird nicht angefasst.

Dateien in `firmware/`:

| Datei | Zweck |
| --- | --- |
| `k8_pro_iso_rgb_akku/` | die Belegung mit Akku-Meldung (`keymap.c`, `rules.mk`) |
| `via-belegung.ps1` | sichert oder schreibt Belegung, Makros und Licht per VIA, vor und nach dem Flashen |
| `flash-k8pro.ps1` | flasht eine `.bin` über den STM32-Bootloader, liest auf Wunsch vorher den ganzen Flash-Speicher als Sicherung aus |

Binärdateien liegen nicht im Repo. Lokal gehören sie nach `firmware/bin/`, `firmware/sicherung/` und `firmware/tools/` (alle drei in der `.gitignore`).

**Firmware bauen:**

1. [QMK MSYS](https://github.com/qmk/qmk_distro_msys) installieren (enthält Compiler und `dfu-util`).
2. Keychrons QMK-Stand holen: `git clone --depth 1 --branch bluetooth_playground https://github.com/Keychron/qmk_firmware`, dann `git submodule update --init --depth 1 lib/chibios lib/chibios-contrib lib/printf lib/lufa lib/vusb`. Getestet mit Commit `618127a`.
3. `firmware/k8_pro_iso_rgb_akku/` als `keyboards/keychron/k8_pro/iso/rgb/keymaps/akku/` hineinkopieren.
4. In QMK MSYS: `make keychron/k8_pro/iso/rgb:akku`. Für andere Varianten der K8 Pro (ANSI, JIS, weiße Beleuchtung) die Tastenebenen aus deren `via`-Belegung übernehmen.

**Flashen:**

```powershell
.\firmware\via-belegung.ps1 -Sichern .\firmware\sicherung\belegung-vor-flash.json
.\firmware\flash-k8pro.ps1 -Firmware .\firmware\bin\keychron_k8_pro_iso_rgb_akku.bin -Sicherung .\firmware\sicherung\firmware-vorher.bin
```

Das Flash-Skript wartet auf den Flash-Modus: Kabel dran, Schalter auf Off, Esc halten, Schalter auf Cable. Es nutzt `firmware\tools\dfu-util.exe` (aus QMK MSYS kopieren) und braucht den WinUSB-Treiber für USB 0483:DF11, den QMK MSYS oder QMK Toolbox einrichten. Eine neue QMK-Firmware setzt die VIA-Belegung auf den Standard zurück. `via-belegung.ps1 -Zurueck … [-NurLicht]` schreibt sie wieder.

**Zurück zum Zustand vorher:** Flash-Modus, dann `.\firmware\flash-k8pro.ps1 -Firmware .\firmware\sicherung\firmware-vorher.bin`.

## Technik

- C# / .NET 10 / WinForms, nur Windows-Bordmittel (`hid.dll`, `cfgmgr32.dll`), keine NuGet-Pakete.
- Corsair: Der Empfänger meldet über HID (Usage-Page `0xFF42`) per Eigenschaft `0x36` eine Bitmaske der verbundenen Funkgeräte. Pro Gerät liest das Tool `0x12` (Produkt-ID), `0x0F` (Akku, 0 bis 1000) und `0x10` (1 lädt, 2 entlädt, 3 voll). Paketaufbau wie in [OpenLinkHub](https://github.com/jurkovic-nikola/OpenLinkHub) und im Corsair-Plugin von SignalRGB.
- Bluetooth: Akkustand aus der Geräteeigenschaft `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`, Verbindung aus `{83DA6326-97A6-4088-9453-A1923F573B29} 15`.

## Lizenz

JUICE selbst steht unter der [MIT-Lizenz](LICENSE). Ausgenommen ist `firmware/k8_pro_iso_rgb_akku/keymap.c`: Es baut auf Keychrons QMK-Belegung auf und steht wie QMK unter der GPL 2.0 oder neuer (siehe Kopf der Datei).
