#!/usr/bin/env bash
set -euo pipefail

# Generate a deterministic synthetic export directory, run a full `ffpr-export db build`,
# and emit the `ffpr-export db hash --json` report for cross-platform reproducibility verification.

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# Resolve native Windows path in Git Bash / MSYS when available so .NET env defaults stay valid.
HOST_ROOT="$(pwd -W 2>/dev/null || pwd)"

HASH_OUT=""
DB_OUT=""
FFPR_EXPORT_BIN="${FFPR_EXPORT_BIN:-}"

usage() {
  cat >&2 <<'EOF'
Usage: scripts/fixture-db.sh [--hash-out <JSON file>] [--db-out <SQLite file>] [--ffpr-export <executable or dll>]

Generates a deterministic synthetic export directory, runs a full `ffpr-export db build`,
and outputs `ffpr-export db hash --json`.
  --hash-out <file>      Write hash JSON to the specified file (defaults to stdout)
  --db-out <file>        Keep the built SQLite database at the specified path (defaults to a temporary file)
  --ffpr-export <file>   Use the specified ffpr-export executable or dll (builds Release automatically if omitted)
  -h, --help             Show this help message
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --hash-out)
      if [[ $# -lt 2 ]]; then
        echo "Error: --hash-out requires a file path argument" >&2
        exit 2
      fi
      HASH_OUT="$2"
      shift 2
      ;;
    --db-out)
      if [[ $# -lt 2 ]]; then
        echo "Error: --db-out requires a file path argument" >&2
        exit 2
      fi
      DB_OUT="$2"
      shift 2
      ;;
    --ffpr-export)
      if [[ $# -lt 2 ]]; then
        echo "Error: --ffpr-export requires a file path argument" >&2
        exit 2
      fi
      FFPR_EXPORT_BIN="$2"
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
USE_DOTNET_EXEC=0
TARGET_BIN=""

if [[ -z "$FFPR_EXPORT_BIN" ]]; then
  resolve_dotnet
  echo "==> Building ffpr-export (Release)" >&2
  "$DOTNET_BIN" build src/FfprExport/FfprExport.csproj -c Release >&2

  TARGET_BIN="$ROOT/src/FfprExport/bin/Release/net10.0/ffpr-export.dll"
  if [[ ! -f "$TARGET_BIN" ]]; then
    echo "Error: dotnet build did not produce $TARGET_BIN" >&2
    exit 2
  fi
  USE_DOTNET_EXEC=1
elif [[ "$FFPR_EXPORT_BIN" == *.dll ]]; then
  if [[ ! -f "$FFPR_EXPORT_BIN" ]]; then
    echo "Error: Specified ffpr-export dll does not exist: $FFPR_EXPORT_BIN" >&2
    exit 2
  fi
  resolve_dotnet
  TARGET_BIN="$FFPR_EXPORT_BIN"
  USE_DOTNET_EXEC=1
else
  if [[ ! -x "$FFPR_EXPORT_BIN" ]]; then
    echo "Error: Specified ffpr-export executable does not exist or is not executable: $FFPR_EXPORT_BIN" >&2
    exit 2
  fi
  TARGET_BIN="$FFPR_EXPORT_BIN"
  USE_DOTNET_EXEC=0
fi

run_ffpr_export() {
  (
    unset FFPR_DEPS
    if [[ "$USE_DOTNET_EXEC" == "1" ]]; then
      exec "$DOTNET_BIN" exec "$(to_native_path "$TARGET_BIN")" "$@"
    else
      exec "$TARGET_BIN" "$@"
    fi
  )
}

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ffpr-fixture-db.XXXXXX")"
trap 'rm -rf "$STAGE_DIR"' EXIT

EXPORT_DIR="$STAGE_DIR/export"

if [[ -n "$DB_OUT" ]]; then
  mkdir -p "$(dirname -- "$DB_OUT")"
  DB_PATH="$DB_OUT"
else
  DB_PATH="$STAGE_DIR/fixture.sqlite"
fi

echo "==> Generating deterministic synthetic export directory" >&2
run_ffpr_export db write-fixture --out "$(to_native_path "$EXPORT_DIR")" >&2

echo "==> Building synthetic database (db build)" >&2
run_ffpr_export db build \
  --export "$(to_native_path "$EXPORT_DIR")" \
  --out "$(to_native_path "$DB_PATH")" >&2

echo "==> Computing synthetic database hashes (db hash --json)" >&2
HASH_JSON="$(run_ffpr_export db hash "$(to_native_path "$DB_PATH")" --json)"
HASH_JSON="${HASH_JSON%$'\r'}"

if [[ -z "$HASH_JSON" ]]; then
  echo "Error: db hash --json produced no output" >&2
  exit 1
fi

if [[ -n "$HASH_OUT" ]]; then
  mkdir -p "$(dirname -- "$HASH_OUT")"
  printf '%s\n' "$HASH_JSON" > "$HASH_OUT"
  echo "==> Wrote hash report: $HASH_OUT" >&2
else
  printf '%s\n' "$HASH_JSON"
fi
