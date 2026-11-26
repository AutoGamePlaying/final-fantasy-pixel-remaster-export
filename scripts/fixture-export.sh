#!/usr/bin/env bash
set -euo pipefail

# Build a deterministic synthetic Steam game (matching the smoke test fixture), run real
# `ffpr-export inventory` and `ffpr-export export`, and emit a per-file SHA-256 manifest
# for Linux vs Windows cross-platform byte-identity verification.

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# Resolve native Windows path in Git Bash / MSYS when available so .NET env defaults stay valid.
HOST_ROOT="$(pwd -W 2>/dev/null || pwd)"

MANIFEST_OUT=""

usage() {
  cat >&2 <<'EOF'
Usage: scripts/fixture-export.sh [--manifest-out <JSON file>]

Runs real `inventory` and `export` against synthetic Unity game assets and collects
a per-file SHA-256 manifest.
  --manifest-out <file>  Write manifest JSON to the specified file (defaults to stdout)
  -h, --help             Show this help message
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest-out)
      if [[ $# -lt 2 ]]; then
        echo "Error: --manifest-out requires a file path argument" >&2
        exit 2
      fi
      MANIFEST_OUT="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Error: Unknown argument: $1" >&2
      usage
      exit 2
      ;;
  esac
done

to_native_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -m "$1"
  else
    printf '%s' "$1"
  fi
}

resolve_dotnet() {
  if [[ -n "${DOTNET_ROOT:-}" ]]; then
    local dotnet_dir="${DOTNET_ROOT%/}"
    dotnet_dir="${dotnet_dir%\\}"
    if [[ -x "$dotnet_dir/dotnet.exe" ]]; then
      DOTNET_BIN="$dotnet_dir/dotnet.exe"
    else
      DOTNET_BIN="$dotnet_dir/dotnet"
    fi
  elif [[ -x "$ROOT/.deps/dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOST_ROOT/.deps/dotnet"
    DOTNET_BIN="$ROOT/.deps/dotnet/dotnet"
  elif [[ -x "$ROOT/.deps/dotnet/dotnet.exe" ]]; then
    export DOTNET_ROOT="$HOST_ROOT/.deps/dotnet"
    DOTNET_BIN="$ROOT/.deps/dotnet/dotnet.exe"
  elif command -v dotnet >/dev/null 2>&1; then
    DOTNET_BIN="$(command -v dotnet)"
  else
    export DOTNET_ROOT="$HOST_ROOT/.deps/dotnet"
    DOTNET_BIN="$ROOT/.deps/dotnet/dotnet"
  fi

  if [[ ! -x "$DOTNET_BIN" ]]; then
    echo "Error: dotnet executable not found: $DOTNET_BIN (install .NET 10 SDK to .deps/dotnet or set DOTNET_ROOT)" >&2
    exit 2
  fi
}

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOST_ROOT/.deps/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOST_ROOT/.deps/nuget}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$HOST_ROOT/.deps/nuget-http}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export UseSharedCompilation=false

DOTNET_BIN=""
resolve_dotnet

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ffpr-fixture-export.XXXXXX")"
trap 'rm -rf "$STAGE_DIR"' EXIT

if [[ -z "${AGP_HOME:-}" ]]; then
  export AGP_HOME="$(to_native_path "$STAGE_DIR/agp-home")"
fi

RAW_MANIFEST="$STAGE_DIR/export-hashes.json"

echo "==> Building synthetic export verification tests (Release)" >&2
"$DOTNET_BIN" build tests/FfprExport.Tests/FfprExport.Tests.csproj -c Release >&2

echo "==> Running synthetic export and collecting per-file SHA-256 manifest" >&2
FFPR_EXPORT_FIXTURE_MANIFEST_OUT="$(to_native_path "$RAW_MANIFEST")" \
  "$DOTNET_BIN" test tests/FfprExport.Tests/FfprExport.Tests.csproj \
    -c Release \
    --no-build \
    --filter "FullyQualifiedName=FfprExport.Tests.LineEndingTests.ExportFixture_GeneratesCrossPlatformHashManifest" >&2

if [[ ! -s "$RAW_MANIFEST" ]]; then
  echo "Error: Synthetic export did not produce a valid SHA-256 manifest: $RAW_MANIFEST" >&2
  exit 1
fi

if [[ -n "$MANIFEST_OUT" ]]; then
  mkdir -p "$(dirname -- "$MANIFEST_OUT")"
  cp "$RAW_MANIFEST" "$MANIFEST_OUT"
  echo "==> Wrote export hash manifest: $MANIFEST_OUT" >&2
else
  cat "$RAW_MANIFEST"
fi
