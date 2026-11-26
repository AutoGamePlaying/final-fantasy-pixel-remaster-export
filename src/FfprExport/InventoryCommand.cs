using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FfprExport;

internal static class InventoryCommand
{
    private sealed record InventoryAssetRow(
        string SourceKind,
        string SourceFile,
        long SourceSize,
        string AssetsFileName,
        long PathId,
        string? ContainerPath,
        int TypeId,
        string AssetType,
        string? ScriptClass,
        string? Name,
        long ByteSize,
        bool TypeTreeEnabled,
        string Decision,
        string Category);

    private sealed record BundleInventorySummary(
        string File,
        long Size,
        bool ExistsOnDisk,
        bool TypeTreeEnabled,
        int AssetsFileCount,
        int CatalogEntryCount,
        int AssetCount,
        int TextAssetCount,
        int ExportableTextAssetCount,
        int MonoBehaviourCount,
        string? Error);

    private sealed record InventoryFailure(
        string SourceFile,
        string Error);

    public static async Task<int> RunAsync(string[] args)
    {
        string? gameArgument = null;
        string? outputArgument = null;
        var force = false;
        var jobs = Math.Clamp(Environment.ProcessorCount, 1, 8);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game":
                    gameArgument = Program.RequireValue(args, ref i, "--game");
                    break;
                case "--out":
                    outputArgument = Program.RequireValue(args, ref i, "--out");
                    break;
                case "--jobs":
                    var rawJobs = Program.RequireValue(args, ref i, "--jobs");
                    if (!int.TryParse(rawJobs, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobs) || jobs < 1)
                    {
                        throw new ArgumentException(L.Format("Cli_JobsMustBePositive", rawJobs));
                    }

                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    throw new ArgumentException(L.Format("Inventory_UnknownArg", args[i]));
            }
        }

        var game = GameDetector.Detect(gameArgument ?? string.Empty);
        outputArgument ??= OutputGuard.GetDefaultInventoryDirectory(game.Spec.Id);
        var outputPath = OutputGuard.ValidateOutputPath(outputArgument, game.GameDirectory);
        OutputGuard.PrepareCleanOutputDirectory(outputPath, outputArgument, force, "inventory");

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var catalog = AddressablesCatalog.Load(game);
        var classDatabase = UnityClassDatabase.CreateBuiltIn2019Bytes();
        var scriptIndex = UnityBundleHelper.BuildMonoScriptIndex(game, catalog.Bundles, classDatabase, jobs);
        var dataFiles = UnityBundleHelper.FindDataAssetsFiles(game.DataDirectory);

        var bundleResults = new ConcurrentBag<(BundleInventorySummary Summary, List<InventoryAssetRow> Rows)>();
        var failures = new ConcurrentBag<InventoryFailure>();

        // Inspect all bundles referenced by catalog.json in parallel.
        Parallel.ForEach(
            catalog.Bundles,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            bundle =>
            {
                var rows = new List<InventoryAssetRow>();
                if (!bundle.ExistsOnDisk)
                {
                    var err = L.Format("Inventory_MissingBundle", bundle.RelativePath);
                    failures.Add(new InventoryFailure(bundle.RelativePath, err));
                    bundleResults.Add((new BundleInventorySummary(
                        bundle.RelativePath, 0, false, false, 0, bundle.CatalogAssets.Count, 0, 0, 0, 0, err), rows));
                    return;
                }

                var manager = UnityBundleHelper.CreateConfiguredManager(classDatabase);
                try
                {
                    var bunInst = manager.LoadBundleFile(bundle.FullPath, true);
                    var assetsFileCount = 0;
                    var anyTypeTree = false;
                    var textCount = 0;
                    var exportTextCount = 0;
                    var mbCount = 0;

                    for (var i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    {
                        var dirInfo = bunInst.file.BlockAndDirInfo.DirectoryInfos[i];
                        if (UnityBundleHelper.IsResourceDataEntry(dirInfo.Name))
                        {
                            continue;
                        }

                        var aInst = manager.LoadAssetsFileFromBundle(bunInst, i, false);
                        if (aInst is null)
                        {
                            continue;
                        }

                        assetsFileCount++;
                        var af = aInst.file;
                        if (af.Metadata.TypeTreeEnabled)
                        {
                            anyTypeTree = true;
                        }

                        var containerMap = UnityBundleHelper.ReadContainerMap(manager, aInst);
                        var localScripts = UnityBundleHelper.ReadLocalMonoScripts(manager, aInst);

                        foreach (var info in af.AssetInfos)
                        {
                            containerMap.TryGetValue(info.PathId, out var containerPath);
                            var typeName = AssetClassifier.GetTypeName(info.TypeId);
                            var classification = AssetClassifier.Classify(info.TypeId, containerPath);
                            string? scriptClass = null;

                            if (info.TypeId == (int)AssetClassID.TextAsset)
                            {
                                textCount++;
                                if (classification.Decision == AssetClassifier.DecisionExportText)
                                {
                                    exportTextCount++;
                                }
                            }
                            else if (info.TypeId == (int)AssetClassID.MonoBehaviour)
                            {
                                mbCount++;
                                scriptClass = TryReadMonoBehaviourScriptName(
                                    manager, aInst, info, localScripts, scriptIndex);
                            }

                            var assetName = UnityClassDatabase.TryReadAssetName(manager, aInst, info);
                            rows.Add(new InventoryAssetRow(
                                "bundle",
                                bundle.RelativePath,
                                bundle.FileSize,
                                dirInfo.Name,
                                info.PathId,
                                containerPath,
                                info.TypeId,
                                typeName,
                                scriptClass,
                                assetName,
                                info.ByteSize,
                                af.Metadata.TypeTreeEnabled,
                                classification.Decision,
                                classification.Category));
                        }
                    }

                    bundleResults.Add((new BundleInventorySummary(
                        bundle.RelativePath,
                        bundle.FileSize,
                        true,
                        anyTypeTree,
                        assetsFileCount,
                        bundle.CatalogAssets.Count,
                        rows.Count,
                        textCount,
                        exportTextCount,
                        mbCount,
                        null), rows));
                }
                catch (Exception ex)
                {
                    var err = OutputGuard.TruncateError(ex.Message);
                    failures.Add(new InventoryFailure(bundle.RelativePath, err));
                    Console.Error.WriteLine(L.Format("Inventory_BundleFailed", bundle.RelativePath, ex.Message));
                    bundleResults.Add((new BundleInventorySummary(
                        bundle.RelativePath, bundle.FileSize, true, false, 0, bundle.CatalogAssets.Count, 0, 0, 0, 0, err), rows));
                }
                finally
                {
                    manager.UnloadAll(true);
                }
            });

        // Inspect *_Data files (resources.assets, sharedassets*.assets, level*).
        var dataRows = new List<InventoryAssetRow>();
        var dataFileSummaries = new List<object>();
        foreach (var dataFile in dataFiles)
        {
            var manager = UnityBundleHelper.CreateConfiguredManager(classDatabase);
            var fileRows = new List<InventoryAssetRow>();
            try
            {
                var aInst = manager.LoadAssetsFile(dataFile.FullPath, false);
                var af = aInst.file;
                var localScripts = UnityBundleHelper.ReadLocalMonoScripts(manager, aInst);

                foreach (var info in af.AssetInfos)
                {
                    var typeName = AssetClassifier.GetTypeName(info.TypeId);
                    var classification = AssetClassifier.Classify(info.TypeId, null);
                    string? scriptClass = null;

                    if (info.TypeId == (int)AssetClassID.MonoBehaviour)
                    {
                        scriptClass = TryReadMonoBehaviourScriptName(
                            manager, aInst, info, localScripts, scriptIndex);
                    }

                    var assetName = UnityClassDatabase.TryReadAssetName(manager, aInst, info);
                    fileRows.Add(new InventoryAssetRow(
                        "data",
                        dataFile.FileName,
                        dataFile.FileSize,
                        dataFile.FileName,
                        info.PathId,
                        null,
                        info.TypeId,
                        typeName,
                        scriptClass,
                        assetName,
                        info.ByteSize,
                        af.Metadata.TypeTreeEnabled,
                        classification.Decision,
                        classification.Category));
                }

                dataRows.AddRange(fileRows);
                dataFileSummaries.Add(new
                {
                    file = dataFile.FileName,
                    size = dataFile.FileSize,
                    typeTreeEnabled = af.Metadata.TypeTreeEnabled,
                    assetCount = fileRows.Count,
                    textAssetCount = fileRows.Count(r => r.TypeId == (int)AssetClassID.TextAsset),
                    monoBehaviourCount = fileRows.Count(r => r.TypeId == (int)AssetClassID.MonoBehaviour),
                });
            }
            catch (Exception ex)
            {
                var err = OutputGuard.TruncateError(ex.Message);
                failures.Add(new InventoryFailure(dataFile.FileName, err));
                Console.Error.WriteLine(L.Format("Inventory_DataFileFailed", dataFile.FileName, ex.Message));
                dataFileSummaries.Add(new
                {
                    file = dataFile.FileName,
                    size = dataFile.FileSize,
                    typeTreeEnabled = false,
                    assetCount = 0,
                    textAssetCount = 0,
                    monoBehaviourCount = 0,
                    error = err,
                });
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }

        // Order everything deterministically before writing inventory.jsonl and summary.json.
        var orderedBundles = bundleResults
            .OrderBy(x => x.Summary.File, StringComparer.Ordinal)
            .ToArray();

        var allRows = new List<InventoryAssetRow>(orderedBundles.Sum(b => b.Rows.Count) + dataRows.Count);
        foreach (var b in orderedBundles)
        {
            allRows.AddRange(b.Rows.OrderBy(r => r.AssetsFileName, StringComparer.Ordinal).ThenBy(r => r.PathId));
        }

        allRows.AddRange(dataRows.OrderBy(r => r.SourceFile, StringComparer.Ordinal).ThenBy(r => r.PathId));

        var jsonlPath = Path.Combine(outputPath, "inventory.jsonl");
        await using (var writer = TextFileWriter.CreateStreamWriter(jsonlPath, append: false))
        {
            foreach (var row in allRows)
            {
                var lineObj = new
                {
                    sourceKind = row.SourceKind,
                    sourceFile = row.SourceFile,
                    sourceSize = row.SourceSize,
                    assetsFile = row.AssetsFileName,
                    pathId = row.PathId,
                    containerPath = row.ContainerPath,
                    assetType = row.AssetType,
                    scriptClass = row.ScriptClass,
                    name = row.Name,
                    byteSize = row.ByteSize,
                    typeTreeEnabled = row.TypeTreeEnabled,
                    decision = row.Decision,
                    category = row.Category,
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(lineObj, FieldJsonWriter.CompactSerializerOptions));
            }
        }

        var byType = allRows
            .GroupBy(r => (r.AssetType, r.Decision, r.Category))
            .Select(g =>
            {
                var sample = g.First();
                var cls = AssetClassifier.Classify(sample.TypeId, sample.ContainerPath);
                return new
                {
                    assetType = g.Key.AssetType,
                    decision = g.Key.Decision,
                    category = g.Key.Category,
                    reason = cls.Reason,
                    count = g.Count(),
                    byteSize = g.Sum(r => r.ByteSize),
                };
            })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.assetType, StringComparer.Ordinal)
            .ToArray();

        var byDirectory = allRows
            .GroupBy(r => AssetClassifier.ExtractDirectoryBucket(r.ContainerPath, r.SourceKind, r.SourceFile), StringComparer.Ordinal)
            .Select(g => new
            {
                directory = g.Key,
                count = g.Count(),
                byteSize = g.Sum(r => r.ByteSize),
                exportCount = g.Count(r => AssetClassifier.IsExportDecision(r.Decision)),
                exportByteSize = g.Where(r => AssetClassifier.IsExportDecision(r.Decision)).Sum(r => r.ByteSize),
            })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.directory, StringComparer.Ordinal)
            .ToArray();

        var byDecision = allRows
            .GroupBy(r => r.Decision, StringComparer.Ordinal)
            .Select(g => new
            {
                decision = g.Key,
                count = g.Count(),
                byteSize = g.Sum(r => r.ByteSize),
            })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.decision, StringComparer.Ordinal)
            .ToArray();

        var mbRows = allRows.Where(r => r.TypeId == (int)AssetClassID.MonoBehaviour).ToArray();
        var mbWithTypeTree = mbRows.Count(r => r.TypeTreeEnabled);
        var mbWithoutTypeTree = mbRows.Length - mbWithTypeTree;

        var orderedFailures = failures
            .OrderBy(f => f.SourceFile, StringComparer.Ordinal)
            .Select(f => new { sourceFile = f.SourceFile, error = f.Error })
            .ToArray();

        stopwatch.Stop();
        var finishedAt = DateTimeOffset.UtcNow;

        var summary = new
        {
            tool = OutputGuard.ToolName,
            toolVersion = Program.ToolVersion,
            assetsToolsVersion = Program.AssetsToolsVersion,
            assetsToolsCpp2IlVersion = Program.AssetsToolsCpp2IlVersion,
            command = "inventory",
            game = new
            {
                id = game.Spec.Id,
                title = game.Spec.Title,
                appId = game.Spec.AppId,
                dataDirName = game.Spec.DataDirName,
            },
            steamBuildId = game.SteamBuildId,
            unityVersion = game.UnityVersion,
            startedAt = OutputGuard.FormatUtc(startedAt),
            finishedAt = OutputGuard.FormatUtc(finishedAt),
            elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            catalog = new
            {
                locatorId = catalog.LocatorId,
                internalIdCount = catalog.TotalInternalIds,
                entryCount = catalog.TotalEntries,
                bundleCount = catalog.Bundles.Count,
                bundleTotalBytes = catalog.Bundles.Sum(b => b.FileSize),
                nonBundleEntryCount = catalog.NonBundleEntries.Count,
                unindexedBundlesOnDisk = catalog.UnindexedBundlesOnDisk,
                encryptedFontBundles = catalog.EncryptedFontBundles,
            },
            totals = new
            {
                bundleCount = orderedBundles.Length,
                dataFileCount = dataFiles.Count,
                totalAssetCount = allRows.Count,
                totalAssetBytes = allRows.Sum(r => r.ByteSize),
                exportAssetCount = allRows.Count(r => AssetClassifier.IsExportDecision(r.Decision)),
                exportAssetBytes = allRows.Where(r => AssetClassifier.IsExportDecision(r.Decision)).Sum(r => r.ByteSize),
                skipAssetCount = allRows.Count(r => !AssetClassifier.IsExportDecision(r.Decision)),
                skipAssetBytes = allRows.Where(r => !AssetClassifier.IsExportDecision(r.Decision)).Sum(r => r.ByteSize),
                monoBehaviourWithTypeTree = mbWithTypeTree,
                monoBehaviourWithoutTypeTree = mbWithoutTypeTree,
                failureCount = orderedFailures.Length,
            },
            byDecision,
            byType,
            byDirectory,
            dataFiles = dataFileSummaries,
            bundles = orderedBundles.Select(b => new
            {
                file = b.Summary.File,
                size = b.Summary.Size,
                existsOnDisk = b.Summary.ExistsOnDisk,
                typeTreeEnabled = b.Summary.TypeTreeEnabled,
                assetsFileCount = b.Summary.AssetsFileCount,
                catalogEntryCount = b.Summary.CatalogEntryCount,
                assetCount = b.Summary.AssetCount,
                textAssetCount = b.Summary.TextAssetCount,
                exportableTextAssetCount = b.Summary.ExportableTextAssetCount,
                monoBehaviourCount = b.Summary.MonoBehaviourCount,
                error = b.Summary.Error,
            }).ToArray(),
            failures = orderedFailures,
        };

        TextFileWriter.WriteJsonAtomic(
            Path.Combine(outputPath, "summary.json"),
            summary,
            FieldJsonWriter.SerializerOptions);

        var manifest = new
        {
            tool = OutputGuard.ToolName,
            toolVersion = Program.ToolVersion,
            assetsToolsVersion = Program.AssetsToolsVersion,
            command = "inventory",
            status = "completed",
            game = new
            {
                id = game.Spec.Id,
                title = game.Spec.Title,
                appId = game.Spec.AppId,
                dataDirName = game.Spec.DataDirName,
            },
            steamBuildId = game.SteamBuildId,
            unityVersion = game.UnityVersion,
            startedAt = OutputGuard.FormatUtc(startedAt),
            finishedAt = OutputGuard.FormatUtc(finishedAt),
            elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            bundleCount = orderedBundles.Length,
            dataFileCount = dataFiles.Count,
            totalAssetCount = allRows.Count,
            failureCount = orderedFailures.Length,
        };

        TextFileWriter.WriteJsonAtomic(
            Path.Combine(outputPath, "manifest.json"),
            manifest,
            FieldJsonWriter.SerializerOptions);

        Console.WriteLine(L.Format(
            "Inventory_Completed",
            game.Spec.Id,
            game.UnityVersion,
            game.SteamBuildId ?? L.Get("Common_Unknown"),
            orderedBundles.Length,
            dataFiles.Count,
            allRows.Count,
            orderedFailures.Length,
            stopwatch.Elapsed.TotalSeconds,
            outputArgument));

        return orderedFailures.Length == 0 ? 0 : 1;
    }

    private static string? TryReadMonoBehaviourScriptName(
        AssetsManager manager,
        AssetsFileInstance aInst,
        AssetFileInfo info,
        IReadOnlyDictionary<long, MonoScriptIdentity> localScripts,
        BundleMonoScriptIndex externalIndex)
    {
        try
        {
            var af = aInst.file;
            AssetTypeValueField baseField;
            if (af.Metadata.TypeTreeEnabled)
            {
                // Read just the first 3 fields (m_GameObject, m_Enabled, m_Script) via ClassDatabase
                // so inventory stays fast without deserializing large MonoBehaviour payloads.
                baseField = manager.GetBaseField(aInst, info, AssetReadFlags.ForceFromCldb | AssetReadFlags.SkipMonoBehaviourFields);
            }
            else
            {
                baseField = manager.GetBaseField(aInst, info, AssetReadFlags.SkipMonoBehaviourFields);
            }

            var fileId = baseField["m_Script.m_FileID"].AsInt;
            var pathId = baseField["m_Script.m_PathID"].AsLong;
            return UnityBundleHelper.ResolveMonoBehaviourScript(af, fileId, pathId, localScripts, externalIndex)?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
