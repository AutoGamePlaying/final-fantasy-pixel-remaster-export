using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal sealed partial class DbBuilder
{
    private static readonly string[] KnownMessageScopes = ["etc_text", "story_cha", "story_mes", "system"];

    private static int ScopePriority(string scope) => scope switch
    {
        "system" => 0,
        "etc_text" => 1,
        "story_mes" => 2,
        "story_cha" => 3,
        _ => 4,
    };

    // =========================================================================
    // Stage 1: Multilingual text (Data/Message/*_<lang>.txt)
    // =========================================================================
    private StageResult ImportTextStage(SqliteConnection conn, SqliteTransaction tx)
    {
        var messageDir = Path.Combine(_exportRoot, "Data", "Message");
        if (!Directory.Exists(messageDir))
        {
            return StageResult.Skipped("缺少目录 Data/Message");
        }

        var files = Directory.EnumerateFiles(messageDir, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            return StageResult.Skipped("Data/Message 中无文本文件");
        }

        var parsedFiles = ParseParallelOrdered(files, _jobs, filePath =>
        {
            var relPath = DbParsers.ToRelativeUnixPath(_exportRoot, filePath);
            var sha = DbParsers.ComputeFileSha256(filePath);
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!TrySplitMessageFileName(fileName, out var scope, out var lang))
            {
                return new ParsedTextFile(relPath, sha, null, null, Array.Empty<(string, string, int)>(), $"无法识别消息文件命名：{fileName}");
            }

            try
            {
                var content = File.ReadAllText(filePath, Encoding.UTF8);
                var entries = DbParsers.ParseMessageTsv(content);
                return new ParsedTextFile(relPath, sha, scope, lang, entries, null);
            }
            catch (Exception ex)
            {
                return new ParsedTextFile(relPath, sha, scope, lang, Array.Empty<(string, string, int)>(), OutputGuard.TruncateError(ex.Message));
            }
        });

        RecordSource("text", "Data/Message", parsedFiles.Select(f => (f.RelativePath, f.FileSha256)));

        // Deduplicate per (scope, key, lang) deterministically (first-seen wins; record issue if values conflict).
        var textMap = new Dictionary<(string Scope, string Key, string Lang), (string Value, string Plain)>();
        foreach (var pf in parsedFiles)
        {
            if (pf.Error is not null || pf.Scope is null || pf.Lang is null)
            {
                RecordIssue("text", "parse_error", pf.RelativePath, pf.Error ?? "未知消息文件错误");
                continue;
            }

            foreach (var (key, value, _) in pf.Entries)
            {
                var composite = (pf.Scope, key, pf.Lang);
                if (textMap.TryGetValue(composite, out var existing))
                {
                    if (!string.Equals(existing.Value, value, StringComparison.Ordinal))
                    {
                        RecordIssue(
                            "text",
                            "conflicting_text_key",
                            $"{pf.Scope}/{key}/{pf.Lang}",
                            $"文件 {pf.RelativePath} 存在同名不同值键，保留首次出现值");
                    }

                    continue;
                }

                var plain = DbParsers.StripMessageTags(value);
                textMap[composite] = (value, plain);

                if (!_textByKey.TryGetValue(key, out var keyEntry))
                {
                    keyEntry = (pf.Scope, new Dictionary<string, string>(StringComparer.Ordinal));
                    _textByKey[key] = keyEntry;
                }

                var existingPrio = ScopePriority(keyEntry.Scope);
                var currentPrio = ScopePriority(pf.Scope);
                if (currentPrio < existingPrio)
                {
                    keyEntry = (pf.Scope, new Dictionary<string, string>(keyEntry.PlainByLang, StringComparer.Ordinal));
                    _textByKey[key] = keyEntry;
                    keyEntry.PlainByLang[pf.Lang] = plain;
                }
                else if (currentPrio == existingPrio || !keyEntry.PlainByLang.ContainsKey(pf.Lang))
                {
                    keyEntry.PlainByLang[pf.Lang] = plain;
                }
            }
        }

        var sortedRows = textMap
            .OrderBy(kv => kv.Key.Scope, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Key, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Lang, StringComparer.Ordinal)
            .ToList();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO text (scope, key, lang, value, value_plain) " +
            "VALUES ($scope, $key, $lang, $value, $plain)";
        var pScope = cmd.Parameters.Add("$scope", SqliteType.Text);
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pLang = cmd.Parameters.Add("$lang", SqliteType.Text);
        var pVal = cmd.Parameters.Add("$value", SqliteType.Text);
        var pPlain = cmd.Parameters.Add("$plain", SqliteType.Text);

        foreach (var (k, v) in sortedRows)
        {
            pScope.Value = k.Scope;
            pKey.Value = k.Key;
            pLang.Value = k.Lang;
            pVal.Value = v.Value;
            pPlain.Value = v.Plain;
            cmd.ExecuteNonQuery();
        }

        return StageResult.Ok(sortedRows.Count);
    }

    private static bool TrySplitMessageFileName(string fileNameWithoutExt, out string scope, out string lang)
    {
        foreach (var candidate in KnownMessageScopes)
        {
            var prefix = candidate + "_";
            if (fileNameWithoutExt.StartsWith(prefix, StringComparison.Ordinal)
                && fileNameWithoutExt.Length > prefix.Length)
            {
                scope = candidate;
                lang = fileNameWithoutExt[prefix.Length..];
                return true;
            }
        }

        var lastUnderscore = fileNameWithoutExt.LastIndexOf('_');
        if (lastUnderscore > 0 && lastUnderscore + 1 < fileNameWithoutExt.Length)
        {
            scope = fileNameWithoutExt[..lastUnderscore];
            lang = fileNameWithoutExt[(lastUnderscore + 1)..];
            return true;
        }

        scope = string.Empty;
        lang = string.Empty;
        return false;
    }

    // =========================================================================
    // Stage 2: Generic master CSV layer (Data/Master/*.csv)
    // =========================================================================
    private StageResult ImportMasterRawStage(SqliteConnection conn, SqliteTransaction tx)
    {
        var masterDir = Path.Combine(_exportRoot, "Data", "Master");
        if (!Directory.Exists(masterDir))
        {
            return StageResult.Skipped("缺少目录 Data/Master");
        }

        var files = Directory.EnumerateFiles(masterDir, "*.csv", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            return StageResult.Skipped("Data/Master 中无 CSV 文件");
        }

        var parsed = ParseParallelOrdered(files, _jobs, filePath =>
        {
            var relPath = DbParsers.ToRelativeUnixPath(_exportRoot, filePath);
            var sha = DbParsers.ComputeFileSha256(filePath);
            var tableName = Path.GetFileNameWithoutExtension(filePath);

            try
            {
                var content = File.ReadAllText(filePath, Encoding.UTF8);
                var (rawHeaders, rawRows) = DbParsers.ParseCsv(content);
                var canonicalHeaders = rawHeaders
                    .Select(h => DbSchema.NormalizeColumnName(tableName, h))
                    .ToArray();
                var canonicalIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < canonicalHeaders.Length; i++)
                {
                    canonicalIndex.TryAdd(canonicalHeaders[i], i);
                }

                var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var rows = new List<ParsedMasterRow>(rawRows.Count);
                var dupList = new List<(string RowKey, int Ord)>();

                for (var rIdx = 0; rIdx < rawRows.Count; rIdx++)
                {
                    var cells = rawRows[rIdx];
                    var rowKey = cells.Length > 0 && !string.IsNullOrEmpty(cells[0])
                        ? cells[0]
                        : $"_row_{rIdx + 1}";
                    keyCounts.TryGetValue(rowKey, out var ord);
                    keyCounts[rowKey] = ord + 1;
                    if (ord > 0)
                    {
                        dupList.Add((rowKey, ord));
                    }

                    var numericId = cells.Length > 0 ? DbParsers.TryParseLong(cells[0]) : null;
                    var rawDocJson = DbParsers.SerializeRowObject(rawHeaders, cells);
                    rows.Add(new ParsedMasterRow(rowKey, ord, numericId, cells, rawDocJson));
                }

                // Sort rows deterministically by natural primary key (row_key, row_ord).
                rows.Sort((a, b) =>
                {
                    var c = StringComparer.Ordinal.Compare(a.RowKey, b.RowKey);
                    return c != 0 ? c : a.RowOrd.CompareTo(b.RowOrd);
                });

                return new ParsedMasterFile(
                    relPath,
                    sha,
                    new ParsedMasterTable(tableName, rawHeaders, canonicalHeaders, canonicalIndex, rows),
                    dupList,
                    null);
            }
            catch (Exception ex)
            {
                return new ParsedMasterFile(relPath, sha, null, Array.Empty<(string, int)>(), OutputGuard.TruncateError(ex.Message));
            }
        });

        RecordSource("master_raw", "Data/Master", parsed.Select(f => (f.RelativePath, f.FileSha256)));

        using var tblCmd = conn.CreateCommand();
        tblCmd.Transaction = tx;
        tblCmd.CommandText =
            "INSERT INTO master_table (name, columns_json, canonical_columns_json, row_count) " +
            "VALUES ($name, $cols, $ccols, $cnt)";
        var pTblName = tblCmd.Parameters.Add("$name", SqliteType.Text);
        var pTblCols = tblCmd.Parameters.Add("$cols", SqliteType.Text);
        var pTblCC = tblCmd.Parameters.Add("$ccols", SqliteType.Text);
        var pTblCnt = tblCmd.Parameters.Add("$cnt", SqliteType.Integer);

        using var rowCmd = conn.CreateCommand();
        rowCmd.Transaction = tx;
        rowCmd.CommandText =
            "INSERT INTO master_row (table_name, row_key, row_ord, id, doc) " +
            "VALUES ($tname, $rkey, $rord, $id, $doc)";
        var pRowTbl = rowCmd.Parameters.Add("$tname", SqliteType.Text);
        var pRowKey = rowCmd.Parameters.Add("$rkey", SqliteType.Text);
        var pRowOrd = rowCmd.Parameters.Add("$rord", SqliteType.Integer);
        var pRowId = rowCmd.Parameters.Add("$id", SqliteType.Integer);
        var pRowDoc = rowCmd.Parameters.Add("$doc", SqliteType.Text);

        long totalRows = 0;
        foreach (var pf in parsed.OrderBy(p => p.Table?.Name ?? p.RelativePath, StringComparer.Ordinal))
        {
            if (pf.Error is not null || pf.Table is null)
            {
                RecordIssue("master_raw", "parse_error", pf.RelativePath, pf.Error ?? "解析 CSV 失败");
                continue;
            }

            var table = pf.Table;
            _masterTables[table.Name] = table;

            foreach (var (dupKey, ord) in pf.Duplicates)
            {
                RecordIssue(
                    "master_raw",
                    "duplicate_master_row_id",
                    $"{table.Name}/{dupKey}",
                    $"主数据表 {table.Name}.csv 出现重复主键 {dupKey}（第 {ord + 1} 次），通用层已通过 row_ord={ord} 保留");
            }

            pTblName.Value = table.Name;
            pTblCols.Value = DbParsers.SerializeStringArray(table.RawHeaders);
            pTblCC.Value = DbParsers.SerializeStringArray(table.CanonicalHeaders);
            pTblCnt.Value = table.Rows.Count;
            tblCmd.ExecuteNonQuery();

            foreach (var row in table.Rows)
            {
                pRowTbl.Value = table.Name;
                pRowKey.Value = row.RowKey;
                pRowOrd.Value = row.RowOrd;
                pRowId.Value = row.NumericId.HasValue ? row.NumericId.Value : DBNull.Value;
                pRowDoc.Value = row.RawDocJson;
                rowCmd.ExecuteNonQuery();
                totalRows++;
            }
        }

        return StageResult.Ok(totalRows, $"{_masterTables.Count} 张主数据表");
    }

    // =========================================================================
    // Stage 3: Typed master tables (indexed columns + column alias normalization + resolved names)
    // =========================================================================
    private StageResult ImportMasterTypedStage(SqliteConnection conn, SqliteTransaction tx)
    {
        if (_masterTables.Count == 0)
        {
            return StageResult.Skipped("未加载主数据 CSV");
        }

        long total = 0;
        total += ImportTypedContent(conn, tx);
        total += ImportTypedItem(conn, tx);
        total += ImportTypedWeapon(conn, tx);
        total += ImportTypedArmor(conn, tx);
        total += ImportTypedAbility(conn, tx);
        total += ImportTypedMonster(conn, tx);
        total += ImportTypedMonsterParty(conn, tx);
        total += ImportTypedMonsterSet(conn, tx);
        total += ImportTypedJob(conn, tx);
        total += ImportTypedCommand(conn, tx);
        total += ImportTypedCondition(conn, tx);
        total += ImportTypedArea(conn, tx);
        total += ImportTypedMap(conn, tx);
        total += ImportTypedEncountArea(conn, tx);
        total += ImportTypedEncountAreaGrid(conn, tx);
        total += ImportTypedProductGroup(conn, tx);
        total += ImportTypedProduct(conn, tx);
        total += ImportTypedCharacterStatus(conn, tx);
        total += ImportTypedBattleEffectAsset(conn, tx);

        // Cache monster_ai_script.csv (id -> script_name) for relations stage.
        if (_masterTables.TryGetValue("monster_ai_script", out var aiTbl))
        {
            foreach (var row in EnumeratePrimaryNumericRows(aiTbl))
            {
                var sname = GetCell(aiTbl, row, "script_name");
                if (!string.IsNullOrWhiteSpace(sname))
                {
                    _monsterAiScriptNameByMasterId[row.NumericId!.Value] = sname.Trim();
                }
            }
        }

        return StageResult.Ok(total);
    }

    private long ImportTypedContent(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("content", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO content (id, mes_id_name, mes_id_battle, mes_id_description,
                                 name_zhc, name_ja, name_en, icon_id, type_id, type_value, doc)
            VALUES ($id, $mn, $mb, $md, $zhc, $ja, $en, $icon, $tid, $tval, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pMb = cmd.Parameters.Add("$mb", SqliteType.Text);
        var pMd = cmd.Parameters.Add("$md", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pIcon = cmd.Parameters.Add("$icon", SqliteType.Integer);
        var pTid = cmd.Parameters.Add("$tid", SqliteType.Integer);
        var pTval = cmd.Parameters.Add("$tval", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var mesBattle = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_battle"));
            var mesDesc = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_description"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);
            var iconId = GetLong(tbl, row, "icon_id");
            var typeId = GetLong(tbl, row, "type_id");
            var typeValue = GetLong(tbl, row, "type_value");

            var summary = new ContentSummary(id, mesName, zhc, ja, en, typeId, typeValue);
            _contentById[id] = summary;
            _contentByTypeValue.TryAdd((typeId, typeValue), summary);

            pId.Value = id;
            pMn.Value = mesName;
            pMb.Value = mesBattle;
            pMd.Value = mesDesc;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pIcon.Value = iconId;
            pTid.Value = typeId;
            pTval.Value = typeValue;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        // Also index duplicate content rows (row_ord > 0) for (type_id, type_value) name lookup (e.g. FF6 abilities 1528-1531).
        foreach (var row in tbl.Rows.Where(r => r.RowOrd > 0 && r.NumericId.HasValue))
        {
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);
            var typeId = GetLong(tbl, row, "type_id");
            var typeValue = GetLong(tbl, row, "type_value");
            _contentByTypeValue.TryAdd((typeId, typeValue), new ContentSummary(row.NumericId!.Value, mesName, zhc, ja, en, typeId, typeValue));
        }

        return count;
    }

    private long ImportTypedItem(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("item", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO item (id, content_id, sort_id, type_id, system_id, item_lv, attribute_id,
                              standard_value, battle_effect_asset_id, buy, sell, sales_not_possible,
                              mes_id_name, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $cid, $sort, $tid, $sid, $lv, $attr, $std, $bea, $buy, $sell, $snp, $mn, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pSort = cmd.Parameters.Add("$sort", SqliteType.Integer);
        var pTid = cmd.Parameters.Add("$tid", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pLv = cmd.Parameters.Add("$lv", SqliteType.Integer);
        var pAttr = cmd.Parameters.Add("$attr", SqliteType.Integer);
        var pStd = cmd.Parameters.Add("$std", SqliteType.Integer);
        var pBea = cmd.Parameters.Add("$bea", SqliteType.Integer);
        var pBuy = cmd.Parameters.Add("$buy", SqliteType.Integer);
        var pSell = cmd.Parameters.Add("$sell", SqliteType.Integer);
        var pSnp = cmd.Parameters.Add("$snp", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            _contentByTypeValue.TryGetValue((1, id), out var content);

            pId.Value = id;
            pCid.Value = content is not null ? content.Id : DBNull.Value;
            pSort.Value = GetLong(tbl, row, "sort_id");
            pTid.Value = GetLong(tbl, row, "type_id");
            pSid.Value = GetLong(tbl, row, "system_id");
            pLv.Value = GetLong(tbl, row, "item_lv");
            pAttr.Value = GetLong(tbl, row, "attribute_id");
            pStd.Value = GetLong(tbl, row, "standard_value");
            pBea.Value = GetLong(tbl, row, "battle_effect_asset_id");
            pBuy.Value = GetLong(tbl, row, "buy");
            pSell.Value = GetLong(tbl, row, "sell");
            pSnp.Value = GetLong(tbl, row, "sales_not_possible");
            pMn.Value = content?.MesIdName ?? string.Empty;
            pZhc.Value = content?.NameZhc ?? string.Empty;
            pJa.Value = content?.NameJa ?? string.Empty;
            pEn.Value = content?.NameEn ?? string.Empty;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedWeapon(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("weapon", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO weapon (id, content_id, sort_id, type_id, category_type, attribute_id,
                                equip_job_group_id, parts_group_id, attack, weight, accuracy_rate,
                                evasion_rate, two_handed_flag, throw_flag, trigger_ability_id,
                                buy, sell, sales_not_possible, mes_id_name, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $cid, $sort, $tid, $cat, $attr, $ejg, $pg, $atk, $wt, $acc, $eva, $th, $thr, $trg,
                    $buy, $sell, $snp, $mn, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pSort = cmd.Parameters.Add("$sort", SqliteType.Integer);
        var pTid = cmd.Parameters.Add("$tid", SqliteType.Integer);
        var pCat = cmd.Parameters.Add("$cat", SqliteType.Integer);
        var pAttr = cmd.Parameters.Add("$attr", SqliteType.Integer);
        var pEjg = cmd.Parameters.Add("$ejg", SqliteType.Integer);
        var pPg = cmd.Parameters.Add("$pg", SqliteType.Integer);
        var pAtk = cmd.Parameters.Add("$atk", SqliteType.Integer);
        var pWt = cmd.Parameters.Add("$wt", SqliteType.Integer);
        var pAcc = cmd.Parameters.Add("$acc", SqliteType.Integer);
        var pEva = cmd.Parameters.Add("$eva", SqliteType.Integer);
        var pTh = cmd.Parameters.Add("$th", SqliteType.Integer);
        var pThr = cmd.Parameters.Add("$thr", SqliteType.Integer);
        var pTrg = cmd.Parameters.Add("$trg", SqliteType.Integer);
        var pBuy = cmd.Parameters.Add("$buy", SqliteType.Integer);
        var pSell = cmd.Parameters.Add("$sell", SqliteType.Integer);
        var pSnp = cmd.Parameters.Add("$snp", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            _contentByTypeValue.TryGetValue((2, id), out var content);

            pId.Value = id;
            pCid.Value = content is not null ? content.Id : DBNull.Value;
            pSort.Value = GetLong(tbl, row, "sort_id");
            pTid.Value = GetLong(tbl, row, "type_id");
            pCat.Value = GetLong(tbl, row, "category_type");
            pAttr.Value = GetLong(tbl, row, "attribute_id");
            pEjg.Value = GetLong(tbl, row, "equip_job_group_id");
            pPg.Value = GetLong(tbl, row, "parts_group_id");
            pAtk.Value = GetLong(tbl, row, "attack");
            pWt.Value = GetLong(tbl, row, "weight");
            pAcc.Value = GetLong(tbl, row, "accuracy_rate");
            pEva.Value = GetLong(tbl, row, "evasion_rate");
            pTh.Value = GetLong(tbl, row, "two_handed_flag");
            pThr.Value = GetLong(tbl, row, "throw_flag");
            pTrg.Value = GetLong(tbl, row, "trigger_ability_id");
            pBuy.Value = GetLong(tbl, row, "buy");
            pSell.Value = GetLong(tbl, row, "sell");
            pSnp.Value = GetLong(tbl, row, "sales_not_possible");
            pMn.Value = content?.MesIdName ?? string.Empty;
            pZhc.Value = content?.NameZhc ?? string.Empty;
            pJa.Value = content?.NameJa ?? string.Empty;
            pEn.Value = content?.NameEn ?? string.Empty;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedArmor(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("armor", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO armor (id, content_id, sort_id, type_id, equip_job_group_id, parts_group_id,
                               defense, ability_defense, weight, evasion_rate, ability_evasion_rate,
                               resistance_attribute, resistance_condition, buy, sell, sales_not_possible,
                               mes_id_name, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $cid, $sort, $tid, $ejg, $pg, $def, $adef, $wt, $eva, $aeva,
                    $ra, $rc, $buy, $sell, $snp, $mn, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pSort = cmd.Parameters.Add("$sort", SqliteType.Integer);
        var pTid = cmd.Parameters.Add("$tid", SqliteType.Integer);
        var pEjg = cmd.Parameters.Add("$ejg", SqliteType.Integer);
        var pPg = cmd.Parameters.Add("$pg", SqliteType.Integer);
        var pDef = cmd.Parameters.Add("$def", SqliteType.Integer);
        var pAdef = cmd.Parameters.Add("$adef", SqliteType.Integer);
        var pWt = cmd.Parameters.Add("$wt", SqliteType.Integer);
        var pEva = cmd.Parameters.Add("$eva", SqliteType.Integer);
        var pAeva = cmd.Parameters.Add("$aeva", SqliteType.Integer);
        var pRa = cmd.Parameters.Add("$ra", SqliteType.Integer);
        var pRc = cmd.Parameters.Add("$rc", SqliteType.Integer);
        var pBuy = cmd.Parameters.Add("$buy", SqliteType.Integer);
        var pSell = cmd.Parameters.Add("$sell", SqliteType.Integer);
        var pSnp = cmd.Parameters.Add("$snp", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            _contentByTypeValue.TryGetValue((3, id), out var content);

            pId.Value = id;
            pCid.Value = content is not null ? content.Id : DBNull.Value;
            pSort.Value = GetLong(tbl, row, "sort_id");
            pTid.Value = GetLong(tbl, row, "type_id");
            pEjg.Value = GetLong(tbl, row, "equip_job_group_id");
            pPg.Value = GetLong(tbl, row, "parts_group_id");
            pDef.Value = GetLong(tbl, row, "defense");
            pAdef.Value = GetLong(tbl, row, "ability_defense");
            pWt.Value = GetLong(tbl, row, "weight");
            pEva.Value = GetLong(tbl, row, "evasion_rate");
            pAeva.Value = GetLong(tbl, row, "ability_evasion_rate");
            pRa.Value = GetLong(tbl, row, "resistance_attribute");
            pRc.Value = GetLong(tbl, row, "resistance_condition");
            pBuy.Value = GetLong(tbl, row, "buy");
            pSell.Value = GetLong(tbl, row, "sell");
            pSnp.Value = GetLong(tbl, row, "sales_not_possible");
            pMn.Value = content?.MesIdName ?? string.Empty;
            pZhc.Value = content?.NameZhc ?? string.Empty;
            pJa.Value = content?.NameJa ?? string.Empty;
            pEn.Value = content?.NameEn ?? string.Empty;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedAbility(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("ability", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO ability (id, content_id, sort_id, ability_lv, ability_group_id, type_id,
                                 attribute_id, system_id, use_value, standard_value, attack_count,
                                 accuracy_rate, use_job_group_id, ability_random_group_id,
                                 buy, sell, sales_not_possible, mes_id_name, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $cid, $sort, $lv, $ag, $tid, $attr, $sid, $uv, $std, $ac, $acc, $ujg, $arg,
                    $buy, $sell, $snp, $mn, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pSort = cmd.Parameters.Add("$sort", SqliteType.Integer);
        var pLv = cmd.Parameters.Add("$lv", SqliteType.Integer);
        var pAg = cmd.Parameters.Add("$ag", SqliteType.Integer);
        var pTid = cmd.Parameters.Add("$tid", SqliteType.Integer);
        var pAttr = cmd.Parameters.Add("$attr", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pUv = cmd.Parameters.Add("$uv", SqliteType.Integer);
        var pStd = cmd.Parameters.Add("$std", SqliteType.Integer);
        var pAc = cmd.Parameters.Add("$ac", SqliteType.Integer);
        var pAcc = cmd.Parameters.Add("$acc", SqliteType.Integer);
        var pUjg = cmd.Parameters.Add("$ujg", SqliteType.Integer);
        var pArg = cmd.Parameters.Add("$arg", SqliteType.Integer);
        var pBuy = cmd.Parameters.Add("$buy", SqliteType.Integer);
        var pSell = cmd.Parameters.Add("$sell", SqliteType.Integer);
        var pSnp = cmd.Parameters.Add("$snp", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            _contentByTypeValue.TryGetValue((4, id), out var content);

            pId.Value = id;
            pCid.Value = content is not null ? content.Id : DBNull.Value;
            pSort.Value = GetLong(tbl, row, "sort_id");
            pLv.Value = GetLong(tbl, row, "ability_lv");
            pAg.Value = GetLong(tbl, row, "ability_group_id");
            pTid.Value = GetLong(tbl, row, "type_id");
            pAttr.Value = GetLong(tbl, row, "attribute_id");
            pSid.Value = GetLong(tbl, row, "system_id");
            pUv.Value = GetLong(tbl, row, "use_value");
            pStd.Value = GetLong(tbl, row, "standard_value");
            pAc.Value = GetLong(tbl, row, "attack_count");
            pAcc.Value = GetLong(tbl, row, "accuracy_rate");
            pUjg.Value = GetLong(tbl, row, "use_job_group_id");
            pArg.Value = GetLong(tbl, row, "ability_random_group_id");
            pBuy.Value = GetLong(tbl, row, "buy");
            pSell.Value = GetLong(tbl, row, "sell");
            pSnp.Value = GetLong(tbl, row, "sales_not_possible");
            pMn.Value = content?.MesIdName ?? string.Empty;
            pZhc.Value = content?.NameZhc ?? string.Empty;
            pJa.Value = content?.NameJa ?? string.Empty;
            pEn.Value = content?.NameEn ?? string.Empty;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedMonster(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("monster", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO monster (id, mes_id_name, name_zhc, name_ja, name_en, lv, hp, mp, exp, gil,
                                 species, boss, attack, defense, ability_attack, ability_defense,
                                 ability_defense_rate, attack_plus_group, script_id, monster_asset_id,
                                 battle_effect_asset_id, drop_rate, doc)
            VALUES ($id, $mn, $zhc, $ja, $en, $lv, $hp, $mp, $exp, $gil, $sp, $boss, $atk, $def,
                    $aatk, $adef, $adr, $apg, $sid, $maid, $bea, $dr, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pLv = cmd.Parameters.Add("$lv", SqliteType.Integer);
        var pHp = cmd.Parameters.Add("$hp", SqliteType.Integer);
        var pMp = cmd.Parameters.Add("$mp", SqliteType.Integer);
        var pExp = cmd.Parameters.Add("$exp", SqliteType.Integer);
        var pGil = cmd.Parameters.Add("$gil", SqliteType.Integer);
        var pSp = cmd.Parameters.Add("$sp", SqliteType.Integer);
        var pBoss = cmd.Parameters.Add("$boss", SqliteType.Integer);
        var pAtk = cmd.Parameters.Add("$atk", SqliteType.Integer);
        var pDef = cmd.Parameters.Add("$def", SqliteType.Integer);
        var pAatk = cmd.Parameters.Add("$aatk", SqliteType.Integer);
        var pAdef = cmd.Parameters.Add("$adef", SqliteType.Integer);
        var pAdr = cmd.Parameters.Add("$adr", SqliteType.Integer);
        var pApg = cmd.Parameters.Add("$apg", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pMaid = cmd.Parameters.Add("$maid", SqliteType.Integer);
        var pBea = cmd.Parameters.Add("$bea", SqliteType.Integer);
        var pDr = cmd.Parameters.Add("$dr", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);
            _monsterNamesById[id] = (zhc, ja, en);

            pId.Value = id;
            pMn.Value = mesName;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pLv.Value = GetLong(tbl, row, "lv");
            pHp.Value = GetLong(tbl, row, "hp");
            pMp.Value = GetLong(tbl, row, "mp");
            pExp.Value = GetLong(tbl, row, "exp");
            pGil.Value = GetLong(tbl, row, "gill");
            pSp.Value = GetLong(tbl, row, "species");
            pBoss.Value = GetLong(tbl, row, "boss");
            pAtk.Value = GetLong(tbl, row, "attack");
            pDef.Value = GetLong(tbl, row, "defense");
            pAatk.Value = GetLong(tbl, row, "ability_attack");
            pAdef.Value = GetLong(tbl, row, "ability_defense");
            pAdr.Value = GetLong(tbl, row, "ability_defense_rate");
            pApg.Value = GetLong(tbl, row, "attack_plus_group");
            pSid.Value = GetLong(tbl, row, "script_id");
            pMaid.Value = GetLong(tbl, row, "monster_asset_id");
            pBea.Value = GetLong(tbl, row, "battle_effect_asset_id");
            pDr.Value = GetLong(tbl, row, "drop_rate");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedMonsterParty(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("monster_party", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO monster_party (id, battle_background_asset_id, battle_bgm_asset_id, script_name,
                                       not_escape, get_value, get_ap, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $bg, $bgm, $sc, $ne, $gv, $gap, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pBg = cmd.Parameters.Add("$bg", SqliteType.Integer);
        var pBgm = cmd.Parameters.Add("$bgm", SqliteType.Integer);
        var pSc = cmd.Parameters.Add("$sc", SqliteType.Text);
        var pNe = cmd.Parameters.Add("$ne", SqliteType.Integer);
        var pGv = cmd.Parameters.Add("$gv", SqliteType.Integer);
        var pGap = cmd.Parameters.Add("$gap", SqliteType.Integer);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            var monsterCounts = new List<(long MonsterId, int Count)>();
            for (var slot = 1; slot <= 9; slot++)
            {
                var mid = GetLong(tbl, row, $"monster{slot}");
                if (mid <= 0)
                {
                    continue;
                }

                var idx = monsterCounts.FindIndex(x => x.MonsterId == mid);
                if (idx >= 0)
                {
                    monsterCounts[idx] = (mid, monsterCounts[idx].Count + 1);
                }
                else
                {
                    monsterCounts.Add((mid, 1));
                }
            }

            var nameZhc = FormatPartySummary(monsterCounts, m => m.Zhc);
            var nameJa = FormatPartySummary(monsterCounts, m => m.Ja);
            var nameEn = FormatPartySummary(monsterCounts, m => m.En);

            pId.Value = id;
            pBg.Value = GetLong(tbl, row, "battle_background_asset_id");
            pBgm.Value = GetLong(tbl, row, "battle_bgm_asset_id");
            pSc.Value = GetCell(tbl, row, "script_name");
            pNe.Value = GetLong(tbl, row, "not_escape");
            pGv.Value = GetLong(tbl, row, "get_value");
            pGap.Value = GetLong(tbl, row, "get_ap");
            pZhc.Value = nameZhc;
            pJa.Value = nameJa;
            pEn.Value = nameEn;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private string FormatPartySummary(
        List<(long MonsterId, int Count)> entries,
        Func<(string Zhc, string Ja, string En), string> selector)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(entries.Count);
        foreach (var (mid, cnt) in entries)
        {
            var label = _monsterNamesById.TryGetValue(mid, out var names) && !string.IsNullOrEmpty(selector(names))
                ? selector(names)
                : $"#{mid}";
            parts.Add(cnt > 1 ? $"{label}×{cnt}" : label);
        }

        return string.Join(", ", parts);
    }

    private long ImportTypedMonsterSet(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("monster_set", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO monster_set (id, doc) VALUES ($id, $doc)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            pId.Value = row.NumericId!.Value;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedJob(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("job", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO job (id, mes_id_name, mes_id_description, name_zhc, name_ja, name_en,
                             strength, vitality, agility, magic, doc)
            VALUES ($id, $mn, $md, $zhc, $ja, $en, $str, $vit, $agi, $mag, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pMd = cmd.Parameters.Add("$md", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pStr = cmd.Parameters.Add("$str", SqliteType.Integer);
        var pVit = cmd.Parameters.Add("$vit", SqliteType.Integer);
        var pAgi = cmd.Parameters.Add("$agi", SqliteType.Integer);
        var pMag = cmd.Parameters.Add("$mag", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var mesDesc = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_description"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);

            pId.Value = row.NumericId!.Value;
            pMn.Value = mesName;
            pMd.Value = mesDesc;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pStr.Value = GetLong(tbl, row, "strength");
            pVit.Value = GetLong(tbl, row, "vitality");
            pAgi.Value = GetLong(tbl, row, "agility");
            pMag.Value = GetLong(tbl, row, "magic");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedCommand(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("command", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO command (id, command_lv, mes_id_name, mes_id_name2, mes_id_description,
                                 name_zhc, name_ja, name_en, command_type, ability_group_id,
                                 ability_id, job_group_id, doc)
            VALUES ($id, $lv, $mn, $mn2, $md, $zhc, $ja, $en, $ct, $ag, $aid, $jg, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pLv = cmd.Parameters.Add("$lv", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pMn2 = cmd.Parameters.Add("$mn2", SqliteType.Text);
        var pMd = cmd.Parameters.Add("$md", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pCt = cmd.Parameters.Add("$ct", SqliteType.Integer);
        var pAg = cmd.Parameters.Add("$ag", SqliteType.Integer);
        var pAid = cmd.Parameters.Add("$aid", SqliteType.Integer);
        var pJg = cmd.Parameters.Add("$jg", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var mesName2 = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name2"));
            var mesDesc = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_description"));
            var (zhc, ja, en) = ResolveMessageNames(string.IsNullOrEmpty(mesName) ? mesName2 : mesName);

            pId.Value = row.NumericId!.Value;
            pLv.Value = GetLong(tbl, row, "command_lv");
            pMn.Value = mesName;
            pMn2.Value = mesName2;
            pMd.Value = mesDesc;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pCt.Value = GetLong(tbl, row, "command_type");
            pAg.Value = GetLong(tbl, row, "ability_group_id");
            pAid.Value = GetLong(tbl, row, "ability_id");
            pJg.Value = GetLong(tbl, row, "job_group_id");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedCondition(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("condition", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO condition (id, condition_type, system_type, mes_id_name, mes_id_description,
                                   name_zhc, name_ja, name_en, icon_id, doc)
            VALUES ($id, $ct, $st, $mn, $md, $zhc, $ja, $en, $icon, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pCt = cmd.Parameters.Add("$ct", SqliteType.Integer);
        var pSt = cmd.Parameters.Add("$st", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pMd = cmd.Parameters.Add("$md", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pIcon = cmd.Parameters.Add("$icon", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var mesDesc = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_description"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);

            pId.Value = row.NumericId!.Value;
            pCt.Value = GetLong(tbl, row, "condition_type");
            pSt.Value = GetLong(tbl, row, "system_type");
            pMn.Value = mesName;
            pMd.Value = mesDesc;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pIcon.Value = GetLong(tbl, row, "icon_id");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedArea(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("area", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO area (id, map_type, area_name_key, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $mt, $ank, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMt = cmd.Parameters.Add("$mt", SqliteType.Integer);
        var pAnk = cmd.Parameters.Add("$ank", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var id = row.NumericId!.Value;
            var key = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "area_name"));
            var (zhc, ja, en) = ResolveMessageNames(key);
            _areaNamesById[id] = (zhc, ja, en);

            pId.Value = id;
            pMt.Value = GetLong(tbl, row, "map_type");
            pAnk.Value = key;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedMap(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("map", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO map (id, map_name, asset_name, map_title_key, name_zhc, name_ja, name_en,
                             area_id, script_id, bgm_id, monster_set_id, encount_area_grid_id,
                             loop_type, floor, submap_id, doc)
            VALUES ($id, $mn, $an, $mtk, $zhc, $ja, $en, $aid, $sid, $bgm, $msid, $eag, $lt, $fl, NULL, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pAn = cmd.Parameters.Add("$an", SqliteType.Text);
        var pMtk = cmd.Parameters.Add("$mtk", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pAid = cmd.Parameters.Add("$aid", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pBgm = cmd.Parameters.Add("$bgm", SqliteType.Integer);
        var pMsid = cmd.Parameters.Add("$msid", SqliteType.Integer);
        var pEag = cmd.Parameters.Add("$eag", SqliteType.Integer);
        var pLt = cmd.Parameters.Add("$lt", SqliteType.Integer);
        var pFl = cmd.Parameters.Add("$fl", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var titleKey = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "map_title"));
            var areaId = GetLong(tbl, row, "area_id");
            var (zhc, ja, en) = ResolveMessageNames(titleKey);
            if (string.IsNullOrEmpty(zhc) && string.IsNullOrEmpty(ja) && string.IsNullOrEmpty(en)
                && _areaNamesById.TryGetValue(areaId, out var areaNames))
            {
                (zhc, ja, en) = areaNames;
            }

            pId.Value = row.NumericId!.Value;
            pMn.Value = GetCell(tbl, row, "map_name");
            pAn.Value = GetCell(tbl, row, "asset_name");
            pMtk.Value = titleKey;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pAid.Value = areaId;
            pSid.Value = GetLong(tbl, row, "script_id");
            pBgm.Value = GetLong(tbl, row, "bgm_id");
            pMsid.Value = GetLong(tbl, row, "monster_set_id");
            pEag.Value = GetLong(tbl, row, "encount_area_grid_id");
            pLt.Value = GetLong(tbl, row, "loop_type");
            pFl.Value = GetLong(tbl, row, "floor");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedEncountArea(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("encount_area", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO encount_area (id, encount_area_grid_number, doc) VALUES ($id, $gn, $doc)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pGn = cmd.Parameters.Add("$gn", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            pId.Value = row.NumericId!.Value;
            pGn.Value = GetLong(tbl, row, "encount_area_grid_number");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedEncountAreaGrid(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("encount_area_grid", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO encount_area_grid (id, grid_width, grid_height, base_number,
                                           x_start_point, y_start_point, doc)
            VALUES ($id, $w, $h, $bn, $x, $y, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pW = cmd.Parameters.Add("$w", SqliteType.Integer);
        var pH = cmd.Parameters.Add("$h", SqliteType.Integer);
        var pBn = cmd.Parameters.Add("$bn", SqliteType.Integer);
        var pX = cmd.Parameters.Add("$x", SqliteType.Integer);
        var pY = cmd.Parameters.Add("$y", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            pId.Value = row.NumericId!.Value;
            pW.Value = GetLong(tbl, row, "grid_width");
            pH.Value = GetLong(tbl, row, "grid_height");
            pBn.Value = GetLong(tbl, row, "base_number");
            pX.Value = GetLong(tbl, row, "x_start_point");
            pY.Value = GetLong(tbl, row, "y_start_point");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedProductGroup(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("product_group", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO product_group (id, mes_id_name, name_zhc, name_ja, name_en, doc)
            VALUES ($id, $mn, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);

            pId.Value = row.NumericId!.Value;
            pMn.Value = mesName;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedProduct(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("product", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO product (id, group_id, content_id, coefficient, purchase_limit,
                                 name_zhc, name_ja, name_en, doc)
            VALUES ($id, $gid, $cid, $coef, $pl, $zhc, $ja, $en, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pGid = cmd.Parameters.Add("$gid", SqliteType.Integer);
        var pCid = cmd.Parameters.Add("$cid", SqliteType.Integer);
        var pCoef = cmd.Parameters.Add("$coef", SqliteType.Integer);
        var pPl = cmd.Parameters.Add("$pl", SqliteType.Integer);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var contentId = GetLong(tbl, row, "content_id");
            _contentById.TryGetValue(contentId, out var content);

            pId.Value = row.NumericId!.Value;
            pGid.Value = GetLong(tbl, row, "group_id");
            pCid.Value = contentId;
            pCoef.Value = GetLong(tbl, row, "coefficient");
            pPl.Value = GetLong(tbl, row, "purchase_limit");
            pZhc.Value = content?.NameZhc ?? string.Empty;
            pJa.Value = content?.NameJa ?? string.Empty;
            pEn.Value = content?.NameEn ?? string.Empty;
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedCharacterStatus(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("character_status", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO character_status (id, mes_id_name, name_zhc, name_ja, name_en, gender, lv, exp,
                                          job_id, hp, mp, strength, vitality, agility, intelligence,
                                          spirit, magic, luck, character_asset_id, doc)
            VALUES ($id, $mn, $zhc, $ja, $en, $gen, $lv, $exp, $jid, $hp, $mp, $str, $vit, $agi,
                    $int, $spi, $mag, $lck, $caid, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pMn = cmd.Parameters.Add("$mn", SqliteType.Text);
        var pZhc = cmd.Parameters.Add("$zhc", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pGen = cmd.Parameters.Add("$gen", SqliteType.Integer);
        var pLv = cmd.Parameters.Add("$lv", SqliteType.Integer);
        var pExp = cmd.Parameters.Add("$exp", SqliteType.Integer);
        var pJid = cmd.Parameters.Add("$jid", SqliteType.Integer);
        var pHp = cmd.Parameters.Add("$hp", SqliteType.Integer);
        var pMp = cmd.Parameters.Add("$mp", SqliteType.Integer);
        var pStr = cmd.Parameters.Add("$str", SqliteType.Integer);
        var pVit = cmd.Parameters.Add("$vit", SqliteType.Integer);
        var pAgi = cmd.Parameters.Add("$agi", SqliteType.Integer);
        var pInt = cmd.Parameters.Add("$int", SqliteType.Integer);
        var pSpi = cmd.Parameters.Add("$spi", SqliteType.Integer);
        var pMag = cmd.Parameters.Add("$mag", SqliteType.Integer);
        var pLck = cmd.Parameters.Add("$lck", SqliteType.Integer);
        var pCaid = cmd.Parameters.Add("$caid", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            var mesName = DbParsers.NormalizeMessageKey(GetCell(tbl, row, "mes_id_name"));
            var (zhc, ja, en) = ResolveMessageNames(mesName);

            pId.Value = row.NumericId!.Value;
            pMn.Value = mesName;
            pZhc.Value = zhc;
            pJa.Value = ja;
            pEn.Value = en;
            pGen.Value = GetLong(tbl, row, "gender");
            pLv.Value = GetLong(tbl, row, "lv");
            pExp.Value = GetLong(tbl, row, "exp");
            pJid.Value = GetLong(tbl, row, "job_id");
            pHp.Value = GetLong(tbl, row, "hp");
            pMp.Value = GetLong(tbl, row, "mp");
            pStr.Value = GetLong(tbl, row, "strength");
            pVit.Value = GetLong(tbl, row, "vitality");
            pAgi.Value = GetLong(tbl, row, "agility");
            pInt.Value = GetLong(tbl, row, "intelligence");
            pSpi.Value = GetLong(tbl, row, "spirit");
            pMag.Value = GetLong(tbl, row, "magic");
            pLck.Value = GetLong(tbl, row, "luck");
            pCaid.Value = GetLong(tbl, row, "character_asset_id");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private long ImportTypedBattleEffectAsset(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!_masterTables.TryGetValue("battle_effect_asset", out var tbl))
        {
            return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO battle_effect_asset (id, asset_name, position_type, display_type,
                                             se_asset_id, se_asset_id_2, critical_se_asset_id, doc)
            VALUES ($id, $an, $pt, $dt, $se1, $se2, $cse, $doc)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pAn = cmd.Parameters.Add("$an", SqliteType.Text);
        var pPt = cmd.Parameters.Add("$pt", SqliteType.Integer);
        var pDt = cmd.Parameters.Add("$dt", SqliteType.Integer);
        var pSe1 = cmd.Parameters.Add("$se1", SqliteType.Integer);
        var pSe2 = cmd.Parameters.Add("$se2", SqliteType.Integer);
        var pCse = cmd.Parameters.Add("$cse", SqliteType.Integer);
        var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

        long count = 0;
        foreach (var row in EnumeratePrimaryNumericRows(tbl))
        {
            pId.Value = row.NumericId!.Value;
            pAn.Value = GetCell(tbl, row, "asset_name");
            pPt.Value = GetLong(tbl, row, "position_type");
            pDt.Value = GetLong(tbl, row, "display_type");
            pSe1.Value = GetLong(tbl, row, "se_asset_id");
            pSe2.Value = GetLong(tbl, row, "se_asset_id_2");
            pCse.Value = GetLong(tbl, row, "critical_se_asset_id");
            pDoc.Value = row.RawDocJson;
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private static IEnumerable<ParsedMasterRow> EnumeratePrimaryNumericRows(ParsedMasterTable table)
    {
        return table.Rows
            .Where(r => r.RowOrd == 0 && r.NumericId.HasValue)
            .OrderBy(r => r.NumericId!.Value);
    }

    private static string GetCell(ParsedMasterTable table, ParsedMasterRow row, string canonicalColumn)
    {
        if (table.CanonicalIndex.TryGetValue(canonicalColumn, out var idx) && idx < row.Cells.Length)
        {
            return row.Cells[idx];
        }

        return string.Empty;
    }

    private static long GetLong(ParsedMasterTable table, ParsedMasterRow row, string canonicalColumn, long defaultValue = 0)
    {
        return DbParsers.ParseLongOrDefault(GetCell(table, row, canonicalColumn), defaultValue);
    }

    private sealed record ParsedTextFile(
        string RelativePath,
        string FileSha256,
        string? Scope,
        string? Lang,
        IReadOnlyList<(string Key, string Value, int LineNumber)> Entries,
        string? Error);

    private sealed record ParsedMasterFile(
        string RelativePath,
        string FileSha256,
        ParsedMasterTable? Table,
        IReadOnlyList<(string RowKey, int Ord)> Duplicates,
        string? Error);
}
