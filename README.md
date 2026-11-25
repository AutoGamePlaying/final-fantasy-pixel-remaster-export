# final-fantasy-pixel-remaster-export

《FINAL FANTASY I–VI Pixel Remaster》（Steam Windows 版，Unity / IL2CPP）的资源盘点、导出与 SQLite 建库工具 `ffpr-export`（C# / .NET 10 / AssetsTools.NET）。

工具以只读方式扫描 Steam 游戏目录，自动识别 FF1–FF6、Unity 版本与 Steam 构建号，解析 Addressables `catalog.json` 与 `*_Data` 资源文件，跳过图像、音频、视频、字体、着色器，将其余结构化文本、CSV、Tiled 地图、事件/战斗脚本以及 `MonoBehaviour` / `ScriptableObject`（优先 TypeTree，缺失时用 IL2CPP 元数据生成类型）导出并构建为可复现的 SQLite 数据库。

> 本仓库为公开仓库，不保存任何游戏文件、导出数据、反编译产物或游戏正文文本；所有本地依赖与导出产物默认留在 `.deps/`（或 `$FFPR_DEPS`）下。

## 快速开始（Windows）

无需安装 .NET 运行时，也无需敲命令，点几下即可完成导出：

1. **下载压缩包**（二选一）：
   - **Releases 页面**：下载最新正式版的 `ffpr-export-<版本>-win-x64.zip`；
   - **Actions 页面**：打开最近一次 `main` 分支通过的 `CI` 工作流，在页面底部 **Artifacts** 下载 `ffpr-export-<版本>-<提交短号>-win-x64`（每次推送到 `main` 自动生成，保留 30 天）。
2. **完整解压**：将压缩包解压到任意文件夹（保持 `ffpr-export-gui.exe`、`ffpr-export.exe` 与 `known-builds.json` 在同一目录）。
3. **双击 `ffpr-export-gui.exe`**：
   - 启动时自动扫描本机全部 Steam 库，列出已安装的 FF1–FF6 像素复刻版、Steam 构建号与目录，并**默认全选**（如未自动找到，也可点“手动选择游戏目录…”添加）；
   - 输出根目录默认为 `%USERPROFILE%\Documents\FF-Export\`（每个游戏生成 `<ffN>-<Steam 构建号>\` 子目录），点“浏览…”可改选其他位置；若目标目录已存在且不是本工具创建的目录，会先弹框确认，不静默覆盖；
   - 点击 **“开始导出”**，程序会依次调用同目录的 `ffpr-export.exe build-db` 逐个构建（不并行），实时显示“第 k / n 个：FFk”进度与日志，随时可点“取消”终止子进程树；
   - 完成后在底部“完成结果”区查看每个游戏的比对结论（`✓ 与登记的版本一致` / `✓ 新版本，未登记` / `✗ 哈希不同`）、数据库路径、文件哈希与内容哈希，并可一键 **“打开输出文件夹”** 或 **“复制哈希”**；
   - 上次的输出根目录与勾选状态会自动保存在 `%APPDATA%\ffpr-export\settings.json`（不写注册表）。

```text
+-----------------------------------------------------------------------------+
| FFPR 导出工具 v0.1.0 — FINAL FANTASY I–VI Pixel Remaster                    |
+-----------------------------------------------------------------------------+
| 1. 游戏（自动扫描 Steam 库，默认全选）                                       |
| [x] 全选  已选 3 / 3 个游戏               [ 重新扫描 ] [ 手动选择游戏目录… ] |
| +------------------------+----------+-------------------------------------+ |
| | 游戏                   | 构建号   | 目录                                | |
| | [x] FINAL FANTASY (FF1)| 20249397 | C:\...\common\FINAL FANTASY PR      | |
| | [x] FINAL FANTASY V... | 20265087 | D:\...\common\FINAL FANTASY V PR    | |
| +------------------------+----------+-------------------------------------+ |
|                                                                             |
| 2. 输出位置                                                                 |
| 输出根目录：[ C:\Users\<用户>\Documents\FF-Export                ] [ 浏览… ] |
| 子目录预览：将分别输出到 ff1-20249397、ff5-20265087 等 3 个子目录            |
|                                                                             |
| [                  开 始 导 出                  ]         [   取  消   ]    |
| 第 2 / 3 个：FF5（步骤 3/6：进程内执行 db build 构建 ff5.sqlite）            |
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
| 数据库路径：C:\Users\<用户>\Documents\FF-Export\ff1-20249397\ff1.sqlite     |
| 文件哈希：  abb52056...                                                     |
| 内容哈希：  45fb76f7...                                                     |
| 比对结论：  ✓ 与登记的版本一致（登记日期：2026-11-25，来源：file）          |
| [ 打开输出文件夹 ]  [ 复制哈希 ]                                            |
+-----------------------------------------------------------------------------+
```

## 命令行用法与控制台向导

### 控制台向导（无参数启动 `ffpr-export.exe`）

若在没有图形界面或需要终端交互的场景下使用，可直接双击或无参数运行 `ffpr-export.exe` 启动中文控制台向导：自动扫描 Steam 游戏库，按编号选择单个游戏或“全部”，确认输出目录后依次执行 `build-db` 并汇总数据库路径、哈希与比对结论。

### 命令行子命令

传入参数时走完整命令行模式。Linux 下通过 `scripts/run.sh` 调用，Windows 下直接运行 `ffpr-export.exe`：

### 1. `build-db` — 一步建库与版本核对

```bash
scripts/run.sh build-db --game "<Steam 游戏目录>" [--out <目录>] [--steam-buildid <编号>] [--keep-json] [--jobs N] [--force]
```

Windows 下直接运行单文件可执行程序（支持带空格路径与超长路径）：

```powershell
.\ffpr-export.exe build-db --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR" --out "D:\FFPR Exports\ff5"
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

### 2. `inventory` — 资源盘点

```bash
scripts/run.sh inventory --game "<Steam 游戏目录>" [--out "<输出目录>"] [--jobs N] [--force]
```

- 自动识别游戏（FF1–FF6，检查 `*_Data` 目录或 `.exe` 名）、读取资源文件头中的 Unity 版本，并向上两级读取 `steamapps/appmanifest_<appid>.acf` 中的 `buildid`。
- 解析 `StreamingAssets/aa/catalog.json` 关联的全部 `.bundle`，同时盘点 `*_Data` 下的 `resources.assets`、`sharedassets*.assets`、`level*`。
- 输出到 `--out`（默认 `.deps/ffpr/<ffN>/inventory`）：
  - `inventory.jsonl`：逐资产明细（来源、容器路径、Unity 类型、脚本类名、字节大小、TypeTree 状态与导出决策）。
  - `summary.json`：按类型、按目录、按决策、按 bundle 汇总统计及分类理由。
  - `manifest.json`：盘点清单（供 `--force` 护栏校验）。

### 3. `export` — 原始资源导出

```bash
scripts/run.sh export --game "<Steam 游戏目录>" [--out "<输出目录>"] [--jobs N] [--force]
```

- 默认输出到 `.deps/ffpr/<ffN>/export`（可通过环境变量 `FFPR_DEPS` 覆盖 `.deps` 基准目录，或通过 `--out` 指定任意合法路径）。
- 导出内容：
  - **非音频 `TextAsset`**：按容器路径原样写出原始字节（`Assets/GameAssets/Serial/` 剥离为 `Res/…`、`Data/…`，与旧版 UnityPy 导出布局逐字节一致；`Assets/GameAssets/` 其余路径输出为 `AssetsPath.json`、`Common/…`；`*_Data` 文本输出至 `_data/<文件名>/`）。
  - **`MonoBehaviour` / `ScriptableObject`**：资源包携带 TypeTree 时直接按 TypeTree 导出；`*_Data` 剥离 TypeTree 时通过 `AssetsTools.NET.Cpp2IL` 读取 `GameAssembly.dll` 与 `il2cpp_data/Metadata/global-metadata.dat` 反序列化为 JSON。独立容器资产写出 `<容器路径>.json`，组件资产按来源归组写出至 `_monobehaviour/bundles/*.json` 与 `_monobehaviour/data/*.json`。
  - **跳过**：图像/网格/渲染组件、音频（含 `Res/Audio/*.mab.bytes` / `*.sab.bytes`）、视频、字体（含加密的 `StreamingAssets/font_*.bundle`）、着色器/材质、纯视觉动画及 Unity 场景骨架对象。
- 护栏与容错：
  - **只读游戏目录**：工具绝不修改 `--game` 下的任何文件，且禁止 `--out` 与 `--game` 重叠或指向 `.deps/` 受保护子目录。
  - **断点续跑**：再次对已有 `export` 输出目录运行（不加 `--force`）自动跳过已导出文件，所有文件均采用临时文件原子替换写出。
  - **`--force` 护栏**：仅允许清空包含本工具同名子命令 `manifest.json`（`tool == "ffpr-export"` 且 `command` 匹配）的目录，拒绝误删外部目录或其他子命令目录。
  - **单资产容错**：单个资源或 bundle 失败记录进 `manifest.json` 的 `failures` 列表，不中断其余导出（有失败时退出码为 `1`，全部成功为 `0`，参数或护栏错误为 `2`）。

Windows 带空格路径示例：

```powershell
.\ffpr-export.exe inventory --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR" --out "D:\FFPR Exports\ff5\inventory"
.\ffpr-export.exe export --game "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR" --out "D:\FFPR Exports\ff5\export" --jobs 8
```

## 环境准备

### Linux 开发环境

默认使用仓库根目录的 `.deps/dotnet`（.NET 10 SDK）与 `.deps/nuget`（NuGet 缓存），不修改 `~/.dotnet`；也可通过 `DOTNET_ROOT` 指定已安装的 .NET 10 SDK：

```bash
mkdir -p .deps
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .deps/dotnet-install.sh
bash .deps/dotnet-install.sh --channel 10.0 --install-dir .deps/dotnet --no-path
```

### Windows 单文件发布与打包

无需在目标机安装 .NET 运行时，可执行打包脚本同时构建自包含单文件 `ffpr-export-gui.exe`（WinForms 图形界面，支持高 DPI 与 `longPathAware`）与 `ffpr-export.exe`（命令行 / 控制台向导），并产出带校验和的发布压缩包：

```bash
scripts/package-windows.sh
```

脚本优先读取当前 HEAD 的 git tag 作为版本号（无 tag 时读取根目录 `VERSION`），在 `dist/` 下生成 `ffpr-export-<版本>-win-x64.zip`，内含 `ffpr-export-gui.exe`、`ffpr-export.exe`、`使用说明.txt`、`known-builds.json`、`README.md`、`LICENSE` 与 `SHA256SUMS`。

## 跨代数据结构调查

全六作的详细结构对比（主数据 CSV 表与列差异、12 语言消息、Tiled 地图、事件/战斗/AI 脚本及 `MonoBehaviour` 分布）见 [docs/data-survey.md](docs/data-survey.md)。

## 构建与测试

```bash
scripts/check.sh
```

脚本依次执行：

1. `dotnet format FfprExport.sln --verify-no-changes`
2. `dotnet build FfprExport.sln -c Release`（启用 `Nullable` 与警告当错误）
3. `dotnet test FfprExport.sln -c Release --no-build`（xUnit，全部使用合成 Unity 资源包测试，不依赖真实游戏文件）

脚本会自动设置 `MSBUILDDISABLENODEREUSE=1`、`DOTNET_CLI_USE_MSBUILD_SERVER=0`、`UseSharedCompilation=false`，避免 MSBuild / 编译器常驻节点滞留并占住文件锁。

## CI 和发布

仓库通过 GitHub Actions 在 `ubuntu-latest` 与 `windows-latest` 上使用各自原生 .NET 10 工具链执行持续集成与自动发版。

### 持续集成（`.github/workflows/ci.yml`）

每次向任意分支 `push` 或提交 `pull_request` 时触发（最小权限 `contents: read`），包含四个 job：

1. **`check`**（`ubuntu-latest` + `windows-latest`）：安装 .NET 10 SDK 并缓存 NuGet 后执行 `scripts/check.sh`（Linux 上额外跑 `dotnet format --verify-no-changes`，两个平台均编译包含 `FfprExport.Gui` 在内的 `FfprExport.sln` 并执行 `dotnet test`；Windows 上另以 `--self-test` 对 `ffpr-export-gui.exe` 做无窗口依赖自检；CI 上不含 `.deps/`）。
2. **`fixture-hash`**（`ubuntu-latest` + `windows-latest`）：执行 `scripts/fixture-db.sh`，生成确定的合成导出目录并运行完整 `ffpr-export db build`，随后执行 `ffpr-export db hash --json` 并将结果上传为 artifact。
3. **`compare-hash`**（依赖两个 `fixture-hash`）：下载 Linux 与 Windows 两份哈希 JSON，比对 `file_sha256`、`content_sha256`、`schema_version` 以及每张普通表的行数与哈希；任意一项不一致即失败并列出存在差异的表。
4. **`package-windows`**（仅在 `push` 到 `main` 时运行，依赖 `check`，`windows-latest`）：调用 `scripts/package-windows.ps1` 构建包含 `ffpr-export-gui.exe`、`ffpr-export.exe` 与 `使用说明.txt` 的 Windows x64 单文件压缩包，解压校验并执行 `ffpr-export-gui.exe --self-test --require-cli`，最后以 `ffpr-export-<版本>-<提交短号>-win-x64` 为名上传为 GitHub Actions Artifact，保留 30 天。

### 跨平台哈希核对的意义

真实游戏文件与导出数据不会上传到公开仓库或 CI。通过同一份确定的合成导出数据在 Linux 与 Windows 上各自原生构建 SQLite 数据库，强制要求 `file_sha256`（整个数据库文件字节级一致）、`content_sha256` 以及全部普通表哈希完全相同，可以在每次改动时持续验证建库流程没有引入与操作系统、路径分隔符、线程调度或换行符相关的非确定性差异。

### 发版流程（`.github/workflows/release.yml`）

发版由 `v*` tag 自动触发，流程如下：

1. 更新仓库根目录的 `VERSION` 文件（例如 `0.1.0`）并提交到目标分支；
2. 创建与 `VERSION` 一致的 tag 并推送：

```bash
git tag v0.1.0
git push origin v0.1.0
```

推送 tag 后，`release.yml` 会在 `windows-latest` 上校验 tag 去掉 `v` 后与 `VERSION` 一致，先完整运行 `scripts/check.sh`，再运行 `scripts/package-windows.ps1` 原生发布 `win-x64` 自包含单文件并生成压缩包与 `SHA256SUMS`，最后使用 `GITHUB_TOKEN` 创建标题为 tag 名的 GitHub Release，上传 `ffpr-export-<版本>-win-x64.zip`、`SHA256SUMS` 与 `known-builds.json`（本地 Linux 交叉编译打包脚本 `scripts/package-windows.sh` 保留供本机使用）。

## 许可

[MIT License](LICENSE)
