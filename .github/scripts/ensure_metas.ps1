param(
    [string]$PackageRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\Packages\com.thecocoa101.cocoas-toolbox")).Path
)

$ErrorActionPreference = "Stop"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Test-IgnoredName([string]$Name) {
    return $Name.StartsWith(".") -or $Name.EndsWith("~") -or ($Name -in @("_site", "dist", "__pycache__"))
}

function New-UnityMetaContent([string]$RelPath, [bool]$IsFolder) {
    $guid = [guid]::NewGuid().ToString("N")
    if ($IsFolder) {
        return @"
fileFormatVersion: 2
guid: $guid
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"@ -replace "`r`n", "`n"
    }

    if ($RelPath.EndsWith(".cs", [System.StringComparison]::OrdinalIgnoreCase)) {
        return @"
fileFormatVersion: 2
guid: $guid
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"@ -replace "`r`n", "`n"
    }

    $importer = "DefaultImporter"
    if ($RelPath.EndsWith(".asmdef", [System.StringComparison]::OrdinalIgnoreCase)) {
        $importer = "AssemblyDefinitionImporter"
    } elseif ($RelPath -eq "package.json") {
        $importer = "PackageManifestImporter"
    } elseif ($RelPath -match "\.(md|txt|json|uss|uxml|shader|cginc|hlsl)$") {
        $importer = "TextScriptImporter"
    }

    return @"
fileFormatVersion: 2
guid: $guid
${importer}:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"@ -replace "`r`n", "`n"
}

function Sync-DirectoryMetas([string]$CurrentDir) {
    $entries = Get-ChildItem -LiteralPath $CurrentDir -Force
    foreach ($entry in $entries) {
        if (Test-IgnoredName $entry.Name) {
            continue
        }

        if ($entry.Extension -eq ".meta") {
            $targetPath = $entry.FullName.Substring(0, $entry.FullName.Length - 5)
            $targetName = [System.IO.Path]::GetFileName($targetPath)
            if ((-not (Test-Path -LiteralPath $targetPath)) -or (Test-IgnoredName $targetName)) {
                Remove-Item -LiteralPath $entry.FullName -Force
                Write-Host "[meta] Removed orphan meta: $($entry.FullName.Substring($PackageRoot.Length + 1))"
            }
            continue
        }

        if ($entry.Extension -eq ".zip") {
            continue
        }

        $metaPath = "$($entry.FullName).meta"
        if (-not (Test-Path -LiteralPath $metaPath)) {
            $rel = $entry.FullName.Substring($PackageRoot.Length + 1).Replace("\", "/")
            $content = New-UnityMetaContent -RelPath $rel -IsFolder $entry.PSIsContainer
            [System.IO.File]::WriteAllText($metaPath, $content, $utf8NoBom)
            Write-Host "[meta] Generated: $rel.meta"
        }

        if ($entry.PSIsContainer) {
            Sync-DirectoryMetas -CurrentDir $entry.FullName
        }
    }
}

Sync-DirectoryMetas -CurrentDir $PackageRoot
