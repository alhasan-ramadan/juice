# Baut dist\Juice.exe. Braucht nur das installierte .NET-10-SDK, lädt nichts herunter.
# Läuft das Tool gerade aus dist\, wird es dafür beendet und danach wieder gestartet.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'dist\Juice.exe'

$running = @(Get-Process -Name Juice -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
$running | Stop-Process -Force
$running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'Juice.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o (Join-Path $root 'dist')
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen ($LASTEXITCODE)" }

if ($running.Count) { Start-Process $exe }
Write-Host "Fertig: $exe"
