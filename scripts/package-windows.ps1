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
        throw "错误：找不到 dotnet 可执行文件（请安装 .NET 10 SDK 或设置 DOTNET_ROOT）"
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
                throw "错误：找不到 git tag 且仓库根目录缺少 VERSION 文件：$versionFile"
            }
            $packageVersion = (Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8).Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($packageVersion)) {
        throw "错误：解析到的版本号为空"
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

    $stageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ffpr-win-pkg-" + [System.Guid]::NewGuid().ToString("N"))
    $dotnetCliOut = Join-Path $stageDir "dotnet-publish-cli"
    $dotnetGuiOut = Join-Path $stageDir "dotnet-publish-gui"
    $packageDir = Join-Path $stageDir "package"
    $distDir = Join-Path $root "dist"
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)

    New-Item -ItemType Directory -Path $dotnetCliOut -Force | Out-Null
    New-Item -ItemType Directory -Path $dotnetGuiOut -Force | Out-Null
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    Write-Host "==> 打包版本：$packageVersion（git: $gitCommit）"
    Write-Host "==> 发布 ffpr-export.exe（win-x64 self-contained single-file）"

    $cliCsprojPath = Join-Path $root "src\FfprExport\FfprExport.csproj"
    & $dotnetBin publish $cliCsprojPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:AssemblyName=ffpr-export `
        "-p:Version=$packageVersion" `
        "-p:FfprGitCommit=$gitCommit" `
        -o $dotnetCliOut
    if ($LASTEXITCODE -ne 0) {
        throw "错误：dotnet publish 发布 $cliCsprojPath 失败（退出码 $LASTEXITCODE）"
    }

    $ffprExportExe = Join-Path $dotnetCliOut "ffpr-export.exe"
    if (-not (Test-Path -LiteralPath $ffprExportExe -PathType Leaf)) {
        throw "错误：dotnet publish 未生成 $ffprExportExe"
    }

    Write-Host "==> 发布 ffpr-export-gui.exe（win-x64 WinForms self-contained single-file）"

    $guiCsprojPath = Join-Path $root "src\FfprExport.Gui\FfprExport.Gui.csproj"
    & $dotnetBin publish $guiCsprojPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:AssemblyName=ffpr-export-gui `
        "-p:Version=$packageVersion" `
        "-p:FfprGitCommit=$gitCommit" `
        -o $dotnetGuiOut
    if ($LASTEXITCODE -ne 0) {
        throw "错误：dotnet publish 发布 $guiCsprojPath 失败（退出码 $LASTEXITCODE）"
    }

    $ffprGuiExe = Join-Path $dotnetGuiOut "ffpr-export-gui.exe"
    if (-not (Test-Path -LiteralPath $ffprGuiExe -PathType Leaf)) {
        throw "错误：dotnet publish 未生成 $ffprGuiExe"
    }

    foreach ($required in @("known-builds.json", "README.md", "LICENSE")) {
        $reqPath = Join-Path $root $required
        if (-not (Test-Path -LiteralPath $reqPath -PathType Leaf)) {
            throw "错误：打包缺少必需文件：$reqPath"
        }
    }

    Copy-Item -LiteralPath $ffprExportExe -Destination (Join-Path $packageDir "ffpr-export.exe") -Force
    Copy-Item -LiteralPath $ffprGuiExe -Destination (Join-Path $packageDir "ffpr-export-gui.exe") -Force
    Copy-Item -LiteralPath (Join-Path $root "known-builds.json") -Destination (Join-Path $packageDir "known-builds.json") -Force
    Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $packageDir "README.md") -Force
    Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination (Join-Path $packageDir "LICENSE") -Force

    Write-Host "==> 执行打包后窗口程序自检：ffpr-export-gui.exe --self-test --require-cli"
    $packagedGuiExe = Join-Path $packageDir "ffpr-export-gui.exe"
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
        throw "错误：打包后的 ffpr-export-gui.exe --self-test --require-cli 失败（退出码 $($selfTestProc.ExitCode)）"
    }

    # Generate short Chinese quick-start guide (<= 10 lines, UTF-8 without BOM, CRLF endings).
    $guideLines = @(
        "FFPR 导出工具（FINAL FANTASY I–VI Pixel Remaster）使用说明",
        "",
        "1. 将整个压缩包完整解压到任意文件夹（不要在压缩包内直接双击运行）。",
        "2. 保持 ffpr-export-gui.exe、ffpr-export.exe 与 known-builds.json 在同一目录。",
        "3. 双击运行 ffpr-export-gui.exe，程序会自动扫描本机 Steam 中的 FF1–FF6。",
        "4. 确认已勾选的游戏后，点击「开始导出」即可依次生成 SQLite 数据库。",
        "5. 导出结果默认保存在「%LOCALAPPDATA%\agp\ffpr-export\output\<游戏代号>-<构建号>\」目录中。",
        "6. 高级命令行用法、AGP_HOME 与说明请查看同目录的 README.md。"
    )
    $guideName = "使用说明.txt"
    $guidePath = Join-Path $packageDir $guideName
    [System.IO.File]::WriteAllText($guidePath, (($guideLines -join "`r`n") + "`r`n"), $utf8NoBom)

    # Write inner SHA256SUMS matching package-windows.sh layout (UTF-8 without BOM, LF endings).
    $innerFiles = @("LICENSE", "README.md", "ffpr-export-gui.exe", "ffpr-export.exe", "known-builds.json", $guideName)
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

    Write-Host "==> 生成压缩包：$zipPath"
    $zipEntries = @("LICENSE", "README.md", "SHA256SUMS", "ffpr-export-gui.exe", "ffpr-export.exe", "known-builds.json", $guideName)
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

    Write-Host "==> 打包完成：$zipPath"
    Get-Item -LiteralPath $zipPath, $distSumsPath, $distKnownBuilds | Format-Table Name, Length -AutoSize
    Write-Host "==> 压缩包内文件与校验（zip 内 SHA256SUMS）："
    Write-Host ($innerLines -join "`n")
    Write-Host "==> 发布产物校验（dist/SHA256SUMS）："
    Write-Host ($releaseLines -join "`n")
} finally {
    Pop-Location
    if ($stageDir -and (Test-Path -LiteralPath $stageDir)) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
