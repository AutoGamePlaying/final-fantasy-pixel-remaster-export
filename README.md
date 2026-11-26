**English** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md)

# final-fantasy-pixel-remaster-export

Asset inventory, extraction, and reproducible SQLite database builder `ffpr-export` (C# / .NET 10 / AssetsTools.NET) for *FINAL FANTASY I–VI Pixel Remaster* (Steam Windows edition, Unity / IL2CPP).

The tool scans Steam game directories in **read-only** mode, automatically detects FF1–FF6, Unity version, and Steam Build ID, parses Addressables `catalog.json` and `*_Data` files, skips images, audio, video, fonts, and shaders, and exports the remaining structured text, CSV master tables, Tiled maps, event/battle scripts, and `MonoBehaviour` / `ScriptableObject` assets (TypeTree first, falling back to IL2CPP metadata) into a reproducible SQLite database.

> This is a public repository and stores **no game files, exported data, decompiled artifacts, or game text**. All local dependencies and intermediate outputs stay under `.deps/` (or `$FFPR_DEPS`).

## Quick Start (Windows)

No .NET runtime installation or command-line typing required:

1. **Download the zip archive** (either source):
   - **Releases page**: download `ffpr-export-<version>-win-x64.zip` from the latest release.
   - **Actions page**: open the latest passing `CI` run on `main` and download `ffpr-export-<version>-<short-sha>-win-x64` from **Artifacts** (built on every push to `main`, retained for 30 days).
2. **Extract the entire archive** to a regular folder (keep `ffpr-export-gui.exe`, `ffpr-export.exe`, sibling native DLLs such as `e_sqlite3.dll`, and `known-builds.json` in the same directory).
3. **Double-click `ffpr-export-gui.exe`**:
   - Automatically scans all local Steam libraries, lists installed FF1–FF6 Pixel Remaster games with their Steam Build IDs and directories, and **selects all by default** (use **Add game folder...** if a game is not auto-detected).
   - Use the **Language** dropdown in the top-right corner (`English` / `简体中文` / `日本語`) to switch the interface language at any time; the UI refreshes immediately and saves your choice to `settings.json`.
   - Output root defaults to `%LOCALAPPDATA%\agp\ffpr-export\output\` (each game writes to `<ffN>-<buildid>\`). Click **Browse...** to choose another location; if a target folder already exists and was not created by `ffpr-export`, the app asks for confirmation before overwriting.
   - Click **Start Export** to run the sibling `ffpr-export.exe build-db` sequentially with `--lang` and `--progress-json`, streaming localized logs and language-independent step progress. Click **Cancel** at any time to terminate the child process tree.
   - When finished, inspect each game's verification verdict (`✓ Matches registered build` / `✓ New version, unregistered` / `✗ Hash mismatch`), database path, file hash, and content hash, then click **Open Output Folder** or **Copy Hashes**.
   - Settings are stored in `%LOCALAPPDATA%\agp\ffpr-export\settings.json` (no Windows Registry writes); startup logs are saved under `%LOCALAPPDATA%\agp\ffpr-export\logs\` (keeping the latest 10 files).

```text
+-----------------------------------------------------------------------------+
| FFPR Export v0.1.0 — FINAL FANTASY I–VI Pixel Remaster                      |
+-----------------------------------------------------------------------------+
|                                                Language: [ English       v] |
| 1. Games (auto-scanned from Steam libraries, all selected by default)       |
| [x] Select all   Selected 3 / 3 game(s)      [ Rescan ] [ Add game folder…] |
| +------------------------+----------+-------------------------------------+ |
| | Game                   | Build ID | Directory                           | |
| | [x] FINAL FANTASY (FF1)| 20249397 | C:\...\common\FINAL FANTASY PR      | |
| | [x] FINAL FANTASY V... | 20265087 | D:\...\common\FINAL FANTASY V PR    | |
| +------------------------+----------+-------------------------------------+ |
|                                                                             |
| 2. Output Location                                                          |
| Output root: [ %LOCALAPPDATA%\agp\ffpr-export\output         ] [ Browse… ] |
| Subdirectory preview: will export to ff1-20249397, ff5-20265087 (3 total)   |
|                                                                             |
| [                 Start Export                  ]        [   Cancel   ]     |
| Game 2 / 3: FF5 (Step 3/6)                                                  |
| [===========================-----------------------]                        |
| | ==> [ff5] Step 1/6: Identifying game version and resource fingerprint   ^ | |
| | ==> [ff5] Step 2/6: Running in-process inventory and export             | | |
| | ==> [ff5] Step 3/6: Running in-process db build to create ff5.sqlite    v | |
|                                                                             |
| 3. Results                                                                  |
| +--------------------+----------+--------------------------+--------------+ |
| | Game               | Build ID | Verdict                  | Database Path| |
| | FINAL FANTASY (FF1)| 20249397 | ✓ Matches registered ... | ...\ff1.sq...| |
| +--------------------+----------+--------------------------+--------------+ |
| Database path: %LOCALAPPDATA%\agp\ffpr-export\output\ff1-20249397\ff1.sqlite|
| File hash:     abb52056...                                                  |
| Content hash:  45fb76f7...                                                  |
| Verdict:       ✓ Matches registered build (registered: 2026-11-25, ...)     |
| [ Open Output Folder ]  [ Copy Hashes ]                                     |
+-----------------------------------------------------------------------------+
```

## Language Selection

Both the GUI and CLI support **English (`en`, default)**, **Simplified Chinese (`zh-Hans`)**, and **Japanese (`ja`)**. The tool does **not** auto-switch based on OS/system locale.

Precedence order (highest to lowest):

1. CLI option: `--lang en|zh-Hans|ja` (e.g., `ffpr-export --lang ja --help`)
2. Environment variable: `AGP_LANG` (`en`, `zh-Hans`, or `ja`)
3. GUI saved setting: `language` in `settings.json` (updated immediately by the top-right dropdown)
4. Default: `en`

When the GUI launches `ffpr-export.exe`, it passes `--lang <current>` and `--progress-json`. With `--progress-json`, the CLI emits language-independent `@@progress {"step":n,"total":m}` lines for the GUI progress bar alongside the localized human-readable log lines.

## Data Directory & `AGP_HOME`

The GUI, console wizard, and CLI `build-db` share a single data root layout:

```text
%LOCALAPPDATA%\agp\ffpr-export\
  output\<game>-<buildid>\   Default export location: SQLite DB, build report, and json\ (with --keep-json)
  logs\                      GUI diagnostic logs (retains latest 10 files)
  settings.json              GUI preferences (output root, language, checked games)
```

### Root Resolution Precedence

1. **Environment variable `AGP_HOME`** (used directly as the `agp` layer; tool folder is `$AGP_HOME/ffpr-export/`).
2. **Windows**: `%LOCALAPPDATA%\agp` (with automatic fallbacks to `LocalLow`, `<exeDir>\agp-data`, `%USERPROFILE%\agp`, or `agp-location.txt` when `%LOCALAPPDATA%` is not writable).
3. **Linux / macOS**: `$XDG_DATA_HOME/agp`, or `~/.local/share/agp` if `XDG_DATA_HOME` is unset.

An explicit `--out <dir>` on the CLI or a browsed folder in the GUI always overrides the default output root.

### `AGP_HOME` Examples

Windows (PowerShell):

```powershell
$env:AGP_HOME = "D:\agp-data"
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR"
# Writes to D:\agp-data\ffpr-export\output\ff5-<buildid>\
```

Linux / macOS (bash):

```bash
AGP_HOME=/tmp/agp-data scripts/run.sh build-db --game "/path/to/FINAL FANTASY V PR"
# Writes to /tmp/agp-data/ffpr-export/output/ff5-<buildid>/
```

### Legacy Locations & Migration

- **Automatic GUI settings migration**: on first launch, if the new `settings.json` does not exist yet while the legacy `%APPDATA%\ffpr-export\settings.json` (Linux: `~/.config/ffpr-export/settings.json`) exists, the GUI migrates it to the new path and renames the legacy file to `settings.json.migrated` (without deleting it). When `AGP_HOME` is set, host `%APPDATA%` is never read or modified.
- **Legacy exports and logs are not migrated automatically**:
  - Old GUI default output: `%USERPROFILE%\Documents\FF-Export\<game>-<buildid>\`
  - Old GUI logs: `%LOCALAPPDATA%\ffpr-export\logs\`
  - Old console wizard default output: `<exeDir>\output\<game>-<buildid>\`
  - Old `build-db` default output without `--out`: `.\ffpr-<ffN>-build\`

## CLI Usage & Console Wizard

### Interactive Console Wizard (no arguments)

Running `ffpr-export.exe` with no arguments (or only `--lang <code>`) launches the interactive console wizard: it scans local Steam libraries, lets you select one game or all games, confirms the output directory, executes `build-db` sequentially, and summarizes database paths, hashes, and `known-builds.json` verdicts.

### CLI Subcommands

Use `scripts/run.sh` on Linux/macOS or `ffpr-export.exe` directly on Windows.

#### 1. `build-db` — One-Step Database Build & Verification

```bash
scripts/run.sh build-db --game "<Steam game dir>" [--out <dir>] [--steam-buildid <id>] [--keep-json] [--jobs N] [--force] [--progress-json] [--lang en|zh-Hans|ja]
```

Omitting `--out` defaults to `<AGP root>/ffpr-export/output/<game>-<buildid>/`.

Windows example (paths with spaces and >260-char long paths supported):

```powershell
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR" --out "D:\FFPR Exports\ff5"
```

Pipeline overview:

1. Identifies the game (FF1–FF6) and collects version metadata:
   - Reads `buildid` from `../../steamapps/appmanifest_<appid>.acf` (or `--steam-buildid`, recorded as `manual`; records `null` with a warning if neither is available).
   - Computes SHA-256 of `GameAssembly.dll` and `global-metadata.dat`.
   - Parses PE version resources (`FileVersion` / `ProductVersion`) from the game `.exe` in cross-platform pure C#.
   - Reads the Unity version from asset headers.
   - Computes the resource fingerprint from `StreamingAssets/aa/catalog.json` and all sorted `.bundle` files.
2. Runs `inventory`, `export` (into `<out>/json/inventory/` and `<out>/json/export/`), `db build` (producing `<out>/<ffN>.sqlite`), and `db hash --json` in-process.
3. Writes `<out>/<ffN>.sqlite.build.json` with game version, tool version/commit, `file_sha256`, `content_sha256`, per-table row counts/hashes, and stage timings.
4. Loads `known-builds.json` (sibling file next to the executable first, then embedded resource) and reports one of three outcomes: matches registered build / new game version / hash mismatch (listing differing tables).
5. Removes temporary `<out>/json/` unless `--keep-json` is passed. Protected by `OutputGuard` (blocks overlaps with `--game`, blocks protected `.deps/` subdirectories, and only clears directories carrying a matching `build-db` `manifest.json` under `--force`).

#### 2. `inventory` — Asset Inventory

```bash
scripts/run.sh inventory --game "<Steam game dir>" [--out "<output dir>"] [--jobs N] [--force] [--lang en|zh-Hans|ja]
```

- Auto-detects FF1–FF6, Unity version, and Steam `buildid`.
- Inspects all Addressables bundles referenced by `StreamingAssets/aa/catalog.json` plus `resources.assets`, `sharedassets*.assets`, and `level*` in `*_Data`.
- Writes `inventory.jsonl`, `summary.json`, and `manifest.json` to `--out` (default `.deps/ffpr/<ffN>/inventory`).

#### 3. `export` — Raw Asset Extraction

```bash
scripts/run.sh export --game "<Steam game dir>" [--out "<output dir>"] [--jobs N] [--force] [--lang en|zh-Hans|ja]
```

- Defaults to `.deps/ffpr/<ffN>/export` (base `.deps` overridable via `FFPR_DEPS`).
- Extracts:
  - **Non-audio `TextAsset`**: writes raw bytes verbatim at container paths (`Assets/GameAssets/Serial/` stripped to `Res/...` and `Data/...`; other `Assets/GameAssets/` paths to `AssetsPath.json`, `Common/...`; `*_Data` text to `_data/<file>/`).
  - **`MonoBehaviour` / `ScriptableObject`**: uses TypeTree when present, falling back to `AssetsTools.NET.Cpp2IL` with `GameAssembly.dll` and `global-metadata.dat`. Standalone container assets write `<container>.json`; component assets group into `_monobehaviour/bundles/*.json` and `_monobehaviour/data/*.json`.
  - **Skips**: images/sprites/meshes, audio (`*.mab.bytes` / `*.sab.bytes`), video, fonts (including encrypted `font_*.bundle`), shaders/materials, animations, and Unity scene scaffolding.
- Safety & fault tolerance:
  - **Read-only game directory**: never modifies `--game`.
  - **Resumable**: re-running without `--force` skips already-exported files; all writes use atomic temp-file replacement.
  - **Per-asset fault tolerance**: individual asset/bundle errors are recorded in `manifest.json` `failures` without aborting (exit `0` = all succeeded, `1` = completed with partial failures, `2` = argument/guardrail error).

## Development Setup

### Linux Environment

Uses `.deps/dotnet` (.NET 10 SDK) and `.deps/nuget` by default without touching `~/.dotnet` (or set `DOTNET_ROOT` to an existing .NET 10 SDK):

```bash
mkdir -p .deps
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .deps/dotnet-install.sh
bash .deps/dotnet-install.sh --channel 10.0 --install-dir .deps/dotnet --no-path
```

### Windows Single-File Packaging

Builds self-contained single-file `ffpr-export-gui.exe` (WinForms, PerMonitorV2 DPI, `longPathAware`) and `ffpr-export.exe` (CLI / wizard) with sibling `e_sqlite3.dll` (no extraction to `%TEMP%`), all three UI languages bundled inside each executable, and `SHA256SUMS`:

```bash
scripts/package-windows.sh
```

Or natively on Windows:

```powershell
.\scripts\package-windows.ps1
```

Outputs `dist/ffpr-export-<version>-win-x64.zip` containing `ffpr-export-gui.exe`, `ffpr-export.exe`, `e_sqlite3.dll`, `README-Windows.txt`, `known-builds.json`, `README.md`, `LICENSE`, and `SHA256SUMS`.

## Cross-Game Data Survey

Detailed structural comparison across all six games (master CSVs, 12-language text, Tiled maps, scripts, and `MonoBehaviour` distribution) is documented in [docs/data-survey.md](docs/data-survey.md) (in Chinese).

## Build & Test

```bash
scripts/check.sh
```

Runs sequentially:

1. `dotnet format FfprExport.sln --verify-no-changes` (on Linux)
2. `dotnet build FfprExport.sln -c Release` (`Nullable` enabled, warnings treated as errors)
3. `dotnet test FfprExport.sln -c Release --no-build` (xUnit tests using synthetic Unity bundles; no real game files required)

The script sets `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`, and `UseSharedCompilation=false` so MSBuild/compiler servers never linger or hold lock files.

## CI & Releases

GitHub Actions runs native .NET 10 checks on both `ubuntu-latest` and `windows-latest`:

### Continuous Integration (`.github/workflows/ci.yml`)

Triggered on every push and pull request (`permissions: contents: read`), with four jobs:

1. **`check`** (`ubuntu-latest` + `windows-latest`): runs `scripts/check.sh`, plus `ffpr-export-gui.exe --self-test` on Windows.
2. **`fixture-hash`** (`ubuntu-latest` + `windows-latest`): runs `scripts/fixture-db.sh` and `scripts/fixture-export.sh` to generate synthetic database and export file hash reports.
3. **`compare-hash`**: compares Linux and Windows `file_sha256`, `content_sha256`, every ordinary SQLite table hash, and every synthetic export file hash to enforce byte-for-byte cross-platform reproducibility.
4. **`package-windows`** (push to `main` only, `windows-latest`): runs `scripts/package-windows.ps1`, extracts and verifies `README-Windows.txt`, `e_sqlite3.dll`, all three languages (`en`, `zh-Hans`, `ja`), blocked-`TEMP` execution, and uploads the zip artifact (retained 30 days).

### Release Workflow (`.github/workflows/release.yml`)

Triggered by pushing a `v*` tag matching `VERSION`:

1. Update `VERSION` (e.g. `0.1.0`) and commit.
2. Tag and push:

```bash
git tag v0.1.0
git push origin v0.1.0
```

`release.yml` verifies tag vs `VERSION`, runs `scripts/check.sh`, packages the native `win-x64` release via `scripts/package-windows.ps1`, verifies the archive, and publishes a GitHub Release with `ffpr-export-<version>-win-x64.zip`, `SHA256SUMS`, and `known-builds.json`.

## License

[MIT License](LICENSE)
