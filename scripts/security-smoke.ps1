param(
    [string]$SetupPath = 'artifacts\release\CodexRouterSetup.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $base = Join-Path $env:TEMP 'codex-router-secret-diag'
    Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
    $routerRoot = Join-Path $base 'root'
    $logs = Join-Path $routerRoot 'logs'
    $unzip = Join-Path $base 'unzip'
    New-Item -ItemType Directory -Force -Path $logs, $unzip | Out-Null

    $secret = 'ZXCVBNMASDFGHJKLQWERTYUIOP1234567890ZXCVBNMASDFGHJKL'
    $bearer = 'abcdefghijklmnopqrstuvwxyz0123456789'
    $email = 'owner@example.test'
    $payload = @(
        "Authorization: Bearer $bearer",
        "{`"access_token`":`"$secret`"}",
        $email
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $logs 'host-test.log'), $payload, [Text.UTF8Encoding]::new($false))

    $setup = if ([IO.Path]::IsPathRooted($SetupPath)) {
        [IO.Path]::GetFullPath($SetupPath)
    } else {
        [IO.Path]::GetFullPath((Join-Path $root $SetupPath))
    }
    if (-not (Test-Path $setup)) { throw "Setup executable not found: $setup" }

    $embeddedInstallRoot = Join-Path $base 'embedded-install'
    $installRaw = & $setup install --root $embeddedInstallRoot --no-startup --no-launch
    if ($LASTEXITCODE -ne 0) { throw "Embedded install command failed with exit code $LASTEXITCODE." }
    $installResult = $installRaw | ConvertFrom-Json
    if (-not $installResult.changed) { throw 'Embedded installer reported no change.' }
    foreach ($installedFile in @('codex-route.exe', 'CodexRouterOverlay.exe')) {
        if (-not (Test-Path (Join-Path $embeddedInstallRoot "bin\$installedFile"))) {
            throw "Embedded installer did not install $installedFile."
        }
    }

    $raw = & $setup diagnostics --root $routerRoot
    if ($LASTEXITCODE -ne 0) { throw "Diagnostics command failed with exit code $LASTEXITCODE." }
    $result = $raw | ConvertFrom-Json
    Expand-Archive -LiteralPath $result.zipPath -DestinationPath $unzip -Force

    $allText = (Get-ChildItem $unzip -Recurse -File | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join [Environment]::NewLine
    foreach ($needle in @($bearer, $secret, $email)) {
        if ($allText.Contains($needle)) { throw "Diagnostics bundle leaked synthetic secret: $needle" }
    }

    $redactedLog = Join-Path $unzip 'logs\host-test.log.redacted.txt'
    if (-not (Test-Path $redactedLog)) { throw 'Expected redacted log was not included.' }
    $redactedText = [IO.File]::ReadAllText($redactedLog)
    if (-not $redactedText.Contains('[REDACTED]')) { throw 'Bearer/access token redaction marker was not found.' }
    if (-not $redactedText.Contains('[EMAIL]')) { throw 'Email redaction marker was not found.' }

    $null = & $setup uninstall --root $embeddedInstallRoot --remove-data
    if ($LASTEXITCODE -ne 0) { throw "Embedded install cleanup failed with exit code $LASTEXITCODE." }

    Write-Host 'Standalone embedded install smoke passed.'
    Write-Host 'Diagnostics synthetic-secret scan passed.'
    Write-Host "Bundle: $($result.zipPath)"
}
finally {
    Pop-Location
}
