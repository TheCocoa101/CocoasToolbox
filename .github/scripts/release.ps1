param(
    [switch]$PreRelease
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$pkgRoot = Join-Path $repoRoot "Packages\com.thecocoa101.cocoas-toolbox"
Set-Location -LiteralPath $repoRoot

if ($PreRelease) {
    Write-Host "=== Cocoa's Toolbox VPM Pre-Release Helper ===" -ForegroundColor Magenta
} else {
    Write-Host "=== Cocoa's Toolbox VPM Release Helper ===" -ForegroundColor Cyan
}

# 1. Ensure all Unity .meta files inside the VPM package are present and valid
& (Join-Path $PSScriptRoot "ensure_metas.ps1") -PackageRoot $pkgRoot

# 2. Read current version from package.json
$pkgPath = Join-Path $pkgRoot "package.json"
$pkgRaw = [System.IO.File]::ReadAllText($pkgPath, [System.Text.Encoding]::UTF8)
if ($pkgRaw -notmatch '"version"\s*:\s*"([^"]+)"') {
    throw "Could not find 'version' field in $pkgPath."
}
$currentVer = $Matches[1]

# 3. Compute default next version
if ($PreRelease) {
    if ($currentVer -match '^(\d+\.\d+\.\d+)-([A-Za-z0-9.-]+?)\.(\d+)$') {
        $baseVer = $Matches[1]
        $tag = $Matches[2]
        $num = [int]$Matches[3] + 1
        $defaultVer = "$baseVer-$tag.$num"
    } elseif ($currentVer -match '^(\d+\.\d+\.\d+)-([A-Za-z0-9.-]+)$') {
        $baseVer = $Matches[1]
        $tag = $Matches[2]
        $defaultVer = "$baseVer-$tag.1"
    } else {
        $defaultVer = "$currentVer-preview.1"
    }
} else {
    if ($currentVer -match '-') {
        # プレリリースからの正式版リリース（例: 1.0.0-preview.1 -> 1.0.0）
        $defaultVer = $currentVer.Split("-")[0]
    } else {
        # まだこのバージョンのタグが存在しない場合は、currentVer そのものを候補にする
        $hasTag = (git tag -l "v$currentVer")
        if (-not $hasTag) {
            $defaultVer = $currentVer
        } else {
            $parts = $currentVer.Split(".")
            $defaultVer = "{0}.{1}.{2}" -f [int]$parts[0], [int]$parts[1], ([int]$parts[2] + 1)
        }
    }
}

Write-Host ""
Write-Host "Current version : $currentVer" -ForegroundColor Yellow
$promptLabel = if ($PreRelease) { "New pre-release version" } else { "New version" }
$inputVer = Read-Host "$promptLabel (Press Enter for $defaultVer)"
$newVer = if ([string]::IsNullOrWhiteSpace($inputVer)) { $defaultVer } else { $inputVer.Trim().TrimStart("v") }

if ($newVer -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid SemVer format: $newVer (Example: 1.1.0 or 1.1.0-preview.1)"
}

$note = Read-Host "Release note for CHANGELOG (Optional, press Enter to skip)"

# 4. Update package.json
$updatedPkg = [regex]::Replace(
    $pkgRaw,
    '("version"\s*:\s*)"[^"]+"',
    "`${1}`"$newVer`"",
    1
)
[System.IO.File]::WriteAllText($pkgPath, $updatedPkg, $utf8NoBom)
Write-Host "Updated package.json -> $newVer" -ForegroundColor Green

# 5. Prepend CHANGELOG.md entry
$changelogPath = Join-Path $pkgRoot "CHANGELOG.md"
if (Test-Path -LiteralPath $changelogPath) {
    $clRaw = [System.IO.File]::ReadAllText($changelogPath, [System.Text.Encoding]::UTF8)
    $today = (Get-Date).ToString("yyyy-MM-dd")
    $defaultBullet = if ($PreRelease -or $newVer -match '-') { "- Pre-release v$newVer" } else { "- Update to v$newVer" }
    $bullet = if ([string]::IsNullOrWhiteSpace($note)) { $defaultBullet } else { "- $note" }
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

# 6. Commit & Push
git add -A
Write-Host ""
git status --short
Write-Host ""

$typePrefix = if ($PreRelease -or $newVer -match '-') { "prerelease" } else { "release" }
$commitMsg = if ([string]::IsNullOrWhiteSpace($note)) {
    "$typePrefix`: v$newVer"
} else {
    "$typePrefix`: v$newVer - $note"
}

git commit -m $commitMsg
git push origin main

Write-Host ""
if ($PreRelease -or $newVer -match '-') {
    Write-Host "Successfully pushed pre-release v$newVer to GitHub!" -ForegroundColor Magenta
    Write-Host "GitHub Actions will automatically create a Pre-release on GitHub & update index.json for VCC / ALCOM."
} else {
    Write-Host "Successfully pushed v$newVer to GitHub!" -ForegroundColor Cyan
    Write-Host "GitHub Actions will automatically build the VPM zip & update index.json for VCC / ALCOM."
}
