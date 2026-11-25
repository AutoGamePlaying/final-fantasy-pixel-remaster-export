using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal sealed partial class DbBuilder
{
    private static readonly HashSet<string> ExplicitMessageMnemonics = new(StringComparer.Ordinal)
    {
        "Msg",
        "MsgFunfare",
        "ShowMapTitle",
        "SpeackerSideRight",
    };

    // =========================================================================
    // Stage 4: Maps (packages, submaps, layers, compact tile grids, objects, chests)
    // =========================================================================
    private StageResult ImportMapsStage(SqliteConnection conn, SqliteTransaction tx)
    {
        var mapRoot = Path.Combine(_exportRoot, "Res", "Map");
        if (!Directory.Exists(mapRoot))
        {
            return StageResult.Skipped("缺少目录 Res/Map");
        }

        var packageDirs = Directory.EnumerateDirectories(mapRoot)
            .Where(d => !string.Equals(Path.GetFileName(d), "Map_Script", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToArray();

        if (packageDirs.Length == 0)
        {
            return StageResult.Skipped("Res/Map 中无地图包目录");
        }

        var parsedPackages = ParseParallelOrdered(packageDirs, _jobs, ParseMapPackageDirectory);

        var sourceEntries = new List<(string RelativePath, string FileSha256)>();
        var allPackages = new List<ParsedMapPackage>();
        var allSubmaps = new Dictionary<string, ParsedSubmap>(StringComparer.Ordinal);

        foreach (var pkg in parsedPackages)
        {
            sourceEntries.AddRange(pkg.SourceFiles);
            foreach (var issue in pkg.Issues)
            {
                _issues.Add(issue);
            }

            if (pkg.Package is not null)
            {
                allPackages.Add(pkg.Package);
            }

            foreach (var sm in pkg.Submaps)
            {
                if (!allSubmaps.ContainsKey(sm.Name))
                {
                    allSubmaps[sm.Name] = sm;
                }
            }
        }

        RecordSource("maps", "Res/Map", sourceEntries);

        long rowsWritten = 0;

        // 1. Insert map_package sorted by name.
        allPackages.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO map_package (name, asset_group_name, path, doc) " +
                "VALUES ($name, $agn, $path, $doc)";
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pAgn = cmd.Parameters.Add("$agn", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);

            foreach (var pkg in allPackages)
            {
                pName.Value = pkg.Name;
                pAgn.Value = pkg.AssetGroupName;
                pPath.Value = pkg.Path;
                pDoc.Value = pkg.DocJson;
                cmd.ExecuteNonQuery();
                rowsWritten++;
            }
        }

        // 2. Insert map_submap, map_layer, map_grid, map_object, treasure_chest in sorted order.
        var sortedSubmaps = allSubmaps.Values
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        using var submapCmd = conn.CreateCommand();
        submapCmd.Transaction = tx;
        submapCmd.CommandText =
            """
            INSERT INTO map_submap (id, package_name, name, width, height, tile_width, tile_height,
                                    layer_names_json, tilemap_path, doc)
            VALUES ($id, $pkg, $name, $w, $h, $tw, $th, $layers, $tm, $doc)
            """;
        var psId = submapCmd.Parameters.Add("$id", SqliteType.Integer);
        var psPkg = submapCmd.Parameters.Add("$pkg", SqliteType.Text);
        var psName = submapCmd.Parameters.Add("$name", SqliteType.Text);
        var psW = submapCmd.Parameters.Add("$w", SqliteType.Integer);
        var psH = submapCmd.Parameters.Add("$h", SqliteType.Integer);
        var psTw = submapCmd.Parameters.Add("$tw", SqliteType.Integer);
        var psTh = submapCmd.Parameters.Add("$th", SqliteType.Integer);
        var psLayers = submapCmd.Parameters.Add("$layers", SqliteType.Text);
        var psTm = submapCmd.Parameters.Add("$tm", SqliteType.Text);
        var psDoc = submapCmd.Parameters.Add("$doc", SqliteType.Text);

        using var layerCmd = conn.CreateCommand();
        layerCmd.Transaction = tx;
        layerCmd.CommandText =
            """
            INSERT INTO map_layer (id, submap_id, source_file, layer_kind, layer_name, tiled_type,
                                   width, height, visible, opacity, doc)
            VALUES ($id, $sid, $src, $kind, $name, $tt, $w, $h, $vis, $op, $doc)
            """;
        var plId = layerCmd.Parameters.Add("$id", SqliteType.Integer);
        var plSid = layerCmd.Parameters.Add("$sid", SqliteType.Integer);
        var plSrc = layerCmd.Parameters.Add("$src", SqliteType.Text);
        var plKind = layerCmd.Parameters.Add("$kind", SqliteType.Text);
        var plName = layerCmd.Parameters.Add("$name", SqliteType.Text);
        var plTt = layerCmd.Parameters.Add("$tt", SqliteType.Text);
        var plW = layerCmd.Parameters.Add("$w", SqliteType.Integer);
        var plH = layerCmd.Parameters.Add("$h", SqliteType.Integer);
        var plVis = layerCmd.Parameters.Add("$vis", SqliteType.Integer);
        var plOp = layerCmd.Parameters.Add("$op", SqliteType.Real);
        var plDoc = layerCmd.Parameters.Add("$doc", SqliteType.Text);

        using var gridCmd = conn.CreateCommand();
        gridCmd.Transaction = tx;
        gridCmd.CommandText =
            """
            INSERT OR REPLACE INTO map_grid (submap_id, layer_kind, layer_name, width, height, encoding, data)
            VALUES ($sid, $kind, $name, $w, $h, 'i32le', $data)
            """;
        var pgSid = gridCmd.Parameters.Add("$sid", SqliteType.Integer);
        var pgKind = gridCmd.Parameters.Add("$kind", SqliteType.Text);
        var pgName = gridCmd.Parameters.Add("$name", SqliteType.Text);
        var pgW = gridCmd.Parameters.Add("$w", SqliteType.Integer);
        var pgH = gridCmd.Parameters.Add("$h", SqliteType.Integer);
        var pgData = gridCmd.Parameters.Add("$data", SqliteType.Blob);

        using var objCmd = conn.CreateCommand();
        objCmd.Transaction = tx;
        objCmd.CommandText =
            """
            INSERT INTO map_object (id, submap_id, source_file, layer_name, tiled_id, name, tiled_type,
                                    object_type, gid, px_x, px_y, width, height, tile_x, tile_y,
                                    entity_id, content_id, content_num, script_id, flag_id, message_key,
                                    properties_json, doc)
            VALUES ($id, $sid, $src, $lname, $tid, $name, $tt, $ot, $gid, $px, $py, $w, $h, $tx, $ty,
                    $eid, $cid, $cnum, $scid, $fid, $mkey, $props, $doc)
            """;
        var poId = objCmd.Parameters.Add("$id", SqliteType.Integer);
        var poSid = objCmd.Parameters.Add("$sid", SqliteType.Integer);
        var poSrc = objCmd.Parameters.Add("$src", SqliteType.Text);
        var poLname = objCmd.Parameters.Add("$lname", SqliteType.Text);
        var poTid = objCmd.Parameters.Add("$tid", SqliteType.Integer);
        var poName = objCmd.Parameters.Add("$name", SqliteType.Text);
        var poTt = objCmd.Parameters.Add("$tt", SqliteType.Text);
        var poOt = objCmd.Parameters.Add("$ot", SqliteType.Integer);
        var poGid = objCmd.Parameters.Add("$gid", SqliteType.Integer);
        var poPx = objCmd.Parameters.Add("$px", SqliteType.Real);
        var poPy = objCmd.Parameters.Add("$py", SqliteType.Real);
        var poW = objCmd.Parameters.Add("$w", SqliteType.Real);
        var poH = objCmd.Parameters.Add("$h", SqliteType.Real);
        var poTx = objCmd.Parameters.Add("$tx", SqliteType.Integer);
        var poTy = objCmd.Parameters.Add("$ty", SqliteType.Integer);
        var poEid = objCmd.Parameters.Add("$eid", SqliteType.Integer);
        var poCid = objCmd.Parameters.Add("$cid", SqliteType.Integer);
        var poCnum = objCmd.Parameters.Add("$cnum", SqliteType.Integer);
        var poScid = objCmd.Parameters.Add("$scid", SqliteType.Integer);
        var poFid = objCmd.Parameters.Add("$fid", SqliteType.Integer);
        var poMkey = objCmd.Parameters.Add("$mkey", SqliteType.Text);
        var poProps = objCmd.Parameters.Add("$props", SqliteType.Text);
        var poDoc = objCmd.Parameters.Add("$doc", SqliteType.Text);

        using var chestCmd = conn.CreateCommand();
        chestCmd.Transaction = tx;
        chestCmd.CommandText =
            """
            INSERT INTO treasure_chest (id, map_object_id, submap_id, source_file, tiled_id, name,
                                        tile_x, tile_y, content_id, content_num, flag_id, message_key,
                                        content_name_zhc, content_name_ja, content_name_en)
            VALUES ($id, $moid, $sid, $src, $tid, $name, $tx, $ty, $cid, $cnum, $fid, $mkey, $zhc, $ja, $en)
            """;
        var pcId = chestCmd.Parameters.Add("$id", SqliteType.Integer);
        var pcMoid = chestCmd.Parameters.Add("$moid", SqliteType.Integer);
        var pcSid = chestCmd.Parameters.Add("$sid", SqliteType.Integer);
        var pcSrc = chestCmd.Parameters.Add("$src", SqliteType.Text);
        var pcTid = chestCmd.Parameters.Add("$tid", SqliteType.Integer);
        var pcName = chestCmd.Parameters.Add("$name", SqliteType.Text);
        var pcTx = chestCmd.Parameters.Add("$tx", SqliteType.Integer);
        var pcTy = chestCmd.Parameters.Add("$ty", SqliteType.Integer);
        var pcCid = chestCmd.Parameters.Add("$cid", SqliteType.Integer);
        var pcCnum = chestCmd.Parameters.Add("$cnum", SqliteType.Integer);
        var pcFid = chestCmd.Parameters.Add("$fid", SqliteType.Integer);
        var pcMkey = chestCmd.Parameters.Add("$mkey", SqliteType.Text);
        var pcZhc = chestCmd.Parameters.Add("$zhc", SqliteType.Text);
        var pcJa = chestCmd.Parameters.Add("$ja", SqliteType.Text);
        var pcEn = chestCmd.Parameters.Add("$en", SqliteType.Text);

        var nextLayerId = 1;
        var nextObjId = 1;
        var nextChestId = 1;

        for (var i = 0; i < sortedSubmaps.Count; i++)
        {
            var sm = sortedSubmaps[i];
            var submapId = i + 1;
            _submapIdByName[sm.Name] = submapId;
            _submapIdByLowerName.TryAdd(sm.Name, submapId);

            var distinctLayerNames = sm.Layers
                .Select(l => l.LayerName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            psId.Value = submapId;
            psPkg.Value = sm.PackageName;
            psName.Value = sm.Name;
            psW.Value = sm.Width;
            psH.Value = sm.Height;
            psTw.Value = sm.TileWidth;
            psTh.Value = sm.TileHeight;
            psLayers.Value = DbParsers.SerializeStringArray(distinctLayerNames);
            psTm.Value = sm.TilemapPath is not null ? sm.TilemapPath : DBNull.Value;
            psDoc.Value = sm.DocJson;
            submapCmd.ExecuteNonQuery();
            rowsWritten++;

            foreach (var layer in sm.Layers
                         .OrderBy(l => l.SourceFile, StringComparer.Ordinal)
                         .ThenBy(l => l.LayerKind, StringComparer.Ordinal)
                         .ThenBy(l => l.LayerName, StringComparer.Ordinal)
                         .ThenBy(l => l.Order))
            {
                plId.Value = nextLayerId++;
                plSid.Value = submapId;
                plSrc.Value = layer.SourceFile;
                plKind.Value = layer.LayerKind;
                plName.Value = layer.LayerName;
                plTt.Value = layer.TiledType;
                plW.Value = layer.Width;
                plH.Value = layer.Height;
                plVis.Value = layer.Visible ? 1 : 0;
                plOp.Value = layer.Opacity;
                plDoc.Value = layer.DocJson;
                layerCmd.ExecuteNonQuery();
                rowsWritten++;
            }

            foreach (var grid in sm.Grids
                         .OrderBy(g => g.LayerKind, StringComparer.Ordinal)
                         .ThenBy(g => g.LayerName, StringComparer.Ordinal))
            {
                pgSid.Value = submapId;
                pgKind.Value = grid.LayerKind;
                pgName.Value = grid.LayerName;
                pgW.Value = grid.Width;
                pgH.Value = grid.Height;
                pgData.Value = grid.Data;
                gridCmd.ExecuteNonQuery();
                rowsWritten++;
            }

            foreach (var obj in sm.Objects
                         .OrderBy(o => o.SourceFile, StringComparer.Ordinal)
                         .ThenBy(o => o.LayerName, StringComparer.Ordinal)
                         .ThenBy(o => o.TiledId)
                         .ThenBy(o => o.Order))
            {
                var objId = nextObjId++;
                var (tileX, tileY) = DbParsers.ComputeTileCoordinates(
                    obj.PxX,
                    obj.PxY,
                    obj.Gid.HasValue,
                    sm.TileWidth,
                    sm.TileHeight);

                poId.Value = objId;
                poSid.Value = submapId;
                poSrc.Value = obj.SourceFile;
                poLname.Value = obj.LayerName;
                poTid.Value = obj.TiledId;
                poName.Value = obj.Name;
                poTt.Value = obj.TiledType;
                poOt.Value = obj.ObjectType.HasValue ? obj.ObjectType.Value : DBNull.Value;
                poGid.Value = obj.Gid.HasValue ? obj.Gid.Value : DBNull.Value;
                poPx.Value = obj.PxX;
                poPy.Value = obj.PxY;
                poW.Value = obj.Width;
                poH.Value = obj.Height;
                poTx.Value = tileX;
                poTy.Value = tileY;
                poEid.Value = obj.EntityId.HasValue ? obj.EntityId.Value : DBNull.Value;
                poCid.Value = obj.ContentId.HasValue ? obj.ContentId.Value : DBNull.Value;
                poCnum.Value = obj.ContentNum.HasValue ? obj.ContentNum.Value : DBNull.Value;
                poScid.Value = obj.ScriptId.HasValue ? obj.ScriptId.Value : DBNull.Value;
                poFid.Value = obj.FlagId.HasValue ? obj.FlagId.Value : DBNull.Value;
                poMkey.Value = obj.MessageKey is not null ? obj.MessageKey : DBNull.Value;
                poProps.Value = obj.PropertiesJson;
                poDoc.Value = obj.DocJson;
                objCmd.ExecuteNonQuery();
                rowsWritten++;

                if (obj.ObjectType == 7)
                {
                    var chestId = nextChestId++;
                    var cnum = (int)(obj.ContentNum ?? 1);
                    ContentSummary? content = null;
                    if (obj.ContentId.HasValue)
                    {
                        _contentById.TryGetValue(obj.ContentId.Value, out content);
                    }

                    pcId.Value = chestId;
                    pcMoid.Value = objId;
                    pcSid.Value = submapId;
                    pcSrc.Value = obj.SourceFile;
                    pcTid.Value = obj.TiledId;
                    pcName.Value = obj.Name;
                    pcTx.Value = tileX;
                    pcTy.Value = tileY;
                    pcCid.Value = obj.ContentId.HasValue ? obj.ContentId.Value : DBNull.Value;
                    pcCnum.Value = cnum;
                    pcFid.Value = obj.FlagId.HasValue ? obj.FlagId.Value : DBNull.Value;
                    pcMkey.Value = obj.MessageKey is not null ? obj.MessageKey : DBNull.Value;
                    pcZhc.Value = content?.NameZhc ?? string.Empty;
                    pcJa.Value = content?.NameJa ?? string.Empty;
                    pcEn.Value = content?.NameEn ?? string.Empty;
                    chestCmd.ExecuteNonQuery();
                    rowsWritten++;

                    _treasureCandidates.Add(new TreasureChestLinkCandidate(
                        chestId,
                        submapId,
                        sm.Name,
                        obj.SourceFile,
                        obj.TiledId,
                        obj.ContentId,
                        cnum));
                }
            }
        }

        // Update map.submap_id linkage on typed map table.
        if (_masterTables.TryGetValue("map", out var mapTbl))
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE map SET submap_id = $sid WHERE id = $mid";
            var puSid = upd.Parameters.Add("$sid", SqliteType.Integer);
            var puMid = upd.Parameters.Add("$mid", SqliteType.Integer);
            foreach (var row in EnumeratePrimaryNumericRows(mapTbl))
            {
                var mapName = GetCell(mapTbl, row, "map_name");
                if (TryFindSubmapId(mapName, out var submapId))
                {
                    puSid.Value = submapId;
                    puMid.Value = row.NumericId!.Value;
                    upd.ExecuteNonQuery();
                }
            }
        }

        return StageResult.Ok(rowsWritten, $"{allPackages.Count} 个地图包，{sortedSubmaps.Count} 个子地图");
    }

    private bool TryFindSubmapId(string mapName, out int submapId)
    {
        if (_submapIdByName.TryGetValue(mapName, out submapId))
        {
            return true;
        }

        return _submapIdByLowerName.TryGetValue(mapName, out submapId);
    }

    private ParsedMapPackageResult ParseMapPackageDirectory(string packageDir)
    {
        var pkgName = Path.GetFileName(packageDir);
        var sourceFiles = new List<(string RelativePath, string FileSha256)>();
        var issues = new List<DbBuildIssue>();
        var submapsByName = new Dictionary<string, SubmapAccumulator>(StringComparer.Ordinal);
        ParsedMapPackage? parsedPkg = null;

        var pkgJsonPath = Path.Combine(packageDir, "package.json");
        if (File.Exists(pkgJsonPath))
        {
            var relPath = DbParsers.ToRelativeUnixPath(_exportRoot, pkgJsonPath);
            sourceFiles.Add((relPath, DbParsers.ComputeFileSha256(pkgJsonPath)));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkgJsonPath, Encoding.UTF8));
                var root = doc.RootElement;
                var name = DbParsers.GetStringProp(root, "name", pkgName);
                var assetGroup = DbParsers.GetStringProp(root, "asset_group_name", string.Empty);

                // Strip base64 inline payloads from package doc summary to keep package doc clean.
                parsedPkg = new ParsedMapPackage(name, assetGroup, relPath, BuildCompactPackageDoc(root));

                if (root.TryGetProperty("map", out var mapsEl) && mapsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mEl in mapsEl.EnumerateArray())
                    {
                        var smName = DbParsers.GetStringProp(mEl, "name");
                        if (string.IsNullOrWhiteSpace(smName))
                        {
                            continue;
                        }

                        var acc = GetOrCreateSubmap(submapsByName, pkgName, smName);
                        acc.PackageEntryDoc = BuildCompactPackageSubmapDoc(mEl);

                        // Capture inline base64 entities declared in package.json (e.g. FF5 world maps).
                        if (mEl.TryGetProperty("entity", out var entArr) && entArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ent in entArr.EnumerateArray())
                            {
                                var entName = DbParsers.GetStringProp(ent, "name");
                                var inlineB64 = DbParsers.GetStringProp(ent, "inline");
                                if (!string.IsNullOrWhiteSpace(entName) && !string.IsNullOrWhiteSpace(inlineB64))
                                {
                                    acc.InlineEntities.TryAdd(entName + ".json", inlineB64);
                                }
                            }
                        }

                        // Record tilemap path hint if submap shares another submap's tilemap.
                        var tmAsset = DbParsers.GetStringProp(mEl, "tilemap");
                        if (!string.IsNullOrWhiteSpace(tmAsset))
                        {
                            acc.TilemapAssetHint = tmAsset;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                issues.Add(new DbBuildIssue("maps", "parse_error", relPath, OutputGuard.TruncateError(ex.Message)));
            }
        }

        // Discover all submap directories on disk under Res/Map/<pkg>/<submap>/.
        foreach (var subDir in Directory.EnumerateDirectories(packageDir).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var smName = Path.GetFileName(subDir);
            var acc = GetOrCreateSubmap(submapsByName, pkgName, smName);
            acc.DiskDirectory = subDir;
        }

        // Parse every submap's files on disk + any inline entities from package.json.
        var parsedSubmaps = new List<ParsedSubmap>();
        foreach (var acc in submapsByName.Values.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var processedFiles = new HashSet<string>(StringComparer.Ordinal);
            if (acc.DiskDirectory is not null)
            {
                var files = Directory.EnumerateFiles(acc.DiskDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                    .ToArray();

                foreach (var filePath in files)
                {
                    var fn = Path.GetFileName(filePath);
                    if (fn.StartsWith("sc_", StringComparison.OrdinalIgnoreCase))
                    {
                        // Map scripts are handled by the scripts stage.
                        continue;
                    }

                    processedFiles.Add(fn);
                    var relPath = DbParsers.ToRelativeUnixPath(_exportRoot, filePath);
                    sourceFiles.Add((relPath, DbParsers.ComputeFileSha256(filePath)));

                    try
                    {
                        var text = File.ReadAllText(filePath, Encoding.UTF8);
                        ParseMapLayerFile(acc, fn, relPath, text);
                    }
                    catch (Exception ex)
                    {
                        issues.Add(new DbBuildIssue("maps", "parse_error", relPath, OutputGuard.TruncateError(ex.Message)));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(acc.TilemapAssetHint))
            {
                // Submap declared in package.json without its own directory (e.g. shares tilemap path).
                var hintPath = Path.Combine(packageDir, acc.TilemapAssetHint.Replace('/', Path.DirectorySeparatorChar) + ".json");
                if (File.Exists(hintPath))
                {
                    var relPath = DbParsers.ToRelativeUnixPath(_exportRoot, hintPath);
                    try
                    {
                        var text = File.ReadAllText(hintPath, Encoding.UTF8);
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        acc.Width = DbParsers.GetIntProp(root, "width", acc.Width);
                        acc.Height = DbParsers.GetIntProp(root, "height", acc.Height);
                        acc.TileWidth = DbParsers.GetIntProp(root, "tilewidth", 16);
                        acc.TileHeight = DbParsers.GetIntProp(root, "tileheight", 16);
                        acc.TilemapPath = relPath;
                    }
                    catch (Exception ex)
                    {
                        issues.Add(new DbBuildIssue("maps", "parse_error", relPath, OutputGuard.TruncateError(ex.Message)));
                    }
                }
            }

            // Parse inline base64 entities that were not already loaded from disk.
            foreach (var (inlineName, b64) in acc.InlineEntities.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (processedFiles.Contains(inlineName))
                {
                    continue;
                }

                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    var virtualRel = $"Res/Map/{pkgName}/{acc.Name}/{inlineName}#inline";
                    ParseMapLayerFile(acc, inlineName, virtualRel, decoded);
                }
                catch (Exception ex)
                {
                    issues.Add(new DbBuildIssue("maps", "parse_error", $"Res/Map/{pkgName}/{acc.Name}/{inlineName}#inline", OutputGuard.TruncateError(ex.Message)));
                }
            }

            parsedSubmaps.Add(new ParsedSubmap(
                acc.PackageName,
                acc.Name,
                acc.Width,
                acc.Height,
                acc.TileWidth > 0 ? acc.TileWidth : 16,
                acc.TileHeight > 0 ? acc.TileHeight : 16,
                acc.TilemapPath,
                acc.SubmapDoc ?? acc.PackageEntryDoc ?? "{}",
                acc.Layers,
                acc.Grids,
                acc.Objects));
        }

        return new ParsedMapPackageResult(parsedPkg, parsedSubmaps, sourceFiles, issues);
    }

    private static void ParseMapLayerFile(SubmapAccumulator acc, string fileName, string relPath, string jsonText)
    {
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var lower = fileName.ToLowerInvariant();
        if (lower == "tilemap.json")
        {
            acc.Width = DbParsers.GetIntProp(root, "width", acc.Width);
            acc.Height = DbParsers.GetIntProp(root, "height", acc.Height);
            acc.TileWidth = DbParsers.GetIntProp(root, "tilewidth", 16);
            acc.TileHeight = DbParsers.GetIntProp(root, "tileheight", 16);
            acc.TilemapPath = relPath;
            acc.SubmapDoc = BuildTilemapMetaDoc(root);
            WalkTiledNode(acc, fileName, "tilemap", root, extractGrid: false, extractObjects: false);
            return;
        }

        if (lower.StartsWith("encount_grid", StringComparison.Ordinal))
        {
            var w = DbParsers.GetIntProp(root, "width");
            var h = DbParsers.GetIntProp(root, "height");
            var layerName = Path.GetFileNameWithoutExtension(fileName);
            acc.Layers.Add(new ParsedMapLayer(
                fileName,
                "encount_grid",
                layerName,
                "encount_grid",
                w,
                h,
                true,
                1.0,
                BuildEncountGridMetaDoc(root),
                acc.Layers.Count));

            if (root.TryGetProperty("cells", out var cellsEl) && cellsEl.ValueKind == JsonValueKind.Array && w > 0 && h > 0)
            {
                var cells = ReadIntArray(cellsEl, w * h);
                acc.Grids.Add(new ParsedMapGrid("encount_grid", layerName, w, h, DbParsers.PackInt32GridLittleEndian(cells)));
            }

            return;
        }

        if (lower.StartsWith("route_instruction", StringComparison.Ordinal))
        {
            var layerName = Path.GetFileNameWithoutExtension(fileName);
            acc.Layers.Add(new ParsedMapLayer(
                fileName,
                "route_instruction",
                layerName,
                "route_instruction",
                acc.Width,
                acc.Height,
                true,
                1.0,
                DbParsers.NormalizeJson(root),
                acc.Layers.Count));
            return;
        }

        var (layerKind, isGrid, isObject) = ClassifyMapJsonFile(lower);
        WalkTiledNode(acc, fileName, layerKind, root, extractGrid: isGrid, extractObjects: isObject);
    }

    private static (string LayerKind, bool IsGrid, bool IsObject) ClassifyMapJsonFile(string lowerFileName)
    {
        return lowerFileName switch
        {
            "collision.json" => ("collision", true, false),
            "damage.json" => ("damage", true, false),
            "hidden_passage.json" => ("hidden_passage", true, false),
            "sub_collision.json" => ("sub_collision", true, false),
            "transportation.json" => ("transportation", true, false),
            "attribute.json" => ("attribute", true, false),
            "entity_default.json" => ("entity_default", false, true),
            "upperobject.json" => ("upper_object", false, true),
            "groundobject.json" => ("ground_object", false, true),
            _ when lowerFileName.StartsWith("ev_", StringComparison.Ordinal) => ("ev", false, true),
            _ => ("other", true, true),
        };
    }

    private static void WalkTiledNode(
        SubmapAccumulator acc,
        string sourceFile,
        string layerKind,
        JsonElement node,
        bool extractGrid,
        bool extractObjects)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var nodeType = DbParsers.GetStringProp(node, "type");
        var nodeName = DbParsers.GetStringProp(node, "name", Path.GetFileNameWithoutExtension(sourceFile));

        if (nodeType is "tilelayer" or "objectgroup")
        {
            var w = DbParsers.GetIntProp(node, "width", acc.Width);
            var h = DbParsers.GetIntProp(node, "height", acc.Height);
            var visible = DbParsers.GetBoolProp(node, "visible", true);
            var opacity = DbParsers.GetDoubleProp(node, "opacity", 1.0);

            acc.Layers.Add(new ParsedMapLayer(
                sourceFile,
                layerKind,
                nodeName,
                nodeType,
                w,
                h,
                visible,
                opacity,
                BuildLayerMetaDoc(node),
                acc.Layers.Count));

            if (extractGrid && nodeType == "tilelayer"
                && node.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind == JsonValueKind.Array
                && w > 0 && h > 0)
            {
                var cells = ReadIntArray(dataEl, w * h);
                var gridLayerName = MakeUniqueGridLayerName(acc.Grids, layerKind, nodeName);
                acc.Grids.Add(new ParsedMapGrid(layerKind, gridLayerName, w, h, DbParsers.PackInt32GridLittleEndian(cells)));
            }

            if (extractObjects && nodeType == "objectgroup"
                && node.TryGetProperty("objects", out var objsEl)
                && objsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var objEl in objsEl.EnumerateArray())
                {
                    if (objEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    acc.Objects.Add(ParseTiledObject(sourceFile, nodeName, objEl, acc.Objects.Count));
                }
            }
        }

        if (node.TryGetProperty("layers", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                WalkTiledNode(acc, sourceFile, layerKind, child, extractGrid, extractObjects);
            }
        }
    }

    private static string MakeUniqueGridLayerName(List<ParsedMapGrid> existing, string kind, string baseName)
    {
        if (!existing.Any(g => g.LayerKind == kind && g.LayerName == baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (existing.Any(g => g.LayerKind == kind && g.LayerName == $"{baseName}#{suffix}"))
        {
            suffix++;
        }

        return $"{baseName}#{suffix}";
    }

    private static ParsedMapObject ParseTiledObject(string sourceFile, string layerName, JsonElement objEl, int order)
    {
        var tiledId = DbParsers.GetIntProp(objEl, "id");
        var name = DbParsers.GetStringProp(objEl, "name");
        var tiledType = DbParsers.GetStringProp(objEl, "type");
        var gid = DbParsers.GetNullableLongProp(objEl, "gid");
        var pxX = DbParsers.GetDoubleProp(objEl, "x");
        var pxY = DbParsers.GetDoubleProp(objEl, "y");
        var width = DbParsers.GetDoubleProp(objEl, "width");
        var height = DbParsers.GetDoubleProp(objEl, "height");

        var propsMap = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (objEl.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var prop in propsEl.EnumerateArray())
            {
                var pName = DbParsers.GetStringProp(prop, "name");
                if (!string.IsNullOrEmpty(pName) && prop.TryGetProperty("value", out var pVal))
                {
                    propsMap[pName] = pVal.Clone();
                }
            }
        }

        var objectType = TryGetPropLong(propsMap, "object_type");
        var entityId = TryGetPropLong(propsMap, "entity_id");
        var contentId = TryGetPropLong(propsMap, "content_id");
        var contentNum = TryGetPropLong(propsMap, "content_num");
        var scriptId = TryGetPropLong(propsMap, "script_id");
        var flagId = TryGetPropLong(propsMap, "flag_id");
        var messageKey = TryGetPropString(propsMap, "message_key");

        string propertiesJson;
        using (var ms = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                foreach (var (k, v) in propsMap)
                {
                    writer.WritePropertyName(k);
                    v.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            propertiesJson = Encoding.UTF8.GetString(ms.ToArray());
        }

        return new ParsedMapObject(
            sourceFile,
            layerName,
            tiledId,
            name,
            tiledType,
            objectType,
            gid,
            pxX,
            pxY,
            width,
            height,
            entityId,
            contentId,
            contentNum,
            scriptId,
            flagId,
            string.IsNullOrEmpty(messageKey) ? null : messageKey,
            propertiesJson,
            DbParsers.NormalizeJson(objEl),
            order);
    }

    private static long? TryGetPropLong(SortedDictionary<string, JsonElement> props, string key)
    {
        if (!props.TryGetValue(key, out var val))
        {
            return null;
        }

        if (val.ValueKind == JsonValueKind.Number && val.TryGetInt64(out var lv))
        {
            return lv;
        }

        if (val.ValueKind == JsonValueKind.String
            && long.TryParse(val.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
        {
            return sv;
        }

        return null;
    }

    private static string? TryGetPropString(SortedDictionary<string, JsonElement> props, string key)
    {
        if (!props.TryGetValue(key, out var val))
        {
            return null;
        }

        return val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText();
    }

    private static int[] ReadIntArray(JsonElement arrayEl, int expectedLen)
    {
        var list = new List<int>(Math.Max(expectedLen, arrayEl.GetArrayLength()));
        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var iv))
            {
                list.Add(iv);
            }
            else if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var uv))
            {
                list.Add(unchecked((int)uv));
            }
            else
            {
                list.Add(0);
            }
        }

        while (list.Count < expectedLen)
        {
            list.Add(0);
        }

        return list.ToArray();
    }

    private static string BuildTilemapMetaDoc(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name is "layers")
                {
                    continue;
                }

                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildLayerMetaDoc(JsonElement node)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var prop in node.EnumerateObject())
            {
                if (prop.Name is "data" or "objects" or "layers")
                {
                    continue;
                }

                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildEncountGridMetaDoc(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "cells")
                {
                    continue;
                }

                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildCompactPackageDoc(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "map" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("map");
                    writer.WriteStartArray();
                    foreach (var m in prop.Value.EnumerateArray())
                    {
                        WritePackageSubmapWithoutInline(writer, m);
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildCompactPackageSubmapDoc(JsonElement mEl)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WritePackageSubmapWithoutInline(writer, mEl);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WritePackageSubmapWithoutInline(Utf8JsonWriter writer, JsonElement mEl)
    {
        if (mEl.ValueKind != JsonValueKind.Object)
        {
            mEl.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var prop in mEl.EnumerateObject())
        {
            if (prop.Name is "entity" or "script" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(prop.Name);
                writer.WriteStartArray();
                foreach (var entry in prop.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        entry.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    foreach (var ep in entry.EnumerateObject())
                    {
                        if (ep.Name == "inline")
                        {
                            writer.WriteBoolean("has_inline", true);
                        }
                        else
                        {
                            ep.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
            else
            {
                prop.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static SubmapAccumulator GetOrCreateSubmap(
        Dictionary<string, SubmapAccumulator> map,
        string packageName,
        string submapName)
    {
        if (!map.TryGetValue(submapName, out var acc))
        {
            acc = new SubmapAccumulator(packageName, submapName);
            map[submapName] = acc;
        }

        return acc;
    }

    // =========================================================================
    // Stage 5: Scripts (map events, resident, battle, monster AI, sandbox, inline)
    // =========================================================================
    private StageResult ImportScriptsStage(SqliteConnection conn, SqliteTransaction tx)
    {
        var candidates = DiscoverScriptCandidates();
        if (candidates.Count == 0)
        {
            return StageResult.Skipped("未找到脚本文件");
        }

        var parsed = ParseParallelOrdered(candidates, _jobs, ParseScriptCandidate);

        var sourceFiles = parsed
            .Where(p => p.FileSha256 is not null)
            .Select(p => (p.LogicalPath, p.FileSha256!))
            .ToList();
        RecordSource("scripts", "scripts", sourceFiles);

        var validScripts = new List<ParsedScript>();
        foreach (var p in parsed)
        {
            if (p.Error is not null || p.Script is null)
            {
                RecordIssue("scripts", "parse_error", p.LogicalPath, p.Error ?? "脚本解析失败");
                continue;
            }

            validScripts.Add(p.Script);
        }

        validScripts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));

        using var scCmd = conn.CreateCommand();
        scCmd.Transaction = tx;
        scCmd.CommandText =
            """
            INSERT INTO script (id, path, kind, name, title, format, submap_name, instruction_count, doc)
            VALUES ($id, $path, $kind, $name, $title, $fmt, $submap, $icnt, $doc)
            """;
        var pScId = scCmd.Parameters.Add("$id", SqliteType.Integer);
        var pScPath = scCmd.Parameters.Add("$path", SqliteType.Text);
        var pScKind = scCmd.Parameters.Add("$kind", SqliteType.Text);
        var pScName = scCmd.Parameters.Add("$name", SqliteType.Text);
        var pScTitle = scCmd.Parameters.Add("$title", SqliteType.Text);
        var pScFmt = scCmd.Parameters.Add("$fmt", SqliteType.Text);
        var pScSubmap = scCmd.Parameters.Add("$submap", SqliteType.Text);
        var pScIcnt = scCmd.Parameters.Add("$icnt", SqliteType.Integer);
        var pScDoc = scCmd.Parameters.Add("$doc", SqliteType.Text);

        using var insCmd = conn.CreateCommand();
        insCmd.Transaction = tx;
        insCmd.CommandText =
            """
            INSERT INTO script_instruction (script_id, ord, label, mnemonic, operands, type, comment)
            VALUES ($sid, $ord, $lbl, $mnem, $ops, $type, $cmt)
            """;
        var pInsSid = insCmd.Parameters.Add("$sid", SqliteType.Integer);
        var pInsOrd = insCmd.Parameters.Add("$ord", SqliteType.Integer);
        var pInsLbl = insCmd.Parameters.Add("$lbl", SqliteType.Text);
        var pInsMnem = insCmd.Parameters.Add("$mnem", SqliteType.Text);
        var pInsOps = insCmd.Parameters.Add("$ops", SqliteType.Text);
        var pInsType = insCmd.Parameters.Add("$type", SqliteType.Integer);
        var pInsCmt = insCmd.Parameters.Add("$cmt", SqliteType.Text);

        long totalRows = 0;
        for (var i = 0; i < validScripts.Count; i++)
        {
            var sc = validScripts[i];
            var scriptId = i + 1;

            // Register by file stem (raw and trimmed, for assets with trailing space before .json) and by internal Name.
            var stem = Path.GetFileNameWithoutExtension(sc.Path.Split('#')[0]);
            _scriptIdByKindAndName.TryAdd($"{sc.Kind}:{stem}", scriptId);
            var trimmedStem = stem.Trim();
            if (!string.IsNullOrEmpty(trimmedStem))
            {
                _scriptIdByKindAndName.TryAdd($"{sc.Kind}:{trimmedStem}", scriptId);
            }

            if (!string.IsNullOrWhiteSpace(sc.Name))
            {
                _scriptIdByKindAndName.TryAdd($"{sc.Kind}:{sc.Name.Trim()}", scriptId);
            }

            pScId.Value = scriptId;
            pScPath.Value = sc.Path;
            pScKind.Value = sc.Kind;
            pScName.Value = sc.Name;
            pScTitle.Value = sc.Title;
            pScFmt.Value = sc.Format;
            pScSubmap.Value = sc.SubmapName is not null ? sc.SubmapName : DBNull.Value;
            pScIcnt.Value = sc.Instructions.Count;
            pScDoc.Value = sc.DocJson;
            scCmd.ExecuteNonQuery();
            totalRows++;

            for (var ord = 0; ord < sc.Instructions.Count; ord++)
            {
                var ins = sc.Instructions[ord];
                pInsSid.Value = scriptId;
                pInsOrd.Value = ord;
                pInsLbl.Value = ins.Label;
                pInsMnem.Value = ins.Mnemonic;
                pInsOps.Value = ins.OperandsJson;
                pInsType.Value = ins.Type.HasValue ? ins.Type.Value : DBNull.Value;
                pInsCmt.Value = ins.Comment;
                insCmd.ExecuteNonQuery();
                totalRows++;

                if (ins.StringOperands.Count > 0)
                {
                    var isExplicit = ExplicitMessageMnemonics.Contains(ins.Mnemonic);
                    for (var opIdx = 0; opIdx < ins.StringOperands.Count; opIdx++)
                    {
                        var val = ins.StringOperands[opIdx];
                        if (string.IsNullOrWhiteSpace(val) || string.Equals(val, "None", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (isExplicit
                            || _textByKey.ContainsKey(val)
                            || val.StartsWith("MSG_", StringComparison.Ordinal))
                        {
                            _scriptTextCandidates.Add(new ScriptTextCandidate(
                                scriptId,
                                sc.Path,
                                ord,
                                ins.Mnemonic,
                                opIdx,
                                val,
                                isExplicit));
                        }
                    }
                }
            }
        }

        return StageResult.Ok(totalRows, $"{validScripts.Count} 个脚本");
    }

    private List<ScriptFileCandidate> DiscoverScriptCandidates()
    {
        var list = new List<ScriptFileCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        void AddDirFiles(string fullDir, string kind, string pattern, bool recursive, Func<string, string?> submapExtractor)
        {
            if (!Directory.Exists(fullDir))
            {
                return;
            }

            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(fullDir, pattern, opt).OrderBy(p => p, StringComparer.Ordinal))
            {
                var rel = DbParsers.ToRelativeUnixPath(_exportRoot, file);
                if (seenPaths.Add(rel))
                {
                    list.Add(new ScriptFileCandidate(rel, kind, submapExtractor(rel), file, null));
                }
            }
        }

        // 1. Resident map scripts.
        AddDirFiles(
            Path.Combine(_exportRoot, "Res", "Map", "Map_Script"),
            "map_resident",
            "*.json",
            recursive: true,
            _ => null);

        // 2. Map event scripts (Res/Map/<pkg>/<submap>/sc_*.json) + inline scripts in package.json.
        var mapRoot = Path.Combine(_exportRoot, "Res", "Map");
        if (Directory.Exists(mapRoot))
        {
            foreach (var pkgDir in Directory.EnumerateDirectories(mapRoot).OrderBy(p => p, StringComparer.Ordinal))
            {
                var pkgName = Path.GetFileName(pkgDir);
                if (string.Equals(pkgName, "Map_Script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var subDir in Directory.EnumerateDirectories(pkgDir).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var subName = Path.GetFileName(subDir);
                    foreach (var scFile in Directory.EnumerateFiles(subDir, "sc_*.json", SearchOption.TopDirectoryOnly)
                                 .OrderBy(p => p, StringComparer.Ordinal))
                    {
                        var rel = DbParsers.ToRelativeUnixPath(_exportRoot, scFile);
                        if (seenPaths.Add(rel))
                        {
                            list.Add(new ScriptFileCandidate(rel, "map_event", subName, scFile, null));
                        }
                    }
                }

                var pkgJson = Path.Combine(pkgDir, "package.json");
                if (File.Exists(pkgJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(pkgJson, Encoding.UTF8));
                        if (doc.RootElement.TryGetProperty("map", out var mapsEl) && mapsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var mEl in mapsEl.EnumerateArray())
                            {
                                var subName = DbParsers.GetStringProp(mEl, "name");
                                if (!mEl.TryGetProperty("script", out var scArr) || scArr.ValueKind != JsonValueKind.Array)
                                {
                                    continue;
                                }

                                foreach (var scEl in scArr.EnumerateArray())
                                {
                                    var scName = DbParsers.GetStringProp(scEl, "name");
                                    var inlineB64 = DbParsers.GetStringProp(scEl, "inline");
                                    var assetRel = DbParsers.GetStringProp(scEl, "asset");
                                    if (string.IsNullOrWhiteSpace(scName) || string.IsNullOrWhiteSpace(inlineB64))
                                    {
                                        continue;
                                    }

                                    var diskRel = string.IsNullOrWhiteSpace(assetRel)
                                        ? $"Res/Map/{pkgName}/{subName}/{scName}.json"
                                        : $"Res/Map/{pkgName}/{assetRel}.json";
                                    if (seenPaths.Contains(diskRel))
                                    {
                                        continue;
                                    }

                                    var inlineLogicalPath = $"Res/Map/{pkgName}/{subName}/{scName}.json#inline";
                                    if (seenPaths.Add(inlineLogicalPath))
                                    {
                                        list.Add(new ScriptFileCandidate(inlineLogicalPath, "map_event", subName, null, inlineB64));
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // package.json parse errors are already reported in the maps stage.
                    }
                }
            }
        }

        // 3. Battle scripts & common battle scripts.
        AddDirFiles(Path.Combine(_exportRoot, "Res", "Battle", "BattleScript"), "battle", "*.json", recursive: true, _ => null);
        AddDirFiles(Path.Combine(_exportRoot, "Common", "Res", "Battle", "CommonBattleScript"), "battle", "*.json", recursive: true, _ => null);

        // 4. Monster AI scripts (both FF1-4 EntryPoint and FF5-6 Segments).
        AddDirFiles(Path.Combine(_exportRoot, "Res", "Battle", "MonsterAI"), "monster_ai", "*.json", recursive: true, _ => null);

        // 5. Sandbox scripts.
        AddDirFiles(Path.Combine(_exportRoot, "Common", "Data", "Sandbox"), "sandbox", "*.json", recursive: true, _ => null);

        list.Sort((a, b) => StringComparer.Ordinal.Compare(a.LogicalPath, b.LogicalPath));
        return list;
    }

    private static ParsedScriptResult ParseScriptCandidate(ScriptFileCandidate candidate)
    {
        string? fileSha = null;
        try
        {
            string jsonText;
            if (candidate.FullDiskPath is not null)
            {
                fileSha = DbParsers.ComputeFileSha256(candidate.FullDiskPath);
                jsonText = File.ReadAllText(candidate.FullDiskPath, Encoding.UTF8);
            }
            else if (candidate.InlineBase64 is not null)
            {
                jsonText = Encoding.UTF8.GetString(Convert.FromBase64String(candidate.InlineBase64));
            }
            else
            {
                return new ParsedScriptResult(candidate.LogicalPath, null, null, "脚本源为空");
            }

            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedScriptResult(candidate.LogicalPath, fileSha, null, "脚本根节点不是 JSON 对象");
            }

            var fallbackName = Path.GetFileNameWithoutExtension(candidate.LogicalPath.Split('#')[0]);
            var name = DbParsers.GetStringProp(root, "Name", fallbackName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = fallbackName;
            }

            var title = ExtractScriptTitle(root);
            var format = root.TryGetProperty("Segments", out _)
                ? "Segments"
                : root.TryGetProperty("EntryPoint", out _)
                    ? "EntryPoint"
                    : "Unknown";

            var instructions = new List<ParsedScriptInstruction>();
            if (root.TryGetProperty("Mnemonics", out var mnemArr) && mnemArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var insEl in mnemArr.EnumerateArray())
                {
                    if (insEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var label = DbParsers.GetStringProp(insEl, "label");
                    var mnemonic = DbParsers.GetStringProp(insEl, "mnemonic");
                    var typeVal = DbParsers.GetNullableLongProp(insEl, "type");
                    var comment = DbParsers.GetStringProp(insEl, "comment");

                    var opsJson = "{}";
                    var stringOps = new List<string>();
                    if (insEl.TryGetProperty("operands", out var opsEl) && opsEl.ValueKind == JsonValueKind.Object)
                    {
                        opsJson = DbParsers.NormalizeJson(opsEl);
                        if (opsEl.TryGetProperty("sValues", out var sValsEl) && sValsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sv in sValsEl.EnumerateArray())
                            {
                                stringOps.Add(sv.ValueKind == JsonValueKind.String ? (sv.GetString() ?? string.Empty) : string.Empty);
                            }
                        }
                    }

                    instructions.Add(new ParsedScriptInstruction(
                        label,
                        mnemonic,
                        opsJson,
                        typeVal,
                        comment,
                        stringOps));
                }
            }

            var script = new ParsedScript(
                candidate.LogicalPath,
                candidate.Kind,
                name,
                title,
                format,
                candidate.SubmapName,
                DbParsers.NormalizeJson(root),
                instructions);
            return new ParsedScriptResult(candidate.LogicalPath, fileSha, script, null);
        }
        catch (Exception ex)
        {
            return new ParsedScriptResult(candidate.LogicalPath, fileSha, null, OutputGuard.TruncateError(ex.Message));
        }
    }

    private static string ExtractScriptTitle(JsonElement root)
    {
        if (!root.TryGetProperty("Title", out var titleEl))
        {
            return string.Empty;
        }

        if (titleEl.ValueKind == JsonValueKind.String)
        {
            return titleEl.GetString() ?? string.Empty;
        }

        if (titleEl.ValueKind == JsonValueKind.Object && titleEl.TryGetProperty("main", out var mainEl))
        {
            return mainEl.ValueKind == JsonValueKind.String ? (mainEl.GetString() ?? string.Empty) : mainEl.GetRawText();
        }

        return string.Empty;
    }

    private sealed class SubmapAccumulator(string packageName, string name)
    {
        public string PackageName { get; } = packageName;
        public string Name { get; } = name;
        public string? DiskDirectory { get; set; }
        public string? TilemapAssetHint { get; set; }
        public string? TilemapPath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int TileWidth { get; set; } = 16;
        public int TileHeight { get; set; } = 16;
        public string? SubmapDoc { get; set; }
        public string? PackageEntryDoc { get; set; }
        public Dictionary<string, string> InlineEntities { get; } = new(StringComparer.Ordinal);
        public List<ParsedMapLayer> Layers { get; } = new();
        public List<ParsedMapGrid> Grids { get; } = new();
        public List<ParsedMapObject> Objects { get; } = new();
    }

    private sealed record ParsedMapPackage(string Name, string AssetGroupName, string Path, string DocJson);

    private sealed record ParsedMapLayer(
        string SourceFile,
        string LayerKind,
        string LayerName,
        string TiledType,
        int Width,
        int Height,
        bool Visible,
        double Opacity,
        string DocJson,
        int Order);

    private sealed record ParsedMapGrid(
        string LayerKind,
        string LayerName,
        int Width,
        int Height,
        byte[] Data);

    private sealed record ParsedMapObject(
        string SourceFile,
        string LayerName,
        int TiledId,
        string Name,
        string TiledType,
        long? ObjectType,
        long? Gid,
        double PxX,
        double PxY,
        double Width,
        double Height,
        long? EntityId,
        long? ContentId,
        long? ContentNum,
        long? ScriptId,
        long? FlagId,
        string? MessageKey,
        string PropertiesJson,
        string DocJson,
        int Order);

    private sealed record ParsedSubmap(
        string PackageName,
        string Name,
        int Width,
        int Height,
        int TileWidth,
        int TileHeight,
        string? TilemapPath,
        string DocJson,
        IReadOnlyList<ParsedMapLayer> Layers,
        IReadOnlyList<ParsedMapGrid> Grids,
        IReadOnlyList<ParsedMapObject> Objects);

    private sealed record ParsedMapPackageResult(
        ParsedMapPackage? Package,
        IReadOnlyList<ParsedSubmap> Submaps,
        IReadOnlyList<(string RelativePath, string FileSha256)> SourceFiles,
        IReadOnlyList<DbBuildIssue> Issues);

    private sealed record ScriptFileCandidate(
        string LogicalPath,
        string Kind,
        string? SubmapName,
        string? FullDiskPath,
        string? InlineBase64);

    private sealed record ParsedScriptInstruction(
        string Label,
        string Mnemonic,
        string OperandsJson,
        long? Type,
        string Comment,
        IReadOnlyList<string> StringOperands);

    private sealed record ParsedScript(
        string Path,
        string Kind,
        string Name,
        string Title,
        string Format,
        string? SubmapName,
        string DocJson,
        IReadOnlyList<ParsedScriptInstruction> Instructions);

    private sealed record ParsedScriptResult(
        string LogicalPath,
        string? FileSha256,
        ParsedScript? Script,
        string? Error);
}
