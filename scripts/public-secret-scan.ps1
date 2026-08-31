param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$blockedNames = @('auth.json', '.env')
$blockedExtensions = @(
    '.age', '.db', '.jsonl', '.key', '.log', '.p12', '.pem', '.pfx', '.sqlite', '.sqlite3'
)
$blockedSegments = @(
    'artifacts', 'archived_sessions', 'diagnostics', 'logs', 'profiles', 'secrets', 'sessions', 'templates'
)
$patterns = [ordered]@{
    'private Windows user path' = '(?i)\b[A-Z]:\\Users\\(?!Public\\|Default\\|Default User\\|All Users\\|<user>\\|username\\|user\\)'
    'private workspace path' = '(?i)\bH:\\ai\\'
    'GitHub credential' = '(?i)\bgh[opusr]_[A-Za-z0-9]{20,}\b'
    'OpenAI-style secret' = '(?i)\bsk-[A-Za-z0-9_-]{20,}\b'
    'JWT' = '\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b'
    'private key' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'Router account id' = '(?i)\bacct-[0-9a-f]{16,}\b'
}

Push-Location $root
try {
    $files = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed; initialize the repository before running the public scan.'
    }

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($relative in $files) {
        $normalized = $relative.Replace('\', '/')
        if ($normalized -eq 'scripts/public-secret-scan.ps1') { continue }

        $segments = $normalized.Split('/')
        $name = [IO.Path]::GetFileName($normalized)
        $extension = [IO.Path]::GetExtension($normalized)
        if ($blockedNames -contains $name -or $blockedExtensions -contains $extension.ToLowerInvariant()) {
            $findings.Add("blocked credential/runtime file: $relative")
            continue
        }
        if ($segments | Where-Object { $blockedSegments -contains $_.ToLowerInvariant() }) {
            $findings.Add("blocked runtime directory: $relative")
            continue
        }

        $path = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -gt 5MB) {
            $findings.Add("unexpected file over 5 MB: $relative")
            continue
        }

        try { $text = [IO.File]::ReadAllText($path) }
        catch { continue }
        if ($text.IndexOf([char]0) -ge 0) { continue }

        foreach ($entry in $patterns.GetEnumerator()) {
            if ([regex]::IsMatch($text, $entry.Value)) {
                $findings.Add("$($entry.Key): $relative")
            }
        }

        foreach ($match in [regex]::Matches($text, '(?i)\b[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,})\b')) {
            $domain = $match.Groups[1].Value.ToLowerInvariant()
            if ($domain -notin @('example.com', 'example.test')) {
                $findings.Add("non-example email address: $relative")
            }
        }
    }

    if ($findings.Count -gt 0) {
        $findings | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
        throw "Public secret scan failed with $($findings.Count) finding(s)."
    }

    Write-Host "Public secret scan passed for $($files.Count) tracked files."
}
finally {
    Pop-Location
}
