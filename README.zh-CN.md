[English](README.md) | **简体中文** | [日本語](README.ja.md)

# final-fantasy-pixel-remaster-export

《FINAL FANTASY I–VI Pixel Remaster》（Steam Windows 版，Unity / IL2CPP）的资源盘点、导出与 SQLite 建库工具 `ffpr-export`（C# / .NET 10 / AssetsTools.NET）。

工具以只读方式扫描 Steam 游戏目录，自动识别 FF1–FF6、Unity 版本与 Steam 构建号，解析 Addressables `catalog.json` 与 `*_Data` 资源文件，跳过图像、音频、视频、字体、着色器，将其余结构化文本、CSV、Tiled 地图、事件/战斗脚本以及 `MonoBehaviour` / `ScriptableObject`（优先 TypeTree，缺失时用 IL2CPP 元数据生成类型）导出并构建为可复现的 SQLite 数据库。

> 本仓库为公开仓库，不保存任何游戏文件、导出数据、反编译产物或游戏正文文本；所有本地依赖与导出产物默认留在 `.deps/`（或 `$FFPR_DEPS`）下。

## 快速开始（Windows）

无需安装 .NET 运行时，也无需敲命令，点几下即可完成导出：

1. **下载压缩包**（二选一）：
   - **Releases 页面**：下载最新正式版的 `ffpr-export-<版本>-win-x64.zip`；
   - **Actions 页面**：打开最近一次 `main` 分支通过的 `CI` 工作流，在页面底部 **Artifacts** 下载 `ffpr-export-<版本>-<提交短号>-win-x64`（每次推送到 `main` 自动生成，保留 30 天）。
2. **完整解压**：将压缩包解压到任意文件夹（保持 `ffpr-export-gui.exe`、`ffpr-export.exe`、`e_sqlite3.dll` 与 `known-builds.json` 在同一目录）。
3. **双击 `ffpr-export-gui.exe`**：
   - 启动时自动扫描本机全部 Steam 库，列出已安装的 FF1–FF6 像素复刻版、Steam 构建号与目录，并**默认全选**（如未自动找到，也可点“手动选择游戏目录…”添加）；
   - 右上角提供语言下拉框（`English` / `简体中文` / `日本語`，默认英文），切换后界面立刻刷新并自动保存到 `settings.json`；
   - 输出根目录默认为 `%LOCALAPPDATA%\agp\ffpr-export\output\`（每个游戏生成 `<ffN>-<Steam 构建号>\` 子目录），点“浏览…”可改选其他位置；若目标目录已存在且不是本工具创建的目录，会先弹框确认，不静默覆盖；
   - 点击 **“开始导出”**，程序会依次调用同目录的 `ffpr-export.exe build-db`（附带 `--lang` 与 `--progress-json`）逐个构建，实时显示进度与日志，随时可点“取消”终止子进程树；
   - 完成后在底部“完成结果”区查看每个游戏的比对结论（`✓ 与登记的版本一致` / `✓ 新版本，未登记` / `✗ 哈希不同`）、数据库路径、文件哈希与内容哈希，并可一键 **“打开输出文件夹”**（打开实际导出的那个目录）或 **“复制哈希”**；
   - 上次的输出根目录、界面语言与勾选状态会自动保存在 `%LOCALAPPDATA%\agp\ffpr-export\settings.json`（不写注册表），启动日志保存在 `%LOCALAPPDATA%\agp\ffpr-export\logs\`（只保留最近 10 个）。

```text
+-----------------------------------------------------------------------------+
| FFPR 导出工具 v0.1.0 — FINAL FANTASY I–VI Pixel Remaster                    |
+-----------------------------------------------------------------------------+
|                                                 语言：[ 简体中文         v] |
| 1. 游戏（自动扫描 Steam 库，默认全选）                                       |
| [x] 全选  已选 3 / 3 个游戏               [ 重新扫描 ] [ 手动选择游戏目录… ] |
| +------------------------+----------+-------------------------------------+ |
| | 游戏                   | 构建号   | 目录                                | |
| | [x] FINAL FANTASY (FF1)| 20249397 | C:\...\common\FINAL FANTASY PR      | |
| | [x] FINAL FANTASY V... | 20265087 | D:\...\common\FINAL FANTASY V PR    | |
| +------------------------+----------+-------------------------------------+ |
|                                                                             |
| 2. 输出位置                                                                 |
| 输出根目录：[ %LOCALAPPDATA%\agp\ffpr-export\output              ] [ 浏览… ] |
| 子目录预览：将分别输出到 ff1-20249397、ff5-20265087 等 3 个子目录            |
|                                                                             |
| [                  开 始 导 出                  ]         [   取  消   ]    |
| 第 2 / 3 个：FF5（步骤 3/6）                                                 |
| [===========================-----------------------]                        |
| | ==> [ff5] 步骤 1/6：识别游戏版本与资源指纹                              ^ | |
| | ==> [ff5] 步骤 2/6：进程内执行 inventory 与 export                      | | |
| | ==> [ff5] 步骤 3/6：进程内执行 db build 构建 ff5.sqlite                 v | |
|                                                                             |
| 3. 完成结果                                                                 |
| +--------------------+----------+--------------------+--------------------+ |
| | 游戏               | 构建号   | 结论               | 库文件路径         | |
| | FINAL FANTASY (FF1)| 20249397 | ✓ 与登记的版本一致 | ...\ff1.sqlite     | |
| +--------------------+----------+--------------------+--------------------+ |
| 数据库路径：%LOCALAPPDATA%\agp\ffpr-export\output\ff1-20249397\ff1.sqlite   |
| 文件哈希：  abb52056...                                                     |
| 内容哈希：  45fb76f7...                                                     |
| 比对结论：  ✓ 与登记的版本一致（登记日期：2026-11-25，来源：file）          |
| [ 打开输出文件夹 ]  [ 复制哈希 ]                                            |
+-----------------------------------------------------------------------------+
```

## 语言选择

窗口程序与命令行均支持 **英文（`en`，默认）**、**简体中文（`zh-Hans`）** 与 **日文（`ja`）**。程序**不会**跟随操作系统语言自动切换。

优先顺序（从高到低）：

1. 命令行参数：`--lang en|zh-Hans|ja`（例如 `ffpr-export --lang zh-Hans --help`）
2. 环境变量：`AGP_LANG`（`en`、`zh-Hans` 或 `ja`）
3. 窗口程序保存的设置：`settings.json` 中的 `language` 字段（通过右上角下拉框即时保存）
4. 默认值：`en`

窗口程序调用 `ffpr-export.exe` 时会自动传入 `--lang <当前语言>` 与 `--progress-json`。开启 `--progress-json` 后，命令行会在人看的本地化日志之外输出与语言无关的 `@@progress {"step":n,"total":m}` 进度行，窗口程序仅凭该协议行推进进度条。

## 数据目录与 `AGP_HOME`

窗口程序、控制台向导与命令行 `build-db` 共用同一套数据根目录布局，避免导出产物、日志与配置文件散落各处：

```text
%LOCALAPPDATA%\agp\ffpr-export\
  output\<游戏代号>-<Steam 构建号>\   默认导出位置：数据库、构建报告、--keep-json 时的 json\
  logs\                              窗口程序日志（只保留最近 10 个）
  settings.json                      窗口程序的设置（输出根目录、语言、勾选状态）
```

### 根目录优先顺序

程序通过统一函数按以下顺序确定 `agp` 根目录：

1. **环境变量 `AGP_HOME`**（直接作为 `agp` 这一层，工具目录为 `$AGP_HOME/ffpr-export/`）；
2. **Windows**：`%LOCALAPPDATA%\agp`（不可写时依次回退至 `LocalLow`、`<exeDir>\agp-data`、`%USERPROFILE%\agp` 或 `agp-location.txt`）；
3. **Linux / macOS**：`$XDG_DATA_HOME/agp`；未设置 `XDG_DATA_HOME` 时使用 `~/.local/share/agp`。

用户显式传入的 `--out <目录>`，或在窗口程序中通过“浏览…”选择的输出根目录，仍然优先于上述默认值；窗口程序的“打开输出文件夹”按钮会直接打开实际导出的那个目录。

### `AGP_HOME` 用法示例

Windows（PowerShell）：

```powershell
$env:AGP_HOME = "D:\agp-data"
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR"
# 输出至 D:\agp-data\ffpr-export\output\ff5-<Steam 构建号>\
```

Linux / macOS（bash）：

```bash
AGP_HOME=/tmp/agp-data scripts/run.sh build-db --game "/path/to/FINAL FANTASY V PR"
# 输出至 /tmp/agp-data/ffpr-export/output/ff5-<Steam 构建号>/
```

### 旧版本位置与自动迁移

- **窗口程序设置自动迁移**：首次启动新版本时，若新位置 `%LOCALAPPDATA%\agp\ffpr-export\settings.json` 尚不存在、且旧位置 `%APPDATA%\ffpr-export\settings.json`（Linux 为 `~/.config/ffpr-export/settings.json`）存在，程序会自动读取旧设置写入新位置，并将旧文件改名为 `settings.json.migrated`（不删除）。当设置了 `AGP_HOME` 时不会读取或改动宿主机的旧 `%APPDATA%` 设置。
- **旧导出结果与旧日志不自动迁移**（如需保留或清理可手动移动/删除）：
  - 窗口程序旧默认输出：`%USERPROFILE%\Documents\FF-Export\<游戏代号>-<Steam 构建号>\`；
  - 窗口程序旧日志：`%LOCALAPPDATA%\ffpr-export\logs\`；
  - 控制台向导旧默认输出：可执行文件同目录下的 `output\<游戏代号>-<Steam 构建号>\`；
  - `build-db` 未传 `--out` 时的旧默认输出：当前目录下的 `ffpr-<ffN>-build\`。

## 命令行用法与控制台向导

### 控制台向导（无参数启动 `ffpr-export.exe`）

若在没有图形界面或需要终端交互的场景下使用，可直接无参数（或仅带 `--lang zh-Hans`）运行 `ffpr-export.exe` 启动控制台向导：自动扫描 Steam 游戏库，按编号选择单个游戏或“全部”，默认输出到 `%LOCALAPPDATA%\agp\ffpr-export\output\<游戏代号>-<Steam 构建号>\`（可自定义路径或通过 `AGP_HOME` 覆盖根目录），确认后依次执行 `build-db` 并汇总数据库路径、哈希与比对结论。

### 命令行子命令

传入参数时走完整命令行模式。Linux 下通过 `scripts/run.sh` 调用，Windows 下直接运行 `ffpr-export.exe`：

#### 1. `build-db` — 一步建库与版本核对

```bash
scripts/run.sh build-db --game "<Steam 游戏目录>" [--out <目录>] [--steam-buildid <编号>] [--keep-json] [--jobs N] [--force] [--progress-json] [--lang en|zh-Hans|ja]
```

不传 `--out` 时默认输出至 `<AGP 根目录>/ffpr-export/output/<游戏代号>-<Steam 构建号>/`；显式指定 `--out` 时优先使用指定目录。

Windows 下直接运行单文件可执行程序（支持带空格路径与超长路径）：

```powershell
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR" --out "D:\FFPR Exports\ff5" --lang zh-Hans
```

一步流程：

1. 自动识别游戏（FF1–FF6）与游戏版本信息：
   - 向上两级读取 `steamapps/appmanifest_<appid>.acf` 的 `buildid`；读不到时可用 `--steam-buildid` 手动指定（来源记为 `manual`）；两者均缺失时记为 `null` 并打印警告；
   - 计算 `GameAssembly.dll` 与 `global-metadata.dat` 的 SHA-256；
   - 跨平台纯 C# 解析游戏 `.exe` 的 PE 版本资源（`FileVersion` / `ProductVersion`）；
   - 读取资源文件头中的 Unity 版本；
   - 计算资源指纹：`StreamingAssets/aa/catalog.json` 的 SHA-256 与全部 `.bundle` 按相对路径排序后的“路径、大小、SHA-256”合成哈希，并记录耗时。
2. 在同一进程内依次执行 `inventory`、`export`（输出至 `<out>/json/inventory/` 与 `<out>/json/export/`）、`db build`（生成 `<out>/<ffN>.sqlite`）与 `db hash --json`。
3. 写出 `<out>/<ffN>.sqlite.build.json`（含游戏版本、工具版本与 git 提交、数据库 `file_sha256` / `content_sha256`、逐表行数与哈希、各阶段耗时）。
4. 按“可执行文件同目录 → 程序内嵌副本”的优先级加载 `known-builds.json`（按 `ff1`–`ff6` 分组）并比对，打印三种结论之一：与登记的版本一致 / 新的游戏版本，未登记 / 游戏版本已登记，但数据库哈希不同（并列出差异表）。
5. 默认清理中间目录 `<out>/json/`；传入 `--keep-json` 可保留用于核对。输出目录遵循 `OutputGuard` 护栏（拒绝与 `--game` 重叠、拒绝 `.deps/` 受保护子目录、非空目录需 `--force` 且仅允许清空 `build-db` 自有目录）。

#### 2. `inventory` — 资源盘点

```bash
scripts/run.sh inventory --game "<Steam 游戏目录>" [--out "<输出目录>"] [--jobs N] [--force] [--lang en|zh-Hans|ja]
```

- 自动识别游戏（FF1–FF6，检查 `*_Data` 目录或 `.exe` 名）、读取资源文件头中的 Unity 版本，并向上两级读取 `steamapps/appmanifest_<appid>.acf` 中的 `buildid`。
- 解析 `StreamingAssets/aa/catalog.json` 关联的全部 `.bundle`，同时盘点 `*_Data` 下的 `resources.assets`、`sharedassets*.assets`、`level*`。
- 输出到 `--out`（默认 `.deps/ffpr/<ffN>/inventory`）：`inventory.jsonl`、`summary.json` 与 `manifest.json`。

#### 3. `export` — 原始资源导出

```bash
scripts/run.sh export --game "<Steam 游戏目录>" [--out "<输出目录>"] [--jobs N] [--force] [--lang en|zh-Hans|ja]
```

- 默认输出到 `.deps/ffpr/<ffN>/export`（可通过环境变量 `FFPR_DEPS` 覆盖 `.deps` 基准目录，或通过 `--out` 指定任意合法路径）。
- 导出非音频 `TextAsset`（原样字节写出）与 `MonoBehaviour` / `ScriptableObject`（优先 TypeTree，缺失时用 `AssetsTools.NET.Cpp2IL` 读取 IL2CPP 元数据）；跳过图像、音频（含 `*.mab.bytes` / `*.sab.bytes`）、视频、字体（含加密的 `font_*.bundle`）、着色器/材质与纯视觉动画。
- 只读游戏目录、支持断点续跑、原子替换写出、单资产容错（退出码 `0` 全部成功，`1` 部分失败，`2` 参数或护栏错误）。

## 环境准备

### Linux 开发环境

默认使用仓库根目录的 `.deps/dotnet`（.NET 10 SDK）与 `.deps/nuget`（NuGet 缓存），不修改 `~/.dotnet`；也可通过 `DOTNET_ROOT` 指定已安装的 .NET 10 SDK：

```bash
mkdir -p .deps
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .deps/dotnet-install.sh
bash .deps/dotnet-install.sh --channel 10.0 --install-dir .deps/dotnet --no-path
```

### Windows 单文件发布与打包

无需在目标机安装 .NET 运行时，可执行打包脚本同时构建自包含单文件 `ffpr-export-gui.exe`（WinForms 图形界面，支持高 DPI 与 `longPathAware`）与 `ffpr-export.exe`（命令行 / 控制台向导），内嵌全部三种语言资源并随目录分发 `e_sqlite3.dll`（不解压至 `%TEMP%`）：

```bash
scripts/package-windows.sh
```

在 Windows 上也可直接执行：

```powershell
.\scripts\package-windows.ps1
```

在 `dist/` 下生成 `ffpr-export-<版本>-win-x64.zip`，内含 `ffpr-export-gui.exe`、`ffpr-export.exe`、`e_sqlite3.dll`、`README-Windows.txt`、`known-builds.json`、`README.md`、`LICENSE` 与 `SHA256SUMS`。

## 跨代数据结构调查

全六作的详细结构对比（主数据 CSV 表与列差异、12 语言消息、Tiled 地图、事件/战斗/AI 脚本及 `MonoBehaviour` 分布）见 [docs/data-survey.md](docs/data-survey.md)。

## 构建与测试

```bash
scripts/check.sh
```

脚本依次执行：

1. `dotnet format FfprExport.sln --verify-no-changes`（Linux 环境）
2. `dotnet build FfprExport.sln -c Release`（启用 `Nullable` 与警告当错误）
3. `dotnet test FfprExport.sln -c Release --no-build`（xUnit，全部使用合成 Unity 资源包测试，不依赖真实游戏文件）

## CI 和发布

仓库通过 GitHub Actions 在 `ubuntu-latest` 与 `windows-latest` 上使用各自原生 .NET 10 工具链执行持续集成与自动发版：

- **`.github/workflows/ci.yml`**：包含 `check`、`fixture-hash`、`compare-hash`（强制核对 Linux 与 Windows 构建出的假数据库和合成导出文件逐字节哈希一致）与 `package-windows`（推送至 `main` 时打包、校验三语言与阻塞 `TEMP` 场景并上传 30 天 Artifact）。
- **`.github/workflows/release.yml`**：推送与 `VERSION` 一致的 `v*` tag 后自动验证、打包并发布 GitHub Release，上传 `ffpr-export-<版本>-win-x64.zip`、`SHA256SUMS` 与 `known-builds.json`。

## 许可

[MIT License](LICENSE)
