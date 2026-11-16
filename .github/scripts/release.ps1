param(
    [switch]$PreRelease,
    [string]$Package = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$packagesDir = Join-Path $repoRoot "Packages"
Set-Location -LiteralPath $repoRoot

if ($PreRelease) {
    Write-Host "=== Cocoa's Toolbox VPM Pre-Release Helper ===" -ForegroundColor Magenta
} else {
    Write-Host "=== Cocoa's Toolbox VPM Release Helper ===" -ForegroundColor Cyan
}

function Test-TagExists([string]$TagName) {
    $localTag = (git tag -l $TagName)
    if ($localTag) { return $true }
    try {
        $remoteOut = (git ls-remote --tags origin "refs/tags/$TagName" 2>$null)
        if ($remoteOut) { return $true }
    } catch {
        # Ignore network errors when checking remote tags
    }
    return $false
}

# Discover packages
$pkgList = @()
$pkgDirs = Get-ChildItem -LiteralPath $packagesDir -Directory -Filter "com.thecocoa101.*" | Sort-Object Name
foreach ($dir in $pkgDirs) {
    $pkgJsonPath = Join-Path $dir.FullName "package.json"
    if (-not (Test-Path -LiteralPath $pkgJsonPath)) { continue }
    $manifest = Get-Content -LiteralPath $pkgJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $relPkgPath = "Packages/$($dir.Name)"
    $dirty = (git status --porcelain -- $relPkgPath)
    $pkgList += [PSCustomObject]@{
        DirName     = $dir.Name
        FullPath    = $dir.FullName
        Name        = [string]$manifest.name
        DisplayName = [string]$manifest.displayName
        Version     = [string]$manifest.version
        Modified    = [bool]$dirty
    }
}

if ($pkgList.Count -eq 0) {
    throw "No packages found under $packagesDir matching com.thecocoa101.*"
}

$selectedPkg = $null
$pushAll = $false

if (-not [string]::IsNullOrWhiteSpace($Package)) {
    if ($Package -eq "all") {
        $pushAll = $true
    } else {
        $selectedPkg = $pkgList | Where-Object { $_.Name -eq $Package -or $_.DirName -eq $Package } | Select-Object -First 1
        if (-not $selectedPkg) {
            throw "Package '$Package' not found."
        }
    }
} else {
    Write-Host ""
    Write-Host "Select a package to release:" -ForegroundColor White
    for ($i = 0; $i -lt $pkgList.Count; $i++) {
        $item = $pkgList[$i]
        $num = $i + 1
        $modMark = if ($item.Modified) { " *modified" } else { "" }
        $line = ("  [{0,2}] {1,-26} ({2})  v{3}{4}" -f $num, $item.DisplayName, $item.Name, $item.Version, $modMark)
        if ($item.Modified) {
            Write-Host $line -ForegroundColor Yellow
        } else {
            Write-Host $line
        }
    }
    Write-Host "  [ A] Push all current package versions as-is (releases any untagged versions)" -ForegroundColor DarkCyan
    Write-Host ""
    $choice = (Read-Host "Enter number or A").Trim()

    if ($choice -eq "A" -or $choice -eq "a") {
        $pushAll = $true
    } elseif ($choice -match '^\d+$') {
        $idx = [int]$choice - 1
        if ($idx -lt 0 -or $idx -ge $pkgList.Count) {
            throw "Selection out of range: $choice"
        }
        $selectedPkg = $pkgList[$idx]
    } else {
        throw "Invalid selection: $choice"
    }
}

if ($pushAll) {
    & (Join-Path $PSScriptRoot "ensure_metas.ps1")

    git add -A
    Write-Host ""
    git status --short
    Write-Host ""

    $hasChanges = (git status --porcelain)
    if ($hasChanges) {
        $note = Read-Host "Commit message note (Optional, press Enter for default)"
        $commitMsg = if ([string]::IsNullOrWhiteSpace($note)) {
            "release: publish packages"
        } else {
            "release: $note"
        }
        git commit -m $commitMsg
    } else {
        Write-Host "No uncommitted changes; pushing current main..." -ForegroundColor Yellow
    }
    git push origin main

    Write-Host ""
    Write-Host "Successfully pushed to GitHub!" -ForegroundColor Cyan
    Write-Host "GitHub Actions will automatically build & release any package versions not yet on GitHub Releases, and update index.json."
    exit 0
}

# Single package release flow
$pkgRoot = $selectedPkg.FullPath
$pkgName = $selectedPkg.Name

& (Join-Path $PSScriptRoot "ensure_metas.ps1") -PackageRoot $pkgRoot

$pkgPath = Join-Path $pkgRoot "package.json"
$pkgRaw = [System.IO.File]::ReadAllText($pkgPath, [System.Text.Encoding]::UTF8)
if ($pkgRaw -notmatch '"version"\s*:\s*"([^"]+)"') {
    throw "Could not find 'version' field in $pkgPath."
}
$currentVer = $Matches[1]

if ($PreRelease) {
    if ($currentVer -match '^(\d+\.\d+\.\d+)-([A-Za-z0-9.-]+?)\.(\d+)$') {
        $baseVer = $Matches[1]
        $tagSuffix = $Matches[2]
        $num = [int]$Matches[3] + 1
        $defaultVer = "$baseVer-$tagSuffix.$num"
    } elseif ($currentVer -match '^(\d+\.\d+\.\d+)-([A-Za-z0-9.-]+)$') {
        $baseVer = $Matches[1]
        $tagSuffix = $Matches[2]
        $defaultVer = "$baseVer-$tagSuffix.1"
    } else {
        $defaultVer = "$currentVer-preview.1"
    }
} else {
    if ($currentVer -match '-') {
        $defaultVer = $currentVer.Split("-")[0]
    } else {
        $hasTag = Test-TagExists "$pkgName-v$currentVer"
        if (-not $hasTag) {
            $defaultVer = $currentVer
        } else {
            $parts = $currentVer.Split(".")
            $defaultVer = "{0}.{1}.{2}" -f [int]$parts[0], [int]$parts[1], ([int]$parts[2] + 1)
        }
    }
}

Write-Host ""
Write-Host "Selected package : $($selectedPkg.DisplayName) ($pkgName)" -ForegroundColor Cyan
Write-Host "Current version  : $currentVer" -ForegroundColor Yellow
$promptLabel = if ($PreRelease) { "New pre-release version" } else { "New version" }
$inputVer = Read-Host "$promptLabel (Press Enter for $defaultVer)"
$newVer = if ([string]::IsNullOrWhiteSpace($inputVer)) { $defaultVer } else { $inputVer.Trim().TrimStart("v") }

if ($newVer -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid SemVer format: $newVer (Example: 1.1.0 or 1.1.0-preview.1)"
}

$note = Read-Host "Release note for CHANGELOG (Optional, press Enter to skip)"

# Update package.json
$updatedPkg = [regex]::Replace(
    $pkgRaw,
    '("version"\s*:\s*)"[^"]+"',
    "`${1}`"$newVer`"",
    1
)
[System.IO.File]::WriteAllText($pkgPath, $updatedPkg, $utf8NoBom)
Write-Host "Updated $pkgName/package.json -> $newVer" -ForegroundColor Green

# Prepend CHANGELOG.md entry
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
    Write-Host "Updated $pkgName/CHANGELOG.md" -ForegroundColor Green
}

# Commit & Push
git add -A
Write-Host ""
git status --short
Write-Host ""

$typePrefix = if ($PreRelease -or $newVer -match '-') { "prerelease" } else { "release" }
$commitMsg = if ([string]::IsNullOrWhiteSpace($note)) {
    "$typePrefix`: $pkgName v$newVer"
} else {
    "$typePrefix`: $pkgName v$newVer - $note"
}

git commit -m $commitMsg
git push origin main

Write-Host ""
if ($PreRelease -or $newVer -match '-') {
    Write-Host "Successfully pushed $pkgName pre-release v$newVer to GitHub!" -ForegroundColor Magenta
    Write-Host "GitHub Actions will automatically create a Pre-release ($pkgName-v$newVer) & update index.json for VCC / ALCOM."
} else {
    Write-Host "Successfully pushed $pkgName v$newVer to GitHub!" -ForegroundColor Cyan
    Write-Host "GitHub Actions will automatically build the VPM zip ($pkgName-v$newVer) & update index.json for VCC / ALCOM."
}
