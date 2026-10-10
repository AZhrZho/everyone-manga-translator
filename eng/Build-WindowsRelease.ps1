[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$ForceModelDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'The Windows installer must currently be built on Windows.'
}

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $workspaceRoot 'MangaTranslator.slnx'
$apiProject = Join-Path $workspaceRoot 'apps\MangaTranslator.Api\MangaTranslator.Api.csproj'
$clientDirectory = Join-Path $workspaceRoot 'apps\emt-client'
$desktopDirectory = Join-Path $workspaceRoot 'apps\emt-desktop'
$stagingDirectory = Join-Path $workspaceRoot 'build\windows-release'
$apiDirectory = Join-Path $stagingDirectory 'api'
$modelsDirectory = Join-Path $stagingDirectory 'models'
$modelCacheDirectory = Join-Path $workspaceRoot 'build\release-model-cache'
$artifactsDirectory = Join-Path $workspaceRoot 'artifacts\windows'
$desktopPackage = Get-Content -LiteralPath (Join-Path $desktopDirectory 'package.json') -Raw | ConvertFrom-Json
$version = [string]$desktopPackage.version

function Assert-WorkspaceChild {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $workspacePrefix = $workspaceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $fullPath"
    }
    return $fullPath
}

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = Assert-WorkspaceChild $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Copy-BundledFontLibrary {
    $sourceDirectory = Join-Path $workspaceRoot 'assets\font-library'
    $targetDirectory = Join-Path $apiDirectory 'font-library'
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'font_catalog.json') -PathType Leaf)) {
        throw "Bundled font catalog is missing: $sourceDirectory"
    }

    Reset-GeneratedDirectory $targetDirectory
    Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $targetDirectory -Recurse -Force
    $catalog = Get-Content -LiteralPath (Join-Path $targetDirectory 'font_catalog.json') -Raw | ConvertFrom-Json
    foreach ($font in @($catalog.fonts)) {
        $fontPath = Join-Path $targetDirectory ([string]$font.regular.relativePath)
        $licensePath = Join-Path $targetDirectory ([string]$font.source.licenseRelativePath)
        if (-not (Test-Path -LiteralPath $fontPath -PathType Leaf)) {
            throw "Bundled font file is missing: $fontPath"
        }
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "Bundled font license is missing: $licensePath"
        }
        $fontHash = (Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash
        $licenseHash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash
        if (-not $fontHash.Equals([string]$font.regular.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Bundled font checksum mismatch: $fontPath"
        }
        if (-not $licenseHash.Equals([string]$font.source.licenseSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Bundled font license checksum mismatch: $licensePath"
        }
    }
    Write-Host "Verified bundled font library: $targetDirectory"
}

function Invoke-External {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    Write-Host "> $Command $($Arguments -join ' ')"
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command exited with code $LASTEXITCODE."
    }
}

function Invoke-ExternalWithRetry {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [int]$MaximumAttempts = 3
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        Write-Host "> $Command $($Arguments -join ' ') (attempt $attempt/$MaximumAttempts)"
        & $Command @Arguments
        if ($LASTEXITCODE -eq 0) {
            return
        }
        if ($attempt -eq $MaximumAttempts) {
            throw "$Command failed after $MaximumAttempts attempts (last exit code: $LASTEXITCODE)."
        }
        Write-Warning "$Command exited with code $LASTEXITCODE; retrying in 3 seconds."
        Start-Sleep -Seconds 3
    }
}

function Test-PackagedApi {
    param([Parameter(Mandatory)] [string]$PublishedApiDirectory)

    $smokeRoot = Join-Path $stagingDirectory 'installed-environment-smoke'
    Reset-GeneratedDirectory $smokeRoot
    $standardOutput = Join-Path $smokeRoot 'api.stdout.log'
    $standardError = Join-Path $smokeRoot 'api.stderr.log'
    $apiExecutable = Join-Path $PublishedApiDirectory 'MangaTranslator.Api.exe'
    $processEnvironment = @{
        ASPNETCORE_URLS = 'http://127.0.0.1:0'
        EMT_HOST_KIND = 'desktop'
        EMT_USER_DATA_ROOT = (Join-Path $smokeRoot 'user-data')
        EMT_CACHE_ROOT = (Join-Path $smokeRoot 'cache')
        EMT_LOGS_ROOT = (Join-Path $smokeRoot 'logs')
        EMT_TEMP_ROOT = (Join-Path $smokeRoot 'temp')
        EMT_WINDOWS_FONTS_ROOT = (Join-Path $smokeRoot 'empty-windows-fonts')
        MANGA_TRANSLATOR_FONT_LIBRARY = $null
    }
    New-Item -ItemType Directory -Path $processEnvironment.EMT_WINDOWS_FONTS_ROOT -Force | Out-Null
    $process = Start-Process `
        -FilePath $apiExecutable `
        -WorkingDirectory $PublishedApiDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $standardOutput `
        -RedirectStandardError $standardError `
        -Environment $processEnvironment `
        -PassThru

    try {
        $baseUrl = $null
        for ($attempt = 1; $attempt -le 80; $attempt++) {
            if ($process.HasExited) {
                $errorText = if (Test-Path -LiteralPath $standardError) {
                    Get-Content -LiteralPath $standardError -Raw
                } else { '' }
                throw "Published API exited with code $($process.ExitCode). $errorText"
            }
            if (Test-Path -LiteralPath $standardOutput) {
                # A redirected stream can create the file before its first bytes are
                # flushed; Get-Content may therefore return $null for an empty file.
                # [regex]::Match requires a non-null input string.
                $outputText = [string](Get-Content -LiteralPath $standardOutput -Raw)
                $readyMatch = [regex]::Match(
                    $outputText,
                    '(?m)^EMT_API_READY=(http://127\.0\.0\.1:\d+)\r?$')
                if ($readyMatch.Success) {
                    $baseUrl = $readyMatch.Groups[1].Value
                    break
                }
            }
            Start-Sleep -Milliseconds 250
        }
        if ([string]::IsNullOrWhiteSpace($baseUrl)) {
            throw 'Published API did not report its dynamically assigned listening address.'
        }
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 5
        }
        catch {
            throw "Published API failed its dynamic-port health check at $baseUrl. $($_.Exception.Message)"
        }
        if ($null -eq $health -or $health.status -ne 'ok') {
            throw 'Published API did not pass its health check.'
        }

        $settings = Invoke-RestMethod -Uri "$baseUrl/api/settings" -TimeoutSec 5
        if (@($settings.fonts).Count -ne 1 -or $settings.fonts[0].displayName -ne '快乐体') {
            throw 'Clean installed-environment smoke test did not initialize the bundled KuaiLe font.'
        }
        $index = Invoke-WebRequest -Uri "$baseUrl/" -UseBasicParsing -TimeoutSec 5
        if ($index.StatusCode -ne 200 -or $index.Content -notmatch '<div id="root"></div>') {
            throw 'Published API did not serve the production client index.'
        }

        $catalog = Join-Path $processEnvironment.EMT_USER_DATA_ROOT 'font-library\font_catalog.json'
        if (-not (Test-Path -LiteralPath $catalog -PathType Leaf)) {
            throw 'Published API did not initialize the writable font library.'
        }
        Write-Host "Published API installed-environment smoke test passed: $baseUrl"
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}

function Test-PackagedDesktop {
    param([Parameter(Mandatory)] [string]$UnpackedDirectory)

    $smokeRoot = Join-Path $stagingDirectory 'desktop-smoke'
    Reset-GeneratedDirectory $smokeRoot
    $processEnvironment = @{
        EMT_DESKTOP_SMOKE_TEST = '1'
        EMT_USER_DATA_ROOT = (Join-Path $smokeRoot 'user-data')
        EMT_CACHE_ROOT = (Join-Path $smokeRoot 'cache')
        EMT_LOGS_ROOT = (Join-Path $smokeRoot 'logs')
        EMT_TEMP_ROOT = (Join-Path $smokeRoot 'temp')
        MANGA_TRANSLATOR_FONT_LIBRARY = $null
    }
    $desktopExecutable = Join-Path $UnpackedDirectory 'EMT Studio.exe'
    $smokeProcesses = @(1, 2 | ForEach-Object {
        $instance = $_
        $standardOutput = Join-Path $smokeRoot "desktop-$instance.stdout.log"
        $standardError = Join-Path $smokeRoot "desktop-$instance.stderr.log"
        $process = Start-Process `
            -FilePath $desktopExecutable `
            -WorkingDirectory $UnpackedDirectory `
            -WindowStyle Hidden `
            -RedirectStandardOutput $standardOutput `
            -RedirectStandardError $standardError `
            -Environment $processEnvironment `
            -PassThru
        [pscustomobject]@{
            Instance = $instance
            Process = $process
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    })

    try {
        $apiAddresses = foreach ($item in $smokeProcesses) {
            if (-not $item.Process.WaitForExit(60000)) {
                throw "Packaged desktop instance $($item.Instance) did not finish within 60 seconds."
            }
            if ($item.Process.ExitCode -ne 0) {
                $errorText = if (Test-Path -LiteralPath $item.StandardError) {
                    Get-Content -LiteralPath $item.StandardError -Raw
                } else { '' }
                throw "Packaged desktop instance $($item.Instance) exited with code $($item.Process.ExitCode). $errorText"
            }
            $outputText = [string](Get-Content -LiteralPath $item.StandardOutput -Raw)
            if ($outputText -notmatch 'Renderer host bridge: desktop') {
                throw "Packaged desktop instance $($item.Instance) exited without confirming the renderer bridge."
            }
            $readyMatch = [regex]::Match(
                $outputText,
                'EMT_API_READY=(http://127\.0\.0\.1:\d+)')
            if (-not $readyMatch.Success) {
                throw "Packaged desktop instance $($item.Instance) did not report a dynamic API address."
            }
            $readyMatch.Groups[1].Value
        }
        if (@($apiAddresses | Select-Object -Unique).Count -ne 2) {
            throw 'Packaged desktop instances did not receive distinct API ports.'
        }
        $catalogPath = Join-Path $processEnvironment.EMT_USER_DATA_ROOT 'font-library\font_catalog.json'
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
        if (-not (@($catalog.fonts) | Where-Object displayName -eq '快乐体')) {
            throw 'Packaged desktop did not seed the bundled KuaiLe font.'
        }
        $windowsYouYuan = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Fonts)) 'SIMYOU.TTF'
        if ((Test-Path -LiteralPath $windowsYouYuan) -and -not (@($catalog.fonts) | Where-Object displayName -eq '幼圆')) {
            throw 'Packaged desktop did not import the available Windows YouYuan font.'
        }
        Write-Host "Packaged Electron multi-instance smoke test passed: $($apiAddresses -join ', ')"
    }
    finally {
        foreach ($item in $smokeProcesses) {
            if (-not $item.Process.HasExited) {
                & taskkill.exe /pid $item.Process.Id /t /f | Out-Null
            }
        }
    }
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$npm = (Get-Command npm.cmd -ErrorAction Stop).Source

Reset-GeneratedDirectory $stagingDirectory
Reset-GeneratedDirectory $artifactsDirectory

if (-not $env:ELECTRON_MIRROR) {
    $env:ELECTRON_MIRROR = 'https://npmmirror.com/mirrors/electron/'
}
if (-not $env:ELECTRON_BUILDER_BINARIES_MIRROR) {
    $env:ELECTRON_BUILDER_BINARIES_MIRROR = 'https://npmmirror.com/mirrors/electron-builder-binaries/'
}
if (-not $env:CSC_LINK) {
    $env:CSC_IDENTITY_AUTO_DISCOVERY = 'false'
}

Write-Host "Building EMT Studio $version for Windows x64"
Invoke-External $npm @('ci', '--prefix', $clientDirectory)
Invoke-External $npm @('ci', '--prefix', $desktopDirectory)
Invoke-External $dotnet @('clean', $solutionPath, '--configuration', 'Release', '--verbosity', 'quiet')
Invoke-External $dotnet @('restore', $solutionPath)

if (-not $SkipTests) {
    Invoke-External $dotnet @('test', $solutionPath, '--configuration', 'Release', '--no-restore')
    Invoke-External $npm @('--prefix', $clientDirectory, 'test')
    Invoke-External $npm @('--prefix', $clientDirectory, 'run', 'lint')
    Invoke-External $npm @('--prefix', $desktopDirectory, 'test')
}

Invoke-External $npm @('--prefix', $clientDirectory, 'run', 'build')
Invoke-External $dotnet @(
    'publish',
    $apiProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    "--property:Version=$version",
    '--output', $apiDirectory
)

$clientOutput = Join-Path $clientDirectory 'dist'
$wwwroot = Join-Path $apiDirectory 'wwwroot'
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $clientOutput '*') -Destination $wwwroot -Recurse -Force

Copy-BundledFontLibrary

Test-PackagedApi $apiDirectory

$modelArguments = @{ DestinationDirectory = $modelCacheDirectory }
if ($ForceModelDownload) {
    $modelArguments.Force = $true
}
& (Join-Path $PSScriptRoot 'Download-ReleaseModels.ps1') @modelArguments
New-Item -ItemType Directory -Path $modelsDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $modelCacheDirectory '*') -Destination $modelsDirectory -Recurse -Force

Invoke-ExternalWithRetry $npm @('--prefix', $desktopDirectory, 'run', 'dist:win')

$unpackedDirectory = Join-Path $artifactsDirectory 'win-unpacked'
Test-PackagedDesktop $unpackedDirectory
if (Test-Path -LiteralPath $unpackedDirectory) {
    Remove-Item -LiteralPath (Assert-WorkspaceChild $unpackedDirectory) -Recurse -Force
}

$installers = @(Get-ChildItem -LiteralPath $artifactsDirectory -Filter '*Setup.exe' -File)
if ($installers.Count -ne 1) {
    throw "Expected one Windows installer in $artifactsDirectory, found $($installers.Count)."
}

$installerHash = Get-FileHash -LiteralPath $installers[0].FullName -Algorithm SHA256
$sourceCommit = (& git -C $workspaceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not determine the source commit.' }
$sourceStatus = @(& git -C $workspaceRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Could not determine the source working tree status.' }
$manifest = [ordered]@{
    version = $version
    sourceCommit = $sourceCommit
    sourceDirty = $sourceStatus.Count -gt 0
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    testsPassed = -not [bool]$SkipTests
    packagedApiSmokePassed = $true
    packagedDesktopSmokePassed = $true
    installer = $installers[0].Name
    installerSizeBytes = $installers[0].Length
    installerSha256 = $installerHash.Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactsDirectory 'release-manifest.json') -Encoding utf8
Write-Host ''
Write-Host "Windows installer: $($installers[0].FullName)"
Write-Host "SHA-256: $($installerHash.Hash.ToLowerInvariant())"
