param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & dotnet build .\CodexRouter.sln -c $Configuration -v:q
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed with exit code $LASTEXITCODE." }

    $testProjects = Get-ChildItem .\tests -Directory |
        Where-Object { $_.Name -like '*.Tests' } |
        Sort-Object Name

    if ($testProjects.Count -eq 0) { throw 'No test projects were found.' }

    $passedAssemblies = 0
    foreach ($project in $testProjects) {
        $expected = "$($project.Name).dll"
        $assembly = Get-ChildItem (Join-Path $project.FullName "bin\$Configuration") -Recurse -File -Filter $expected |
            Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' } |
            Select-Object -First 1
        if ($null -eq $assembly) { throw "Built test assembly '$expected' was not found for $($project.Name)." }

        Write-Host "==> $($project.Name)"
        & dotnet vstest $assembly.FullName --nologo
        if ($LASTEXITCODE -ne 0) { throw "$($project.Name) failed with exit code $LASTEXITCODE." }
        $passedAssemblies++
    }

    Write-Host "All $passedAssemblies test assemblies passed."
}
finally {
    Pop-Location
}
