[CmdletBinding()]
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $root

function Invoke-ProcessCaptured {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [hashtable]$EnvironmentOverrides = @{}
    )

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8NoBom
    $psi.StandardErrorEncoding = $utf8NoBom

    foreach ($arg in $Arguments) {
        [void]$psi.ArgumentList.Add($arg)
    }

    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            [void]$psi.Environment.Remove([string]$entry.Key)
        } else {
            $psi.Environment[[string]$entry.Key] = [string]$entry.Value
        }
    }

    $proc = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $proc) {
        throw "Error: Failed to start process: $FilePath"
    }

    try {
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        $stdoutText = $proc.StandardOutput.ReadToEnd()
        $stderrText = $stderrTask.GetAwaiter().GetResult()
        $proc.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $proc.ExitCode
            Stdout   = $stdoutText.Trim()
            Stderr   = $stderrText.Trim()
        }
    } finally {
        $proc.Dispose()
    }
}

$stageDir = $null
try {
    # Disable telemetry, logos, and persistent MSBuild / compiler servers.
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:MSBUILDDISABLENODEREUSE = "1"
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = "0"
    $env:UseSharedCompilation = "false"

    # Resolve dotnet executable: DOTNET_ROOT -> .deps/dotnet -> PATH.
    $dotnetBin = $null
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidate = Join-Path $env:DOTNET_ROOT "dotnet.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $dotnetBin = $candidate
        }
    }
    if (-not $dotnetBin) {
        $localDotnet = Join-Path $root ".deps\dotnet\dotnet.exe"
        if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
            $env:DOTNET_ROOT = Join-Path $root ".deps\dotnet"
            $dotnetBin = $localDotnet
        }
    }
    if (-not $dotnetBin) {
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($dotnetCmd) {
            $dotnetBin = $dotnetCmd.Source
        }
    }
    if (-not $dotnetBin) {
        throw "Error: dotnet executable not found (install .NET 10 SDK or set DOTNET_ROOT)"
    }

    # Resolve package version: explicit -Version -> exact git tag on HEAD -> VERSION file.
    $packageVersion = $Version.Trim()
    if ($packageVersion.StartsWith("v", [System.StringComparison]::Ordinal)) {
        $packageVersion = $packageVersion.Substring(1)
    }
    if ([string]::IsNullOrWhiteSpace($packageVersion)) {
        $rawTag = ""
        try {
            $tagOutput = & git describe --tags --exact-match HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $tagOutput) {
                $rawTag = ($tagOutput | Select-Object -First 1).Trim()
            }
        } catch {
            $rawTag = ""
        }

        if (-not [string]::IsNullOrWhiteSpace($rawTag)) {
            $packageVersion = if ($rawTag.StartsWith("v", [System.StringComparison]::Ordinal)) {
                $rawTag.Substring(1)
            } else {
                $rawTag
            }
        } else {
            $versionFile = Join-Path $root "VERSION"
            if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
                throw "Error: No exact git tag found and VERSION file is missing: $versionFile"
            }
            $packageVersion = (Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8).Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($packageVersion)) {
        throw "Error: Resolved package version is empty"
    }

    # Resolve short git commit hash for embedding into assembly metadata.
    $gitCommit = "unknown"
    try {
        $commitOutput = & git rev-parse --short=12 HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $commitOutput) {
            $trimmedCommit = ($commitOutput | Select-Object -First 1).Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmedCommit)) {
                $gitCommit = $trimmedCommit
            }
        }
    } catch {
        $gitCommit = "unknown"
    }

    $stageBase = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        $env:RUNNER_TEMP
    } elseif (-not [string]::IsNullOrWhiteSpace($env:TEMP)) {
        $env:TEMP
    } else {
        Join-Path $root ".tmp-win-pkg"
    }
    $stageDir = Join-Path $stageBase ("ffpr-win-pkg-" + [System.Guid]::NewGuid().ToString("N"))
    $dotnetCliOut = Join-Path $stageDir "dotnet-publish-cli"
    $dotnetGuiOut = Join-Path $stageDir "dotnet-publish-gui"
    $packageDir = Join-Path $stageDir "package"
    $distDir = Join-Path $root "dist"
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)

    New-Item -ItemType Directory -Path $dotnetCliOut -Force | Out-Null
    New-Item -ItemType Directory -Path $dotnetGuiOut -Force | Out-Null
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    Write-Host "==> Packaging version: $packageVersion (git: $gitCommit)"
    Write-Host "==> Publishing ffpr-export.exe (win-x64 self-contained single-file, sibling native DLLs without TEMP extraction)"

    $cliCsprojPath = Join-Path $root "src\FfprExport\FfprExport.csproj"
    & $dotnetBin publish $cliCsprojPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:AssemblyName=ffpr-export `
        "-p:Version=$packageVersion" `
        "-p:FfprGitCommit=$gitCommit" `
        -o $dotnetCliOut
    if ($LASTEXITCODE -ne 0) {
        throw "Error: dotnet publish failed for $cliCsprojPath (exit code $LASTEXITCODE)"
    }

    $ffprExportExe = Join-Path $dotnetCliOut "ffpr-export.exe"
    if (-not (Test-Path -LiteralPath $ffprExportExe -PathType Leaf)) {
        throw "Error: dotnet publish did not produce $ffprExportExe"
    }

    Write-Host "==> Publishing ffpr-export-gui.exe (win-x64 WinForms self-contained single-file, sibling native DLLs without TEMP extraction)"

    $guiCsprojPath = Join-Path $root "src\FfprExport.Gui\FfprExport.Gui.csproj"
    & $dotnetBin publish $guiCsprojPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:AssemblyName=ffpr-export-gui `
        "-p:Version=$packageVersion" `
        "-p:FfprGitCommit=$gitCommit" `
        -o $dotnetGuiOut
    if ($LASTEXITCODE -ne 0) {
        throw "Error: dotnet publish failed for $guiCsprojPath (exit code $LASTEXITCODE)"
    }

    $ffprGuiExe = Join-Path $dotnetGuiOut "ffpr-export-gui.exe"
    if (-not (Test-Path -LiteralPath $ffprGuiExe -PathType Leaf)) {
        throw "Error: dotnet publish did not produce $ffprGuiExe"
    }

    foreach ($required in @("known-builds.json", "README.md", "LICENSE")) {
        $reqPath = Join-Path $root $required
        if (-not (Test-Path -LiteralPath $reqPath -PathType Leaf)) {
            throw "Error: Missing required packaging file: $reqPath"
        }
    }

    Copy-Item -LiteralPath $ffprExportExe -Destination (Join-Path $packageDir "ffpr-export.exe") -Force
    Copy-Item -LiteralPath $ffprGuiExe -Destination (Join-Path $packageDir "ffpr-export-gui.exe") -Force

    # Copy all sibling native DLLs (such as e_sqlite3.dll) from both publish directories.
    $copiedDllHashes = @{}
    foreach ($pubDir in @($dotnetCliOut, $dotnetGuiOut)) {
        foreach ($dll in (Get-ChildItem -LiteralPath $pubDir -Filter "*.dll" -File)) {
            $dllHash = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($copiedDllHashes.ContainsKey($dll.Name)) {
                if ($copiedDllHashes[$dll.Name] -ne $dllHash) {
                    throw "Error: Native library $($dll.Name) differs between CLI and GUI publish directories; refusing to overwrite"
                }
                continue
            }
            Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $packageDir $dll.Name) -Force
            $copiedDllHashes[$dll.Name] = $dllHash
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $packageDir "e_sqlite3.dll") -PathType Leaf)) {
        throw "Error: Packaging directory is missing native library e_sqlite3.dll (verify IncludeNativeLibrariesForSelfExtract=false)"
    }

    Copy-Item -LiteralPath (Join-Path $root "known-builds.json") -Destination (Join-Path $packageDir "known-builds.json") -Force
    Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $packageDir "README.md") -Force
    Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination (Join-Path $packageDir "LICENSE") -Force

    Write-Host "==> Running post-package GUI self-test: ffpr-export-gui.exe --self-test --require-cli"
    $packagedGuiExe = Join-Path $packageDir "ffpr-export-gui.exe"
    $packagedCliExe = Join-Path $packageDir "ffpr-export.exe"
    $selfTestOut = Join-Path $stageDir "gui-selftest.stdout.txt"
    $selfTestErr = Join-Path $stageDir "gui-selftest.stderr.txt"
    $previousAgpHome = $env:AGP_HOME
    try {
        $env:AGP_HOME = Join-Path $stageDir "agp-home"
        $selfTestProc = Start-Process `
            -FilePath $packagedGuiExe `
            -ArgumentList @("--self-test", "--require-cli") `
            -WorkingDirectory $packageDir `
            -RedirectStandardOutput $selfTestOut `
            -RedirectStandardError $selfTestErr `
            -Wait `
            -PassThru `
            -NoNewWindow
    } finally {
        if ($null -eq $previousAgpHome) {
            Remove-Item Env:AGP_HOME -ErrorAction SilentlyContinue
        } else {
            $env:AGP_HOME = $previousAgpHome
        }
    }
    if (Test-Path -LiteralPath $selfTestOut -PathType Leaf) {
        $outText = [System.IO.File]::ReadAllText($selfTestOut, $utf8NoBom).Trim()
        if (-not [string]::IsNullOrWhiteSpace($outText)) {
            Write-Host $outText
        }
    }
    if (Test-Path -LiteralPath $selfTestErr -PathType Leaf) {
        $errText = [System.IO.File]::ReadAllText($selfTestErr, $utf8NoBom).Trim()
        if (-not [string]::IsNullOrWhiteSpace($errText)) {
            [Console]::Error.WriteLine($errText)
        }
    }
    if ($selfTestProc.ExitCode -ne 0) {
        throw "Error: Packaged ffpr-export-gui.exe --self-test --require-cli failed (exit code $($selfTestProc.ExitCode))"
    }

    Write-Host "==> Running unwritable TEMP simulation self-test (TEMP/TMP pointing to a regular file)"
    $blockedTempPath = Join-Path $stageDir "blocked-temp-file"
    [System.IO.File]::WriteAllText($blockedTempPath, "blocked-temp`n", $utf8NoBom)
    $noTempEnv = @{
        "TEMP"     = $blockedTempPath
        "TMP"      = $blockedTempPath
        "AGP_HOME" = (Join-Path $stageDir "agp-home-notemp")
    }

    $guiNoTemp = Invoke-ProcessCaptured `
        -FilePath $packagedGuiExe `
        -Arguments @("--self-test", "--require-cli") `
        -WorkingDirectory $packageDir `
        -EnvironmentOverrides $noTempEnv
    if (-not [string]::IsNullOrWhiteSpace($guiNoTemp.Stdout)) {
        Write-Host $guiNoTemp.Stdout
    }
    if (-not [string]::IsNullOrWhiteSpace($guiNoTemp.Stderr)) {
        [Console]::Error.WriteLine($guiNoTemp.Stderr)
    }
    if ($guiNoTemp.ExitCode -ne 0) {
        throw "Error: Packaged ffpr-export-gui.exe --self-test --require-cli failed under blocked TEMP (exit code $($guiNoTemp.ExitCode))"
    }

    $cliHelpNoTemp = Invoke-ProcessCaptured `
        -FilePath $packagedCliExe `
        -Arguments @("--help") `
        -WorkingDirectory $packageDir `
        -EnvironmentOverrides $noTempEnv
    if ($cliHelpNoTemp.ExitCode -ne 0) {
        if (-not [string]::IsNullOrWhiteSpace($cliHelpNoTemp.Stderr)) {
            [Console]::Error.WriteLine($cliHelpNoTemp.Stderr)
        }
        throw "Error: Packaged ffpr-export.exe --help failed under blocked TEMP (exit code $($cliHelpNoTemp.ExitCode))"
    }

    # Generate English quick-start guide with concise Chinese and Japanese appendices (UTF-8 without BOM, CRLF endings).
    $guideLines = @(
        'FFPR Export (FINAL FANTASY I-VI Pixel Remaster) - Windows Quick Start',
        '',
        '1. Extract the entire zip archive to a regular folder (do not run directly from inside the zip).',
        '2. Keep ffpr-export-gui.exe, ffpr-export.exe, sibling native DLLs (e.g. e_sqlite3.dll), and known-builds.json in the same folder.',
        '3. Double-click ffpr-export-gui.exe. It auto-scans your Steam libraries for FF1-FF6 and selects them by default.',
        '4. Use the Language dropdown in the top-right corner to switch between English, Simplified Chinese, and Japanese.',
        '5. Click [Start Export] to build SQLite databases sequentially. Default output: %LOCALAPPDATA%\agp\ffpr-export\output\<game>-<buildid>\',
        '6. For CLI options (--lang, AGP_HOME, AGP_LANG) and full documentation, see README.md in this folder.',
        '',
        '--- 简体中文简明说明 ---',
        '1. 请将压缩包完整解压到任意文件夹，并保持所有文件（含 e_sqlite3.dll）在同一目录。',
        '2. 双击 ffpr-export-gui.exe，可在右上角切换语言，确认勾选的游戏后点击 [开始导出]。',
        '3. 默认输出目录：%LOCALAPPDATA%\agp\ffpr-export\output\<游戏代号>-<构建号>\。详见 README.md。',
        '',
        '--- 日本語クイックガイド ---',
        '1. zip 全体を任意のフォルダに展開し、すべてのファイル（e_sqlite3.dll を含む）を同じフォルダに置いてください。',
        '2. ffpr-export-gui.exe を起動し、右上で言語を切り替え、ゲームを確認して [エクスポート開始] を押します。',
        '3. 既定の出力先：%LOCALAPPDATA%\agp\ffpr-export\output\<ゲームID>-<ビルドID>\。詳細は README.md を参照してください。'
    )
    $guideName = "README-Windows.txt"
    $guidePath = Join-Path $packageDir $guideName
    [System.IO.File]::WriteAllText($guidePath, (($guideLines -join "`r`n") + "`r`n"), $utf8NoBom)

    # Write inner SHA256SUMS over every packaged file in ordinal order (UTF-8 without BOM, LF endings).
    $innerFiles = @(
        Get-ChildItem -LiteralPath $packageDir -File |
            Where-Object { $_.Name -ne "SHA256SUMS" } |
            Select-Object -ExpandProperty Name
    )
    [System.Array]::Sort($innerFiles, [System.StringComparer]::Ordinal)

    $innerLines = foreach ($name in $innerFiles) {
        $filePath = Join-Path $packageDir $name
        $digest = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $name"
    }
    $innerSumsPath = Join-Path $packageDir "SHA256SUMS"
    [System.IO.File]::WriteAllText($innerSumsPath, (($innerLines -join "`n") + "`n"), $utf8NoBom)

    $zipName = "ffpr-export-$packageVersion-win-x64.zip"
    $zipPath = Join-Path $distDir $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "==> Creating archive: $zipPath"
    $zipEntries = @(
        Get-ChildItem -LiteralPath $packageDir -File |
            Select-Object -ExpandProperty Name
    )
    [System.Array]::Sort($zipEntries, [System.StringComparer]::Ordinal)

    $archive = [System.IO.Compression.ZipFile]::Open(
        $zipPath,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $utf8NoBom
    )
    try {
        foreach ($name in $zipEntries) {
            $sourceFile = Join-Path $packageDir $name
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $sourceFile,
                $name,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }

    # Inspect generated zip entries and confirm native library presence.
    Write-Host "==> Archive entries:"
    $verifyArchive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = @($verifyArchive.Entries | Select-Object -ExpandProperty FullName)
        $verifyArchive.Entries | Select-Object FullName, Length, CompressedLength | Format-Table -AutoSize
        if ($entryNames -notcontains "e_sqlite3.dll") {
            throw "Error: Generated archive is missing native library e_sqlite3.dll"
        }
        if ($entryNames -notcontains "README-Windows.txt") {
            throw "Error: Generated archive is missing README-Windows.txt"
        }
    } finally {
        $verifyArchive.Dispose()
    }

    # Copy known-builds.json and generate release-level dist/SHA256SUMS for GitHub Releases upload.
    $distKnownBuilds = Join-Path $distDir "known-builds.json"
    Copy-Item -LiteralPath (Join-Path $root "known-builds.json") -Destination $distKnownBuilds -Force

    $releaseFiles = @($zipName, "known-builds.json")
    $releaseLines = foreach ($name in $releaseFiles) {
        $filePath = Join-Path $distDir $name
        $digest = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $name"
    }
    $distSumsPath = Join-Path $distDir "SHA256SUMS"
    [System.IO.File]::WriteAllText($distSumsPath, (($releaseLines -join "`n") + "`n"), $utf8NoBom)

    Write-Host "==> Packaging complete: $zipPath"
    Get-Item -LiteralPath $zipPath, $distSumsPath, $distKnownBuilds | Format-Table Name, Length -AutoSize
    Write-Host "==> Packaged files and checksums (inner SHA256SUMS):"
    Write-Host ($innerLines -join "`n")
    Write-Host "==> Release asset checksums (dist/SHA256SUMS):"
    Write-Host ($releaseLines -join "`n")
} finally {
    Pop-Location
    if ($stageDir -and (Test-Path -LiteralPath $stageDir)) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
