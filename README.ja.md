[English](README.md) | [简体中文](README.zh-CN.md) | **日本語**

# final-fantasy-pixel-remaster-export

*FINAL FANTASY I–VI Pixel Remaster*（Steam Windows 版、Unity / IL2CPP）向けのアセット棚卸し・抽出・再現可能な SQLite データベース構築ツール `ffpr-export`（C# / .NET 10 / AssetsTools.NET）です。

Steam のゲームディレクトリを**読み取り専用**でスキャンし、FF1–FF6・Unity バージョン・Steam ビルド ID を自動検出します。Addressables の `catalog.json` と `*_Data` ファイルを解析し、画像・音声・動画・フォント・シェーダーをスキップしたうえで、構造化テキスト・マスター CSV・Tiled マップ・イベント/バトルスクリプト・`MonoBehaviour` / `ScriptableObject`（TypeTree 優先、欠落時は IL2CPP メタデータから復元）を抽出し、再現可能な SQLite データベースを構築します。

> 本リポジトリは公開リポジトリであり、**ゲームファイル・エクスポートデータ・逆コンパイル成果物・ゲーム本文は一切含みません**。ローカル依存関係や作業出力は `.deps/`（または `$FFPR_DEPS`）配下に配置されます。

## クイックスタート（Windows）

.NET ランタイムのインストールやコマンド入力は不要です。

1. **zip アーカイブをダウンロード**（いずれか）：
   - **Releases ページ**：最新リリースの `ffpr-export-<バージョン>-win-x64.zip` をダウンロード。
   - **Actions ページ**：`main` ブランチの最新成功 `CI` ワークフローを開き、**Artifacts** から `ffpr-export-<バージョン>-<短縮コミット>-win-x64` をダウンロード（`main` へのプッシュごとに自動生成、30 日間保存）。
2. **アーカイブ全体を展開**：任意のフォルダにすべて展開してください（`ffpr-export-gui.exe`、`ffpr-export.exe`、`e_sqlite3.dll` などのネイティブ DLL、`known-builds.json` を同じフォルダに保ちます）。
3. **`ffpr-export-gui.exe` をダブルクリック**：
   - 起動時にローカルの Steam ライブラリを自動スキャンし、インストール済みの FF1–FF6・Steam ビルド ID・ディレクトリを一覧表示して**既定で全選択**します（自動検出されない場合は **ゲームフォルダを追加...** から追加可能）。
   - 右上の **言語** ドロップダウン（`English` / `简体中文` / `日本語`、既定は英語）でいつでも表示言語を切り替えられます。切り替え後は画面全体が即座に更新され、`settings.json` に保存されます。
   - 出力ルートの既定値は `%LOCALAPPDATA%\agp\ffpr-export\output\`（ゲームごとに `<ffN>-<ビルド ID>\` サブディレクトリを作成）です。**参照...** から変更でき、本ツール以外の既存フォルダがある場合は上書き前に確認ダイアログを表示します。
   - **エクスポート開始** をクリックすると、同じフォルダの `ffpr-export.exe build-db` を `--lang` と `--progress-json` 付きで順番に実行し、ログと進捗をリアルタイム表示します。**キャンセル** で子プロセスツリーをいつでも終了できます。
   - 完了後は下部の「完了結果」で照合結果（`✓ 登録済みビルドと一致` / `✓ 新バージョン（未登録）` / `✗ ハッシュ不一致`）、データベースパス、ファイルハッシュ、コンテンツハッシュを確認し、**出力フォルダを開く** や **ハッシュをコピー** が利用できます。
   - 設定は `%LOCALAPPDATA%\agp\ffpr-export\settings.json` に保存され（レジストリは使用しません）、起動ログは `%LOCALAPPDATA%\agp\ffpr-export\logs\` に最新 10 件まで保持されます。

```text
+-----------------------------------------------------------------------------+
| FFPR エクスポートツール v0.1.0 — FINAL FANTASY I–VI Pixel Remaster          |
+-----------------------------------------------------------------------------+
|                                                 言語：[ 日本語           v] |
| 1. ゲーム（Steam ライブラリを自動スキャン、既定で全選択）                   |
| [x] すべて選択  3 / 3 本のゲームを選択中     [ 再スキャン ] [ フォルダ追加 ] |
| +------------------------+----------+-------------------------------------+ |
| | ゲーム                 | ビルドID | ディレクトリ                        | |
| | [x] FINAL FANTASY (FF1)| 20249397 | C:\...\common\FINAL FANTASY PR      | |
| | [x] FINAL FANTASY V... | 20265087 | D:\...\common\FINAL FANTASY V PR    | |
| +------------------------+----------+-------------------------------------+ |
|                                                                             |
| 2. 出力先                                                                   |
| 出力ルート：[ %LOCALAPPDATA%\agp\ffpr-export\output             ] [ 参照… ] |
| サブディレクトリプレビュー：ff1-20249397、ff5-20265087 など計 3 個          |
|                                                                             |
| [              エクスポート開始              ]          [ キャンセル ]      |
| 2 / 3 本目：FF5（ステップ 3/6）                                             |
| [===========================-----------------------]                        |
| | ==> [ff5] ステップ 1/6：ゲームバージョンとリソースフィンガープリント... ^ | |
| | ==> [ff5] ステップ 2/6：プロセス内で inventory と export を実行中       | | |
| | ==> [ff5] ステップ 3/6：プロセス内で db build を実行して ff5.sqlite...  v | |
|                                                                             |
| 3. 完了結果                                                                 |
| +--------------------+----------+----------------------+------------------+ |
| | ゲーム             | ビルドID | 判定                 | データベースパス | |
| | FINAL FANTASY (FF1)| 20249397 | ✓ 登録済みビルドと... | ...\ff1.sqlite   | |
| +--------------------+----------+----------------------+------------------+ |
| データベースパス：%LOCALAPPDATA%\agp\ffpr-export\output\ff1-20249397\ff1... |
| ファイルハッシュ：abb52056...                                               |
| コンテンツハッシュ：45fb76f7...                                             |
| 照合結果：✓ 登録済みビルドと一致（登録日：2026-11-25、取得元：file）        |
| [ 出力フォルダを開く ]  [ ハッシュをコピー ]                                |
+-----------------------------------------------------------------------------+
```

## 言語の選択

GUI・CLI ともに **英語（`en`、既定）**・**簡体字中国語（`zh-Hans`）**・**日本語（`ja`）** に対応しています。OS のシステム言語には自動追従しません。

優先順位（高い順）：

1. CLI オプション：`--lang en|zh-Hans|ja`（例：`ffpr-export --lang ja --help`）
2. 環境変数：`AGP_LANG`（`en`、`zh-Hans`、または `ja`）
3. GUI に保存された設定：`settings.json` の `language`（右上のドロップダウンで即時保存）
4. 既定値：`en`

GUI から `ffpr-export.exe` を起動する際は `--lang <現在の言語>` と `--progress-json` が自動的に渡されます。`--progress-json` 指定時、CLI はローカライズ済みの人間向けログに加えて言語非依存の `@@progress {"step":n,"total":m}` 行を出力し、GUI はこの行のみを使って進捗バーを更新します。

## データディレクトリと `AGP_HOME`

GUI・コンソールウィザード・CLI の `build-db` は共通のデータルート構造を使用します。

```text
%LOCALAPPDATA%\agp\ffpr-export\
  output\<ゲームID>-<ビルドID>\   既定の出力先：SQLite DB、ビルドレポート、--keep-json 時の json\
  logs\                           GUI 診断ログ（最新 10 件を保持）
  settings.json                   GUI 設定（出力ルート・言語・選択状態）
```

### ルート解決の優先順位

1. **環境変数 `AGP_HOME`**（`agp` 層として直接使用され、ツール用ディレクトリは `$AGP_HOME/ffpr-export/` になります）
2. **Windows**：`%LOCALAPPDATA%\agp`（書き込み不可の場合は `LocalLow`、`<exeDir>\agp-data`、`%USERPROFILE%\agp`、`agp-location.txt` へ自動フォールバック）
3. **Linux / macOS**：`$XDG_DATA_HOME/agp`（未設定時は `~/.local/share/agp`）

CLI の `--out <ディレクトリ>` や GUI で選択した出力ルートは、常に上記の既定値より優先されます。

### `AGP_HOME` の使用例

Windows（PowerShell）：

```powershell
$env:AGP_HOME = "D:\agp-data"
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR"
# 出力先：D:\agp-data\ffpr-export\output\ff5-<ビルドID>\
```

Linux / macOS（bash）：

```bash
AGP_HOME=/tmp/agp-data scripts/run.sh build-db --game "/path/to/FINAL FANTASY V PR"
# 出力先：/tmp/agp-data/ffpr-export/output/ff5-<ビルドID>/
```

### 旧バージョンからの設定移行

- 新しい `settings.json` が未作成で旧位置 `%APPDATA%\ffpr-export\settings.json`（Linux：`~/.config/ffpr-export/settings.json`）が存在する場合、初回起動時に自動で移行し、旧ファイルは削除せず `settings.json.migrated` にリネームされます（`AGP_HOME` が設定されている場合はホストの旧設定には触れません）。
- 旧バージョンの出力データやログは自動移行されません。

## CLI の使い方とコンソールウィザード

### 対話式コンソールウィザード（引数なし）

引数なし（または `--lang ja` のみ）で `ffpr-export.exe` を起動すると、対話式ウィザードが開始します。Steam ライブラリを自動スキャンし、単一ゲームまたは「すべて」を選択して順番に `build-db` を実行し、データベースパス・ハッシュ・`known-builds.json` 照合結果をまとめて表示します。

### サブコマンド

Linux/macOS では `scripts/run.sh`、Windows では `ffpr-export.exe` を直接実行します。

#### 1. `build-db` — ワンステップ構築＆照合

```bash
scripts/run.sh build-db --game "<Steam ゲームディレクトリ>" [--out <ディレクトリ>] [--steam-buildid <ID>] [--keep-json] [--jobs N] [--force] [--progress-json] [--lang en|zh-Hans|ja]
```

Windows での実行例（スペース含むパス・260 文字超の長いパス対応）：

```powershell
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR" --out "D:\FFPR Exports\ff5" --lang ja
```

処理の流れ：

1. ゲーム（FF1–FF6）とバージョン情報を自動識別（`appmanifest_<appid>.acf` の `buildid`、`GameAssembly.dll` / `global-metadata.dat` の SHA-256、`.exe` の PE バージョンリソース、Unity バージョン、`catalog.json` + 全 `.bundle` のリソースフィンガープリント）。
2. 同一プロセス内で `inventory` → `export` → `db build`（`<out>/<ffN>.sqlite`）→ `db hash --json` を順次実行。
3. `<out>/<ffN>.sqlite.build.json` を書き出し、`known-builds.json`（実行ファイル同ディレクトリ優先、なければ内蔵版）と照合して 3 種類の判定のいずれかを表示。
4. 既定では中間ディレクトリ `<out>/json/` を削除（`--keep-json` 指定時は保持）。`OutputGuard` によりゲームディレクトリ重複や外部ディレクトリ誤削除を防止します。

#### 2. `inventory` — アセット棚卸し

```bash
scripts/run.sh inventory --game "<Steam ゲームディレクトリ>" [--out "<出力ディレクトリ>"] [--jobs N] [--force] [--lang en|zh-Hans|ja]
```

`catalog.json` 配下の全 `.bundle` および `*_Data` 内のアセットを棚卸しし、`inventory.jsonl`・`summary.json`・`manifest.json` を出力します（既定：`.deps/ffpr/<ffN>/inventory`）。

#### 3. `export` — 生アセット抽出

```bash
scripts/run.sh export --game "<Steam ゲームディレクトリ>" [--out "<出力ディレクトリ>"] [--jobs N] [--force] [--lang en|zh-Hans|ja]
```

非音声 `TextAsset` をバイト列のまま抽出し、`MonoBehaviour` / `ScriptableObject` を JSON 化します（TypeTree 優先、欠落時は `AssetsTools.NET.Cpp2IL` で IL2CPP メタデータを解析）。画像・音声・動画・フォント・シェーダーはスキップされます。読み取り専用・途中再開対応・単一アセット障害時継続（終了コード：`0` 全成功、`1` 一部失敗あり、`2` 引数またはガードエラー）。

## 開発環境とパッケージング

### Linux 開発環境

既定ではリポジトリ直下の `.deps/dotnet`（.NET 10 SDK）と `.deps/nuget` を使用し、`~/.dotnet` を変更しません（既存の .NET 10 SDK を使う場合は `DOTNET_ROOT` を設定）。

```bash
mkdir -p .deps
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .deps/dotnet-install.sh
bash .deps/dotnet-install.sh --channel 10.0 --install-dir .deps/dotnet --no-path
```

### Windows 単一ファイルパッケージング

```bash
scripts/package-windows.sh
```

または Windows 上で直接実行：

```powershell
.\scripts\package-windows.ps1
```

自己完結型の単一ファイル `ffpr-export-gui.exe` / `ffpr-export.exe`（3 言語リソース内蔵、ネイティブ DLL の `e_sqlite3.dll` は `%TEMP%` 展開せず同フォルダ配置）、`README-Windows.txt`、`known-builds.json`、`README.md`、`LICENSE`、`SHA256SUMS` を含む `dist/ffpr-export-<バージョン>-win-x64.zip` を生成します。

## クロスゲームデータ調査

全 6 作の詳細なデータ構造比較は [docs/data-survey.md](docs/data-survey.md)（中国語）を参照してください。

## ビルドとテスト

```bash
scripts/check.sh
```

`dotnet format --verify-no-changes`（Linux）・`dotnet build -c Release`（`Nullable` 有効、警告はエラー扱い）・`dotnet test -c Release --no-build`（合成 Unity バンドルによる xUnit テスト）を順に実行します。

## CI とリリース

GitHub Actions 上で `ubuntu-latest` と `windows-latest` のネイティブ .NET 10 環境による継続的インテグレーションと自動リリースを行います。

- **`.github/workflows/ci.yml`**：`check`、`fixture-hash`、`compare-hash`（Linux と Windows で構築した合成 DB・合成エクスポートのバイト完全一致検証）、`package-windows`（`main` プッシュ時に 3 言語および `TEMP` 書き込み不可テストを含めて検証し、30 日間保存される Artifact をアップロード）。
- **`.github/workflows/release.yml`**：`VERSION` と一致する `v*` タグのプッシュで自動検証・ビルド・パッケージングを行い、GitHub Release を作成します。

## ライセンス

[MIT License](LICENSE)
