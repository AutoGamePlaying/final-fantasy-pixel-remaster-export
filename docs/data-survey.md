# FINAL FANTASY I–VI Pixel Remaster 数据结构盘点与跨代对比

本文基于 `ffpr-export` 对《FINAL FANTASY I–VI Pixel Remaster》（Steam Windows 版，Unity `2019.4.26f1` / IL2CPP）全 6 作执行 `inventory` 与 `export` 后的实测结果，汇总各作资源分布、导出分类决策、主数据表、多语言文本、地图、事件与战斗脚本以及 `MonoBehaviour` 类结构，作为后续 SQLite 数据库 Schema 设计的直接依据。

> 本文仅记录目录结构、Schema、类名与统计数据，不包含任何游戏正文文本或游戏资产内容。

## 一、六作总体规模与导出统计

六作均运行在 Unity `2019.4.26f1`（Addressables `1.16.10`），Addressables 资源包位于 `*_Data/StreamingAssets/aa/StandaloneWindows64/*.bundle`（无加密，100% 携带完整 TypeTree）；`*_Data` 下的 `resources.assets`、`sharedassets0.assets`、`level0` 剥离了 TypeTree，需结合内置 Unity 2019.4 基础类定义与 `AssetsTools.NET.Cpp2IL`（读取 `GameAssembly.dll` + `global-metadata.dat`）反序列化；`StreamingAssets/font_*.bundle`（每作 12 个）为独立加密字体包，按规则跳过。

| 游戏 | Steam AppID | BuildID | Bundle 数（磁盘 MiB） | 盘点总资产 | 导出 TextAsset | 导出 MonoBehaviour | 写出文件数 | 导出总大小（MiB） | 盘点 / 导出耗时 | 失败数 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| FF1 | 1173770 | 20249397 | 821（261.3） | 215,986 | 1,466（17.8 MiB） | 76,670（78.9 MiB） | 1,693 | 96.7 | 2.51s / 3.23s | 0 |
| FF2 | 1173780 | 20251646 | 898（263.0） | 225,279 | 2,285（29.9 MiB） | 78,290（80.8 MiB） | 2,514 | 110.7 | 2.73s / 2.89s | 0 |
| FF3 | 1173790 | 20264899 | 1,190（396.0） | 226,063 | 2,901（31.2 MiB） | 77,338（80.4 MiB） | 3,148 | 111.5 | 2.68s / 3.75s | 0 |
| FF4 | 1173800 | 20265023 | 1,458（396.7） | 235,988 | 3,198（32.7 MiB） | 76,499（82.4 MiB） | 3,550 | 115.0 | 3.08s / 3.93s | 0 |
| FF5 | 1173810 | 20265087 | 2,448（615.9） | 277,393 | 6,661（96.6 MiB） | 79,867（88.8 MiB） | 7,128 | 185.5 | 3.47s / 4.25s | 0 |
| FF6 | 1173820 | 20265119 | 2,382（1,004.9） | 313,674 | 7,352（89.3 MiB） | 88,610（161.9 MiB） | 8,183 | 251.2 | 3.87s / 4.83s | 0 |

## 二、资源分类与导出决策

全六作共盘点 1,494,383 个 Unity 资产，全部落入明确分类（未归类 `skip_other` 为 **0**）：

| 决策（`decision`） | 类别（`category`） | 覆盖 Unity 类型 / 条件 | 决策说明 |
| --- | --- | --- | --- |
| `export_text` | `text_asset` | 非音频 `TextAsset`（`.csv`、`.txt`、`.json`、`.config`） | 按容器路径原样写出原始字节（`Assets/GameAssets/Serial/` 映射至根目录，与旧版 UnityPy 导出布局一致） |
| `export_json` | `monobehaviour` | 全部 `MonoBehaviour` / `ScriptableObject` | 独立容器资产（如 FF6 歌剧院 `.playable`）按容器路径写单文件 JSON；Prefab/Scene 及 `*_Data` 组件按 bundle 或数据文件归组写 JSON |
| `skip_audio` | `audio` | `Res/Audio/*.mab.bytes`、`*.sab.bytes`（Square Enix 音频封装为 `TextAsset`）及 `AudioListener`/`AudioSource` | 全 6 作所有 `.bytes` 扩展名 `TextAsset` 均为 `mabf`/`sabf` 音频流（0 个非音频 `.bytes`），统一跳过 |
| `skip_image` | `image_rendering` | `Texture2D`、`Sprite`、`SpriteAtlas`、`RenderTexture`、`Mesh`、`CanvasRenderer`、`ParticleSystem*`、`SpriteRenderer`、`MeshRenderer`、`SkinnedMeshRenderer`、`TrailRenderer`、`Light`、`Cloth`、`BillboardRenderer` 等 | 图像、精灵、网格与视觉渲染组件 |
| `skip_shader` | `shader_material` | `Shader`、`Material` | 着色器与材质 |
| `skip_font` | `font` | `Font`、`TextMesh` 及 `StreamingAssets/font_*.bundle` | 字体资产与加密字体包 |
| `skip_video` | `video` | `VideoClip`、`VideoPlayer` | 视频资产（当前 6 作数量为 0） |
| `skip_animation` | `animation` | `Animation`、`AnimationClip`、`AnimatorController`、`AnimatorOverrideController`、`Animator` | 动画片段与动画状态机（角色动画结构元数据已在 `Res/Chara/**/anim_info.json` 导出） |
| `skip_scene_object` | `unity_runtime` | `GameObject`、`Transform`、`RectTransform`、`Canvas*`、`Camera`、`*Collider*`、`Rigidbody2D`、`AssetBundle`、`MonoScript`、`PreloadData`、`PlayableDirector`、`SortingGroup` 等 | Unity 场景/预制体/资源包骨架对象；`GameObject` 名称与 `MonoScript` 类名已解析并内嵌进 `MonoBehaviour` JSON 与 `inventory.jsonl` |

## 三、主数据表（`Data/Master/*.csv`）

### 1. 公共表与独有表

六作共存在 **97** 张 CSV 主数据表，其中 **91 张为全六作共有**（FF1–FF4 恰好均为这 91 张；FF5 为 92 张；FF6 为 96 张）：

- **全六作共有（91 张）**：
  `ability`, `ability_group`, `ability_random_group`, `achievement`, `action`, `area`, `armor`, `attribute_group`, `battle_background_asset`, `battle_effect_asset`, `battle_equipment_asset`, `battle_flag_group`, `bgm_asset`, `chanting`, `character_asset`, `character_asset_posture`, `character_default_name`, `character_status`, `command`, `command_group`, `condition`, `condition_group`, `content`, `content_flag_group`, `content_type`, `encount_area`, `encount_area_grid`, `encount_effect`, `equipment_asset_posture`, `erase_condition_group`, `exp_table`, `field_character_asset`, `foot_information`, `function`, `function_group`, `gallery_list`, `game_constant_int`, `gps`, `gps_point`, `gps_point_group`, `growth_curve`, `habitat_asset`, `habitat_point`, `habitat_point_group`, `icon`, `initialize_data`, `initialize_name`, `intermediate_growth_curve`, `item`, `job`, `job_group`, `landing_group`, `lang`, `learning`, `map`, `map_script`, `mapobject`, `message_conclusion`, `monster`, `monster_ai_script`, `monster_asset`, `monster_flag_group`, `monster_party`, `monster_set`, `name_sort`, `parameter`, `parameter_correction`, `parameter_group`, `parameter_set`, `parts_group`, `picture_book`, `picture_book_group`, `player_lines`, `product`, `product_group`, `resident_mapobject`, `script`, `se_asset`, `shop_sign`, `sound_npc_list`, `sound_pc_list`, `sound_player_list`, `speaker_asset`, `species_group`, `sub_command`, `tile_damage`, `transportation`, `transportation_group`, `treasure_box`, `weapon`, `weapon_group`。
- **单作独有（6 张）**：
  - **仅 FF5（1 张）**：`wepon_group.csv`（21 行，原版拼写少 `a`，与 `weapon_group.csv` 并存）。
  - **仅 FF6（5 张）**：`attribute.csv`（8 行）、`battle_bgm_asset.csv`（0 行，空表头）、`battle_weapon_asset.csv`（2 行）、`name.csv`（532 行）、`npc.csv`（3 行）。

### 2. 共有表的列定义差异（建库必读）

91 张共有表中，有 **87 张在六作间列名与列序完全一致**，其余 **4 张存在跨代列名拼写或列位差异**，建库导入器需做别名归一化：

| 表名 | 列数 | 差异详情 |
| --- | ---: | --- |
| `ability.csv` | 38 | 第 30–32 列：FF1/2/4/5/6 为 `buy`, `sell`, `sales_not_possible`；**FF3** 为 `ability_random_group_id`, `buy`, `sell`（整体右移一列且无 `sales_not_possible`） |
| `battle_effect_asset.csv` | 7 | 第 5 列：FF1/2/3/5/6 为 `se_asset_id_2`；**FF4** 为 `se_asset_id_1` |
| `item.csv` | 26 | 第 19 列：FF2–FF6 为 `battle_effect_asset_id`；**FF1** 拼写缺失首字母为 `attle_effect_asset_id` |
| `monster.csv` | 68 | 第 17 列：FF1–FF4 为 `attack_plus_group`，**FF5/FF6** 拼写为 `attack_plus_grop`；第 29 列：FF2/4/5/6 为 `ability_defense_rate`，**FF1/FF3** 拼写为 `ability_defense_reat` |

### 3. 核心主表行数规模对照

| 表名 | FF1 | FF2 | FF3 | FF4 | FF5 | FF6 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `monster.csv` | 129 | 142 | 238 | 264 | 407 | 509 |
| `monster_party.csv` | 830 | 1,183 | 568 | 476 | 658 | 1,071 |
| `ability.csv` | 96 | 808 | 209 | 446 | 1,004 | 1,631 |
| `item.csv` | 36 | 85 | 104 | 75 | 47 | 50 |
| `weapon.csv` | 41 | 54 | 85 | 93 | 108 | 91 |
| `armor.csv` | 44 | 53 | 68 | 83 | 83 | 132 |
| `job.csv` | 12 | 9 | 22 | 15 | 22 | 22 |
| `character_status.csv` | 6 | 10 | 4 | 14 | 6 | 34 |
| `command.csv` | 6 | 7 | 28 | 35 | 238 | 696 |
| `condition.csv` | 32 | 158 | 37 | 88 | 108 | 102 |
| `map.csv` | 130 | 347 | 361 | 353 | 643 | 649 |
| `area.csv` | 29 | 33 | 74 | 60 | 86 | 77 |
| `encount_area.csv` | 128 | 128 | 77 | 81 | 287 | 188 |
| `product.csv` | 201 | 150 | 394 | 248 | 341 | 633 |
| `growth_curve.csv` | 1,176 | 0 | 2,156 | 1,105 | 99 | 99 |

> 注：FF2 采用熟练度成长机制，因此 `growth_curve.csv` 仅保留表头（0 行）。

## 四、多语言文本（`Data/Message/*_<lang>.txt`）

六作消息目录结构 **100% 一致**：

- **文件构成**：每作固定 **48** 个 UTF-8 制表符分隔（TSV，`key\ttext`，2 列）文件 = **4 组前缀 × 12 种语言**。
- **4 组前缀**：`etc_text`、`story_cha`、`story_mes`、`system`。
- **12 种语言**：`de`、`en`、`es`、`fr`、`it`、`ja`、`ko`、`pt`、`ru`、`th`、`zhc`（简体中文）、`zht`（繁体中文）。
- **单语种条目总数（以 `en` 计）**：FF1 = 2,118；FF2 = 3,140；FF3 = 4,244；FF4 = 7,234；FF5 = 10,103；FF6 = 11,296（其中 `story_cha` 与 `story_mes` 行数严格相等）。

## 五、地图结构（`Res/Map/`）

六作地图均采用 **Tiled JSON v1.2**（`orthogonal` 正交网格，瓦片尺寸统一 `16×16`，导出工具版本 `tiledversion` 介于 `1.2.4`–`1.3.5`）。每个地图包目录包含 `package.json`，每个子地图目录均包含核心三件套：`tilemap.json`、`collision.json`、`entity_default.json`，以及可选事件布局 `ev_*.json`（Tiled 图层对象 JSON）与事件脚本 `sc_*.json`。同时每作均包含常驻地图脚本目录 `Res/Map/Map_Script/Resident/`。

| 项目 | FF1 | FF2 | FF3 | FF4 | FF5 | FF6 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 地图包目录数 | 47 | 48 | 100 | 90 | 188 | 209 |
| 子地图数（`tilemap.json`） | 128 | 347 | 354 | 358 | 641 | 634 |
| 事件布局（`ev_*.json`） | 111 | 269 | 214 | 363 | 673 | 1,006 |
| 地图脚本（`sc_*.json`） | 556 | 554 | 839 | 904 | 2,630 | 2,846 |
| `damage.json` | 7 | 8 | 7 | 7 | 93 | 34 |
| `hidden_passage.json` | 0 | 0 | 59 | 48 | 40 | 13 |
| `route_instruction*.json` | 0 | 0 | 0 | 0 | 4 | 168 |
| 特色图层文件 | — | `encount_grid_after.json` (1) | `encount_grid_airship.json` (3) | `sub_collision.json` (2) | — | `UpperObject.json` (2)、`GroundObject.json` (2) |

## 六、事件脚本、战斗脚本、怪物 AI 与其他配置

### 1. 事件脚本与战斗脚本

- **地图事件脚本（`Res/Map/**/sc_*.json`）** 与 **战斗脚本（`Res/Battle/BattleScript/sc_b_*.json`）** 在全六作采用统一的指令集 JSON 结构：
  - 顶层键：`Name`、`Title`、`Segments`、`Mnemonics`、`ScriptLocal`、`ScriptLocalValue`（FF1 地图脚本部分不含）、`SystemFlag`、`Animations`。
  - `Mnemonics` 每条指令包含：`label`、`mnemonic`、`operands`、`type`、`comment`。
- 战斗脚本数量：FF1 = 1，FF2 = 3，FF3 = 2，FF4 = 13，FF5 = 42，FF6 = 52（另每作共有 `Common/Res/Battle/CommonBattleScript/sc_b_none.json`）。

### 2. 怪物 AI（`Res/Battle/MonsterAI/*.json`）的跨代差异

怪物 AI 存在明显的**前后两代格式分界**：

- **FF1–FF4**（49 / 64 / 227 / 257 个文件）：采用轻量 AI 脚本结构，顶层键为 `['Count', 'EntryPoint', 'Mnemonics', 'Name', 'Title']`。
- **FF5–FF6**（383 / 430 个文件）：升级为与事件脚本同源的完整 VM 结构，顶层键为 `['Mnemonics', 'Name', 'ScriptLocal', 'ScriptLocalValue', 'Segments', 'SystemFlag', 'Title']`（FF6 另增 `'Animations'`）。

### 3. 其他结构化文本与配置

| 路径 | 覆盖游戏 | 说明 |
| --- | --- | --- |
| `AssetsPath.json` | 全 6 作 | 游戏逻辑资源路径映射总表（单文件 0.8–2.5 MiB） |
| `Res/Chara/**/anim_info.json` | 全 6 作 | 角色/怪物/武器精灵帧动画元数据（120–473 个/作） |
| `Data/BattleExtraData/battle_extra_data.json` | 全 6 作 | 战斗补充规则表（FF1/3/4 仅含 `critical_effect_info`；FF2 3 项；FF5 20 项含调合/捕获/操纵限制；FF6 14 项含怪物交换道具/盾牌变形等） |
| `Data/Misc/serial_misc_assets.json` | 全 6 作 | 单作杂项玩法与表现配置（FF1 13 键 → FF5 35 键 → FF6 55 键，含地形伤害、兽原遇敌表、飞空艇自动航线等） |
| `Common/Data/Tools/ScriptEditor/common_master.json` | 全 6 作 | 脚本编辑器公共枚举/常量表 |
| `Data/Tools/ScriptEditor/serial_master.json` | 仅 FF1–FF3 | 单作脚本编辑器枚举表（FF4–FF6 打包时已移除） |
| `Common/Data/Sandbox/sc_e_*.json` | 全 6 作 | 3 个测试/沙盒脚本 |
| `Res/EndRoll/*.txt`、`Res/License/license.txt`、`Res/SoundPlayer/animation_catalog.json` | 全 6 作 | 演职员表、许可声明与音乐播放器目录 |
| `_data/sharedassets0.assets/sead.{adr,ios,win}.config` | 全 6 作 | Square Enix 音频引擎（SEAD）平台配置 |

## 七、`MonoBehaviour` / `ScriptableObject` 类分布

### 1. 解析方式（TypeTree vs IL2CPP）

- **Addressables 资源包（100% TypeTree）**：全 6 作共 9,197 个 `.bundle` 内的全部 `MonoBehaviour`（76,374–88,478 个/作）均自带完整 TypeTree，直接按 TypeTree 反序列化。
- **`*_Data` 数据文件（`level0`、`sharedassets0.assets`，TypeTree 已剥离）**：通过 `AssetsTools.NET.Cpp2IL` 读取游戏目录的 `GameAssembly.dll` + `il2cpp_data/Metadata/global-metadata.dat` 生成字段模板反序列化：
  - 每作 `sharedassets0.assets` 含 1 个 **`UnityEngine.InputSystem.InputActionAsset`**（含完整的 `m_ActionMaps` 与 `m_ControlSchemes`，对下游输入注入直接可用）。
  - 每作 `level0`（启动场景）含 124–131 个组件（54–56 个脚本类，如 `Last.Management.ResourceManager`、`System.Input.InputSystemManager`、`Last.Map.BirdView`、`SeadInitializer` 等）；其中 2 个被条件编译剔除的 32 字节空桩（`Last.Debugs.GitRevision`、`Last.Management.iOSAchievmentManager`，无自定义字段字节）及 FF5/FF6 的 1 个空脚本引用组件以 `BaseOnly` 完整保留。

### 2. 类结构统计与跨代异同

| 游戏 | 独立脚本类数 | 总实例数 | `UnityEngine.UI` | `Assembly-CSharp` | 独立容器 `ScriptableObject` |
| --- | ---: | ---: | ---: | ---: | --- |
| FF1 | 884 | 76,670 | 55,328 | 21,335 | 0 |
| FF2 | 892 | 78,290 | 56,476 | 21,807 | 0 |
| FF3 | 890 | 77,338 | 55,587 | 21,743 | 0 |
| FF4 | 874 | 76,499 | 54,613 | 21,879 | 0 |
| FF5 | 895 | 79,867 | 56,486 | 23,372 | 0 |
| FF6 | 952 | 88,610 | 59,319 | 28,964 | **32**（`Unity.Timeline.TimelineAsset`，`Res/Opera/**/*.playable`） |

- **共同点**：约 70% 为 `UnityEngine.UI.*` 界面布局组件；约 28% 为 `Assembly-CSharp` 游戏组件，主要分布在 `Last.UI.*`（约 1.9–2.1 万）、`Last.Battle.*`（特效定位/反应配置）、`Serial.FF<N>.*`（各作特化 UI/流程组件，87–349 个）、`Last.Map.*`、`Last.Entity.*`、`Last.Management.*`、`Last.Interpreter.*`。
- **FF6 独有**：唯一在 `AssetBundle.m_Container` 中包含独立 `ScriptableObject` 资产的作品——共 32 个歌剧院演出时间轴（`Res/Opera/**/*.playable.json`，`Unity.Timeline.TimelineAsset`），并附带 264 个 `Unity.Timeline.*` 轨道/剪辑 `MonoBehaviour`。

## 八、后续建库（SQLite）设计建议

1. **统一主数据导入层 + 4 处列映射**：91 张公共表可直接共享建表与加载逻辑，但需在导入阶段对 `item.csv`（FF1 `attle_effect_asset_id`）、`monster.csv`（`ability_defense_reat` / `attack_plus_grop`）、`battle_effect_asset.csv`（FF4 `se_asset_id_1`）及 `ability.csv`（FF3 列位）做显式列别名映射，并为 FF5 `wepon_group` 与 FF6 的 5 张独有表保留扩展表。
2. **多语言统一表**：6 作 `Data/Message` 格式完全一致，可建统一 `(scope, key, lang, text)` 表（4 个 `scope` × 12 语言）。
3. **地图与脚本分层建模**：地图三件套（`tilemap`/`collision`/`entity_default`）及 `ev_*`/`sc_*` 在六作间格式高度一致，适合建统一表；`hidden_passage`、`damage`、`route_instruction`、`sub_collision`、`UpperObject`/`GroundObject` 作为可选子表按存在性导入；`MonsterAI` 需兼容 FF1–FF4（`EntryPoint`）与 FF5–FF6（`Segments`）两种顶层结构。
4. **补充配置 JSON 表**：将 `battle_extra_data.json`、`serial_misc_assets.json`、`AssetsPath.json` 以及 `_monobehaviour/data/sharedassets0.assets.json`（`InputActionAsset`）直接纳入数据库元数据/配置表，供下游 AI 玩家读取战斗特殊规则与输入映射。
