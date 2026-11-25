using System.Text;

namespace FfprExport.Db;

internal static class SyntheticDbFixture
{
    public static string CreateSyntheticExport(
        string exportRoot,
        bool useFf3AbilityLayout = false,
        bool includeDanglingReferences = true,
        int extraRowsPerTypedTable = 0)
    {
        Directory.CreateDirectory(exportRoot);

        // 1. Export manifest.json
        WriteText(
            exportRoot,
            "manifest.json",
            """
            {
              "tool": "ffpr-export",
              "toolVersion": "0.1.0",
              "command": "export",
              "status": "completed",
              "game": {
                "id": "ff5",
                "title": "FINAL FANTASY V",
                "appId": 1173810,
                "dataDirName": "FINAL FANTASY V_Data"
              },
              "steamBuildId": "99999999",
              "unityVersion": "2019.4.26f1"
            }
            """);

        // 2. Multilingual messages (4 scopes x 3 languages including control/icon tags)
        var extraSysZhc = new StringBuilder();
        var extraSysJa = new StringBuilder();
        var extraSysEn = new StringBuilder();
        for (var i = 1; i <= extraRowsPerTypedTable; i++)
        {
            var id = 100 + i;
            extraSysZhc.Append($"MSG_EXTRA_{id}\t<IC_BAG>合成条目{id}\n");
            extraSysJa.Append($"MSG_EXTRA_{id}\t<IC_BAG>テスト項目{id}\n");
            extraSysEn.Append($"MSG_EXTRA_{id}\t<IC_BAG>Synth Entry {id}\n");
        }

        WriteText(
            exportRoot,
            "Data/Message/system_zhc.txt",
            string.Join(
                '\n',
                "MSG_ITEM_01\t<IC_DRAG>合成药水",
                "MSG_ITEM_02\t<IC_BAG>合成羽翼",
                "MSG_WPN_01\t<IC_SRD>合成长剑",
                "MSG_ARM_01\t<IC_ARMOR>合成皮甲",
                "MSG_ABL_01\t<IC_WMGC>合成治愈",
                "MSG_MON_01\t合成哥布林",
                "MSG_JOB_01\t合成骑士",
                "MSG_CMD_01\t合成攻击",
                "MSG_COND_01\t合成中毒",
                "MSG_AREA_01\t合成平原",
                "MSG_MAP_01\t合成城镇",
                "MSG_SHOP_01\t合成杂货店",
                "MSG_CHAR_01\t合成主角",
                "MSG_GIL\t金币") + "\n" + extraSysZhc);

        WriteText(
            exportRoot,
            "Data/Message/system_ja.txt",
            string.Join(
                '\n',
                "MSG_ITEM_01\t<IC_DRAG>テストポーション",
                "MSG_ITEM_02\t<IC_BAG>テストフェザー",
                "MSG_WPN_01\t<IC_SRD>テストソード",
                "MSG_ARM_01\t<IC_ARMOR>テストアーマー",
                "MSG_ABL_01\t<IC_WMGC>テストケアル",
                "MSG_MON_01\tテストゴブリン",
                "MSG_JOB_01\tテストナイト",
                "MSG_CMD_01\tテストたたかう",
                "MSG_COND_01\tテストどく",
                "MSG_AREA_01\tテストへいげん",
                "MSG_MAP_01\tテストのまち",
                "MSG_SHOP_01\tテストどうぐや",
                "MSG_CHAR_01\tテストしゅじんこう",
                "MSG_GIL\tギル") + "\n" + extraSysJa);

        WriteText(
            exportRoot,
            "Data/Message/system_en.txt",
            string.Join(
                '\n',
                "MSG_ITEM_01\t<IC_DRAG>Synth Potion",
                "MSG_ITEM_02\t<IC_BAG>Synth Feather",
                "MSG_WPN_01\t<IC_SRD>Synth Sword",
                "MSG_ARM_01\t<IC_ARMOR>Synth Leather",
                "MSG_ABL_01\t<IC_WMGC>Synth Cure",
                "MSG_MON_01\tSynth Goblin",
                "MSG_JOB_01\tSynth Knight",
                "MSG_CMD_01\tSynth Attack",
                "MSG_COND_01\tSynth Poison",
                "MSG_AREA_01\tSynth Plains",
                "MSG_MAP_01\tSynth Town",
                "MSG_SHOP_01\tSynth Shop",
                "MSG_CHAR_01\tSynth Hero",
                "MSG_GIL\tGil") + "\n" + extraSysEn);

        WriteText(exportRoot, "Data/Message/etc_text_zhc.txt", "ETC_01\t<CT>额外条目说明<P>\n");
        WriteText(exportRoot, "Data/Message/etc_text_ja.txt", "ETC_01\t<CT>その他テスト<P>\n");
        WriteText(exportRoot, "Data/Message/etc_text_en.txt", "ETC_01\t<CT>Extra Text Entry<P>\n");

        WriteText(exportRoot, "Data/Message/story_cha_zhc.txt", "STORY_01\t合成角色\n");
        WriteText(exportRoot, "Data/Message/story_cha_ja.txt", "STORY_01\tテストキャラ\n");
        WriteText(exportRoot, "Data/Message/story_cha_en.txt", "STORY_01\tSynth Speaker\n");

        WriteText(exportRoot, "Data/Message/story_mes_zhc.txt", "STORY_01\t<WAIT00>欢迎来到合成世界！<PRESS>\n");
        WriteText(exportRoot, "Data/Message/story_mes_ja.txt", "STORY_01\t<WAIT00>テストせかいへようこそ！<PRESS>\n");
        WriteText(exportRoot, "Data/Message/story_mes_en.txt", "STORY_01\t<WAIT00>Welcome to Synth World!<PRESS>\n");

        // 3. Master CSVs (including all 4 alias/layout variants + duplicate id + custom table)
        WriteText(
            exportRoot,
            "Data/Master/content.csv",
            """
            id,mes_id_name,mes_id_battle,mes_id_description,icon_id,type_id,type_value
            1,MSG_GIL,None,None,0,5,1
            2,MSG_ITEM_01,None,None,0,1,1
            4,MSG_ITEM_02,None,None,0,1,2
            10,MSG_WPN_01,None,None,0,2,1
            20,MSG_ARM_01,None,None,0,3,1
            30,MSG_ABL_01,None,None,0,4,1
            30,MSG_ABL_01,None,None,0,4,2
            """);

        // item.csv uses FF1 typo column "attle_effect_asset_id" to verify alias normalization.
        WriteText(
            exportRoot,
            "Data/Master/item.csv",
            """
            id,sort_id,type_id,system_id,item_lv,attribute_id,accuracy_rate,destroy_rate,standard_value,renge_id,menu_renge_id,battle_renge_id,invalid_reflection,period_id,throw_flag,preparation_flag,drink_flag,machine_flag,condition_group_id,attle_effect_asset_id,menu_se_asset_id,menu_function_group_id,battle_function_group_id,buy,sell,sales_not_possible
            1,1,1,1,0,0,100,0,50,1,1,1,0,0,0,0,0,0,0,54,10,1,1,40,20,0
            2,2,1,1,0,0,100,0,100,1,1,1,0,0,0,0,0,0,0,55,11,1,1,300,150,0
            """);

        WriteText(
            exportRoot,
            "Data/Master/weapon.csv",
            """
            id,sort_id,type_id,category_type,attribute_id,attribute_group_id,equip_job_group_id,parts_group_id,attack,weight,accuracy_rate,evasion_rate,ability_disturbed_rate,destroy_rate,magnetic_force,throw_flag,back_flag,two_handed_flag,invalid_reflection,trigger_ability_id,wear_function_group_id,wear_condition_group_id,weak_attribute,effective_species,additional_condition_group_id,strength,vitality,agility,intelligence,spirit,magic,critical_rate,max_hp_up,max_mp_up,battle_equipment_asset_id,battle_effect_asset_id,guard_icon,buy,sell,sales_not_possible,process_prog
            1,101,1,5,0,0,1,1,18,0,95,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,5,0,0,1,10,0,200,100,0,None
            """);

        WriteText(
            exportRoot,
            "Data/Master/armor.csv",
            """
            id,sort_id,type_id,equip_job_group_id,parts_group_id,defense,ability_defense,weight,evasion_rate,ability_evasion_rate,ability_disturbed_rate,destroy_rate,magnetic_force,invalid_reflection,trigger_ability_id,wear_function_group_id,wear_condition_group_id,resistance_attribute,resistance_condition,resistance_species,strength,vitality,agility,intelligence,spirit,magic,max_hp_up,max_mp_up,battle_effect_asset_id,guard_icon,se_asset_id,buy,sell,sales_not_possible,process_prog
            1,1,2,1,2,12,4,2,5,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,180,90,0,None
            """);

        if (useFf3AbilityLayout)
        {
            WriteText(
                exportRoot,
                "Data/Master/ability.csv",
                """
                id,sort_id,ability_lv,ability_group_id,type_id,attribute_id,attribute_group_id,system_id,use_value,standard_value,adding_hit_rate,valid_hit_rate,weak_hit_rate,attack_count,accuracy_rate,Impact_status,use_job_group_id,condition_group_id,renge_id,menu_renge_id,battle_renge_id,content_flag_group_id,invalid_reflection,invalid_boss,resistance_attribute,battle_effect_asset_id,menu_se_asset_id,reaction_type,menu_function_group_id,battle_function_group_id,ability_random_group_id,buy,sell,ability_wait,process_prog,data_a,data_b,data_c
                1,1,1,2,4,0,0,1,4,30,0,0,0,1,100,0,1,0,1,1,1,0,0,0,0,0,0,0,0,1,77,150,75,0,None,0,0,0
                """);
        }
        else
        {
            WriteText(
                exportRoot,
                "Data/Master/ability.csv",
                """
                id,sort_id,ability_lv,ability_group_id,type_id,attribute_id,attribute_group_id,system_id,use_value,standard_value,adding_hit_rate,valid_hit_rate,weak_hit_rate,attack_count,accuracy_rate,Impact_status,use_job_group_id,condition_group_id,renge_id,menu_renge_id,battle_renge_id,content_flag_group_id,invalid_reflection,invalid_boss,resistance_attribute,battle_effect_asset_id,menu_se_asset_id,reaction_type,menu_function_group_id,battle_function_group_id,buy,sell,sales_not_possible,ability_wait,process_prog,data_a,data_b,data_c
                1,1,1,2,4,0,0,1,4,30,0,0,0,1,100,0,1,0,1,1,1,0,0,0,0,0,0,0,0,1,150,75,1,0,None,0,0,0
                """);
        }

        // monster.csv uses both FF5/6 typo "attack_plus_grop" AND FF1/3 typo "ability_defense_reat".
        var monsterRows = includeDanglingReferences
            ? """
              1,MSG_MON_01,0,0,1,1,0,0,0,0,5,30,5,15,20,1,0,33,0,5,5,8,4,4,4,9,0,3,5,44,80,0,0,0,0,0,0,0,0,0,50,2,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,10,0,0,0,0
              2,MSG_MON_01,0,0,1,1,0,0,0,0,8,55,0,25,30,1,0,34,0,6,6,9,4,4,4,12,0,4,5,45,80,0,0,0,0,0,0,0,0,0,50,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2,2,10,0,0,0,0
              3,MSG_MON_01,0,0,1,1,0,0,0,0,9,60,0,30,35,1,0,35,0,7,7,9,4,4,4,14,0,4,5,46,80,0,0,0,0,0,0,0,0,0,50,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,99,3,10,0,0,0,0
              """
            : """
              1,MSG_MON_01,0,0,1,1,0,0,0,0,5,30,5,15,20,1,0,33,0,5,5,8,4,4,4,9,0,3,5,44,80,0,0,0,0,0,0,0,0,0,50,2,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,10,0,0,0,0
              2,MSG_MON_01,0,0,1,1,0,0,0,0,8,55,0,25,30,1,0,34,0,6,6,9,4,4,4,12,0,4,5,45,80,0,0,0,0,0,0,0,0,0,50,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2,2,10,0,0,0,0
              """;
        WriteText(
            exportRoot,
            "Data/Master/monster.csv",
            "id,mes_id_name,cursor_x_position,cursor_y_position,in_type_id,disappear_type_id,species,resistance_attribute,resistance_condition,initial_condition,lv,hp,mp,exp,gill,attack_count,attack_plus,attack_plus_grop,attack_attribute,strength,vitality,agility,intelligence,spirit,magic,attack,ability_attack,defense,ability_defense,ability_defense_reat,accuracy_rate,dodge_times,evasion_rate,magic_evasion_rate,ability_disturbed_rate,critical_rate,luck,weight,boss,monster_flag_group_id,drop_rate,drop_content_id1,drop_content_id1_value,drop_content_id2,drop_content_id2_value,drop_content_id3,drop_content_id3_value,drop_content_id4,drop_content_id4_value,drop_content_id5,drop_content_id5_value,drop_content_id6,drop_content_id6_value,drop_content_id7,drop_content_id7_value,drop_content_id8,drop_content_id8_value,steal_content_id1,steal_content_id2,steal_content_id3,steal_content_id4,script_id,monster_asset_id,battle_effect_asset_id,p_use_ability_random_group_id,command_group_type,release_ability_random_group_id,rage_ability_random_group_id\n"
            + monsterRows);

        WriteText(
            exportRoot,
            "Data/Master/monster_ai_script.csv",
            includeDanglingReferences
                ? """
                  id,script_name
                  1,sc_ai_legacy
                  2,sc_ai_modern
                  99,sc_ai_missing_file
                  """
                : """
                  id,script_name
                  1,sc_ai_legacy
                  2,sc_ai_modern
                  """);

        WriteText(
            exportRoot,
            "Data/Master/monster_party.csv",
            "id,battle_background_asset_id,battle_bgm_asset_id,appearance_production,script_name,battle_pattern1,battle_pattern2,battle_pattern3,battle_pattern4,battle_pattern5,battle_pattern6,not_escape,battle_flag_group_id,get_value,get_ap,monster1,monster1_x_position,monster1_y_position,monster1_group,monster2,monster2_x_position,monster2_y_position,monster2_group,monster3,monster3_x_position,monster3_y_position,monster3_group,monster4,monster4_x_position,monster4_y_position,monster4_group,monster5,monster5_x_position,monster5_y_position,monster5_group,monster6,monster6_x_position,monster6_y_position,monster6_group,monster7,monster7_x_position,monster7_y_position,monster7_group,monster8,monster8_x_position,monster8_y_position,monster8_group,monster9,monster9_x_position,monster9_y_position,monster9_group\n"
            + "1,1,6,1,0,1,0,0,0,0,0,0,0,0,2,1,10,20,1,1,30,40,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"
            + (includeDanglingReferences
                ? "2,1,6,1,0,1,0,0,0,0,0,0,0,0,1,999,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"
                : string.Empty));

        WriteText(
            exportRoot,
            "Data/Master/monster_set.csv",
            "id,monster_set1,monster_set1_rate,monster_set2,monster_set2_rate,monster_set3,monster_set3_rate,monster_set4,monster_set4_rate,monster_set5,monster_set5_rate,monster_set6,monster_set6_rate,monster_set7,monster_set7_rate,monster_set8,monster_set8_rate,monster_set9,monster_set9_rate,monster_set10,monster_set10_rate,monster_set11,monster_set11_rate,monster_set12,monster_set12_rate,monster_set13,monster_set13_rate,monster_set14,monster_set14_rate,monster_set15,monster_set15_rate,monster_set16,monster_set16_rate\n"
            + (includeDanglingReferences
                ? "10,1,700,888,300,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"
                : "10,1,1000,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"));

        WriteText(
            exportRoot,
            "Data/Master/encount_area.csv",
            "id,encount_area_grid_number,foot01_monster_set,foot02_monster_set,foot03_monster_set,foot04_monster_set,foot05_monster_set,foot06_monster_set,foot07_monster_set,foot08_monster_set,foot09_monster_set,foot10_monster_set,foot11_monster_set,foot12_monster_set,foot13_monster_set,foot14_monster_set,foot15_monster_set,foot16_monster_set,foot17_monster_set,foot18_monster_set,foot19_monster_set,foot20_monster_set,foot21_monster_set,foot22_monster_set,foot23_monster_set,foot24_monster_set,foot25_monster_set,foot26_monster_set,foot27_monster_set,foot28_monster_set,foot29_monster_set,foot30_monster_set,foot31_monster_set,foot32_monster_set\n"
            + (includeDanglingReferences
                ? "1,100,10,999,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"
                : "1,100,10,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n"));

        WriteText(
            exportRoot,
            "Data/Master/encount_area_grid.csv",
            """
            id,grid_width,grid_height,base_number,x_start_point,y_start_point
            1,8,8,100,0,0
            """);

        WriteText(
            exportRoot,
            "Data/Master/job.csv",
            """
            id,mes_id_name,mes_id_description,change_command_1,change_command_2,change_command_3,change_command_4,change_command_5,initial_condition,strength,vitality,agility,magic
            1,MSG_JOB_01,None,1,0,0,0,0,0,28,26,22,18
            """);

        WriteText(
            exportRoot,
            "Data/Master/command.csv",
            """
            id,command_lv,mes_id_name,mes_id_name2,mes_id_description,consume_type,command_type,execution_type,list_type,number_select,ability_group_id,ability_id,job_group_id,condition_group_id,release_type,release_value1,release_value2,command_wait,adjust_strength,adjust_vitality,adjust_agility,adjust_magic,auto_execution_ability_id
            1,0,MSG_CMD_01,MSG_CMD_01,None,0,1,1,0,0,0,1,1,0,0,0,0,0,0,0,0,0,0
            """);

        WriteText(
            exportRoot,
            "Data/Master/condition.csv",
            """
            id,condition_type,system_type,resistance_decision,overlapped_type,invalid_type,target_type,until_type,until_value,rate_type1,rate_value1,rate_type2,rate_value2,parameter_group_id,attribute_group_id,condition_group_id,species_group_id,recovery_invalid,attack_target,game_over_target,state_cover_unavailable,mes_id_name,mes_id_description,erase_condition_group_id,icon_id,battle_effect_asset_id,battle_effect_shadow_cloning,damage_number,field_se_asset_id
            1,1,4,1,0,0,0,20,0,0,0,0,0,0,0,0,0,0,0,0,0,MSG_COND_01,None,1,3,0,0,0,0
            """);

        WriteText(
            exportRoot,
            "Data/Master/area.csv",
            """
            id,map_type,area_name
            1,0,MSG_AREA_01
            """);

        WriteText(
            exportRoot,
            "Data/Master/map.csv",
            "id,map_name,asset_name,map_title,script_id,bgm_id,reverb_flag,environmental_se_id,required_steps_min,required_steps_max,subtract_steps,monster_set_id,encount_area_grid_id,encount_effect_id,area_id,loop_type,moving_availability,sleeping_availability,save_availability,floor\n"
            + "30,Map_20010,map_20010,MSG_MAP_01,30,10,0,0,0,0,0,10,1,1,1,0,1,0,0,0\n"
            + (includeDanglingReferences
                ? "99,Map_Missing,map_missing,None,0,10,0,0,0,0,0,0,0,1,1,0,1,0,0,0\n"
                : string.Empty));

        WriteText(
            exportRoot,
            "Data/Master/product_group.csv",
            """
            id,mes_id_name
            1,MSG_SHOP_01
            """);

        WriteText(
            exportRoot,
            "Data/Master/product.csv",
            "id,content_id,group_id,coefficient,purchase_limit\n"
            + "1,2,1,0,0\n"
            + "2,10,1,0,0\n"
            + (includeDanglingReferences ? "3,9999,1,0,0\n4,2,999,0,0\n" : string.Empty));

        WriteText(
            exportRoot,
            "Data/Master/character_status.csv",
            """
            id,gender,dominant_arm,lv,exp,growth_curve_group_id,job_id,mes_id_name,in_type_id,hp,mp,magical_times1,magical_times2,magical_times3,magical_times4,magical_times5,magical_times6,magical_times7,magical_times8,strength,vitality,agility,intelligence,spirit,magic,luck,attack,defense,accuracy_rate,dodge_times,evasion_rate,ability_defense,magic_evasion_rate,corps,command_id1,command_id2,command_id3,command_id4,command_id5,command_id6,content_id1,content_id2,content_id3,content_id4,content_id5,content_id6,ability_random_group_id,initial_condition_group,character_asset_id
            1,1,1,1,0,1,1,MSG_CHAR_01,2,40,10,0,0,0,0,0,0,0,0,10,9,8,7,6,5,4,0,0,0,0,0,0,0,1,1,0,0,0,0,0,10,0,0,0,0,0,0,0,1
            """);

        // battle_effect_asset.csv uses FF4 column name "se_asset_id_1" to verify alias normalization to "se_asset_id_2".
        WriteText(
            exportRoot,
            "Data/Master/battle_effect_asset.csv",
            """
            id,asset_name,position_type,display_type,se_asset_id,se_asset_id_1,critical_se_asset_id
            10,ef_synth_slash,1,0,100,200,300
            """);

        // Extra custom CSV to verify generic master_table / master_row ingestion.
        WriteText(
            exportRoot,
            "Data/Master/custom_extra.csv",
            """
            id,label,val
            1,alpha,10
            2,beta,20
            """);

        // 4. Maps (package.json with inline base64 entity + inline base64 script, tilemap, grids, objects, chests)
        var inlineEvJson =
            """{"id":99,"name":"ev_inline","type":"group","opacity":1,"visible":true,"layers":[{"id":100,"name":"InlineObjects","type":"objectgroup","opacity":1,"visible":true,"objects":[{"id":500,"name":"InlineTrigger","type":"trigger","x":64,"y":32,"width":16,"height":16,"properties":[{"name":"object_type","type":"int","value":3}]}]}]}""";
        var inlineScJson =
            """{"Name":"sc_inline_test","Title":"Inline Script","Segments":[{"Label":"Main","EntryPoint":0,"Count":1}],"Mnemonics":[{"label":"Main","mnemonic":"Msg","operands":{"sValues":["STORY_01"]},"type":1,"comment":""}]}""";
        var inlineEvB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(inlineEvJson));
        var inlineScB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(inlineScJson));

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/package.json",
            $$"""
              {
                "name": "Map_20010",
                "asset_group_name": "map_20010",
                "map": [
                  {
                    "name": "Map_20010",
                    "tilemap": "Map_20010/tilemap",
                    "collision": "Map_20010/collision",
                    "entity_default": "Map_20010/entity_default",
                    "entity": [
                      { "name": "ev_e_0001", "asset": "Map_20010/ev_e_0001" },
                      { "name": "ev_inline", "asset": "Map_20010/ev_inline", "inline": "{{inlineEvB64}}" }
                    ],
                    "script": [
                      { "name": "sc_e_0001", "asset": "Map_20010/sc_e_0001" },
                      { "name": "sc_inline_test", "asset": "Map_20010/sc_inline_test", "inline": "{{inlineScB64}}" }
                    ]
                  }
                ]
              }
              """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/tilemap.json",
            """
            {
              "width": 4,
              "height": 4,
              "tilewidth": 16,
              "tileheight": 16,
              "orientation": "orthogonal",
              "layers": [
                {
                  "id": 1,
                  "name": "TileGroup",
                  "type": "group",
                  "opacity": 1,
                  "visible": true,
                  "layers": [
                    { "id": 2, "name": "Ground", "type": "tilelayer", "width": 4, "height": 4, "opacity": 1, "visible": true, "data": [1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1] }
                  ]
                }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/collision.json",
            """
            {
              "id": 6,
              "name": "CollisionGroup",
              "type": "group",
              "opacity": 1,
              "visible": true,
              "layers": [
                { "id": 7, "name": "GroundCollision", "type": "tilelayer", "width": 4, "height": 4, "opacity": 1, "visible": true, "data": [1,0,0,1, 0,0,2,0, 0,3,0,0, 1,1,1,1] }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/damage.json",
            """
            {
              "id": 8,
              "name": "DamageGroup",
              "type": "group",
              "opacity": 1,
              "visible": true,
              "layers": [
                { "id": 9, "name": "GroundDamage", "type": "tilelayer", "width": 4, "height": 4, "opacity": 1, "visible": true, "data": [0,0,0,0, 0,5,0,0, 0,0,0,0, 0,0,0,0] }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/hidden_passage.json",
            """
            {
              "id": 10,
              "name": "HiddenPassageGroup",
              "type": "group",
              "opacity": 1,
              "visible": true,
              "layers": [
                { "id": 11, "name": "GroundLayer", "type": "tilelayer", "width": 4, "height": 4, "opacity": 1, "visible": true, "data": [0,0,0,0, 0,0,0,0, 0,0,1,0, 0,0,0,0] }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/sub_collision.json",
            """
            {
              "id": 12,
              "name": "SubCollisionGroup",
              "type": "group",
              "opacity": 1,
              "visible": true,
              "layers": [
                { "id": 13, "name": "GroundSubCollision", "type": "tilelayer", "width": 4, "height": 4, "opacity": 1, "visible": true, "data": [0,0,0,0, 0,0,0,0, 0,0,0,2, 0,0,0,0] }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/encount_grid_airship.json",
            """
            {
              "width": 4,
              "height": 4,
              "cells": [100,100,101,101, 100,102,102,101, 100,100,103,103, 100,100,100,100],
              "unique_grid_number": [100, 101, 102, 103]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/route_instruction_1.json",
            """{"instruction_list":[{"step":1,"dir":"up"}]}""");

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/UpperObject.json",
            """
            {
              "id": 25,
              "name": "UpperObject",
              "type": "objectgroup",
              "opacity": 1,
              "visible": true,
              "width": 4,
              "height": 4,
              "objects": [
                { "id": 301, "name": "UpperDoor", "type": "", "x": 16, "y": 32, "width": 16, "height": 16, "properties": [ { "name": "object_type", "type": "int", "value": 8 } ] }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/GroundObject.json",
            """
            {
              "id": 26,
              "name": "GroundObject",
              "type": "objectgroup",
              "opacity": 1,
              "visible": true,
              "width": 4,
              "height": 4,
              "objects": [
                { "id": 302, "name": "GroundSwitch", "type": "", "x": 32, "y": 48, "width": 16, "height": 16, "properties": [ { "name": "object_type", "type": "int", "value": 9 } ] }
              ]
            }
            """);

        var danglingChestJson = includeDanglingReferences
            ? """
              ,
              {
                "gid": 1023,
                "id": 24,
                "name": "BrokenChest",
                "type": "",
                "x": 32,
                "y": 48,
                "width": 16,
                "height": 16,
                "properties": [
                  { "name": "object_type", "type": "int", "value": 7 },
                  { "name": "content_id", "type": "int", "value": 99999 },
                  { "name": "content_num", "type": "int", "value": 1 }
                ]
              }
              """
            : string.Empty;

        // Object id=23 has gid=1023 at (480, 224) -> tile (30, 13); object id=25 has NO gid at (480, 224) -> tile (30, 14).
        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/entity_default.json",
            $$"""
              {
                "id": 17,
                "name": "DefaultObjectGroup",
                "type": "group",
                "opacity": 1,
                "visible": true,
                "layers": [
                  {
                    "id": 18,
                    "name": "GroundObject",
                    "type": "objectgroup",
                    "opacity": 1,
                    "visible": true,
                    "objects": [
                      {
                        "gid": 1023,
                        "id": 23,
                        "name": "SynthChest",
                        "type": "",
                        "x": 480,
                        "y": 224,
                        "width": 16,
                        "height": 16,
                        "properties": [
                          { "name": "object_type", "type": "int", "value": 7 },
                          { "name": "content_id", "type": "int", "value": 4 },
                          { "name": "content_num", "type": "int", "value": 2 },
                          { "name": "flag_id", "type": "int", "value": 10 },
                          { "name": "message_key", "type": "string", "value": "STORY_01" }
                        ]
                      },
                      {
                        "id": 25,
                        "name": "RegionWithoutGid",
                        "type": "region",
                        "x": 480,
                        "y": 224,
                        "width": 16,
                        "height": 32,
                        "properties": [
                          { "name": "object_type", "type": "int", "value": 1 },
                          { "name": "entity_id", "type": "int", "value": 2 }
                        ]
                      }{{danglingChestJson}}
                    ]
                  }
                ]
              }
              """);

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/ev_e_0001.json",
            """
            {
              "id": 47,
              "name": "ev_e_0001",
              "type": "group",
              "opacity": 1,
              "visible": true,
              "layers": [
                {
                  "id": 46,
                  "name": "EventLayer",
                  "type": "objectgroup",
                  "opacity": 1,
                  "visible": true,
                  "objects": [
                    { "gid": 999, "id": 101, "name": "EventNpc", "type": "", "x": 16, "y": 32, "width": 16, "height": 16, "properties": [ { "name": "object_type", "type": "int", "value": 2 }, { "name": "script_id", "type": "int", "value": 1 } ] }
                  ]
                }
              ]
            }
            """);

        // 5. Scripts (map event, resident, battle, legacy EntryPoint AI, modern Segments AI, sandbox)
        var missingMsgInstruction = includeDanglingReferences
            ? """
              ,
              { "label": "", "mnemonic": "Msg", "operands": { "sValues": ["MSG_NON_EXISTENT_KEY"] }, "type": 1, "comment": "" }
              """
            : string.Empty;

        WriteText(
            exportRoot,
            "Res/Map/Map_20010/Map_20010/sc_e_0001.json",
            $$"""
              {
                "Name": "sc_e_0001",
                "Title": "Map Event Script",
                "Segments": [{ "Label": "Main", "EntryPoint": 0, "Count": 3 }],
                "Mnemonics": [
                  { "label": "Main", "mnemonic": "Nop", "operands": { "iValues": [], "rValues": [], "sValues": [] }, "type": 1, "comment": "" },
                  { "label": "", "mnemonic": "Msg", "operands": { "iValues": [], "rValues": [], "sValues": ["STORY_01"] }, "type": 1, "comment": "dialog" }{{missingMsgInstruction}}
                ]
              }
              """);

        WriteText(
            exportRoot,
            "Res/Map/Map_Script/Resident/sc_resident.json",
            """
            {
              "Name": "sc_resident",
              "Title": "Resident Script",
              "Segments": [{ "Label": "Main", "EntryPoint": 0, "Count": 1 }],
              "Mnemonics": [
                { "label": "Main", "mnemonic": "ShowMapTitle", "operands": { "sValues": ["MSG_MAP_01"] }, "type": 1, "comment": "" }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Res/Battle/BattleScript/sc_b_0001.json",
            """
            {
              "Name": "sc_b_0001",
              "Title": "Battle Script",
              "Segments": [{ "Label": "Main", "EntryPoint": 0, "Count": 1 }],
              "Mnemonics": [
                { "label": "Main", "mnemonic": "Wait", "operands": { "iValues": [10] }, "type": 1, "comment": "" }
              ]
            }
            """);

        // Legacy FF1-FF4 Monster AI (EntryPoint format)
        WriteText(
            exportRoot,
            "Res/Battle/MonsterAI/sc_ai_legacy.json",
            """
            {
              "Name": "sc_ai_legacy",
              "Title": "Legacy AI",
              "EntryPoint": 0,
              "Count": 2,
              "Mnemonics": [
                { "label": "0000", "mnemonic": "Nop", "comment": "start" },
                { "label": "0001", "mnemonic": "SyncAI", "operands": { "iValues": [6] }, "comment": "wait" }
              ]
            }
            """);

        // Modern FF5-FF6 Monster AI (Segments format)
        WriteText(
            exportRoot,
            "Res/Battle/MonsterAI/sc_ai_modern.json",
            """
            {
              "Name": "sc_ai_modern",
              "Title": "Modern AI",
              "Segments": [{ "Label": "Main", "EntryPoint": 0, "Count": 2 }],
              "Mnemonics": [
                { "label": "Main", "mnemonic": "Nop", "comment": "start" },
                { "label": "", "mnemonic": "StartTable", "operands": { "iValues": [0] }, "comment": "table" }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "Common/Data/Sandbox/sc_e_sandbox.json",
            """
            {
              "Name": "sc_e_sandbox",
              "Title": "Sandbox",
              "Segments": [{ "Label": "Main", "EntryPoint": 0, "Count": 1 }],
              "Mnemonics": [
                { "label": "Main", "mnemonic": "Nop", "operands": {}, "type": 0, "comment": "" }
              ]
            }
            """);

        // 6. Unity MonoBehaviours & InputActionAsset
        WriteText(
            exportRoot,
            "_monobehaviour/data/sharedassets0.assets.json",
            """
            {
              "sourceFile": "sharedassets0.assets",
              "count": 1,
              "monoBehaviours": [
                {
                  "assetsFile": "sharedassets0.assets",
                  "pathId": 28,
                  "gameObjectPathId": 0,
                  "gameObjectName": null,
                  "name": "last_default",
                  "scriptAssembly": "Unity.InputSystem.dll",
                  "scriptNamespace": "UnityEngine.InputSystem",
                  "scriptClass": "InputActionAsset",
                  "typeSource": "Cpp2IL",
                  "data": {
                    "m_Name": "last_default",
                    "m_ActionMaps": [
                      {
                        "m_Name": "Player",
                        "m_Id": "map-guid-1",
                        "m_Actions": [
                          { "m_Name": "Move", "m_Type": 0, "m_ExpectedControlType": "Vector2", "m_Id": "act-guid-1" },
                          { "m_Name": "Confirm", "m_Type": 1, "m_ExpectedControlType": "Button", "m_Id": "act-guid-2" }
                        ],
                        "m_Bindings": [
                          { "m_Name": "", "m_Id": "bind-guid-1", "m_Path": "<Gamepad>/leftStick", "m_Interactions": "", "m_Processors": "", "m_Groups": "Gamepad", "m_Action": "Move", "m_Flags": 0 },
                          { "m_Name": "", "m_Id": "bind-guid-2", "m_Path": "<Keyboard>/enter", "m_Interactions": "", "m_Processors": "", "m_Groups": "Keyboard&Mouse", "m_Action": "Confirm", "m_Flags": 0 }
                        ]
                      }
                    ]
                  }
                }
              ]
            }
            """);

        WriteText(
            exportRoot,
            "_monobehaviour/bundles/ui_test.json",
            """
            {
              "sourceFile": "StreamingAssets/aa/StandaloneWindows64/ui_test.bundle",
              "count": 2,
              "monoBehaviours": [
                {
                  "assetsFile": "CAB-111",
                  "pathId": 1001,
                  "gameObjectPathId": 500,
                  "gameObjectName": "RootCanvas",
                  "name": "MainWindow",
                  "scriptAssembly": "Assembly-CSharp.dll",
                  "scriptNamespace": "Last.UI",
                  "scriptClass": "CommonWindow",
                  "typeSource": "TypeTree",
                  "data": { "m_Enabled": 1, "width": 1280 }
                },
                {
                  "assetsFile": "CAB-222",
                  "pathId": 1001,
                  "gameObjectPathId": 501,
                  "gameObjectName": "SubCanvas",
                  "name": "SubWindow",
                  "scriptAssembly": "Assembly-CSharp.dll",
                  "scriptNamespace": "Last.UI",
                  "scriptClass": "CommonWindow",
                  "typeSource": "TypeTree",
                  "data": { "m_Enabled": 1, "width": 640 }
                }
              ]
            }
            """);

        // 7. Config docs
        WriteText(exportRoot, "AssetsPath.json", """{"keys":["Map_20010"]}""");
        WriteText(exportRoot, "Data/BattleExtraData/battle_extra_data.json", """{"critical_effect_info":{"rate":2}}""");
        WriteText(exportRoot, "Data/Misc/serial_misc_assets.json", """{"tile_damage_rate":1}""");
        WriteText(exportRoot, "Common/Data/Tools/ScriptEditor/common_master.json", """{"enums":["A"]}""");
        WriteText(exportRoot, "Data/Tools/ScriptEditor/serial_master.json", """{"serial_enums":["B"]}""");
        WriteText(exportRoot, "Res/Chara/Map/hero/anim_info.json", """{"frames":[0,1]}""");
        WriteText(exportRoot, "_data/sharedassets0.assets/sead.win.config", "max_voices=64\n");

        if (extraRowsPerTypedTable > 0)
        {
            AppendExtraRowsForIndexVerification(exportRoot, extraRowsPerTypedTable, useFf3AbilityLayout);
        }

        return exportRoot;
    }

    private static void AppendExtraRowsForIndexVerification(string root, int count, bool useFf3AbilityLayout)
    {
        var content = new StringBuilder();
        var item = new StringBuilder();
        var weapon = new StringBuilder();
        var armor = new StringBuilder();
        var ability = new StringBuilder();
        var monster = new StringBuilder();
        var party = new StringBuilder();
        var mset = new StringBuilder();
        var job = new StringBuilder();
        var command = new StringBuilder();
        var condition = new StringBuilder();
        var area = new StringBuilder();
        var map = new StringBuilder();
        var encount = new StringBuilder();
        var egrid = new StringBuilder();
        var pgroup = new StringBuilder();
        var product = new StringBuilder();
        var chara = new StringBuilder();
        var beffect = new StringBuilder();
        var extraSubmaps = Math.Min(count, 16);

        for (var i = 1; i <= count; i++)
        {
            var id = 100 + i;
            var msg = $"MSG_EXTRA_{id}";
            var aiId = i <= extraSubmaps ? id : 1;

            content.Append($"{1000 + i},{msg},None,None,0,1,{id}\n");
            content.Append($"{2000 + i},{msg},None,None,0,2,{id}\n");
            content.Append($"{3000 + i},{msg},None,None,0,3,{id}\n");
            content.Append($"{4000 + i},{msg},None,None,0,4,{id}\n");

            item.Append($"{id},{id},1,1,0,0,100,0,50,1,1,1,0,0,0,0,0,0,0,54,10,1,1,40,20,0\n");
            weapon.Append($"{id},{id},1,5,0,0,1,1,10,0,90,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,10,0,100,50,0,None\n");
            armor.Append($"{id},{id},2,1,2,8,2,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,100,50,0,None\n");
            if (useFf3AbilityLayout)
            {
                ability.Append($"{id},{id},1,2,4,0,0,1,4,20,0,0,0,1,100,0,1,0,1,1,1,0,0,0,0,0,0,0,0,1,0,100,50,0,None,0,0,0\n");
            }
            else
            {
                ability.Append($"{id},{id},1,2,4,0,0,1,4,20,0,0,0,1,100,0,1,0,1,1,1,0,0,0,0,0,0,0,0,1,100,50,0,0,None,0,0,0\n");
            }

            monster.Append($"{id},{msg},0,0,1,1,0,0,0,0,5,30,5,15,20,1,0,0,0,5,5,8,4,4,4,9,0,3,5,0,80,0,0,0,0,0,0,0,0,0,50,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,{aiId},1,10,0,0,0,0\n");
            party.Append($"{id},1,6,1,0,1,0,0,0,0,0,0,0,0,1,{id},0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n");
            mset.Append($"{id},{id},1000,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n");
            job.Append($"{id},{msg},None,1,0,0,0,0,0,20,20,20,20\n");
            command.Append($"{id},0,{msg},{msg},None,0,1,1,0,0,0,1,1,0,0,0,0,0,0,0,0,0,0\n");
            condition.Append($"{id},1,4,1,0,0,0,20,0,0,0,0,0,0,0,0,0,0,0,0,0,{msg},None,1,3,0,0,0,0\n");
            area.Append($"{id},0,{msg}\n");
            map.Append($"{id},Map_Extra_{id},map_extra_{id},{msg},0,10,0,0,0,0,0,{id},1,1,{id},0,1,0,0,0\n");
            encount.Append($"{id},{200 + i},{id},0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\n");
            egrid.Append($"{id},8,8,{200 + i},0,0\n");
            pgroup.Append($"{id},{msg}\n");
            product.Append($"{id},{1000 + i},{id},0,0\n");
            chara.Append($"{id},1,1,1,0,1,1,{msg},2,40,10,0,0,0,0,0,0,0,0,10,9,8,7,6,5,4,0,0,0,0,0,0,0,1,1,0,0,0,0,0,10,0,0,0,0,0,0,0,1\n");
            beffect.Append($"{id},ef_extra_{id},1,0,100,200,300\n");
        }

        AppendText(root, "Data/Master/content.csv", content.ToString());
        AppendText(root, "Data/Master/item.csv", item.ToString());
        AppendText(root, "Data/Master/weapon.csv", weapon.ToString());
        AppendText(root, "Data/Master/armor.csv", armor.ToString());
        AppendText(root, "Data/Master/ability.csv", ability.ToString());
        AppendText(root, "Data/Master/monster.csv", monster.ToString());
        AppendText(root, "Data/Master/monster_party.csv", party.ToString());
        AppendText(root, "Data/Master/monster_set.csv", mset.ToString());
        AppendText(root, "Data/Master/job.csv", job.ToString());
        AppendText(root, "Data/Master/command.csv", command.ToString());
        AppendText(root, "Data/Master/condition.csv", condition.ToString());
        AppendText(root, "Data/Master/area.csv", area.ToString());
        AppendText(root, "Data/Master/map.csv", map.ToString());
        AppendText(root, "Data/Master/encount_area.csv", encount.ToString());
        AppendText(root, "Data/Master/encount_area_grid.csv", egrid.ToString());
        AppendText(root, "Data/Master/product_group.csv", pgroup.ToString());
        AppendText(root, "Data/Master/product.csv", product.ToString());
        AppendText(root, "Data/Master/character_status.csv", chara.ToString());
        AppendText(root, "Data/Master/battle_effect_asset.csv", beffect.ToString());

        var extraUnityJson = new StringBuilder();
        for (var i = 1; i <= extraSubmaps; i++)
        {
            var id = 100 + i;
            var smName = $"Map_Extra_{id}";
            WriteText(
                root,
                $"Res/Map/{smName}/package.json",
                $$"""{"name":"{{smName}}","asset_group_name":"map_extra_{{id}}","map":[{"name":"{{smName}}","tilemap":"{{smName}}/tilemap","collision":"{{smName}}/collision","entity_default":"{{smName}}/entity_default"}]}""");
            WriteText(
                root,
                $"Res/Map/{smName}/{smName}/tilemap.json",
                """{"width":4,"height":4,"tilewidth":16,"tileheight":16,"orientation":"orthogonal","layers":[]}""");
            WriteText(
                root,
                $"Res/Map/{smName}/{smName}/entity_default.json",
                $$"""{"id":1,"name":"DefaultObjectGroup","type":"group","opacity":1,"visible":true,"layers":[{"id":2,"name":"GroundObject","type":"objectgroup","opacity":1,"visible":true,"objects":[{"gid":1023,"id":{{id}},"name":"ExtraChest_{{id}}","type":"","x":{{i * 16}},"y":{{(i + 1) * 16}},"width":16,"height":16,"properties":[{"name":"object_type","type":"int","value":7},{"name":"content_id","type":"int","value":{{1000 + i}}},{"name":"content_num","type":"int","value":1}]}]}]}""");
            WriteText(
                root,
                $"Res/Map/{smName}/{smName}/sc_extra_{id}.json",
                $$"""{"Name":"sc_extra_{{id}}","Title":"Extra","Segments":[{"Label":"Main","EntryPoint":0,"Count":2}],"Mnemonics":[{"label":"Main","mnemonic":"Op{{i}}","operands":{"sValues":[]},"type":1,"comment":""},{"label":"","mnemonic":"Msg","operands":{"sValues":["MSG_EXTRA_{{id}}"]},"type":1,"comment":""}]}""");
            WriteText(
                root,
                $"Res/Battle/MonsterAI/sc_ai_extra_{id}.json",
                $$"""{"Name":"sc_ai_extra_{{id}}","Title":"AI","EntryPoint":0,"Count":1,"Mnemonics":[{"label":"0000","mnemonic":"SyncAI","operands":{"iValues":[6]},"comment":""}]}""");
            AppendText(root, "Data/Master/monster_ai_script.csv", $"{id},sc_ai_extra_{id}\n");

            if (i > 1)
            {
                extraUnityJson.Append(',');
            }

            extraUnityJson.Append(
                $$"""{"assetsFile":"CAB-EXTRA-{{id}}","pathId":{{2000 + id}},"gameObjectPathId":0,"gameObjectName":"ExtraGo{{id}}","name":"ExtraObj{{id}}","scriptAssembly":"Assembly-CSharp.dll","scriptNamespace":"Last.Extra","scriptClass":"ExtraClass{{id}}","typeSource":"TypeTree","data":{"v":{{id}} } }""");
        }

        if (extraSubmaps > 0)
        {
            WriteText(
                root,
                "_monobehaviour/bundles/extra_test.json",
                $$"""{"sourceFile":"StreamingAssets/aa/StandaloneWindows64/extra_test.bundle","count":{{extraSubmaps}},"monoBehaviours":[{{extraUnityJson}}]}""");
        }
    }

    private static void AppendText(string root, string relativeUnixPath, string content)
    {
        var fullPath = Path.Combine(root, relativeUnixPath.Replace('/', Path.DirectorySeparatorChar));
        var existing = File.ReadAllText(fullPath, Encoding.UTF8);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sep = existing.EndsWith('\n') ? string.Empty : "\n";
        File.WriteAllText(fullPath, existing + sep + normalized, OutputGuard.Utf8WithoutBom);
    }

    private static void WriteText(string root, string relativeUnixPath, string content)
    {
        var fullPath = Path.Combine(root, relativeUnixPath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(fullPath, normalized, OutputGuard.Utf8WithoutBom);
    }
}
