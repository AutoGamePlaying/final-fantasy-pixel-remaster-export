using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal static class DbSchema
{
    public const int SchemaVersion = 1;
    public const int PageSize = 4096;

    // ASCII "FFPR" in big-endian (0x46465052 = 1179013202).
    public const int ApplicationId = 0x46465052;
    public const int HashFormatVersion = 1;
    public const int ManifestFormatVersion = 1;

    public const string MetaKeySchemaVersion = "schema_version";
    public const string MetaKeyGameId = "game_id";
    public const string MetaKeyToolVersion = "tool_version";
    public const string MetaKeySteamBuildId = "steam_build_id";
    public const string MetaKeyUnityVersion = "unity_version";

    /// <summary>
    /// Known cross-game master CSV column alias mappings documented in docs/data-survey.md.
    /// Key is (tableName, rawColumnName) -> canonicalColumnName.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Table, string RawColumn), string> ColumnAliases =
        new Dictionary<(string, string), string>
        {
            [("item", "attle_effect_asset_id")] = "battle_effect_asset_id",
            [("monster", "attack_plus_grop")] = "attack_plus_group",
            [("monster", "ability_defense_reat")] = "ability_defense_rate",
            [("battle_effect_asset", "se_asset_id_1")] = "se_asset_id_2",
        };

    public static string NormalizeColumnName(string tableName, string rawColumn)
    {
        return ColumnAliases.TryGetValue((tableName, rawColumn), out var canonical)
            ? canonical
            : rawColumn;
    }

    public static void ApplyBuildPragmas(SqliteConnection conn)
    {
        ExecuteNonQuery(
            conn,
            $"""
             PRAGMA page_size = {PageSize};
             PRAGMA auto_vacuum = NONE;
             PRAGMA encoding = 'UTF-8';
             PRAGMA application_id = {ApplicationId};
             PRAGMA user_version = {SchemaVersion};
             PRAGMA journal_mode = OFF;
             PRAGMA synchronous = OFF;
             PRAGMA temp_store = MEMORY;
             PRAGMA foreign_keys = OFF;
             """);
    }

    public static void ApplyTableSchemas(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, TableSchemaSql);
    }

    public static void ApplyIndexSchemas(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, IndexSchemaSql);
    }

    public static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static readonly IReadOnlyDictionary<string, string> TableAreas =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["meta"] = "meta",
            ["source"] = "meta",
            ["stage_report"] = "meta",
            ["build_issue"] = "meta",

            ["text"] = "text",

            ["master_table"] = "master_raw",
            ["master_row"] = "master_raw",

            ["content"] = "master_typed",
            ["item"] = "master_typed",
            ["weapon"] = "master_typed",
            ["armor"] = "master_typed",
            ["ability"] = "master_typed",
            ["monster"] = "master_typed",
            ["monster_party"] = "master_typed",
            ["monster_set"] = "master_typed",
            ["job"] = "master_typed",
            ["command"] = "master_typed",
            ["condition"] = "master_typed",
            ["area"] = "master_typed",
            ["map"] = "master_typed",
            ["encount_area"] = "master_typed",
            ["encount_area_grid"] = "master_typed",
            ["product_group"] = "master_typed",
            ["product"] = "master_typed",
            ["character_status"] = "master_typed",
            ["battle_effect_asset"] = "master_typed",

            ["map_package"] = "map",
            ["map_submap"] = "map",
            ["map_layer"] = "map",
            ["map_grid"] = "map",
            ["map_object"] = "map",
            ["treasure_chest"] = "map",

            ["script"] = "script",
            ["script_instruction"] = "script",

            ["unity_object"] = "unity",
            ["input_action_map"] = "unity",
            ["input_action"] = "unity",
            ["input_binding"] = "unity",

            ["config_doc"] = "config",

            ["rel_encount_area_party"] = "relation",
            ["rel_monster_set_party"] = "relation",
            ["rel_monster_party_member"] = "relation",
            ["rel_shop_product"] = "relation",
            ["rel_treasure_content"] = "relation",
            ["rel_monster_ai"] = "relation",
            ["rel_map_submap"] = "relation",
            ["rel_script_text"] = "relation",
        };

    private const string TableSchemaSql =
        """
        -- =========================================================
        -- Area 1: Build metadata (no volatile timestamps or paths)
        -- =========================================================
        CREATE TABLE meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE source (
            stage TEXT NOT NULL,
            path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            file_count INTEGER NOT NULL,
            PRIMARY KEY (stage, path)
        );

        CREATE TABLE stage_report (
            stage TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            rows INTEGER NOT NULL,
            note TEXT NOT NULL
        );

        CREATE TABLE build_issue (
            id INTEGER PRIMARY KEY,
            stage TEXT NOT NULL,
            kind TEXT NOT NULL,
            subject TEXT NOT NULL,
            detail TEXT NOT NULL
        );

        -- =========================================================
        -- Area 2: Multilingual text & FTS5 trigram index
        -- =========================================================
        CREATE TABLE text (
            scope TEXT NOT NULL,
            key TEXT NOT NULL,
            lang TEXT NOT NULL,
            value TEXT NOT NULL,
            value_plain TEXT NOT NULL,
            PRIMARY KEY (scope, key, lang)
        );

        -- =========================================================
        -- Area 3a: Generic master CSV layer (all 97 CSV tables verbatim)
        -- =========================================================
        CREATE TABLE master_table (
            name TEXT PRIMARY KEY,
            columns_json TEXT NOT NULL,
            canonical_columns_json TEXT NOT NULL,
            row_count INTEGER NOT NULL
        );

        CREATE TABLE master_row (
            table_name TEXT NOT NULL,
            row_key TEXT NOT NULL,
            row_ord INTEGER NOT NULL,
            id INTEGER,
            doc TEXT NOT NULL,
            PRIMARY KEY (table_name, row_key, row_ord)
        );

        -- =========================================================
        -- Area 3b: Typed master tables (indexed columns + resolved names + raw doc)
        -- =========================================================
        CREATE TABLE content (
            id INTEGER PRIMARY KEY,
            mes_id_name TEXT NOT NULL,
            mes_id_battle TEXT NOT NULL,
            mes_id_description TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            icon_id INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            type_value INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE item (
            id INTEGER PRIMARY KEY,
            content_id INTEGER,
            sort_id INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            system_id INTEGER NOT NULL,
            item_lv INTEGER NOT NULL,
            attribute_id INTEGER NOT NULL,
            standard_value INTEGER NOT NULL,
            battle_effect_asset_id INTEGER NOT NULL,
            buy INTEGER NOT NULL,
            sell INTEGER NOT NULL,
            sales_not_possible INTEGER NOT NULL,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE weapon (
            id INTEGER PRIMARY KEY,
            content_id INTEGER,
            sort_id INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            category_type INTEGER NOT NULL,
            attribute_id INTEGER NOT NULL,
            equip_job_group_id INTEGER NOT NULL,
            parts_group_id INTEGER NOT NULL,
            attack INTEGER NOT NULL,
            weight INTEGER NOT NULL,
            accuracy_rate INTEGER NOT NULL,
            evasion_rate INTEGER NOT NULL,
            two_handed_flag INTEGER NOT NULL,
            throw_flag INTEGER NOT NULL,
            trigger_ability_id INTEGER NOT NULL,
            buy INTEGER NOT NULL,
            sell INTEGER NOT NULL,
            sales_not_possible INTEGER NOT NULL,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE armor (
            id INTEGER PRIMARY KEY,
            content_id INTEGER,
            sort_id INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            equip_job_group_id INTEGER NOT NULL,
            parts_group_id INTEGER NOT NULL,
            defense INTEGER NOT NULL,
            ability_defense INTEGER NOT NULL,
            weight INTEGER NOT NULL,
            evasion_rate INTEGER NOT NULL,
            ability_evasion_rate INTEGER NOT NULL,
            resistance_attribute INTEGER NOT NULL,
            resistance_condition INTEGER NOT NULL,
            buy INTEGER NOT NULL,
            sell INTEGER NOT NULL,
            sales_not_possible INTEGER NOT NULL,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE ability (
            id INTEGER PRIMARY KEY,
            content_id INTEGER,
            sort_id INTEGER NOT NULL,
            ability_lv INTEGER NOT NULL,
            ability_group_id INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            attribute_id INTEGER NOT NULL,
            system_id INTEGER NOT NULL,
            use_value INTEGER NOT NULL,
            standard_value INTEGER NOT NULL,
            attack_count INTEGER NOT NULL,
            accuracy_rate INTEGER NOT NULL,
            use_job_group_id INTEGER NOT NULL,
            ability_random_group_id INTEGER NOT NULL,
            buy INTEGER NOT NULL,
            sell INTEGER NOT NULL,
            sales_not_possible INTEGER NOT NULL,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE monster (
            id INTEGER PRIMARY KEY,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            lv INTEGER NOT NULL,
            hp INTEGER NOT NULL,
            mp INTEGER NOT NULL,
            exp INTEGER NOT NULL,
            gil INTEGER NOT NULL,
            species INTEGER NOT NULL,
            boss INTEGER NOT NULL,
            attack INTEGER NOT NULL,
            defense INTEGER NOT NULL,
            ability_attack INTEGER NOT NULL,
            ability_defense INTEGER NOT NULL,
            ability_defense_rate INTEGER NOT NULL,
            attack_plus_group INTEGER NOT NULL,
            script_id INTEGER NOT NULL,
            monster_asset_id INTEGER NOT NULL,
            battle_effect_asset_id INTEGER NOT NULL,
            drop_rate INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE monster_party (
            id INTEGER PRIMARY KEY,
            battle_background_asset_id INTEGER NOT NULL,
            battle_bgm_asset_id INTEGER NOT NULL,
            script_name TEXT NOT NULL,
            not_escape INTEGER NOT NULL,
            get_value INTEGER NOT NULL,
            get_ap INTEGER NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE monster_set (
            id INTEGER PRIMARY KEY,
            doc TEXT NOT NULL
        );

        CREATE TABLE job (
            id INTEGER PRIMARY KEY,
            mes_id_name TEXT NOT NULL,
            mes_id_description TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            strength INTEGER NOT NULL,
            vitality INTEGER NOT NULL,
            agility INTEGER NOT NULL,
            magic INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE command (
            id INTEGER PRIMARY KEY,
            command_lv INTEGER NOT NULL,
            mes_id_name TEXT NOT NULL,
            mes_id_name2 TEXT NOT NULL,
            mes_id_description TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            command_type INTEGER NOT NULL,
            ability_group_id INTEGER NOT NULL,
            ability_id INTEGER NOT NULL,
            job_group_id INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE condition (
            id INTEGER PRIMARY KEY,
            condition_type INTEGER NOT NULL,
            system_type INTEGER NOT NULL,
            mes_id_name TEXT NOT NULL,
            mes_id_description TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            icon_id INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE area (
            id INTEGER PRIMARY KEY,
            map_type INTEGER NOT NULL,
            area_name_key TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE map (
            id INTEGER PRIMARY KEY,
            map_name TEXT NOT NULL,
            asset_name TEXT NOT NULL,
            map_title_key TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            area_id INTEGER NOT NULL,
            script_id INTEGER NOT NULL,
            bgm_id INTEGER NOT NULL,
            monster_set_id INTEGER NOT NULL,
            encount_area_grid_id INTEGER NOT NULL,
            loop_type INTEGER NOT NULL,
            floor INTEGER NOT NULL,
            submap_id INTEGER,
            doc TEXT NOT NULL
        );

        CREATE TABLE encount_area (
            id INTEGER PRIMARY KEY,
            encount_area_grid_number INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE encount_area_grid (
            id INTEGER PRIMARY KEY,
            grid_width INTEGER NOT NULL,
            grid_height INTEGER NOT NULL,
            base_number INTEGER NOT NULL,
            x_start_point INTEGER NOT NULL,
            y_start_point INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE product_group (
            id INTEGER PRIMARY KEY,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE product (
            id INTEGER PRIMARY KEY,
            group_id INTEGER NOT NULL,
            content_id INTEGER NOT NULL,
            coefficient INTEGER NOT NULL,
            purchase_limit INTEGER NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE character_status (
            id INTEGER PRIMARY KEY,
            mes_id_name TEXT NOT NULL,
            name_zhc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            gender INTEGER NOT NULL,
            lv INTEGER NOT NULL,
            exp INTEGER NOT NULL,
            job_id INTEGER NOT NULL,
            hp INTEGER NOT NULL,
            mp INTEGER NOT NULL,
            strength INTEGER NOT NULL,
            vitality INTEGER NOT NULL,
            agility INTEGER NOT NULL,
            intelligence INTEGER NOT NULL,
            spirit INTEGER NOT NULL,
            magic INTEGER NOT NULL,
            luck INTEGER NOT NULL,
            character_asset_id INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE battle_effect_asset (
            id INTEGER PRIMARY KEY,
            asset_name TEXT NOT NULL,
            position_type INTEGER NOT NULL,
            display_type INTEGER NOT NULL,
            se_asset_id INTEGER NOT NULL,
            se_asset_id_2 INTEGER NOT NULL,
            critical_se_asset_id INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        -- =========================================================
        -- Area 4: Maps, submaps, layers, compact grids, objects, chests
        -- =========================================================
        CREATE TABLE map_package (
            name TEXT PRIMARY KEY,
            asset_group_name TEXT NOT NULL,
            path TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE map_submap (
            id INTEGER PRIMARY KEY,
            package_name TEXT NOT NULL,
            name TEXT NOT NULL UNIQUE,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            tile_width INTEGER NOT NULL,
            tile_height INTEGER NOT NULL,
            layer_names_json TEXT NOT NULL,
            tilemap_path TEXT,
            doc TEXT NOT NULL
        );

        CREATE TABLE map_layer (
            id INTEGER PRIMARY KEY,
            submap_id INTEGER NOT NULL,
            source_file TEXT NOT NULL,
            layer_kind TEXT NOT NULL,
            layer_name TEXT NOT NULL,
            tiled_type TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            visible INTEGER NOT NULL,
            opacity REAL NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE map_grid (
            submap_id INTEGER NOT NULL,
            layer_kind TEXT NOT NULL,
            layer_name TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            encoding TEXT NOT NULL,
            data BLOB NOT NULL,
            PRIMARY KEY (submap_id, layer_kind, layer_name)
        );

        CREATE TABLE map_object (
            id INTEGER PRIMARY KEY,
            submap_id INTEGER NOT NULL,
            source_file TEXT NOT NULL,
            layer_name TEXT NOT NULL,
            tiled_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            tiled_type TEXT NOT NULL,
            object_type INTEGER,
            gid INTEGER,
            px_x REAL NOT NULL,
            px_y REAL NOT NULL,
            width REAL NOT NULL,
            height REAL NOT NULL,
            tile_x INTEGER NOT NULL,
            tile_y INTEGER NOT NULL,
            entity_id INTEGER,
            content_id INTEGER,
            content_num INTEGER,
            script_id INTEGER,
            flag_id INTEGER,
            message_key TEXT,
            properties_json TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE treasure_chest (
            id INTEGER PRIMARY KEY,
            map_object_id INTEGER NOT NULL UNIQUE,
            submap_id INTEGER NOT NULL,
            source_file TEXT NOT NULL,
            tiled_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            tile_x INTEGER NOT NULL,
            tile_y INTEGER NOT NULL,
            content_id INTEGER,
            content_num INTEGER NOT NULL,
            flag_id INTEGER,
            message_key TEXT,
            content_name_zhc TEXT NOT NULL,
            content_name_ja TEXT NOT NULL,
            content_name_en TEXT NOT NULL
        );

        -- =========================================================
        -- Area 5: Event, battle, monster AI, and sandbox scripts
        -- =========================================================
        CREATE TABLE script (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            title TEXT NOT NULL,
            format TEXT NOT NULL,
            submap_name TEXT,
            instruction_count INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE script_instruction (
            script_id INTEGER NOT NULL,
            ord INTEGER NOT NULL,
            label TEXT NOT NULL,
            mnemonic TEXT NOT NULL,
            operands TEXT NOT NULL,
            type INTEGER,
            comment TEXT NOT NULL,
            PRIMARY KEY (script_id, ord)
        );

        -- =========================================================
        -- Area 6: Unity MonoBehaviour objects & InputActionAsset
        -- =========================================================
        CREATE TABLE unity_object (
            source TEXT NOT NULL,
            assets_file TEXT NOT NULL,
            path_id INTEGER NOT NULL,
            class TEXT NOT NULL,
            script_assembly TEXT NOT NULL,
            script_namespace TEXT NOT NULL,
            script_class TEXT NOT NULL,
            name TEXT NOT NULL,
            game_object_name TEXT,
            type_source TEXT NOT NULL,
            doc TEXT NOT NULL,
            PRIMARY KEY (source, assets_file, path_id)
        );

        CREATE TABLE input_action_map (
            id TEXT PRIMARY KEY,
            source TEXT NOT NULL,
            asset_name TEXT NOT NULL,
            name TEXT NOT NULL,
            ord INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE input_action (
            id TEXT PRIMARY KEY,
            map_id TEXT NOT NULL,
            name TEXT NOT NULL,
            action_type INTEGER NOT NULL,
            expected_control_type TEXT NOT NULL,
            ord INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        CREATE TABLE input_binding (
            id TEXT PRIMARY KEY,
            map_id TEXT NOT NULL,
            action_name TEXT NOT NULL,
            name TEXT NOT NULL,
            path TEXT NOT NULL,
            interactions TEXT NOT NULL,
            processors TEXT NOT NULL,
            groups TEXT NOT NULL,
            flags INTEGER NOT NULL,
            ord INTEGER NOT NULL,
            doc TEXT NOT NULL
        );

        -- =========================================================
        -- Area 7: Configuration & auxiliary structured documents
        -- =========================================================
        CREATE TABLE config_doc (
            path TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            doc TEXT NOT NULL
        );

        -- =========================================================
        -- Area 8: Cross-entity relationship tables
        -- =========================================================
        CREATE TABLE rel_encount_area_party (
            encount_area_id INTEGER NOT NULL,
            foot_slot INTEGER NOT NULL,
            monster_set_id INTEGER NOT NULL,
            set_slot INTEGER NOT NULL,
            monster_party_id INTEGER NOT NULL,
            rate INTEGER NOT NULL,
            PRIMARY KEY (encount_area_id, foot_slot, set_slot)
        );

        CREATE TABLE rel_monster_set_party (
            monster_set_id INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            monster_party_id INTEGER NOT NULL,
            rate INTEGER NOT NULL,
            PRIMARY KEY (monster_set_id, slot)
        );

        CREATE TABLE rel_monster_party_member (
            monster_party_id INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            monster_id INTEGER NOT NULL,
            pos_x INTEGER NOT NULL,
            pos_y INTEGER NOT NULL,
            group_id INTEGER NOT NULL,
            PRIMARY KEY (monster_party_id, slot)
        );

        CREATE TABLE rel_shop_product (
            shop_id INTEGER NOT NULL,
            product_id INTEGER NOT NULL,
            content_id INTEGER NOT NULL,
            coefficient INTEGER NOT NULL,
            purchase_limit INTEGER NOT NULL,
            PRIMARY KEY (shop_id, product_id)
        );

        CREATE TABLE rel_treasure_content (
            treasure_chest_id INTEGER PRIMARY KEY,
            submap_id INTEGER NOT NULL,
            content_id INTEGER NOT NULL,
            content_num INTEGER NOT NULL
        );

        CREATE TABLE rel_monster_ai (
            monster_id INTEGER PRIMARY KEY,
            ai_script_master_id INTEGER NOT NULL,
            script_name TEXT NOT NULL,
            script_id INTEGER NOT NULL
        );

        CREATE TABLE rel_map_submap (
            map_id INTEGER PRIMARY KEY,
            submap_id INTEGER NOT NULL,
            map_name TEXT NOT NULL,
            asset_name TEXT NOT NULL
        );

        CREATE TABLE rel_script_text (
            script_id INTEGER NOT NULL,
            instruction_ord INTEGER NOT NULL,
            mnemonic TEXT NOT NULL,
            operand_index INTEGER NOT NULL,
            text_key TEXT NOT NULL,
            scope TEXT NOT NULL,
            PRIMARY KEY (script_id, instruction_ord, operand_index)
        );
        """;

    private const string IndexSchemaSql =
        """
        -- Build issues
        CREATE INDEX idx_build_issue_stage_kind ON build_issue(stage, kind);
        CREATE INDEX idx_build_issue_subject ON build_issue(subject);

        -- Text & FTS5 trigram index
        CREATE INDEX idx_text_key_lang ON text(key, lang);
        CREATE INDEX idx_text_lang_plain ON text(lang, value_plain);

        CREATE VIRTUAL TABLE text_fts USING fts5(
            scope UNINDEXED,
            key UNINDEXED,
            lang UNINDEXED,
            value_plain,
            tokenize='trigram'
        );
        INSERT INTO text_fts (scope, key, lang, value_plain)
        SELECT scope, key, lang, value_plain
        FROM text
        ORDER BY scope, key, lang;

        -- Master raw
        CREATE INDEX idx_master_row_table_id ON master_row(table_name, id);

        -- Typed master tables (name & lookup indexes)
        CREATE INDEX idx_content_type_value ON content(type_id, type_value);
        CREATE INDEX idx_content_mes_id_name ON content(mes_id_name);
        CREATE INDEX idx_content_name_zhc ON content(name_zhc);
        CREATE INDEX idx_content_name_ja ON content(name_ja);
        CREATE INDEX idx_content_name_en ON content(name_en);

        CREATE INDEX idx_item_content_id ON item(content_id);
        CREATE INDEX idx_item_name_zhc ON item(name_zhc);
        CREATE INDEX idx_item_name_ja ON item(name_ja);
        CREATE INDEX idx_item_name_en ON item(name_en);

        CREATE INDEX idx_weapon_content_id ON weapon(content_id);
        CREATE INDEX idx_weapon_name_zhc ON weapon(name_zhc);
        CREATE INDEX idx_weapon_name_ja ON weapon(name_ja);
        CREATE INDEX idx_weapon_name_en ON weapon(name_en);

        CREATE INDEX idx_armor_content_id ON armor(content_id);
        CREATE INDEX idx_armor_name_zhc ON armor(name_zhc);
        CREATE INDEX idx_armor_name_ja ON armor(name_ja);
        CREATE INDEX idx_armor_name_en ON armor(name_en);

        CREATE INDEX idx_ability_content_id ON ability(content_id);
        CREATE INDEX idx_ability_group_id ON ability(ability_group_id);
        CREATE INDEX idx_ability_name_zhc ON ability(name_zhc);
        CREATE INDEX idx_ability_name_ja ON ability(name_ja);
        CREATE INDEX idx_ability_name_en ON ability(name_en);

        CREATE INDEX idx_monster_name_zhc ON monster(name_zhc);
        CREATE INDEX idx_monster_name_ja ON monster(name_ja);
        CREATE INDEX idx_monster_name_en ON monster(name_en);
        CREATE INDEX idx_monster_script_id ON monster(script_id);

        CREATE INDEX idx_monster_party_name_zhc ON monster_party(name_zhc);
        CREATE INDEX idx_monster_party_name_ja ON monster_party(name_ja);
        CREATE INDEX idx_monster_party_name_en ON monster_party(name_en);
        CREATE INDEX idx_monster_party_script_name ON monster_party(script_name);

        CREATE INDEX idx_job_name_zhc ON job(name_zhc);
        CREATE INDEX idx_job_name_ja ON job(name_ja);
        CREATE INDEX idx_job_name_en ON job(name_en);

        CREATE INDEX idx_command_name_zhc ON command(name_zhc);
        CREATE INDEX idx_command_name_ja ON command(name_ja);
        CREATE INDEX idx_command_name_en ON command(name_en);

        CREATE INDEX idx_condition_name_zhc ON condition(name_zhc);
        CREATE INDEX idx_condition_name_ja ON condition(name_ja);
        CREATE INDEX idx_condition_name_en ON condition(name_en);

        CREATE INDEX idx_area_name_zhc ON area(name_zhc);
        CREATE INDEX idx_area_name_ja ON area(name_ja);
        CREATE INDEX idx_area_name_en ON area(name_en);

        CREATE INDEX idx_map_map_name ON map(map_name);
        CREATE INDEX idx_map_asset_name ON map(asset_name);
        CREATE INDEX idx_map_area_id ON map(area_id);
        CREATE INDEX idx_map_submap_id ON map(submap_id);
        CREATE INDEX idx_map_name_zhc ON map(name_zhc);
        CREATE INDEX idx_map_name_ja ON map(name_ja);
        CREATE INDEX idx_map_name_en ON map(name_en);

        CREATE INDEX idx_encount_area_grid_number ON encount_area(encount_area_grid_number);
        CREATE INDEX idx_encount_area_grid_base ON encount_area_grid(base_number);

        CREATE INDEX idx_product_group_name_zhc ON product_group(name_zhc);
        CREATE INDEX idx_product_group_name_ja ON product_group(name_ja);
        CREATE INDEX idx_product_group_name_en ON product_group(name_en);

        CREATE INDEX idx_product_group_id ON product(group_id);
        CREATE INDEX idx_product_content_id ON product(content_id);
        CREATE INDEX idx_product_name_zhc ON product(name_zhc);
        CREATE INDEX idx_product_name_ja ON product(name_ja);
        CREATE INDEX idx_product_name_en ON product(name_en);

        CREATE INDEX idx_character_status_name_zhc ON character_status(name_zhc);
        CREATE INDEX idx_character_status_name_ja ON character_status(name_ja);
        CREATE INDEX idx_character_status_name_en ON character_status(name_en);
        CREATE INDEX idx_character_status_job_id ON character_status(job_id);

        CREATE INDEX idx_battle_effect_asset_name ON battle_effect_asset(asset_name);

        -- Maps
        CREATE INDEX idx_map_submap_package ON map_submap(package_name);
        CREATE INDEX idx_map_layer_submap ON map_layer(submap_id, layer_kind);
        CREATE INDEX idx_map_layer_name ON map_layer(layer_name);
        CREATE INDEX idx_map_grid_kind ON map_grid(layer_kind);
        CREATE INDEX idx_map_object_submap ON map_object(submap_id);
        CREATE INDEX idx_map_object_type ON map_object(object_type);
        CREATE INDEX idx_map_object_tile ON map_object(submap_id, tile_x, tile_y);
        CREATE INDEX idx_map_object_content ON map_object(content_id);
        CREATE INDEX idx_map_object_name ON map_object(name);
        CREATE INDEX idx_treasure_chest_submap ON treasure_chest(submap_id);
        CREATE INDEX idx_treasure_chest_content ON treasure_chest(content_id);

        -- Scripts
        CREATE INDEX idx_script_kind ON script(kind);
        CREATE INDEX idx_script_name ON script(name);
        CREATE INDEX idx_script_submap ON script(submap_name);
        CREATE INDEX idx_script_instruction_mnemonic ON script_instruction(mnemonic);

        -- Unity & Input
        CREATE INDEX idx_unity_object_class ON unity_object(class);
        CREATE INDEX idx_unity_object_name ON unity_object(name);
        CREATE INDEX idx_input_action_map_name ON input_action_map(name);
        CREATE INDEX idx_input_action_map ON input_action(map_id);
        CREATE INDEX idx_input_action_name ON input_action(name);
        CREATE INDEX idx_input_binding_map ON input_binding(map_id);
        CREATE INDEX idx_input_binding_action ON input_binding(action_name);
        CREATE INDEX idx_input_binding_path ON input_binding(path);

        -- Config
        CREATE INDEX idx_config_doc_kind ON config_doc(kind);

        -- Relations (both directions indexed)
        CREATE INDEX idx_rel_encount_party ON rel_encount_area_party(monster_party_id);
        CREATE INDEX idx_rel_encount_set ON rel_encount_area_party(monster_set_id);
        CREATE INDEX idx_rel_monster_set_party ON rel_monster_set_party(monster_party_id);
        CREATE INDEX idx_rel_party_member_monster ON rel_monster_party_member(monster_id);
        CREATE INDEX idx_rel_shop_product_content ON rel_shop_product(content_id);
        CREATE INDEX idx_rel_shop_product_product ON rel_shop_product(product_id);
        CREATE INDEX idx_rel_treasure_submap ON rel_treasure_content(submap_id);
        CREATE INDEX idx_rel_treasure_content_id ON rel_treasure_content(content_id);
        CREATE INDEX idx_rel_monster_ai_script ON rel_monster_ai(script_id);
        CREATE INDEX idx_rel_map_submap_submap ON rel_map_submap(submap_id);
        CREATE INDEX idx_rel_script_text_key ON rel_script_text(text_key);
        CREATE INDEX idx_rel_script_text_script ON rel_script_text(script_id);
        """;
}
