using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FfprExport;

internal static class ExportCommand
{
    private sealed class TypeExportStats
    {
        public required string AssetType { get; init; }
        public required string Decision { get; init; }
        public required string Category { get; init; }
        public required string Reason { get; init; }
        public int TotalCount;
        public int ExportedCount;
        public int NewlyExportedCount;
        public int ResumedCount;
        public int SkippedCount;
        public int FailedCount;
        public long SourceBytes;
        public long OutputBytes;
    }

    private sealed record ExportFailure(
        string SourceFile,
        string? ContainerPath,
        string AssetType,
        long PathId,
        string Error);

    private sealed record ExportedMonoBehaviourItem(
        string AssetsFile,
        long PathId,
        long GameObjectPathId,
        string? GameObjectName,
        string? ContainerPath,
        string Name,
        string? ScriptAssembly,
        string? ScriptNamespace,
        string? ScriptClass,
        string TypeSource,
        AssetTypeValueField Field);

    public static Task<int> RunAsync(string[] args)
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
                        throw new ArgumentException($"--jobs 必须是正整数：{rawJobs}");
                    }

                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    throw new ArgumentException($"export 收到未知参数：{args[i]}");
            }
        }

        var game = GameDetector.Detect(gameArgument ?? string.Empty);
        outputArgument ??= OutputGuard.GetDefaultExportDirectory(game.Spec.Id);
        var outputPath = OutputGuard.ValidateOutputPath(outputArgument, game.GameDirectory);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        OutputGuard.PrepareResumableOutputDirectory(outputPath, outputArgument, force, "export", game.Spec.Id);

        // Write an in-progress sentinel manifest immediately so an interrupted run can safely resume or --force clean.
        WriteSentinelManifest(outputPath, game, startedAt, jobs);

        var catalog = AddressablesCatalog.Load(game);
        var classDatabase = UnityClassDatabase.CreateBuiltIn2019Bytes();
        var scriptIndex = UnityBundleHelper.BuildMonoScriptIndex(game, catalog.Bundles, classDatabase, jobs);
        var dataFiles = UnityBundleHelper.FindDataAssetsFiles(game.DataDirectory);

        SafeCpp2IlGenerator? cpp2Il = null;
        if (game.GlobalMetadataPath is not null && game.GameAssemblyPath is not null)
        {
            cpp2Il = new SafeCpp2IlGenerator(game.GlobalMetadataPath, game.GameAssemblyPath, game.UnityVersion);
        }

        var trackers = new ConcurrentDictionary<(string AssetType, string Decision), TypeExportStats>();
        var failures = new ConcurrentBag<ExportFailure>();
        var mbTypeTreeCount = 0;
        var mbCpp2IlCount = 0;
        var mbBaseOnlyCount = 0;
        var filesWritten = 0;
        var filesResumed = 0;

        TypeExportStats GetTracker(int typeId, string? containerPath)
        {
            var typeName = AssetClassifier.GetTypeName(typeId);
            var cls = AssetClassifier.Classify(typeId, containerPath);
            return trackers.GetOrAdd((typeName, cls.Decision), _ => new TypeExportStats
            {
                AssetType = typeName,
                Decision = cls.Decision,
                Category = cls.Category,
                Reason = cls.Reason,
            });
        }

        try
        {
            // 1. Export Addressables bundles in parallel.
            Parallel.ForEach(
                catalog.Bundles,
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                bundle =>
                {
                    if (!bundle.ExistsOnDisk)
                    {
                        var err = $"目录中声明的 bundle 文件不存在：{bundle.RelativePath}";
                        failures.Add(new ExportFailure(bundle.RelativePath, null, "AssetBundle", 0, err));
                        Console.Error.WriteLine($"导出失败：{bundle.RelativePath}：{err}");
                        return;
                    }

                    var manager = UnityBundleHelper.CreateConfiguredManager(classDatabase, cpp2Il);
                    try
                    {
                        var bunInst = manager.LoadBundleFile(bundle.FullPath, true);
                        var bundleStem = Path.GetFileNameWithoutExtension(bundle.FullPath);
                        var groupedMbTarget = OutputGuard.BuildSafeTargetPath(
                            outputPath,
                            $"_monobehaviour/bundles/{bundleStem}.json");
                        var groupedMbExistsOnResume = !force && File.Exists(groupedMbTarget);
                        var groupedMbItems = new List<ExportedMonoBehaviourItem>();
                        var groupedMbResumedCount = 0;

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

                            var af = aInst.file;
                            var containerMap = UnityBundleHelper.ReadContainerMap(manager, aInst);
                            var localScripts = UnityBundleHelper.ReadLocalMonoScripts(manager, aInst);
                            Dictionary<long, string>? gameObjectNames = null;

                            foreach (var info in af.AssetInfos)
                            {
                                containerMap.TryGetValue(info.PathId, out var containerPath);
                                var tracker = GetTracker(info.TypeId, containerPath);
                                Interlocked.Increment(ref tracker.TotalCount);
                                Interlocked.Add(ref tracker.SourceBytes, info.ByteSize);

                                if (tracker.Decision == AssetClassifier.DecisionExportText)
                                {
                                    ExportBundleTextAsset(
                                        manager,
                                        aInst,
                                        info,
                                        bundle.RelativePath,
                                        bundleStem,
                                        containerPath,
                                        outputPath,
                                        force,
                                        tracker,
                                        failures,
                                        ref filesWritten,
                                        ref filesResumed);
                                }
                                else if (tracker.Decision == AssetClassifier.DecisionExportJson)
                                {
                                    var isStandaloneContainerMb = !string.IsNullOrEmpty(containerPath);
                                    if (isStandaloneContainerMb)
                                    {
                                        ExportStandaloneMonoBehaviour(
                                            manager,
                                            aInst,
                                            info,
                                            bundle.RelativePath,
                                            containerPath!,
                                            localScripts,
                                            scriptIndex,
                                            cpp2Il,
                                            game.UnityVersion,
                                            outputPath,
                                            force,
                                            tracker,
                                            failures,
                                            ref mbTypeTreeCount,
                                            ref mbCpp2IlCount,
                                            ref mbBaseOnlyCount,
                                            ref filesWritten,
                                            ref filesResumed);
                                    }
                                    else if (groupedMbExistsOnResume)
                                    {
                                        Interlocked.Increment(ref tracker.ExportedCount);
                                        Interlocked.Increment(ref tracker.ResumedCount);
                                        groupedMbResumedCount++;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            gameObjectNames ??= UnityBundleHelper.ReadGameObjectNames(manager, aInst);
                                            var (field, typeSource, script) = UnityBundleHelper.DeserializeMonoBehaviour(
                                                manager, aInst, info, localScripts, scriptIndex, cpp2Il, game.UnityVersion);
                                            RecordTypeSource(typeSource, ref mbTypeTreeCount, ref mbCpp2IlCount, ref mbBaseOnlyCount);

                                            var goPathId = field["m_GameObject.m_PathID"].AsLong;
                                            var goName = goPathId != 0 ? gameObjectNames.GetValueOrDefault(goPathId) : null;
                                            var name = field["m_Name"].AsString ?? string.Empty;

                                            groupedMbItems.Add(new ExportedMonoBehaviourItem(
                                                dirInfo.Name,
                                                info.PathId,
                                                goPathId,
                                                goName,
                                                containerPath,
                                                name,
                                                script?.AssemblyName,
                                                script?.Namespace,
                                                script?.ClassName,
                                                typeSource,
                                                field));
                                        }
                                        catch (Exception ex)
                                        {
                                            Interlocked.Increment(ref tracker.FailedCount);
                                            failures.Add(new ExportFailure(
                                                bundle.RelativePath, containerPath, "MonoBehaviour", info.PathId, OutputGuard.TruncateError(ex.Message)));
                                            Console.Error.WriteLine(
                                                $"导出 MonoBehaviour 失败：{bundle.RelativePath} (PathId={info.PathId})：{ex.Message}");
                                        }
                                    }
                                }
                                else
                                {
                                    Interlocked.Increment(ref tracker.SkippedCount);
                                }
                            }
                        }

                        if (groupedMbExistsOnResume && groupedMbResumedCount > 0)
                        {
                            var existingSize = new FileInfo(groupedMbTarget).Length;
                            var mbTracker = GetTracker((int)AssetClassID.MonoBehaviour, null);
                            Interlocked.Add(ref mbTracker.OutputBytes, existingSize);
                            Interlocked.Increment(ref filesResumed);
                        }
                        else if (groupedMbItems.Count > 0)
                        {
                            var bytesWritten = WriteGroupedMonoBehaviours(
                                groupedMbTarget,
                                bundle.RelativePath,
                                groupedMbItems);
                            var mbTracker = GetTracker((int)AssetClassID.MonoBehaviour, null);
                            Interlocked.Add(ref mbTracker.ExportedCount, groupedMbItems.Count);
                            Interlocked.Add(ref mbTracker.NewlyExportedCount, groupedMbItems.Count);
                            Interlocked.Add(ref mbTracker.OutputBytes, bytesWritten);
                            Interlocked.Increment(ref filesWritten);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new ExportFailure(
                            bundle.RelativePath, null, "AssetBundle", 0, OutputGuard.TruncateError(ex.Message)));
                        Console.Error.WriteLine($"打开或导出 bundle 失败：{bundle.RelativePath}：{ex.Message}");
                    }
                    finally
                    {
                        manager.UnloadAll(true);
                    }
                });

            // 2. Export *_Data files (resources.assets, sharedassets*.assets, level*).
            foreach (var dataFile in dataFiles)
            {
                var manager = UnityBundleHelper.CreateConfiguredManager(classDatabase, cpp2Il);
                try
                {
                    var aInst = manager.LoadAssetsFile(dataFile.FullPath, false);
                    var af = aInst.file;
                    var localScripts = UnityBundleHelper.ReadLocalMonoScripts(manager, aInst);
                    Dictionary<long, string>? gameObjectNames = null;

                    var groupedMbTarget = OutputGuard.BuildSafeTargetPath(
                        outputPath,
                        $"_monobehaviour/data/{dataFile.FileName}.json");
                    var groupedMbExistsOnResume = !force && File.Exists(groupedMbTarget);
                    var groupedMbItems = new List<ExportedMonoBehaviourItem>();
                    var groupedMbResumedCount = 0;

                    foreach (var info in af.AssetInfos)
                    {
                        var tracker = GetTracker(info.TypeId, null);
                        Interlocked.Increment(ref tracker.TotalCount);
                        Interlocked.Add(ref tracker.SourceBytes, info.ByteSize);

                        if (tracker.Decision == AssetClassifier.DecisionExportText)
                        {
                            ExportDataTextAsset(
                                manager,
                                aInst,
                                info,
                                dataFile.FileName,
                                outputPath,
                                force,
                                tracker,
                                failures,
                                ref filesWritten,
                                ref filesResumed);
                        }
                        else if (tracker.Decision == AssetClassifier.DecisionExportJson)
                        {
                            if (groupedMbExistsOnResume)
                            {
                                Interlocked.Increment(ref tracker.ExportedCount);
                                Interlocked.Increment(ref tracker.ResumedCount);
                                groupedMbResumedCount++;
                            }
                            else
                            {
                                try
                                {
                                    gameObjectNames ??= UnityBundleHelper.ReadGameObjectNames(manager, aInst);
                                    var (field, typeSource, script) = UnityBundleHelper.DeserializeMonoBehaviour(
                                        manager, aInst, info, localScripts, scriptIndex, cpp2Il, game.UnityVersion);
                                    RecordTypeSource(typeSource, ref mbTypeTreeCount, ref mbCpp2IlCount, ref mbBaseOnlyCount);

                                    var goPathId = field["m_GameObject.m_PathID"].AsLong;
                                    var goName = goPathId != 0 ? gameObjectNames.GetValueOrDefault(goPathId) : null;
                                    var name = field["m_Name"].AsString ?? string.Empty;

                                    groupedMbItems.Add(new ExportedMonoBehaviourItem(
                                        dataFile.FileName,
                                        info.PathId,
                                        goPathId,
                                        goName,
                                        null,
                                        name,
                                        script?.AssemblyName,
                                        script?.Namespace,
                                        script?.ClassName,
                                        typeSource,
                                        field));
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref tracker.FailedCount);
                                    failures.Add(new ExportFailure(
                                        dataFile.FileName, null, "MonoBehaviour", info.PathId, OutputGuard.TruncateError(ex.Message)));
                                    Console.Error.WriteLine(
                                        $"导出 MonoBehaviour 失败：{dataFile.FileName} (PathId={info.PathId})：{ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref tracker.SkippedCount);
                        }
                    }

                    if (groupedMbExistsOnResume && groupedMbResumedCount > 0)
                    {
                        var existingSize = new FileInfo(groupedMbTarget).Length;
                        var mbTracker = GetTracker((int)AssetClassID.MonoBehaviour, null);
                        Interlocked.Add(ref mbTracker.OutputBytes, existingSize);
                        Interlocked.Increment(ref filesResumed);
                    }
                    else if (groupedMbItems.Count > 0)
                    {
                        var bytesWritten = WriteGroupedMonoBehaviours(
                            groupedMbTarget,
                            dataFile.FileName,
                            groupedMbItems);
                        var mbTracker = GetTracker((int)AssetClassID.MonoBehaviour, null);
                        Interlocked.Add(ref mbTracker.ExportedCount, groupedMbItems.Count);
                        Interlocked.Add(ref mbTracker.NewlyExportedCount, groupedMbItems.Count);
                        Interlocked.Add(ref mbTracker.OutputBytes, bytesWritten);
                        Interlocked.Increment(ref filesWritten);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(new ExportFailure(
                        dataFile.FileName, null, "AssetsFile", 0, OutputGuard.TruncateError(ex.Message)));
                    Console.Error.WriteLine($"打开或导出数据文件失败：{dataFile.FileName}：{ex.Message}");
                }
                finally
                {
                    manager.UnloadAll(true);
                }
            }
        }
        finally
        {
            cpp2Il?.Dispose();
        }

        stopwatch.Stop();
        var finishedAt = DateTimeOffset.UtcNow;

        var orderedTrackers = trackers.Values
            .OrderByDescending(t => t.TotalCount)
            .ThenBy(t => t.AssetType, StringComparer.Ordinal)
            .ToArray();

        var orderedFailures = failures
            .OrderBy(f => f.SourceFile, StringComparer.Ordinal)
            .ThenBy(f => f.PathId)
            .Select(f => new
            {
                sourceFile = f.SourceFile,
                containerPath = f.ContainerPath,
                assetType = f.AssetType,
                pathId = f.PathId,
                error = f.Error,
            })
            .ToArray();

        var byDecision = orderedTrackers
            .GroupBy(t => t.Decision, StringComparer.Ordinal)
            .Select(g => new
            {
                decision = g.Key,
                totalCount = g.Sum(x => x.TotalCount),
                exportedCount = g.Sum(x => x.ExportedCount),
                newlyExportedCount = g.Sum(x => x.NewlyExportedCount),
                resumedCount = g.Sum(x => x.ResumedCount),
                skippedCount = g.Sum(x => x.SkippedCount),
                failedCount = g.Sum(x => x.FailedCount),
                sourceBytes = g.Sum(x => x.SourceBytes),
                outputBytes = g.Sum(x => x.OutputBytes),
            })
            .OrderByDescending(x => x.totalCount)
            .ThenBy(x => x.decision, StringComparer.Ordinal)
            .ToArray();

        var totalExportedAssets = orderedTrackers.Sum(t => t.ExportedCount);
        var totalNewlyExportedAssets = orderedTrackers.Sum(t => t.NewlyExportedCount);
        var totalResumedAssets = orderedTrackers.Sum(t => t.ResumedCount);
        var totalSkippedAssets = orderedTrackers.Sum(t => t.SkippedCount);
        var totalOutputBytes = orderedTrackers.Sum(t => t.OutputBytes);
        var totalExportSourceBytes = orderedTrackers
            .Where(t => AssetClassifier.IsExportDecision(t.Decision))
            .Sum(t => t.SourceBytes);
        var totalSkippedSourceBytes = orderedTrackers
            .Where(t => !AssetClassifier.IsExportDecision(t.Decision))
            .Sum(t => t.SourceBytes);

        var manifest = new
        {
            tool = OutputGuard.ToolName,
            toolVersion = Program.ToolVersion,
            assetsToolsVersion = Program.AssetsToolsVersion,
            assetsToolsCpp2IlVersion = Program.AssetsToolsCpp2IlVersion,
            command = "export",
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
            jobs,
            startedAt = OutputGuard.FormatUtc(startedAt),
            finishedAt = OutputGuard.FormatUtc(finishedAt),
            elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            monoBehaviourTypeSources = new
            {
                typeTree = mbTypeTreeCount,
                cpp2Il = mbCpp2IlCount,
                baseOnly = mbBaseOnlyCount,
            },
            totals = new
            {
                bundleCount = catalog.Bundles.Count,
                dataFileCount = dataFiles.Count,
                exportedAssets = totalExportedAssets,
                newlyExportedAssets = totalNewlyExportedAssets,
                resumedAssets = totalResumedAssets,
                skippedAssets = totalSkippedAssets,
                failedAssets = orderedFailures.Length,
                outputFilesWritten = filesWritten,
                outputFilesResumed = filesResumed,
                exportSourceBytes = totalExportSourceBytes,
                skippedSourceBytes = totalSkippedSourceBytes,
                totalOutputBytes,
            },
            byDecision,
            byType = orderedTrackers.Select(t => new
            {
                assetType = t.AssetType,
                decision = t.Decision,
                category = t.Category,
                reason = t.Reason,
                totalCount = t.TotalCount,
                exportedCount = t.ExportedCount,
                newlyExportedCount = t.NewlyExportedCount,
                resumedCount = t.ResumedCount,
                skippedCount = t.SkippedCount,
                failedCount = t.FailedCount,
                sourceBytes = t.SourceBytes,
                outputBytes = t.OutputBytes,
            }).ToArray(),
            failures = orderedFailures,
        };

        OutputGuard.WriteUtf8Atomic(
            Path.Combine(outputPath, "manifest.json"),
            JsonSerializer.Serialize(manifest, FieldJsonWriter.SerializerOptions));

        Console.WriteLine(
            $"[{game.Spec.Id}] 导出完成：资产成功 {totalExportedAssets}（新增 {totalNewlyExportedAssets}，续跑跳过 {totalResumedAssets}），" +
            $"跳过 {totalSkippedAssets}，失败 {orderedFailures.Length}，写出文件 {filesWritten}（续跑 {filesResumed}），" +
            $"输出 {totalOutputBytes} 字节，MonoBehaviour(TypeTree={mbTypeTreeCount}, Cpp2IL={mbCpp2IlCount}, BaseOnly={mbBaseOnlyCount})，" +
            $"耗时 {stopwatch.Elapsed.TotalSeconds:F2}s -> {outputArgument}");

        return Task.FromResult(orderedFailures.Length == 0 ? 0 : 1);
    }

    private static void ExportBundleTextAsset(
        AssetsManager manager,
        AssetsFileInstance aInst,
        AssetFileInfo info,
        string sourceFile,
        string bundleStem,
        string? containerPath,
        string outputPath,
        bool force,
        TypeExportStats tracker,
        ConcurrentBag<ExportFailure> failures,
        ref int filesWritten,
        ref int filesResumed)
    {
        try
        {
            string relativePath;
            if (!string.IsNullOrEmpty(containerPath))
            {
                relativePath = AssetClassifier.MapContainerPathToRelativeOutput(containerPath);
            }
            else
            {
                var name = AssetClassifier.SanitizeFileName(
                    UnityClassDatabase.TryReadAssetName(manager, aInst, info) ?? $"text_{info.PathId}");
                relativePath = $"_unindexed/{bundleStem}/{name}_{info.PathId}.txt";
            }

            var targetPath = OutputGuard.BuildSafeTargetPath(outputPath, relativePath);
            if (!force && File.Exists(targetPath))
            {
                var existingSize = new FileInfo(targetPath).Length;
                Interlocked.Increment(ref tracker.ExportedCount);
                Interlocked.Increment(ref tracker.ResumedCount);
                Interlocked.Add(ref tracker.OutputBytes, existingSize);
                Interlocked.Increment(ref filesResumed);
                return;
            }

            var field = manager.GetBaseField(aInst, info);
            var rawBytes = field["m_Script"].AsByteArray ?? [];
            OutputGuard.WriteBytesAtomic(targetPath, rawBytes);

            Interlocked.Increment(ref tracker.ExportedCount);
            Interlocked.Increment(ref tracker.NewlyExportedCount);
            Interlocked.Add(ref tracker.OutputBytes, rawBytes.LongLength);
            Interlocked.Increment(ref filesWritten);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref tracker.FailedCount);
            failures.Add(new ExportFailure(sourceFile, containerPath, "TextAsset", info.PathId, OutputGuard.TruncateError(ex.Message)));
            Console.Error.WriteLine($"导出 TextAsset 失败：{sourceFile} ({containerPath ?? info.PathId.ToString()})：{ex.Message}");
        }
    }

    private static void ExportDataTextAsset(
        AssetsManager manager,
        AssetsFileInstance aInst,
        AssetFileInfo info,
        string sourceFile,
        string outputPath,
        bool force,
        TypeExportStats tracker,
        ConcurrentBag<ExportFailure> failures,
        ref int filesWritten,
        ref int filesResumed)
    {
        try
        {
            var field = manager.GetBaseField(aInst, info);
            var rawName = field["m_Name"].AsString;
            var safeName = AssetClassifier.SanitizeFileName(
                string.IsNullOrWhiteSpace(rawName) ? $"text_{info.PathId}" : rawName);
            if (string.IsNullOrEmpty(Path.GetExtension(safeName)))
            {
                safeName += ".txt";
            }

            var relativePath = $"_data/{sourceFile}/{safeName}";
            var targetPath = OutputGuard.BuildSafeTargetPath(outputPath, relativePath);
            if (!force && File.Exists(targetPath))
            {
                var existingSize = new FileInfo(targetPath).Length;
                Interlocked.Increment(ref tracker.ExportedCount);
                Interlocked.Increment(ref tracker.ResumedCount);
                Interlocked.Add(ref tracker.OutputBytes, existingSize);
                Interlocked.Increment(ref filesResumed);
                return;
            }

            var rawBytes = field["m_Script"].AsByteArray ?? [];
            OutputGuard.WriteBytesAtomic(targetPath, rawBytes);

            Interlocked.Increment(ref tracker.ExportedCount);
            Interlocked.Increment(ref tracker.NewlyExportedCount);
            Interlocked.Add(ref tracker.OutputBytes, rawBytes.LongLength);
            Interlocked.Increment(ref filesWritten);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref tracker.FailedCount);
            failures.Add(new ExportFailure(sourceFile, null, "TextAsset", info.PathId, OutputGuard.TruncateError(ex.Message)));
            Console.Error.WriteLine($"导出数据 TextAsset 失败：{sourceFile} (PathId={info.PathId})：{ex.Message}");
        }
    }

    private static void ExportStandaloneMonoBehaviour(
        AssetsManager manager,
        AssetsFileInstance aInst,
        AssetFileInfo info,
        string sourceFile,
        string containerPath,
        IReadOnlyDictionary<long, MonoScriptIdentity> localScripts,
        BundleMonoScriptIndex scriptIndex,
        SafeCpp2IlGenerator? cpp2Il,
        string unityVersion,
        string outputPath,
        bool force,
        TypeExportStats tracker,
        ConcurrentBag<ExportFailure> failures,
        ref int mbTypeTreeCount,
        ref int mbCpp2IlCount,
        ref int mbBaseOnlyCount,
        ref int filesWritten,
        ref int filesResumed)
    {
        try
        {
            var mapped = AssetClassifier.MapContainerPathToRelativeOutput(containerPath);
            var relativeJsonPath = mapped.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? mapped
                : mapped + ".json";
            var targetPath = OutputGuard.BuildSafeTargetPath(outputPath, relativeJsonPath);

            if (!force && File.Exists(targetPath))
            {
                var existingSize = new FileInfo(targetPath).Length;
                Interlocked.Increment(ref tracker.ExportedCount);
                Interlocked.Increment(ref tracker.ResumedCount);
                Interlocked.Add(ref tracker.OutputBytes, existingSize);
                Interlocked.Increment(ref filesResumed);
                return;
            }

            var (field, typeSource, script) = UnityBundleHelper.DeserializeMonoBehaviour(
                manager, aInst, info, localScripts, scriptIndex, cpp2Il, unityVersion);
            RecordTypeSource(typeSource, ref mbTypeTreeCount, ref mbCpp2IlCount, ref mbBaseOnlyCount);

            var name = field["m_Name"].AsString ?? string.Empty;
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, FieldJsonWriter.IndentedOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("sourceFile", sourceFile);
                writer.WriteString("containerPath", containerPath);
                writer.WriteNumber("pathId", info.PathId);
                writer.WriteString("name", name);
                writer.WriteString("scriptAssembly", script?.AssemblyName);
                writer.WriteString("scriptNamespace", script?.Namespace);
                writer.WriteString("scriptClass", script?.ClassName);
                writer.WriteString("typeSource", typeSource);
                writer.WritePropertyName("data");
                FieldJsonWriter.WriteFieldValue(writer, field);
                writer.WriteEndObject();
            }

            var bytes = ms.ToArray();
            OutputGuard.WriteBytesAtomic(targetPath, bytes);

            Interlocked.Increment(ref tracker.ExportedCount);
            Interlocked.Increment(ref tracker.NewlyExportedCount);
            Interlocked.Add(ref tracker.OutputBytes, bytes.LongLength);
            Interlocked.Increment(ref filesWritten);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref tracker.FailedCount);
            failures.Add(new ExportFailure(sourceFile, containerPath, "MonoBehaviour", info.PathId, OutputGuard.TruncateError(ex.Message)));
            Console.Error.WriteLine($"导出 MonoBehaviour 失败：{sourceFile} ({containerPath})：{ex.Message}");
        }
    }

    private static long WriteGroupedMonoBehaviours(
        string targetPath,
        string sourceFile,
        List<ExportedMonoBehaviourItem> items)
    {
        var ordered = items
            .OrderBy(x => x.AssetsFile, StringComparer.Ordinal)
            .ThenBy(x => x.PathId)
            .ToArray();

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, FieldJsonWriter.IndentedOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceFile", sourceFile);
            writer.WriteNumber("count", ordered.Length);
            writer.WritePropertyName("monoBehaviours");
            writer.WriteStartArray();

            foreach (var item in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("assetsFile", item.AssetsFile);
                writer.WriteNumber("pathId", item.PathId);
                writer.WriteNumber("gameObjectPathId", item.GameObjectPathId);
                writer.WriteString("gameObjectName", item.GameObjectName);
                writer.WriteString("name", item.Name);
                writer.WriteString("scriptAssembly", item.ScriptAssembly);
                writer.WriteString("scriptNamespace", item.ScriptNamespace);
                writer.WriteString("scriptClass", item.ScriptClass);
                writer.WriteString("typeSource", item.TypeSource);
                writer.WritePropertyName("data");
                FieldJsonWriter.WriteFieldValue(writer, item.Field);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = ms.ToArray();
        OutputGuard.WriteBytesAtomic(targetPath, bytes);
        return bytes.LongLength;
    }

    private static void RecordTypeSource(
        string typeSource,
        ref int typeTreeCount,
        ref int cpp2IlCount,
        ref int baseOnlyCount)
    {
        switch (typeSource)
        {
            case "TypeTree":
                Interlocked.Increment(ref typeTreeCount);
                break;
            case "Cpp2IL":
                Interlocked.Increment(ref cpp2IlCount);
                break;
            default:
                Interlocked.Increment(ref baseOnlyCount);
                break;
        }
    }

    private static void WriteSentinelManifest(
        string outputPath,
        DetectedGame game,
        DateTimeOffset startedAt,
        int jobs)
    {
        var sentinel = new
        {
            tool = OutputGuard.ToolName,
            toolVersion = Program.ToolVersion,
            assetsToolsVersion = Program.AssetsToolsVersion,
            assetsToolsCpp2IlVersion = Program.AssetsToolsCpp2IlVersion,
            command = "export",
            status = "in_progress",
            game = new
            {
                id = game.Spec.Id,
                title = game.Spec.Title,
                appId = game.Spec.AppId,
                dataDirName = game.Spec.DataDirName,
            },
            steamBuildId = game.SteamBuildId,
            unityVersion = game.UnityVersion,
            jobs,
            startedAt = OutputGuard.FormatUtc(startedAt),
        };

        OutputGuard.WriteUtf8Atomic(
            Path.Combine(outputPath, "manifest.json"),
            JsonSerializer.Serialize(sentinel, FieldJsonWriter.SerializerOptions));
    }
}
