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
  echo "Error: dotnet executable not found: $DOTNET_BIN (install .NET 10 SDK to .deps/dotnet or set DOTNET_ROOT)" >&2
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
  echo "Error: No exact git tag found and VERSION file is missing in repository root" >&2
  exit 2
fi

if [[ -z "$VERSION" ]]; then
  echo "Error: Resolved package version is empty" >&2
  exit 2
fi

GIT_COMMIT="$(git rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ffpr-win-pkg.XXXXXX")"
DOTNET_CLI_OUT="$STAGE_DIR/dotnet-publish-cli"
DOTNET_GUI_OUT="$STAGE_DIR/dotnet-publish-gui"
PACKAGE_DIR="$STAGE_DIR/package"
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$DOTNET_CLI_OUT" "$DOTNET_GUI_OUT" "$PACKAGE_DIR" "$ROOT/dist"

echo "==> Packaging version: $VERSION (git: $GIT_COMMIT)"
echo "==> Publishing ffpr-export.exe (win-x64 self-contained single-file, sibling native DLLs without TEMP extraction)"
"$DOTNET_BIN" publish "$ROOT/src/FfprExport/FfprExport.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:AssemblyName=ffpr-export \
  -p:Version="$VERSION" \
  -p:FfprGitCommit="$GIT_COMMIT" \
  -o "$DOTNET_CLI_OUT"

if [[ ! -f "$DOTNET_CLI_OUT/ffpr-export.exe" ]]; then
  echo "Error: dotnet publish did not produce $DOTNET_CLI_OUT/ffpr-export.exe" >&2
  exit 2
fi

echo "==> Publishing ffpr-export-gui.exe (win-x64 WinForms self-contained single-file, sibling native DLLs without TEMP extraction)"
"$DOTNET_BIN" publish "$ROOT/src/FfprExport.Gui/FfprExport.Gui.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -p:AssemblyName=ffpr-export-gui \
  -p:Version="$VERSION" \
  -p:FfprGitCommit="$GIT_COMMIT" \
  -o "$DOTNET_GUI_OUT"

if [[ ! -f "$DOTNET_GUI_OUT/ffpr-export-gui.exe" ]]; then
  echo "Error: dotnet publish did not produce $DOTNET_GUI_OUT/ffpr-export-gui.exe" >&2
  exit 2
fi

for required in known-builds.json README.md LICENSE; do
  if [[ ! -f "$ROOT/$required" ]]; then
    echo "Error: Missing required packaging file: $ROOT/$required" >&2
    exit 2
  fi
done

cp "$DOTNET_CLI_OUT/ffpr-export.exe" "$PACKAGE_DIR/ffpr-export.exe"
cp "$DOTNET_GUI_OUT/ffpr-export-gui.exe" "$PACKAGE_DIR/ffpr-export-gui.exe"

# Copy all sibling native DLLs (such as e_sqlite3.dll) from both publish outputs.
shopt -s nullglob
for pub_dir in "$DOTNET_CLI_OUT" "$DOTNET_GUI_OUT"; do
  for dll_path in "$pub_dir"/*.dll; do
    dll_name="$(basename "$dll_path")"
    dest_path="$PACKAGE_DIR/$dll_name"
    if [[ -f "$dest_path" ]]; then
      if ! cmp -s "$dll_path" "$dest_path"; then
        echo "Error: Native library $dll_name differs between CLI and GUI publish directories; refusing to overwrite" >&2
        exit 2
      fi
      continue
    fi
    cp "$dll_path" "$dest_path"
  done
done
shopt -u nullglob

if [[ ! -f "$PACKAGE_DIR/e_sqlite3.dll" ]]; then
  echo "Error: Packaging directory is missing native library e_sqlite3.dll (verify IncludeNativeLibrariesForSelfExtract=false)" >&2
  exit 2
fi

cp "$ROOT/known-builds.json" "$PACKAGE_DIR/known-builds.json"
cp "$ROOT/README.md" "$PACKAGE_DIR/README.md"
cp "$ROOT/LICENSE" "$PACKAGE_DIR/LICENSE"

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    echo "==> Running post-package GUI self-test: ffpr-export-gui.exe --self-test --require-cli"
    (
      cd "$PACKAGE_DIR"
      if command -v cygpath >/dev/null 2>&1; then
        export AGP_HOME="$(cygpath -w "$STAGE_DIR/agp-home")"
      else
        export AGP_HOME="$STAGE_DIR/agp-home"
      fi
      ./ffpr-export-gui.exe --self-test --require-cli
    )

    echo "==> Running unwritable TEMP simulation self-test (TEMP/TMP pointing to a regular file)"
    printf 'blocked-temp\n' > "$STAGE_DIR/blocked-temp-file"
    (
      cd "$PACKAGE_DIR"
      if command -v cygpath >/dev/null 2>&1; then
        BLOCKED_TEMP_NATIVE="$(cygpath -w "$STAGE_DIR/blocked-temp-file")"
        export AGP_HOME="$(cygpath -w "$STAGE_DIR/agp-home-notemp")"
      else
        BLOCKED_TEMP_NATIVE="$STAGE_DIR/blocked-temp-file"
        export AGP_HOME="$STAGE_DIR/agp-home-notemp"
      fi
      export TEMP="$BLOCKED_TEMP_NATIVE"
      export TMP="$BLOCKED_TEMP_NATIVE"
      ./ffpr-export-gui.exe --self-test --require-cli
      ./ffpr-export.exe --help >/dev/null
    )
    ;;
  *)
    echo "==> Skipping post-package GUI self-test (non-Windows cross-packaging host; verified natively on Windows by package-windows.ps1 and CI)"
    ;;
esac

# Generate English quick-start guide with concise Chinese and Japanese appendices (UTF-8 without BOM, CRLF for Windows Notepad).
printf '%s\r\n' \
  'FFPR Export (FINAL FANTASY I-VI Pixel Remaster) - Windows Quick Start' \
  '' \
  '1. Extract the entire zip archive to a regular folder (do not run directly from inside the zip).' \
  '2. Keep ffpr-export-gui.exe, ffpr-export.exe, sibling native DLLs (e.g. e_sqlite3.dll), and known-builds.json in the same folder.' \
  '3. Double-click ffpr-export-gui.exe. It auto-scans your Steam libraries for FF1-FF6 and selects them by default.' \
  '4. Use the Language dropdown in the top-right corner to switch between English, Simplified Chinese, and Japanese.' \
  '5. Click [Start Export] to build SQLite databases sequentially. Default output: %LOCALAPPDATA%\agp\ffpr-export\output\<game>-<buildid>\' \
  '6. For CLI options (--lang, AGP_HOME, AGP_LANG) and full documentation, see README.md in this folder.' \
  '' \
  '--- 简体中文简明说明 ---' \
  '1. 请将压缩包完整解压到任意文件夹，并保持所有文件（含 e_sqlite3.dll）在同一目录。' \
  '2. 双击 ffpr-export-gui.exe，可在右上角切换语言，确认勾选的游戏后点击 [开始导出]。' \
  '3. 默认输出目录：%LOCALAPPDATA%\agp\ffpr-export\output\<游戏代号>-<构建号>\。详见 README.md。' \
  '' \
  '--- 日本語クイックガイド ---' \
  '1. zip 全体を任意のフォルダに展開し、すべてのファイル（e_sqlite3.dll を含む）を同じフォルダに置いてください。' \
  '2. ffpr-export-gui.exe を起動し、右上で言語を切り替え、ゲームを確認して [エクスポート開始] を押します。' \
  '3. 既定の出力先：%LOCALAPPDATA%\agp\ffpr-export\output\<ゲームID>-<ビルドID>\。詳細は README.md を参照してください。' \
  > "$PACKAGE_DIR/README-Windows.txt"

(
  cd "$PACKAGE_DIR"
  mapfile -t INNER_FILES < <(find . -maxdepth 1 -type f ! -name 'SHA256SUMS' -printf '%P\n' | LC_ALL=C sort)
  sha256sum "${INNER_FILES[@]}" > SHA256SUMS
)

ZIP_PATH="$ROOT/dist/ffpr-export-${VERSION}-win-x64.zip"
rm -f "$ZIP_PATH"

echo "==> Creating archive: $ZIP_PATH"
if command -v python3 >/dev/null 2>&1; then
  python3 - "$PACKAGE_DIR" "$ZIP_PATH" <<'PY'
import os
import sys
import zipfile

pkg_dir, zip_path = sys.argv[1], sys.argv[2]
files = sorted(
    name for name in os.listdir(pkg_dir)
    if os.path.isfile(os.path.join(pkg_dir, name))
)
with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for name in files:
        zf.write(os.path.join(pkg_dir, name), arcname=name)

print("==> Archive entries:")
with zipfile.ZipFile(zip_path, "r") as zf:
    names = [info.filename for info in zf.infolist()]
    for info in zf.infolist():
        print(f"  {info.filename:32s}  {info.file_size:12d} bytes (compressed {info.compress_size:10d})")
    if "e_sqlite3.dll" not in names:
        print("Error: Generated archive is missing native library e_sqlite3.dll", file=sys.stderr)
        sys.exit(2)
    if "README-Windows.txt" not in names:
        print("Error: Generated archive is missing README-Windows.txt", file=sys.stderr)
        sys.exit(2)
PY
elif command -v zip >/dev/null 2>&1; then
  (
    cd "$PACKAGE_DIR"
    mapfile -t ZIP_FILES < <(find . -maxdepth 1 -type f -printf '%P\n' | LC_ALL=C sort)
    zip -q -9 "$ZIP_PATH" "${ZIP_FILES[@]}"
  )
  if command -v unzip >/dev/null 2>&1; then
    echo "==> Archive entries:"
    unzip -l "$ZIP_PATH"
  fi
else
  echo "Error: Neither python3 nor zip is installed; cannot create .zip archive" >&2
  exit 2
fi

echo "==> Packaging complete: $ZIP_PATH"
ls -lh "$ZIP_PATH"
echo "==> Packaged files and checksums:"
ls -lh "$PACKAGE_DIR"
cat "$PACKAGE_DIR/SHA256SUMS"
