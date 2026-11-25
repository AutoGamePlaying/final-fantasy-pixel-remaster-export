# FINAL FANTASY I–VI Pixel Remaster 本地 SQLite 数据库设计（`ffpr-export db`）

本文档说明 `ffpr-export db` 将 `ffpr-export export` 导出的结构化资产构建为单游戏 SQLite 数据库（`ff1.sqlite` … `ff6.sqlite`）的表结构、索引策略、坐标与别名归一化规则、可复现构建机制及规范化内容哈希算法。

> 数据库文件（`.deps/ffpr/<ffN>/db/<ffN>.sqlite`）与游戏导出数据一样**不进入版本库**；单元测试全部使用自编合成数据。

## 1. 设计原则

- **一作一库**：每部作品（FF1–FF6）独立构建为一个 SQLite 文件，运行依赖 `Microsoft.Data.Sqlite`（固定版本，内置 `SQLitePCLRaw.bundle_e_sqlite3`）。
- **索引列 + `doc` 列 + 关系表**：
  - 常用查找字段（主键、外键、文本键、解析好的中/日/英名称、分类、坐标、助记符、Unity 类名等）拆成独立索引列；
  - `doc` 列完整保留原始 JSON（主数据 CSV 的 `doc` 保留原始列名，不做删改）；
  - 跨表/跨区域引用由独立关系表（`rel_*`）表达并双向建索引；悬空引用、重复主键或解析错误记入 `build_issue`，不静默丢弃也不中断整库构建。
- **每次查询走索引**：所有类型层表的主键查询、名称/关键列查询及关系表正反查均通过 `EXPLAIN QUERY PLAN` 测试确认使用索引（`SEARCH`），无全表扫描。
- **字节级可复现**：库内不写构建时间、阶段耗时或本机路径；所有并行解析先收集再按自然主键排序插入；固定数据库头参数并以 `ANALYZE; VACUUM;` 收尾；易变构建元信息写进旁边的 `<db>.manifest.json`。

## 2. 区域划分与表结构（Schema v1）

数据库共划分为 9 个逻辑区域（46 张普通表 + 1 张 FTS5 虚拟表）：

| 区域 | 来源 | 表 | 说明 |
| --- | --- | --- | --- |
| `meta` | 构建器 | `meta`、`source`、`stage_report`、`build_issue` | 表结构版本、游戏代号、工具版本、相对输入路径及源哈希、阶段行数（无耗时）、构建问题 |
| `text` | `Data/Message/*_<lang>.txt` | `text`、`text_fts` | 4 个 scope × 12 种语言；`value_plain` 去除控制/图标标签；`text_fts` 为 FTS5 `trigram` 全文索引 |
| `master_raw` | `Data/Master/*.csv` | `master_table`、`master_row` | 通用层：97 张主数据 CSV（每作 91–96 张）原样入库，通过 `(table_name, row_key, row_ord)` 兼容重复主键 |
| `master_typed` | `Data/Master/*.csv` + `text` | `content`、`item`、`weapon`、`armor`、`ability`、`monster`、`monster_party`、`monster_set`、`job`、`command`、`condition`、`area`、`map`、`encount_area`、`encount_area_grid`、`product_group`、`product`、`character_status`、`battle_effect_asset` | 类型层：常用主表展开索引列、做列别名归一化并附带解析好的 `name_zhc` / `name_ja` / `name_en` |
| `map` | `Res/Map/**` | `map_package`、`map_submap`、`map_layer`、`map_grid`、`map_object`、`treasure_chest` | 地图包、子地图、图层元数据、紧凑二维格子 BLOB、地图对象（含像素与格子坐标）、宝箱 |
| `script` | `Res/Map/**/sc_*.json`、`Map_Script/Resident/`、`Res/Battle/**`、`Common/Data/Sandbox/`、`package.json` 内联脚本 | `script`、`script_instruction` | 地图事件、常驻脚本、战斗脚本、两代怪物 AI（`EntryPoint` 与 `Segments`）及沙盒脚本；指令按 `mnemonic` 建索引 |
| `unity` | `_monobehaviour/**`、`Res/Opera/**/*.playable.json` | `unity_object`、`input_action_map`、`input_action`、`input_binding` | 全部 `MonoBehaviour` / `ScriptableObject`（按 `class` 建索引），并将 `InputActionAsset` 拆成输入映射/动作/绑定表 |
| `config` | `AssetsPath.json`、`battle_extra_data.json`、`serial_misc_assets.json`、`common_master.json`、`serial_master.json`、`anim_info.json`、`sead.*.config` 等 | `config_doc` | 杂项配置、动画元数据与音频引擎配置（非 JSON 文本包装为 `{"raw": "..."}`） |
| `relation` | 跨表连接 | `rel_encount_area_party`、`rel_monster_set_party`、`rel_monster_party_member`、`rel_shop_product`、`rel_treasure_content`、`rel_monster_ai`、`rel_map_submap`、`rel_script_text` | 遭遇区域→怪物编组→怪物、商店→商品、宝箱→内容、怪物→AI 脚本、`map.csv`→子地图、脚本→文本键 |

### 2.1 构建信息（`meta` 区域）

- `meta(key PRIMARY KEY, value)`：仅存放确定性元信息：`game_id`、`schema_version`、`tool_version`（以及导出清单中的 `steam_build_id`、`unity_version`）。**不存构建时间、不存本机绝对路径**。
- `source(stage, path, sha256, file_count, PRIMARY KEY(stage, path))`：`path` 为相对导出根目录的逻辑路径；`sha256` 为该源目录下按相对路径排序的 `(rel_path, file_sha256)` 汇总哈希。
- `stage_report(stage PRIMARY KEY, status, rows, note)`：不含耗时（耗时记录在 `<db>.manifest.json`）。
- `build_issue(id PRIMARY KEY, stage, kind, subject, detail)`：所有构建期解析错误、重复主键、文本键冲突及悬空引用排序后以连续 `id` 写入。

### 2.2 主数据列别名归一化与名字解析

按照 `docs/data-survey.md` 第三节第 2 点的跨代差异，导入类型层时对以下 4 张表做列名/列位归一化，原始列名与值完整保留在 `doc` 列中：

| CSV 表 | 跨代差异 | 规范列名 |
| --- | --- | --- |
| `item.csv` | FF1 第 19 列拼写缺失首字母 `attle_effect_asset_id` | `battle_effect_asset_id` |
| `monster.csv` | FF5/FF6 第 17 列 `attack_plus_grop`；FF1/FF3 第 29 列 `ability_defense_reat` | `attack_plus_group`、`ability_defense_rate` |
| `battle_effect_asset.csv` | FF4 第 5 列 `se_asset_id_1` | `se_asset_id_2` |
| `ability.csv` | FF3 第 30–32 列为 `ability_random_group_id`、`buy`、`sell`（无 `sales_not_possible`） | 按表头名对齐 `ability_random_group_id`、`buy`、`sell`、`sales_not_possible` |

名称解析规则：
- `text.value_plain` 移除形如 `<IC_…>`、`<P>`、`<CT>`、`<WAIT…>`、`<PRESS>`、`<josa(…)>` 等以 ASCII 字母开头的尖括号标签（正则 `<[A-Za-z][^>]*>`）。
- 存在同名跨 scope 键时，按 `system` > `etc_text` > `story_mes` > `story_cha` 优先级解析 `name_zhc`、`name_ja`、`name_en`（均取 `value_plain`）。
- `item` / `weapon` / `armor` / `ability` 通过 `content(type_id, type_value)`（`type_id` 分别对应 `1` / `2` / `3` / `4`）关联 `content_id` 与 `mes_id_name`，再解析出三语名称。
- `map` 优先使用 `map_title` 解析名称，若为 `None` 或空则回退至所属 `area.area_name`。
- `monster_party` 根据 `monster1..monster9` 槽位组合生成确定性摘要名称（如多只同类合并为 `×N`）。

### 2.3 地图坐标规则与格子图层

- **Tiled 格子坐标换算规则**（瓦片尺寸由子地图 `tile_width × tile_height` 决定，FF1–FF6 统一为 `16 × 16`）：
  - **带 `gid` 的对象（Tile Object，左下角锚点）**：
    - `tile_x = floor(px_x / tile_width)`
    - `tile_y = floor(px_y / tile_height) - 1`
    - 例：像素 `(480, 224)`、瓦片 `16×16` → 格子 `(30, 13)`。
  - **不带 `gid` 的对象（矩形 / 点 / 多边形 / 椭圆，左上角锚点）**：
    - `tile_x = floor(px_x / tile_width)`
    - `tile_y = floor(px_y / tile_height)`
    - 例：像素 `(480, 224)`、瓦片 `16×16` → 格子 `(30, 14)`。
- **对象来源**：合并导入 `entity_default.json`、`ev_*.json`（含世界地图 `package.json` 中以 Base64 内联的 `entity[].inline`）、以及 FF6 独有的 `UpperObject.json` 与 `GroundObject.json`。
- **宝箱**：所有 `object_type == 7` 的对象抽取至 `treasure_chest`，并通过 `rel_treasure_content` 连接到 `content`；无效或缺失 `content_id` 记入 `build_issue(kind = 'dangling_treasure_content')`。
- **紧凑格子图层（`map_grid`）**：
  - 覆盖 `collision`、`damage`、`hidden_passage`、`sub_collision`、`transportation`、`attribute`、`encount_grid`（含 `encount_grid_after`、`encount_grid_airship`）。
  - 编码格式固定为 `encoding = 'i32le'`：按行主序（`index = y * width + x`）紧密打包的 32 位小端有符号整数二进制 `BLOB`（总长度 `width * height * 4` 字节）。
  - C# 读取 API：`DbReader.QueryMapGridCell(submapName, layerKind, x, y, layerName?)` 与 `DbReader.QueryAllMapGridCellsAt(submapName, x, y)`，时间复杂度 `O(1)`。
  - 也可在只读 SQL 中直接按字节偏移切出 4 字节：`substr(data, ((y * width + x) * 4) + 1, 4)`。
- **其他可选图层**：`route_instruction*.json` 完整存入 `map_layer(layer_kind = 'route_instruction', doc = …)`。
- **`map.csv` 与子地图关联**：通过 `map.map_name` 与 `map_submap.name` 匹配，写入 `map.submap_id` 及 `rel_map_submap`；未匹配到的记入 `build_issue(kind = 'dangling_map_submap')`。

### 2.4 脚本与怪物 AI

- 扫描目录：`Res/Map/**/sc_*.json`、`Res/Map/Map_Script/Resident/*.json`、`Res/Map/*/package.json` 中的内联 Base64 脚本、`Res/Battle/BattleScript/*.json`、`Common/Res/Battle/CommonBattleScript/*.json`、`Res/Battle/MonsterAI/*.json`、`Common/Data/Sandbox/*.json`。
- 兼容两代怪物 AI 格式：`format = 'EntryPoint'`（FF1–FF4，`Title` 支持字符串或 `{"main": "…"}` 对象）与 `format = 'Segments'`（FF5–FF6 及全部地图/战斗/沙盒脚本）。
- 怪物与 AI 关联链：`monster.script_id` → `monster_ai_script.csv(id)` → `script_name` → `script(kind = 'monster_ai')`（匹配文件名主干时自动兼容原版资源文件名末尾多余空格），结果写入 `rel_monster_ai`。
- 指令级索引：`script_instruction(script_id, ord, label, mnemonic, operands, type, comment)` 在 `mnemonic` 上建索引；对显式消息指令（`Msg`、`MsgFunfare`、`ShowMapTitle`、`SpeackerSideRight`）及其他指令中命中文本键或以 `MSG_` 开头的字符串操作数建立 `rel_script_text`，缺失键记入 `build_issue(kind = 'dangling_script_text')`。

### 2.5 Unity 对象与输入映射

- `unity_object` 主键为 `(source, assets_file, path_id)`（同一 bundle 内不同 `assetsFile` 可能复用相同 `path_id`），在 `class`（完整命名空间+类名）与 `name` 上建索引；同时覆盖 `_monobehaviour/{data,bundles}/*.json` 与 FF6 独立 `ScriptableObject`（`Res/Opera/**/*.playable.json`）。
- 识别 `UnityEngine.InputSystem.InputActionAsset`，将其 `m_ActionMaps`、`m_Actions`、`m_Bindings` 分别展开至 `input_action_map`、`input_action`、`input_binding`。

## 3. 可复现构建与内容哈希

### 3.1 确定性构建步骤

1. 在目标目录创建同目录临时文件 `.<filename>.tmp.<pid>.<nonce>`，固定 SQLite 头部与构建期参数：
   - `PRAGMA page_size = 4096;`
   - `PRAGMA auto_vacuum = NONE;`
   - `PRAGMA encoding = 'UTF-8';`
   - `PRAGMA application_id = 1179013202;`（ASCII `"FFPR"`，`0x46465052`）
   - `PRAGMA user_version = 1;`
   - `PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA temp_store = MEMORY;`
2. 执行建表脚本，再按固定阶段顺序（`text` → `master_raw` → `master_typed` → `maps` → `scripts` → `unity` → `config` → `relations`）每阶段单事务导入；多线程（`--jobs N`）仅用于文件并行解析，写入数据库前统一按自然主键排序。
3. 写入排序后的 `build_issue`、`source`、`stage_report`、`meta`。
4. 批量数据写完后再创建全部二级索引并按 `(scope, key, lang)` 顺序填充 `text_fts`。
5. 执行 `ANALYZE; VACUUM;`，关闭连接并原子替换目标文件。
6. 计算完整文件哈希与规范化内容哈希，并将构建时间、耗时、线程数及哈希写入旁边的 `<db>.manifest.json`。

### 3.2 规范化内容哈希算法（与 `ffr-db hash` 一致）

- **排除范围**：排除 `sqlite_*`、`sqlite_stat*`、所有 `CREATE VIRTUAL TABLE` 虚拟表及 `PRAGMA table_list` 报告的 `virtual` / `shadow` / `view` 表（含 FTS5 / R*Tree 影子表后缀 `_data`、`_idx`、`_content`、`_docsize`、`_config`、`_node`、`_rowid`、`_parent`、`_segments`、`_segdir`、`_stat`）。
- **表顺序**：普通表按表名字典序（Ordinal）排序。
- **单表哈希**：
  1. 读取 `PRAGMA table_info(table)` 按 `cid` 排序，先哈希 `u64(列数)`（小端）及每列长度前缀 UTF-8 的 `(列名, 列类型)`；
  2. 按显式主键列（按 `pk` 升序；无主键则按 `rowid`）扫描全部行，每个字段按规范类型标记编码：
     - `NULL`：`[0x00]`
     - `INTEGER`：`[0x01] || u64(8) LE || i64 LE`
     - `REAL`：`[0x02] || u64(8) LE || f64_bits LE`
     - `TEXT`：`[0x03] || u64(len) LE || utf8_bytes`
     - `BLOB`：`[0x04] || u64(len) LE || raw_bytes`
  3. 末尾追加 `u64(行数)`（小端），输出小写十六进制 SHA-256。
- **整库 `content_sha256`**：哈希 `u64(表数)`（小端）及每张表的长度前缀 UTF-8 `(表名, 表 sha256)`。

`ffpr-export db hash <库> --json` 固定输出单行 JSON：

```json
{"format":1,"file_sha256":"…","content_sha256":"…","schema_version":1,"sqlite_version":"3.x.y","tables":[{"name":"item","rows":36,"sha256":"…"}]}
```

## 4. 命令行用法

```bash
# 从导出目录构建单作 SQLite 数据库及 <库>.manifest.json
ffpr-export db build --export .deps/ffpr/ff5/export --out .deps/ffpr/ff5/db/ff5.sqlite --jobs 8

# 计算文件哈希与规范化内容哈希（--json 供下游自动化读取）
ffpr-export db hash .deps/ffpr/ff5/db/ff5.sqlite --json

# 查看库大小、元信息、各区域及各表行数、build_issue 汇总
ffpr-export db stats .deps/ffpr/ff5/db/ff5.sqlite

# 执行单条只读 SQL（拒绝任何写/DDL/PRAGMA 修改与多语句），按 NDJSON 输出
ffpr-export db sql .deps/ffpr/ff5/db/ff5.sqlite "SELECT id, name_zhc FROM item ORDER BY id LIMIT 5"
```
