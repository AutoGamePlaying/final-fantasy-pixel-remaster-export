using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal sealed partial class DbBuilder
{
    // =========================================================================
    // Stage 6: Unity MonoBehaviour / ScriptableObject & InputActionAsset
    // =========================================================================
    private StageResult ImportUnityStage(SqliteConnection conn, SqliteTransaction tx)
    {
        var files = new List<(string FullPath, string RelativePath, bool IsStandalone)>();

        var monoDir = Path.Combine(_exportRoot, "_monobehaviour");
        if (Directory.Exists(monoDir))
        {
            foreach (var f in Directory.EnumerateFiles(monoDir, "*.json", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                files.Add((f, DbParsers.ToRelativeUnixPath(_exportRoot, f), false));
            }
        }

        // Standalone ScriptableObject exports (e.g. FF6 Res/Opera/**/*.playable.json).
        var operaDir = Path.Combine(_exportRoot, "Res", "Opera");
        if (Directory.Exists(operaDir))
        {
            foreach (var f in Directory.EnumerateFiles(operaDir, "*.json", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                files.Add((f, DbParsers.ToRelativeUnixPath(_exportRoot, f), true));
            }
        }

        if (files.Count == 0)
        {
            return StageResult.Skipped("未找到 _monobehaviour 或独立 ScriptableObject 文件");
        }

        var parsed = ParseParallelOrdered(files, _jobs, item => ParseUnityJsonFile(item.FullPath, item.RelativePath, item.IsStandalone));

        RecordSource("unity", "_monobehaviour", parsed.Select(p => (p.RelativePath, p.FileSha256)));

        var allObjects = new Dictionary<(string Source, string AssetsFile, long PathId), ParsedUnityObject>();
        var allActionMaps = new Dictionary<string, ParsedInputActionMap>(StringComparer.Ordinal);
        var allActions = new Dictionary<string, ParsedInputAction>(StringComparer.Ordinal);
        var allBindings = new Dictionary<string, ParsedInputBinding>(StringComparer.Ordinal);

        foreach (var pf in parsed)
        {
            if (pf.Error is not null)
            {
                RecordIssue("unity", "parse_error", pf.RelativePath, pf.Error);
                continue;
            }

            foreach (var obj in pf.Objects)
            {
                allObjects.TryAdd((obj.Source, obj.AssetsFile, obj.PathId), obj);
            }

            foreach (var m in pf.ActionMaps)
            {
                allActionMaps.TryAdd(m.Id, m);
            }

            foreach (var a in pf.Actions)
            {
                allActions.TryAdd(a.Id, a);
            }

            foreach (var b in pf.Bindings)
            {
                allBindings.TryAdd(b.Id, b);
            }
        }

        long rowsWritten = 0;

        // 1. Insert unity_object sorted by (source, assets_file, path_id).
        var sortedObjects = allObjects.Values
            .OrderBy(o => o.Source, StringComparer.Ordinal)
            .ThenBy(o => o.AssetsFile, StringComparer.Ordinal)
            .ThenBy(o => o.PathId)
            .ToList();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO unity_object (source, assets_file, path_id, class, script_assembly,
                                          script_namespace, script_class, name, game_object_name,
                                          type_source, doc)
                VALUES ($src, $af, $pid, $cls, $asm, $ns, $sc, $name, $gon, $ts, $doc)
                """;
            var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var pAf = cmd.Parameters.Add("$af", SqliteType.Text);
            var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
            var pCls = cmd.Parameters.Add("$cls", SqliteType.Text);
            var pAsm = cmd.Parameters.Add("$asm", SqliteType.Text);
            var pNs = cmd.Parameters.Add("$ns", SqliteType.Text);
            var pSc = cmd.Parameters.Add("$sc", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pGon = cmd.Parameters.Add("$gon", SqliteType.Text);
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

            foreach (var obj in sortedObjects)
            {
                pSrc.Value = obj.Source;
                pAf.Value = obj.AssetsFile;
                pPid.Value = obj.PathId;
                pCls.Value = obj.FullClass;
                pAsm.Value = obj.ScriptAssembly;
                pNs.Value = obj.ScriptNamespace;
                pSc.Value = obj.ScriptClass;
                pName.Value = obj.Name;
                pGon.Value = obj.GameObjectName is not null ? obj.GameObjectName : DBNull.Value;
                pTs.Value = obj.TypeSource;
                pDoc.Value = obj.DocJson;
                cmd.ExecuteNonQuery();
                rowsWritten++;
            }
        }

        // 2. Insert InputActionAsset unpacked tables sorted by id.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO input_action_map (id, source, asset_name, name, ord, doc) " +
                "VALUES ($id, $src, $an, $name, $ord, $doc)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var pAn = cmd.Parameters.Add("$an", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pOrd = cmd.Parameters.Add("$ord", SqliteType.Integer);
            var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

            foreach (var m in allActionMaps.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                pId.Value = m.Id;
                pSrc.Value = m.Source;
                pAn.Value = m.AssetName;
                pName.Value = m.Name;
                pOrd.Value = m.Ord;
                pDoc.Value = m.DocJson;
                cmd.ExecuteNonQuery();
                rowsWritten++;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO input_action (id, map_id, name, action_type, expected_control_type, ord, doc)
                VALUES ($id, $mid, $name, $at, $ect, $ord, $doc)
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pMid = cmd.Parameters.Add("$mid", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pAt = cmd.Parameters.Add("$at", SqliteType.Integer);
            var pEct = cmd.Parameters.Add("$ect", SqliteType.Text);
            var pOrd = cmd.Parameters.Add("$ord", SqliteType.Integer);
            var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

            foreach (var a in allActions.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                pId.Value = a.Id;
                pMid.Value = a.MapId;
                pName.Value = a.Name;
                pAt.Value = a.ActionType;
                pEct.Value = a.ExpectedControlType;
                pOrd.Value = a.Ord;
                pDoc.Value = a.DocJson;
                cmd.ExecuteNonQuery();
                rowsWritten++;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO input_binding (id, map_id, action_name, name, path, interactions,
                                           processors, groups, flags, ord, doc)
                VALUES ($id, $mid, $an, $name, $path, $inter, $proc, $grp, $flg, $ord, $doc)
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pMid = cmd.Parameters.Add("$mid", SqliteType.Text);
            var pAn = cmd.Parameters.Add("$an", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pInter = cmd.Parameters.Add("$inter", SqliteType.Text);
            var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);
            var pGrp = cmd.Parameters.Add("$grp", SqliteType.Text);
            var pFlg = cmd.Parameters.Add("$flg", SqliteType.Integer);
            var pOrd = cmd.Parameters.Add("$ord", SqliteType.Integer);
            var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

            foreach (var b in allBindings.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                pId.Value = b.Id;
                pMid.Value = b.MapId;
                pAn.Value = b.ActionName;
                pName.Value = b.Name;
                pPath.Value = b.Path;
                pInter.Value = b.Interactions;
                pProc.Value = b.Processors;
                pGrp.Value = b.Groups;
                pFlg.Value = b.Flags;
                pOrd.Value = b.Ord;
                pDoc.Value = b.DocJson;
                cmd.ExecuteNonQuery();
                rowsWritten++;
            }
        }

        return StageResult.Ok(
            rowsWritten,
            $"{sortedObjects.Count} 个 Unity 对象，{allActionMaps.Count} 个输入表 / {allActions.Count} 个动作 / {allBindings.Count} 个绑定");
    }

    private static ParsedUnityFileResult ParseUnityJsonFile(string fullPath, string relativePath, bool isStandalone)
    {
        var sha = DbParsers.ComputeFileSha256(fullPath);
        var objects = new List<ParsedUnityObject>();
        var maps = new List<ParsedInputActionMap>();
        var actions = new List<ParsedInputAction>();
        var bindings = new List<ParsedInputBinding>();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedUnityFileResult(relativePath, sha, objects, maps, actions, bindings, "根节点不是 JSON 对象");
            }

            if (isStandalone || !root.TryGetProperty("monoBehaviours", out var mbArray))
            {
                var source = DbParsers.GetStringProp(root, "sourceFile", relativePath);
                var assetsFile = DbParsers.GetStringProp(root, "assetsFile", relativePath);
                ExtractUnityObject(source, assetsFile, root, objects, maps, actions, bindings);
            }
            else if (mbArray.ValueKind == JsonValueKind.Array)
            {
                var source = DbParsers.GetStringProp(root, "sourceFile", relativePath);
                foreach (var mb in mbArray.EnumerateArray())
                {
                    if (mb.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var assetsFile = DbParsers.GetStringProp(mb, "assetsFile", source);
                    ExtractUnityObject(source, assetsFile, mb, objects, maps, actions, bindings);
                }
            }

            return new ParsedUnityFileResult(relativePath, sha, objects, maps, actions, bindings, null);
        }
        catch (Exception ex)
        {
            return new ParsedUnityFileResult(relativePath, sha, objects, maps, actions, bindings, OutputGuard.TruncateError(ex.Message));
        }
    }

    private static void ExtractUnityObject(
        string source,
        string assetsFile,
        JsonElement mb,
        List<ParsedUnityObject> objects,
        List<ParsedInputActionMap> maps,
        List<ParsedInputAction> actions,
        List<ParsedInputBinding> bindings)
    {
        var pathId = DbParsers.GetNullableLongProp(mb, "pathId") ?? 0;
        var scriptAssembly = DbParsers.GetStringProp(mb, "scriptAssembly");
        var scriptNamespace = DbParsers.GetStringProp(mb, "scriptNamespace");
        var scriptClass = DbParsers.GetStringProp(mb, "scriptClass");
        var fullClass = string.IsNullOrEmpty(scriptNamespace)
            ? scriptClass
            : $"{scriptNamespace}.{scriptClass}";
        var name = DbParsers.GetStringProp(mb, "name");
        string? gameObjectName = null;
        if (mb.TryGetProperty("gameObjectName", out var gonEl) && gonEl.ValueKind == JsonValueKind.String)
        {
            gameObjectName = gonEl.GetString();
        }

        var typeSource = DbParsers.GetStringProp(mb, "typeSource");
        var hasData = mb.TryGetProperty("data", out var dataEl);
        var docJson = hasData ? DbParsers.NormalizeJson(dataEl) : DbParsers.NormalizeJson(mb);

        objects.Add(new ParsedUnityObject(
            source,
            assetsFile,
            pathId,
            fullClass,
            scriptAssembly,
            scriptNamespace,
            scriptClass,
            name,
            gameObjectName,
            typeSource,
            docJson));

        if (hasData
            && dataEl.ValueKind == JsonValueKind.Object
            && (string.Equals(scriptClass, "InputActionAsset", StringComparison.Ordinal)
                || string.Equals(fullClass, "UnityEngine.InputSystem.InputActionAsset", StringComparison.Ordinal)))
        {
            ExtractInputActionAsset(source, name, dataEl, maps, actions, bindings);
        }
    }

    private static void ExtractInputActionAsset(
        string source,
        string assetName,
        JsonElement dataEl,
        List<ParsedInputActionMap> maps,
        List<ParsedInputAction> actions,
        List<ParsedInputBinding> bindings)
    {
        if (!dataEl.TryGetProperty("m_ActionMaps", out var mapsEl) || mapsEl.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var mapOrd = 0;
        foreach (var mapEl in mapsEl.EnumerateArray())
        {
            if (mapEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var mapName = DbParsers.GetStringProp(mapEl, "m_Name");
            var mapId = DbParsers.GetStringProp(mapEl, "m_Id");
            if (string.IsNullOrWhiteSpace(mapId))
            {
                mapId = $"{source}:{assetName}:map:{mapOrd}";
            }

            maps.Add(new ParsedInputActionMap(mapId, source, assetName, mapName, mapOrd, DbParsers.NormalizeJson(mapEl)));

            if (mapEl.TryGetProperty("m_Actions", out var actsEl) && actsEl.ValueKind == JsonValueKind.Array)
            {
                var actOrd = 0;
                foreach (var actEl in actsEl.EnumerateArray())
                {
                    if (actEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var actId = DbParsers.GetStringProp(actEl, "m_Id");
                    if (string.IsNullOrWhiteSpace(actId))
                    {
                        actId = $"{mapId}:action:{actOrd}";
                    }

                    actions.Add(new ParsedInputAction(
                        actId,
                        mapId,
                        DbParsers.GetStringProp(actEl, "m_Name"),
                        DbParsers.GetIntProp(actEl, "m_Type"),
                        DbParsers.GetStringProp(actEl, "m_ExpectedControlType"),
                        actOrd,
                        DbParsers.NormalizeJson(actEl)));
                    actOrd++;
                }
            }

            if (mapEl.TryGetProperty("m_Bindings", out var bindsEl) && bindsEl.ValueKind == JsonValueKind.Array)
            {
                var bindOrd = 0;
                foreach (var bindEl in bindsEl.EnumerateArray())
                {
                    if (bindEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var bindId = DbParsers.GetStringProp(bindEl, "m_Id");
                    if (string.IsNullOrWhiteSpace(bindId))
                    {
                        bindId = $"{mapId}:binding:{bindOrd}";
                    }

                    bindings.Add(new ParsedInputBinding(
                        bindId,
                        mapId,
                        DbParsers.GetStringProp(bindEl, "m_Action"),
                        DbParsers.GetStringProp(bindEl, "m_Name"),
                        DbParsers.GetStringProp(bindEl, "m_Path"),
                        DbParsers.GetStringProp(bindEl, "m_Interactions"),
                        DbParsers.GetStringProp(bindEl, "m_Processors"),
                        DbParsers.GetStringProp(bindEl, "m_Groups"),
                        DbParsers.GetIntProp(bindEl, "m_Flags"),
                        bindOrd,
                        DbParsers.NormalizeJson(bindEl)));
                    bindOrd++;
                }
            }

            mapOrd++;
        }
    }

    // =========================================================================
    // Stage 7: Configuration & auxiliary structured documents (config_doc)
    // =========================================================================
    private StageResult ImportConfigStage(SqliteConnection conn, SqliteTransaction tx)
    {
        var candidates = DiscoverConfigCandidates();
        if (candidates.Count == 0)
        {
            return StageResult.Skipped("未找到配置文件");
        }

        var parsed = ParseParallelOrdered(candidates, _jobs, item =>
        {
            var sha = DbParsers.ComputeFileSha256(item.FullPath);
            try
            {
                var text = DbParsers.StripBom(File.ReadAllText(item.FullPath, Encoding.UTF8));
                string docJson;
                if (item.IsJson)
                {
                    using var jdoc = JsonDocument.Parse(text);
                    docJson = DbParsers.NormalizeJson(jdoc.RootElement);
                }
                else
                {
                    docJson = DbParsers.SerializeRawTextAsJson(text);
                }

                return new ParsedConfigDoc(item.RelativePath, item.Kind, sha, docJson, null);
            }
            catch (Exception ex)
            {
                return new ParsedConfigDoc(item.RelativePath, item.Kind, sha, null, OutputGuard.TruncateError(ex.Message));
            }
        });

        RecordSource("config", "config_docs", parsed.Select(p => (p.RelativePath, p.FileSha256)));

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO config_doc (path, kind, doc) VALUES ($path, $kind, $doc)";
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long rows = 0;
        foreach (var item in parsed.OrderBy(p => p.RelativePath, StringComparer.Ordinal))
        {
            if (item.Error is not null || item.DocJson is null)
            {
                RecordIssue("config", "parse_error", item.RelativePath, item.Error ?? "解析配置文档失败");
                continue;
            }

            pPath.Value = item.RelativePath;
            pKind.Value = item.Kind;
            pDoc.Value = item.DocJson;
            cmd.ExecuteNonQuery();
            rows++;
        }

        return StageResult.Ok(rows);
    }

    private List<(string FullPath, string RelativePath, string Kind, bool IsJson)> DiscoverConfigCandidates()
    {
        var list = new List<(string FullPath, string RelativePath, string Kind, bool IsJson)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddSingleFile(string relativeUnixPath, string kind, bool isJson)
        {
            var fullPath = Path.Combine(_exportRoot, relativeUnixPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath) && seen.Add(relativeUnixPath))
            {
                list.Add((fullPath, relativeUnixPath, kind, isJson));
            }
        }

        void AddDirPattern(string relativeUnixDir, string pattern, string kind, bool isJson, bool recursive)
        {
            var fullDir = Path.Combine(_exportRoot, relativeUnixDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullDir))
            {
                return;
            }

            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(fullDir, pattern, opt).OrderBy(p => p, StringComparer.Ordinal))
            {
                var rel = DbParsers.ToRelativeUnixPath(_exportRoot, file);
                if (seen.Add(rel))
                {
                    list.Add((file, rel, kind, isJson));
                }
            }
        }

        AddSingleFile("AssetsPath.json", "assets_path", isJson: true);
        AddSingleFile("Data/BattleExtraData/battle_extra_data.json", "battle_extra_data", isJson: true);
        AddSingleFile("Data/Misc/serial_misc_assets.json", "serial_misc_assets", isJson: true);
        AddSingleFile("Common/Data/Tools/ScriptEditor/common_master.json", "script_editor_master", isJson: true);
        AddSingleFile("Data/Tools/ScriptEditor/serial_master.json", "script_editor_master", isJson: true);
        AddSingleFile("Res/SoundPlayer/animation_catalog.json", "sound_player", isJson: true);
        AddSingleFile("Common/Res/Effects/Meshes/package.json", "effect_mesh_package", isJson: true);
        AddDirPattern("Res/Chara", "anim_info.json", "anim_info", isJson: true, recursive: true);
        AddDirPattern("_data", "*.config", "sead_config", isJson: false, recursive: true);
        AddDirPattern("Res/EndRoll", "*.txt", "end_roll", isJson: false, recursive: true);
        AddDirPattern("Res/License", "*.txt", "license", isJson: false, recursive: true);

        list.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        return list;
    }

    // =========================================================================
    // Stage 8: Cross-entity relations & dangling reference verification
    // =========================================================================
    private StageResult ImportRelationsStage(SqliteConnection conn, SqliteTransaction tx)
    {
        long total = 0;
        total += BuildEncounterRelations(conn, tx);
        total += BuildShopRelations(conn, tx);
        total += BuildTreasureRelations(conn, tx);
        total += BuildMonsterAiRelations(conn, tx);
        total += BuildMapSubmapRelations(conn, tx);
        total += BuildScriptTextRelations(conn, tx);
        return StageResult.Ok(total);
    }

    private long BuildEncounterRelations(SqliteConnection conn, SqliteTransaction tx)
    {
        long total = 0;

        var validMonsters = new HashSet<long>(_monsterNamesById.Keys);
        var validParties = new HashSet<long>();
        var setEntries = new Dictionary<long, List<(int Slot, long PartyId, long Rate)>>();

        // 1. monster_party -> monster (rel_monster_party_member)
        if (_masterTables.TryGetValue("monster_party", out var mpTbl))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO rel_monster_party_member (monster_party_id, slot, monster_id, pos_x, pos_y, group_id)
                VALUES ($mpid, $slot, $mid, $px, $py, $gid)
                """;
            var pMpid = cmd.Parameters.Add("$mpid", SqliteType.Integer);
            var pSlot = cmd.Parameters.Add("$slot", SqliteType.Integer);
            var pMid = cmd.Parameters.Add("$mid", SqliteType.Integer);
            var pPx = cmd.Parameters.Add("$px", SqliteType.Integer);
            var pPy = cmd.Parameters.Add("$py", SqliteType.Integer);
            var pGid = cmd.Parameters.Add("$gid", SqliteType.Integer);

            foreach (var row in EnumeratePrimaryNumericRows(mpTbl))
            {
                var mpId = row.NumericId!.Value;
                validParties.Add(mpId);

                for (var slot = 1; slot <= 9; slot++)
                {
                    var mid = GetLong(mpTbl, row, $"monster{slot}");
                    if (mid <= 0)
                    {
                        continue;
                    }

                    if (!validMonsters.Contains(mid))
                    {
                        RecordIssue(
                            "relations",
                            "dangling_party_monster",
                            $"monster_party/{mpId}/slot{slot}",
                            $"怪物编组 {mpId} 槽位 {slot} 引用不存在的怪物 id={mid}");
                        continue;
                    }

                    pMpid.Value = mpId;
                    pSlot.Value = slot;
                    pMid.Value = mid;
                    pPx.Value = GetLong(mpTbl, row, $"monster{slot}_x_position");
                    pPy.Value = GetLong(mpTbl, row, $"monster{slot}_y_position");
                    pGid.Value = GetLong(mpTbl, row, $"monster{slot}_group");
                    cmd.ExecuteNonQuery();
                    total++;
                }
            }
        }

        // 2. monster_set -> monster_party (rel_monster_set_party)
        if (_masterTables.TryGetValue("monster_set", out var msTbl))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO rel_monster_set_party (monster_set_id, slot, monster_party_id, rate)
                VALUES ($msid, $slot, $mpid, $rate)
                """;
            var pMsid = cmd.Parameters.Add("$msid", SqliteType.Integer);
            var pSlot = cmd.Parameters.Add("$slot", SqliteType.Integer);
            var pMpid = cmd.Parameters.Add("$mpid", SqliteType.Integer);
            var pRate = cmd.Parameters.Add("$rate", SqliteType.Integer);

            foreach (var row in EnumeratePrimaryNumericRows(msTbl))
            {
                var msId = row.NumericId!.Value;
                var list = new List<(int Slot, long PartyId, long Rate)>();
                setEntries[msId] = list;

                for (var slot = 1; slot <= 16; slot++)
                {
                    var partyId = GetLong(msTbl, row, $"monster_set{slot}");
                    var rate = GetLong(msTbl, row, $"monster_set{slot}_rate");
                    if (partyId <= 0)
                    {
                        continue;
                    }

                    if (!validParties.Contains(partyId))
                    {
                        RecordIssue(
                            "relations",
                            "dangling_monster_set_party",
                            $"monster_set/{msId}/slot{slot}",
                            $"怪物集合 {msId} 槽位 {slot} 引用不存在的怪物编组 id={partyId}");
                        continue;
                    }

                    list.Add((slot, partyId, rate));
                    pMsid.Value = msId;
                    pSlot.Value = slot;
                    pMpid.Value = partyId;
                    pRate.Value = rate;
                    cmd.ExecuteNonQuery();
                    total++;
                }
            }
        }

        // 3. encount_area -> monster_set -> monster_party (rel_encount_area_party)
        if (_masterTables.TryGetValue("encount_area", out var eaTbl))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO rel_encount_area_party (encount_area_id, foot_slot, monster_set_id, set_slot, monster_party_id, rate)
                VALUES ($eaid, $foot, $msid, $sslot, $mpid, $rate)
                """;
            var pEaid = cmd.Parameters.Add("$eaid", SqliteType.Integer);
            var pFoot = cmd.Parameters.Add("$foot", SqliteType.Integer);
            var pMsid = cmd.Parameters.Add("$msid", SqliteType.Integer);
            var pSslot = cmd.Parameters.Add("$sslot", SqliteType.Integer);
            var pMpid = cmd.Parameters.Add("$mpid", SqliteType.Integer);
            var pRate = cmd.Parameters.Add("$rate", SqliteType.Integer);

            foreach (var row in EnumeratePrimaryNumericRows(eaTbl))
            {
                var eaId = row.NumericId!.Value;
                for (var foot = 1; foot <= 32; foot++)
                {
                    var msId = GetLong(eaTbl, row, $"foot{foot:00}_monster_set");
                    if (msId <= 0)
                    {
                        continue;
                    }

                    if (!setEntries.TryGetValue(msId, out var parties))
                    {
                        RecordIssue(
                            "relations",
                            "dangling_encount_monster_set",
                            $"encount_area/{eaId}/foot{foot:00}",
                            $"遭遇区域 {eaId} 地形槽位 {foot:00} 引用不存在的怪物集合 id={msId}");
                        continue;
                    }

                    foreach (var (setSlot, partyId, rate) in parties)
                    {
                        pEaid.Value = eaId;
                        pFoot.Value = foot;
                        pMsid.Value = msId;
                        pSslot.Value = setSlot;
                        pMpid.Value = partyId;
                        pRate.Value = rate;
                        cmd.ExecuteNonQuery();
                        total++;
                    }
                }
            }
        }

        return total;
    }

    private long BuildShopRelations(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("product", out var prodTbl))
        {
            return 0;
        }

        var validShopIds = new HashSet<long>();
        if (_masterTables.TryGetValue("product_group", out var pgTbl))
        {
            foreach (var row in EnumeratePrimaryNumericRows(pgTbl))
            {
                validShopIds.Add(row.NumericId!.Value);
            }
        }

        var rows = new List<(long ShopId, long ProductId, long ContentId, long Coef, long Limit)>();
        foreach (var row in EnumeratePrimaryNumericRows(prodTbl))
        {
            var prodId = row.NumericId!.Value;
            var shopId = GetLong(prodTbl, row, "group_id");
            var contentId = GetLong(prodTbl, row, "content_id");
            var coef = GetLong(prodTbl, row, "coefficient");
            var limit = GetLong(prodTbl, row, "purchase_limit");

            var valid = true;
            if (!validShopIds.Contains(shopId))
            {
                RecordIssue(
                    "relations",
                    "dangling_shop_group",
                    $"product/{prodId}",
                    $"商品 {prodId} 引用不存在的商店分组 product_group.id={shopId}");
                valid = false;
            }

            if (!_contentById.ContainsKey(contentId))
            {
                RecordIssue(
                    "relations",
                    "dangling_shop_content",
                    $"product/{prodId}",
                    $"商品 {prodId}（商店 {shopId}）引用不存在的 content.id={contentId}");
                valid = false;
            }

            if (valid)
            {
                rows.Add((shopId, prodId, contentId, coef, limit));
            }
        }

        rows.Sort((a, b) =>
        {
            var c = a.ShopId.CompareTo(b.ShopId);
            return c != 0 ? c : a.ProductId.CompareTo(b.ProductId);
        });

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rel_shop_product (shop_id, product_id, content_id, coefficient, purchase_limit)
            VALUES ($sid, $pid, $cid, $coef, $lim)
            """;
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pCoef = cmd.Parameters.Add("$coef", SqliteType.Integer);
        var pLim = cmd.Parameters.Add("$lim", SqliteType.Integer);

        foreach (var r in rows)
        {
            pSid.Value = r.ShopId;
            pPid.Value = r.ProductId;
            pCid.Value = r.ContentId;
            pCoef.Value = r.Coef;
            pLim.Value = r.Limit;
            cmd.ExecuteNonQuery();
        }

        return rows.Count;
    }

    private long BuildTreasureRelations(SqliteConnection conn, SqliteTransaction tx)
    {
        if (_treasureCandidates.Count == 0)
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rel_treasure_content (treasure_chest_id, submap_id, content_id, content_num)
            VALUES ($tcid, $sid, $cid, $cnum)
            """;
        var pTcid = cmd.Parameters.Add("$tcid", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pCnum = cmd.Parameters.Add("$cnum", SqliteType.Integer);

        long total = 0;
        foreach (var tc in _treasureCandidates.OrderBy(c => c.TreasureChestId))
        {
            if (!tc.ContentId.HasValue || tc.ContentId.Value <= 0 || !_contentById.ContainsKey(tc.ContentId.Value))
            {
                RecordIssue(
                    "relations",
                    "dangling_treasure_content",
                    $"{tc.SubmapName}/{tc.SourceFile}#{tc.TiledId}",
                    $"宝箱（id={tc.TreasureChestId}）引用无效的 content_id={tc.ContentId?.ToString() ?? "null"}");
                continue;
            }

            pTcid.Value = tc.TreasureChestId;
            pSid.Value = tc.SubmapId;
            pCid.Value = tc.ContentId.Value;
            pCnum.Value = tc.ContentNum;
            cmd.ExecuteNonQuery();
            total++;
        }

        return total;
    }

    private long BuildMonsterAiRelations(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("monster", out var monTbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rel_monster_ai (monster_id, ai_script_master_id, script_name, script_id)
            VALUES ($mid, $asid, $sname, $sid)
            """;
        var pMid = cmd.Parameters.Add("$mid", SqliteType.Integer);
        var pAsid = cmd.Parameters.Add("$asid", SqliteType.Integer);
        var pSname = cmd.Parameters.Add("$sname", SqliteType.Text);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);

        long total = 0;
        foreach (var row in EnumeratePrimaryNumericRows(monTbl))
        {
            var monsterId = row.NumericId!.Value;
            var aiMasterId = GetLong(monTbl, row, "script_id");
            if (aiMasterId <= 0)
            {
                continue;
            }

            if (!_monsterAiScriptNameByMasterId.TryGetValue(aiMasterId, out var scriptName))
            {
                RecordIssue(
                    "relations",
                    "dangling_monster_ai_master",
                    $"monster/{monsterId}",
                    $"怪物 {monsterId} 引用不存在的 monster_ai_script.id={aiMasterId}");
                continue;
            }

            if (!_scriptIdByKindAndName.TryGetValue($"monster_ai:{scriptName}", out var scriptId))
            {
                RecordIssue(
                    "relations",
                    "dangling_monster_ai_script",
                    $"monster/{monsterId}",
                    $"怪物 {monsterId} 对应的 AI 脚本文件不存在：{scriptName}（monster_ai_script.id={aiMasterId}）");
                continue;
            }

            pMid.Value = monsterId;
            pAsid.Value = aiMasterId;
            pSname.Value = scriptName;
            pSid.Value = scriptId;
            cmd.ExecuteNonQuery();
            total++;
        }

        return total;
    }

    private long BuildMapSubmapRelations(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("map", out var mapTbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rel_map_submap (map_id, submap_id, map_name, asset_name)
            VALUES ($mid, $sid, $mn, $an)
            """;
        var pMid = cmd.Parameters.Add("$mid", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pAn = cmd.Parameters.Add("$an", SqliteType.Text);

        long total = 0;
        foreach (var row in EnumeratePrimaryNumericRows(mapTbl))
        {
            var mapId = row.NumericId!.Value;
            var mapName = GetCell(mapTbl, row, "map_name");
            var assetName = GetCell(mapTbl, row, "asset_name");

            if (!TryFindSubmapId(mapName, out var submapId))
            {
                RecordIssue(
                    "relations",
                    "dangling_map_submap",
                    $"map/{mapId}/{mapName}",
                    $"map.csv 行 id={mapId}（map_name={mapName}, asset_name={assetName}）未匹配到子地图资源");
                continue;
            }

            pMid.Value = mapId;
            pSid.Value = submapId;
            pMn.Value = mapName;
            pAn.Value = assetName;
            cmd.ExecuteNonQuery();
            total++;
        }

        return total;
    }

    private long BuildScriptTextRelations(SqliteConnection conn, SqliteTransaction tx)
    {
        if (_scriptTextCandidates.Count == 0)
        {
            return 0;
        }

        var sorted = _scriptTextCandidates
            .OrderBy(c => c.ScriptId)
            .ThenBy(c => c.InstructionOrd)
            .ThenBy(c => c.OperandIndex)
            .ToList();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rel_script_text (script_id, instruction_ord, mnemonic, operand_index, text_key, scope)
            VALUES ($sid, $ord, $mnem, $op, $key, $scope)
            """;
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pOrd = cmd.Parameters.Add("$ord", SqliteType.Integer);
        var pMnem = cmd.Parameters.Add("$mnem", SqliteType.Text);
        var pOp = cmd.Parameters.Add("$op", SqliteType.Integer);
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pScope = cmd.Parameters.Add("$scope", SqliteType.Text);

        long total = 0;
        foreach (var cand in sorted)
        {
            if (_textByKey.TryGetValue(cand.RawValue, out var entry))
            {
                pSid.Value = cand.ScriptId;
                pOrd.Value = cand.InstructionOrd;
                pMnem.Value = cand.Mnemonic;
                pOp.Value = cand.OperandIndex;
                pKey.Value = cand.RawValue;
                pScope.Value = entry.Scope;
                cmd.ExecuteNonQuery();
                total++;
            }
            else if (cand.ExplicitMessageMnemonic || cand.RawValue.StartsWith("MSG_", StringComparison.Ordinal))
            {
                RecordIssue(
                    "relations",
                    "dangling_script_text",
                    $"{cand.ScriptPath}#{cand.InstructionOrd}",
                    $"脚本指令 {cand.Mnemonic}[{cand.OperandIndex}] 引用不存在的文本键：{cand.RawValue}");
            }
        }

        return total;
    }

    private sealed record ParsedUnityObject(
        string Source,
        string AssetsFile,
        long PathId,
        string FullClass,
        string ScriptAssembly,
        string ScriptNamespace,
        string ScriptClass,
        string Name,
        string? GameObjectName,
        string TypeSource,
        string DocJson);

    private sealed record ParsedInputActionMap(
        string Id,
        string Source,
        string AssetName,
        string Name,
        int Ord,
        string DocJson);

    private sealed record ParsedInputAction(
        string Id,
        string MapId,
        string Name,
        int ActionType,
        string ExpectedControlType,
        int Ord,
        string DocJson);

    private sealed record ParsedInputBinding(
        string Id,
        string MapId,
        string ActionName,
        string Name,
        string Path,
        string Interactions,
        string Processors,
        string Groups,
        int Flags,
        int Ord,
        string DocJson);

    private sealed record ParsedUnityFileResult(
        string RelativePath,
        string FileSha256,
        IReadOnlyList<ParsedUnityObject> Objects,
        IReadOnlyList<ParsedInputActionMap> ActionMaps,
        IReadOnlyList<ParsedInputAction> Actions,
        IReadOnlyList<ParsedInputBinding> Bindings,
        string? Error);

    private sealed record ParsedConfigDoc(
        string RelativePath,
        string Kind,
        string FileSha256,
        string? DocJson,
        string? Error);
}
