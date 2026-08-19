[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path $PSScriptRoot '..\build\release-model-cache'),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $PSScriptRoot 'release-models.json'
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1 -or @($manifest.models).Count -eq 0) {
    throw "Unsupported or empty release model manifest: $manifestPath"
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null

function Test-ModelFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [long]$ExpectedSize,
        [Parameter(Mandatory)] [string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $ExpectedSize) {
        return $false
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return $actualHash.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)
}

foreach ($model in $manifest.models) {
    $targetPath = Join-Path $destination $model.fileName
    $downloadPath = "$targetPath.download"

    if (-not $Force -and (Test-ModelFile `
        -Path $targetPath `
        -ExpectedSize ([long]$model.sizeBytes) `
        -ExpectedSha256 ([string]$model.sha256))) {
        Write-Host "Using verified $($model.id) model: $targetPath"
        continue
    }

    if (Test-Path -LiteralPath $targetPath) {
        Remove-Item -LiteralPath $targetPath -Force
    }
    if (Test-Path -LiteralPath $downloadPath) {
        Remove-Item -LiteralPath $downloadPath -Force
    }

    Write-Host "Downloading $($model.id) from $($model.artifactRepository)"
    try {
        Invoke-WebRequest `
            -Uri $model.downloadUrl `
            -OutFile $downloadPath `
            -Headers @{ 'User-Agent' = 'EMT-Studio-Release-Build' }

        if (-not (Test-ModelFile `
            -Path $downloadPath `
            -ExpectedSize ([long]$model.sizeBytes) `
            -ExpectedSha256 ([string]$model.sha256))) {
            $actualSize = (Get-Item -LiteralPath $downloadPath).Length
            $actualHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
            throw "Model verification failed for $($model.id). Expected $($model.sizeBytes) bytes / $($model.sha256), got $actualSize bytes / $actualHash."
        }

        Move-Item -LiteralPath $downloadPath -Destination $targetPath
        Write-Host "Verified $($model.id): $($model.sha256)"
    }
    catch {
        if (Test-Path -LiteralPath $downloadPath) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
        throw
    }
}

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destination 'model-sources.json') -Force
Write-Host "Release models are ready in $destination"
