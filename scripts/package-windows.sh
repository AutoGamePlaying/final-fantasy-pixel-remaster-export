#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ -n "${DOTNET_ROOT:-}" ]]; then
  DOTNET_BIN="$DOTNET_ROOT/dotnet"
else
  export DOTNET_ROOT="$ROOT/.deps/dotnet"
  DOTNET_BIN="$DOTNET_ROOT/dotnet"
fi

if [[ ! -x "$DOTNET_BIN" ]]; then
  echo "错误：找不到 dotnet 可执行文件：$DOTNET_BIN（请安装 .NET 10 SDK 到 .deps/dotnet 或设置 DOTNET_ROOT）" >&2
  exit 2
fi

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.deps/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.deps/nuget}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$ROOT/.deps/nuget-http}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# Do not leave MSBuild / compiler server processes running after the script exits.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export UseSharedCompilation=false

# Resolve package version: prefer exact git tag on HEAD, fallback to VERSION file.
RAW_TAG="$(git describe --tags --exact-match HEAD 2>/dev/null || true)"
if [[ -n "$RAW_TAG" ]]; then
  VERSION="${RAW_TAG#v}"
elif [[ -f "$ROOT/VERSION" ]]; then
  VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
else
  echo "错误：找不到 git tag 且仓库根目录缺少 VERSION 文件" >&2
  exit 2
fi

if [[ -z "$VERSION" ]]; then
  echo "错误：解析到的版本号为空" >&2
  exit 2
fi

GIT_COMMIT="$(git rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ffpr-win-pkg.XXXXXX")"
DOTNET_CLI_OUT="$STAGE_DIR/dotnet-publish-cli"
DOTNET_GUI_OUT="$STAGE_DIR/dotnet-publish-gui"
PACKAGE_DIR="$STAGE_DIR/package"
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$DOTNET_CLI_OUT" "$DOTNET_GUI_OUT" "$PACKAGE_DIR" "$ROOT/dist"

echo "==> 打包版本：$VERSION（git: $GIT_COMMIT）"
echo "==> 发布 ffpr-export.exe（win-x64 self-contained single-file）"
"$DOTNET_BIN" publish "$ROOT/src/FfprExport/FfprExport.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:AssemblyName=ffpr-export \
  -p:Version="$VERSION" \
  -p:FfprGitCommit="$GIT_COMMIT" \
  -o "$DOTNET_CLI_OUT"

if [[ ! -f "$DOTNET_CLI_OUT/ffpr-export.exe" ]]; then
  echo "错误：dotnet publish 未生成 $DOTNET_CLI_OUT/ffpr-export.exe" >&2
  exit 2
fi

echo "==> 发布 ffpr-export-gui.exe（win-x64 WinForms self-contained single-file）"
"$DOTNET_BIN" publish "$ROOT/src/FfprExport.Gui/FfprExport.Gui.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:AssemblyName=ffpr-export-gui \
  -p:Version="$VERSION" \
  -p:FfprGitCommit="$GIT_COMMIT" \
  -o "$DOTNET_GUI_OUT"

if [[ ! -f "$DOTNET_GUI_OUT/ffpr-export-gui.exe" ]]; then
  echo "错误：dotnet publish 未生成 $DOTNET_GUI_OUT/ffpr-export-gui.exe" >&2
  exit 2
fi

for required in known-builds.json README.md LICENSE; do
  if [[ ! -f "$ROOT/$required" ]]; then
    echo "错误：打包缺少必需文件：$ROOT/$required" >&2
    exit 2
  fi
done

cp "$DOTNET_CLI_OUT/ffpr-export.exe" "$PACKAGE_DIR/ffpr-export.exe"
cp "$DOTNET_GUI_OUT/ffpr-export-gui.exe" "$PACKAGE_DIR/ffpr-export-gui.exe"
cp "$ROOT/known-builds.json" "$PACKAGE_DIR/known-builds.json"
cp "$ROOT/README.md" "$PACKAGE_DIR/README.md"
cp "$ROOT/LICENSE" "$PACKAGE_DIR/LICENSE"

# Generate short Chinese quick-start guide (<= 10 lines, CRLF for Windows Notepad).
printf '%s\r\n' \
  'FFPR 导出工具（FINAL FANTASY I–VI Pixel Remaster）使用说明' \
  '' \
  '1. 将整个压缩包完整解压到任意文件夹（不要在压缩包内直接双击运行）。' \
  '2. 保持 ffpr-export-gui.exe、ffpr-export.exe 与 known-builds.json 在同一目录。' \
  '3. 双击运行 ffpr-export-gui.exe，程序会自动扫描本机 Steam 中的 FF1–FF6。' \
  '4. 确认已勾选的游戏后，点击“开始导出”即可依次生成 SQLite 数据库。' \
  '5. 导出结果默认保存在“文档\FF-Export\<游戏代号>-<构建号>\”目录中。' \
  '6. 高级命令行用法与说明请查看同目录的 README.md。' \
  > "$PACKAGE_DIR/使用说明.txt"

(
  cd "$PACKAGE_DIR"
  sha256sum LICENSE README.md ffpr-export-gui.exe ffpr-export.exe known-builds.json 使用说明.txt > SHA256SUMS
)

ZIP_PATH="$ROOT/dist/ffpr-export-${VERSION}-win-x64.zip"
rm -f "$ZIP_PATH"

echo "==> 生成压缩包：$ZIP_PATH"
# Prefer python3 zipfile so non-ASCII entry names (使用说明.txt) always carry the UTF-8 flag bit on Windows.
if command -v python3 >/dev/null 2>&1; then
  python3 - "$PACKAGE_DIR" "$ZIP_PATH" <<'PY'
import os
import sys
import zipfile

pkg_dir, zip_path = sys.argv[1], sys.argv[2]
files = [
    "LICENSE",
    "README.md",
    "SHA256SUMS",
    "ffpr-export-gui.exe",
    "ffpr-export.exe",
    "known-builds.json",
    "使用说明.txt",
]
with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for name in files:
        zf.write(os.path.join(pkg_dir, name), arcname=name)
PY
elif command -v zip >/dev/null 2>&1; then
  (
    cd "$PACKAGE_DIR"
    zip -q -9 "$ZIP_PATH" LICENSE README.md SHA256SUMS ffpr-export-gui.exe ffpr-export.exe known-builds.json 使用说明.txt
  )
else
  echo "错误：缺少 python3 或 zip，无法生成 .zip 压缩包" >&2
  exit 2
fi

echo "==> 打包完成：$ZIP_PATH"
ls -lh "$ZIP_PATH"
echo "==> 压缩包内文件与校验："
ls -lh "$PACKAGE_DIR"
cat "$PACKAGE_DIR/SHA256SUMS"
