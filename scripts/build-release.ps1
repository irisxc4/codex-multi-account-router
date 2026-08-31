param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputRoot = 'artifacts\release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $output = [System.IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output | Out-Null

    if (-not $SkipTests) {
        & (Join-Path $PSScriptRoot 'test-all.ps1') -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    }

    $vulnerabilityOutput = & dotnet list .\CodexRouter.sln package --vulnerable --include-transitive 2>&1
    if ($LASTEXITCODE -ne 0) { throw "NuGet vulnerability audit failed with exit code $LASTEXITCODE.`n$($vulnerabilityOutput -join [Environment]::NewLine)" }
    $vulnerabilityText = $vulnerabilityOutput -join [Environment]::NewLine
    if ($vulnerabilityText -match 'has the following vulnerable packages|具有下列易受攻击的包') {
        throw "Release blocked by vulnerable NuGet packages.`n$vulnerabilityText"
    }
    Write-Host 'NuGet vulnerability audit passed.'

    $cli = Join-Path $output 'publish-cli'
    $overlay = Join-Path $output 'publish-overlay'
    $setupBootstrap = Join-Path $output 'publish-setup-bootstrap'
    $setupFinal = Join-Path $output 'publish-setup-final'
    $package = Join-Path $output 'package'
    $packageZip = Join-Path $output '.package-payload.zip'

    & dotnet publish .\apps\CodexRouter.Cli\CodexRouter.Cli.csproj -c Release --no-restore -o $cli -v:q
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    & dotnet publish .\apps\CodexRouter.Overlay\CodexRouter.Overlay.csproj -c Release --no-restore -o $overlay -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Overlay publish failed.' }
    & dotnet publish .\apps\CodexRouter.Setup\CodexRouter.Setup.csproj -c Release --no-restore -o $setupBootstrap -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap Setup publish failed.' }

    $bootstrapSetupExe = Join-Path $setupBootstrap 'CodexRouterSetup.exe'
    & $bootstrapSetupExe package --cli $cli --overlay $overlay --out $package --version $Version --arch win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Package creation failed.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $package,
        $packageZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    & dotnet publish .\apps\CodexRouter.Setup\CodexRouter.Setup.csproj -c Release --no-restore -o $setupFinal -v:q "-p:EmbeddedPackageZip=$packageZip"
    if ($LASTEXITCODE -ne 0) { throw 'Payload-bearing Setup publish failed.' }

    $finalSetup = Join-Path $output 'CodexRouterSetup.exe'
    Copy-Item (Join-Path $setupFinal 'CodexRouterSetup.exe') $finalSetup
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($finalSetup)
        try {
            $sha = ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally { $stream.Dispose() }
    }
    finally { $sha256.Dispose() }
    $releaseInfo = [ordered]@{
        version = $Version
        architecture = 'win-x64'
        builtAt = [DateTimeOffset]::UtcNow.ToString('o')
        setupSha256 = $sha
        package = 'package'
    }
    $releaseJson = $releaseInfo | ConvertTo-Json -Depth 5
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $output 'release.json'), $releaseJson, $utf8NoBom)

    & (Join-Path $PSScriptRoot 'security-smoke.ps1') -SetupPath $finalSetup
    if ($LASTEXITCODE -ne 0) { throw "Security smoke failed with exit code $LASTEXITCODE." }

    Remove-Item $cli, $overlay, $setupBootstrap, $setupFinal -Recurse -Force
    Remove-Item $packageZip -Force
    Write-Host "Release ready: $output"
}
finally {
    Pop-Location
}
