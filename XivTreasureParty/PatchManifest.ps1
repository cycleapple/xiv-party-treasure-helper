param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][string]$AssemblyName,
    [Parameter(Mandatory=$true)][int]$ApiLevel
)

$ErrorActionPreference = 'Stop'

$candidates = @(
    (Join-Path $OutputPath "$AssemblyName.json"),
    (Join-Path $OutputPath "$AssemblyName\$AssemblyName.json")
)

Write-Host "PatchManifest: checking candidates (OutputPath='$OutputPath', AssemblyName='$AssemblyName')"
foreach ($c in $candidates) { Write-Host "  candidate: $c exists=$(Test-Path -LiteralPath $c)" }

foreach ($path in $candidates) {
    if (Test-Path -LiteralPath $path) {
        $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $json.DalamudApiLevel = $ApiLevel
        $jsonText = $json | ConvertTo-Json -Depth 20
        [System.IO.File]::WriteAllText($path, $jsonText, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "Patched DalamudApiLevel=$ApiLevel -> $path"
    }
}

# 同時處理 DalamudPackager 生成的 latest.zip 裡的 manifest
$zipPath = Join-Path $OutputPath "$AssemblyName\latest.zip"
if (Test-Path -LiteralPath $zipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tmpDir = Join-Path $env:TEMP ("dalamud-patch-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $tmpDir)
        $jsonInZip = Join-Path $tmpDir "$AssemblyName.json"
        if (Test-Path -LiteralPath $jsonInZip) {
            $json = Get-Content -LiteralPath $jsonInZip -Raw -Encoding UTF8 | ConvertFrom-Json
            $json.DalamudApiLevel = $ApiLevel
            $jsonText = $json | ConvertTo-Json -Depth 20
            [System.IO.File]::WriteAllText($jsonInZip, $jsonText, (New-Object System.Text.UTF8Encoding $false))
            Remove-Item -LiteralPath $zipPath
            [System.IO.Compression.ZipFile]::CreateFromDirectory($tmpDir, $zipPath)
            Write-Host "Patched DalamudApiLevel=$ApiLevel in zip -> $zipPath"
        }
    } finally {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
