<#
.SYNOPSIS
  Veroeffentlicht eine neue MailVault-Version auf mailvault.zerze.de.

  1. Tests, dann eigenstaendig lauffaehiges Paket MailVault-<version>-win-x64.zip (kein .NET beim Benutzer noetig).
  2. Webseite aus site\ nach dist\site kopieren, Versionsangaben einsetzen.
  3. Bisherige Update-Listen vom Server holen, neue Version eintragen und signieren (tools\MailVault.Release).
  4. Mit -Deploy alles per scp auf den Server laden.

  Aenderungen je Sprache stehen in build\notes\<version>.json ({"de":[...],"en":[...],"fr":[...],"es":[...]}).
  Fehlt die Datei, werden die deutschen Commit-Betreffzeilen seit der letzten Version verwendet.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\release.ps1
  powershell -ExecutionPolicy Bypass -File build\release.ps1 -Deploy

  Zugangsdaten zum Server stehen NICHT im Repository, sondern in build\deploy.local.json (von Git ignoriert):
    {"Target": "user@host:/var/www/mailvault.zerze.de", "PasswordFile": "C:\\pfad\\zur\\passwortdatei", "HostKey": "SHA256:..."}
  Ohne PasswordFile wird scp mit SSH-Schluessel verwendet.
#>
param(
    [string]$Version = "",
    [string]$Out = "dist",
    [string]$Site = "https://mailvault.zerze.de",
    [string]$Target = "",
    [switch]$Deploy,
    [switch]$SkipTests,
    [switch]$SiteOnly,
    [string]$PasswordFile = "",
    # Oeffentliches Git-Repository (Link auf der Webseite)
    [string]$RepoUrl = "https://github.com/twirmer-cyber/mailvault",
    # Fingerabdruck des Host-Schluessels; pscp prueft ihn fest (Schutz vor einem falschen Server)
    [string]$HostKey = ""
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$props = [xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw)
$propsVersion = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { $Version = $propsVersion }
if ($Version -notmatch '^(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' hat nicht die Form X.Y.Z oder X.Y.Z-beta.N" }
if ($Matches[1] -ne $propsVersion) { throw "Directory.Build.props enthaelt $propsVersion, veroeffentlicht werden soll $Version. Bitte angleichen." }

$outDir = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$siteOut = Join-Path $outDir "site"
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $siteOut) { Remove-Item $siteOut -Recurse -Force }
Copy-Item (Join-Path $root "site") $siteOut -Recurse

function Invoke-GitQuiet {
    $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $o = & git @args 2>$null; if ($LASTEXITCODE -ne 0) { return $null }; return $o }
    finally { $ErrorActionPreference = $old }
}

$zipName = "MailVault-$Version-win-x64.zip"
$zip = Join-Path $outDir $zipName

if (-not $SiteOnly) {
    if (-not $SkipTests) {
        dotnet test (Join-Path $root "tests\MailVault.Tests") --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen - es wird nichts veroeffentlicht." }
    }
    $stage = Join-Path $outDir "MailVault"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    dotnet publish (Join-Path $root "src\MailVault.App\MailVault.App.csproj") -c Release -r win-x64 --self-contained `
        -p:Version=$Version -o $stage --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen" }
    Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item
    Copy-Item (Join-Path $PSScriptRoot "Installieren.cmd") $stage
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # Aenderungsliste
    $notes = Join-Path $PSScriptRoot "notes\$Version.json"
    if (-not (Test-Path $notes)) {
        Write-Warning "build\notes\$Version.json fehlt - verwende deutsche Commit-Betreffzeilen."
        $prev = Invoke-GitQuiet describe --tags --abbrev=0 --match "v*" "HEAD"
        $range = if ($prev) { @("$prev..HEAD") } else { @("-20", "HEAD") }
        $lines = @(Invoke-GitQuiet -c i18n.logOutputEncoding=UTF-8 log --no-merges --format="%s" @range | Where-Object { $_ -notmatch '^(Merge|WIP)' })
        $notes = Join-Path $outDir "notes.json"
        [IO.File]::WriteAllText($notes, (@{ de = $lines } | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding($false)))
    }

    # Bisherige Update-Listen vom Server, damit die Versionsgeschichte erhalten bleibt
    $updDir = Join-Path $siteOut "updates"
    New-Item -ItemType Directory -Force $updDir | Out-Null
    foreach ($ch in "stable", "beta") {
        try { Invoke-WebRequest -UseBasicParsing "$Site/updates/$ch.json" -OutFile (Join-Path $updDir "$ch.json") }
        catch { Write-Host "Noch keine $ch.json auf dem Server - wird neu angelegt." }
        $f = Join-Path $updDir "$ch.json"
        if ((Test-Path $f) -and ((Get-Content $f -Raw) -notmatch '^\s*\{')) { Remove-Item $f; Write-Host "$ch.json vom Server ist kein JSON - wird neu angelegt." }
    }
    dotnet run --project (Join-Path $root "tools\MailVault.Release") -c Release -v q -- feed --zip $zip --version $Version --site $siteOut --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "Update-Liste konnte nicht erstellt werden" }
    dotnet run --project (Join-Path $root "tools\MailVault.Release") -c Release -v q -- verify --site $siteOut
    if ($LASTEXITCODE -ne 0) { throw "Signaturpruefung fehlgeschlagen" }

    # Setup-Datei fuer den Download: kleiner Entpacker (.NET Framework 4.8) mit dem Paket als Ressource
    $setupOut = Join-Path $outDir "setup"
    if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
    dotnet build (Join-Path $root "tools\MailVault.Setup") -c Release --nologo -v q "-p:Payload=$zip" "-p:Version=$($Matches[1])" -o $setupOut
    if ($LASTEXITCODE -ne 0) { throw "Setup-Datei konnte nicht gebaut werden" }
    $setupName = "MailVault-Setup-$Version.exe"
    Copy-Item (Join-Path $setupOut "MailVault-Setup.exe") (Join-Path $siteOut "download\$setupName")
    # Probe: nur entpacken (installiert nichts) und pruefen, dass MailVault.exe enthalten ist
    $probe = Join-Path $outDir "setup-probe"
    if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
    $p = Start-Process (Join-Path $siteOut "download\$setupName") -ArgumentList "/verify-extract `"$probe`"" -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup-Datei ist fehlerhaft (Probe-Entpacken: $($p.ExitCode))" }
    Remove-Item $probe -Recurse -Force
    Write-Host "Setup: download/$setupName"
}

# Versionsangaben in die Webseite (Download-Knopf funktioniert auch ohne JavaScript)
$stable = Join-Path $siteOut "updates\stable.json"
if (-not (Test-Path $stable)) {
    try { New-Item -ItemType Directory -Force (Split-Path $stable) | Out-Null; Invoke-WebRequest -UseBasicParsing "$Site/updates/stable.json" -OutFile $stable } catch {}
}
if (Test-Path $stable) {
    $feed = Get-Content $stable -Raw -Encoding UTF8 | ConvertFrom-Json
    $latest = $feed.versions | Select-Object -First 1
    $file = Split-Path $latest.package.url -Leaf
    $vars = @{
        "{{VERSION}}" = $latest.version
        "{{ZIP}}" = "download/$file"
        "{{SIZE_MB}}" = ("{0:N0}" -f ($latest.package.size / 1MB))
        "{{SHA256}}" = $latest.package.sha256
        "{{DATE}}" = ([DateTime]$latest.published).ToString("yyyy-MM-dd")
        "{{SETUP}}" = "download/MailVault-Setup-$($latest.version).exe"
        "{{SETUP_MB}}" = "{0:N0}" -f ($latest.package.size / 1MB)
        "{{REPO_URL}}" = $RepoUrl
    }
    $setupFile = Join-Path $siteOut "download\MailVault-Setup-$($latest.version).exe"
    if (Test-Path $setupFile) { $vars["{{SETUP_MB}}"] = "{0:N0}" -f ((Get-Item $setupFile).Length / 1MB) }
    foreach ($page in Get-ChildItem $siteOut -Include *.html, *.js -Recurse) {
        $t = [IO.File]::ReadAllText($page.FullName)
        foreach ($k in $vars.Keys) { $t = $t.Replace($k, [string]$vars[$k]) }
        [IO.File]::WriteAllText($page.FullName, $t, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Host "Webseite zeigt Version $($latest.version)"
} else { Write-Warning "Keine stable.json - Platzhalter in der Webseite bleiben stehen." }

if ($Deploy) {
    $local = Join-Path $PSScriptRoot "deploy.local.json"
    if (Test-Path $local) {
        $cfg = Get-Content $local -Raw | ConvertFrom-Json
        if (-not $Target) { $Target = $cfg.Target }
        if (-not $PasswordFile) { $PasswordFile = $cfg.PasswordFile }
        if (-not $HostKey) { $HostKey = $cfg.HostKey }
    }
    if ($PasswordFile -and -not $HostKey) { throw "Mit PasswordFile bitte auch HostKey angeben (Schutz vor einem falschen Server)." }
    if (-not $Target) { throw "Fuer -Deploy bitte -Target user@host:/pfad/zum/webroot angeben." }
    Write-Host "Lade nach $Target ..."
    # Pakete zuerst, dann Webseite, Update-Listen zuletzt: Ein Client sieht eine neue Version erst, wenn das Paket da ist.
    # Mit -PasswordFile per PuTTY-pscp (Passwort aus Zeile 1 der Datei, Host-Schlüssel fest geprüft), sonst scp mit SSH-Schlüssel.
    function Send-Files([string[]]$Paths, [string]$What) {
        if ($PasswordFile) { pscp -batch -q -r -hostkey $HostKey -pwfile $PasswordFile @Paths "$Target/" }
        else { scp -r @Paths "$Target/" }
        if ($LASTEXITCODE -ne 0) { throw "Upload $What fehlgeschlagen" }
    }
    $dl = Join-Path $siteOut "download"
    if (Test-Path $dl) { Send-Files @($dl) "der Pakete" }
    $items = Get-ChildItem $siteOut | Where-Object { $_.Name -notin "download", "updates" } | ForEach-Object { $_.FullName }
    Send-Files $items "der Webseite"
    $upd = Join-Path $siteOut "updates"
    if (Test-Path $upd) { Send-Files @($upd) "der Update-Listen" }
    Write-Host "Fertig: $Site"
}
