$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$pkgRoot = Join-Path $repoRoot "Packages\com.thecocoa101.cocoas-toolbox"
Set-Location -LiteralPath $repoRoot

Write-Host "=== Cocoa's Toolbox VPM Release Helper ===" -ForegroundColor Cyan

# 1. Ensure all Unity .meta files inside the VPM package are present and valid
& (Join-Path $PSScriptRoot "ensure_metas.ps1") -PackageRoot $pkgRoot

# 2. Read current version from package.json
$pkgPath = Join-Path $pkgRoot "package.json"
$pkgRaw = [System.IO.File]::ReadAllText($pkgPath, [System.Text.Encoding]::UTF8)
if ($pkgRaw -notmatch '"version"\s*:\s*"([^"]+)"') {
    throw "Could not find 'version' field in $pkgPath."
}
$currentVer = $Matches[1]

# Compute default next patch version
$parts = $currentVer.Split("-")[0].Split(".")
$nextPatch = "{0}.{1}.{2}" -f [int]$parts[0], [int]$parts[1], ([int]$parts[2] + 1)

Write-Host ""
Write-Host "Current version : $currentVer" -ForegroundColor Yellow
$inputVer = Read-Host "New version (Press Enter for $nextPatch)"
$newVer = if ([string]::IsNullOrWhiteSpace($inputVer)) { $nextPatch } else { $inputVer.Trim().TrimStart("v") }

if ($newVer -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid SemVer format: $newVer (Example: 1.0.1 or 1.1.0)"
}

$note = Read-Host "Release note for CHANGELOG (Optional, press Enter to skip)"

# 3. Update package.json
$updatedPkg = [regex]::Replace(
    $pkgRaw,
    '("version"\s*:\s*)"[^"]+"',
    "`${1}`"$newVer`"",
    1
)
[System.IO.File]::WriteAllText($pkgPath, $updatedPkg, $utf8NoBom)
Write-Host "Updated package.json -> $newVer" -ForegroundColor Green

# 4. Prepend CHANGELOG.md entry
$changelogPath = Join-Path $pkgRoot "CHANGELOG.md"
if (Test-Path -LiteralPath $changelogPath) {
    $clRaw = [System.IO.File]::ReadAllText($changelogPath, [System.Text.Encoding]::UTF8)
    $today = (Get-Date).ToString("yyyy-MM-dd")
    $bullet = if ([string]::IsNullOrWhiteSpace($note)) { "- Update to v$newVer" } else { "- $note" }
    $entry = "## [$newVer] - $today`n`n$bullet`n`n"
    if ($clRaw -match "^# Changelog\s*\r?\n+") {
        $updatedCl = $clRaw -replace "^(# Changelog\s*\r?\n+)", "`$1$entry"
    } else {
        $updatedCl = "# Changelog`n`n$entry$clRaw"
    }
    $updatedCl = $updatedCl -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($changelogPath, $updatedCl, $utf8NoBom)
    Write-Host "Updated CHANGELOG.md" -ForegroundColor Green
}

# 5. Commit & Push
git add -A
Write-Host ""
git status --short
Write-Host ""

$commitMsg = if ([string]::IsNullOrWhiteSpace($note)) {
    "release: v$newVer"
} else {
    "release: v$newVer - $note"
}

git commit -m $commitMsg
git push origin main

Write-Host ""
Write-Host "Successfully pushed v$newVer to GitHub!" -ForegroundColor Cyan
Write-Host "GitHub Actions will automatically build the VPM zip & update index.json for VCC / ALCOM."
