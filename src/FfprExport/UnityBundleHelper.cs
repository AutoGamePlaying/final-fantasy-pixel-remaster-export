using System.Collections.Concurrent;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FfprExport;

internal sealed record DataAssetsFileEntry(
    string FileName,
    string FullPath,
    long FileSize);

internal sealed record BundleMonoScriptIndex(
    IReadOnlyDictionary<long, MonoScriptIdentity> GlobalScripts,
    IReadOnlyDictionary<(string CabName, long PathId), MonoScriptIdentity> CabScripts);

internal static class UnityBundleHelper
{
    public static IReadOnlyList<DataAssetsFileEntry> FindDataAssetsFiles(string dataDir)
    {
        var results = new List<DataAssetsFileEntry>();
        if (!Directory.Exists(dataDir))
        {
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(dataDir, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(name, "resources.assets", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
                || IsLevelFileName(name))
            {
                results.Add(new DataAssetsFileEntry(name, path, new FileInfo(path).Length));
            }
        }

        return results;
    }

    private static bool IsLevelFileName(string name)
    {
        if (!name.StartsWith("level", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name[5..];
        return suffix.Length > 0 && suffix.All(char.IsDigit);
    }

    public static AssetsManager CreateConfiguredManager(
        byte[] classDatabaseBytes,
        IMonoBehaviourTemplateGenerator? monoTempGenerator = null)
    {
        var manager = new AssetsManager
        {
            UseTemplateFieldCache = true,
            UseMonoTemplateFieldCache = true,
            UseRefTypeManagerCache = true,
            UseQuickLookup = true,
        };
        using var cldbStream = new MemoryStream(classDatabaseBytes, writable: false);
        manager.LoadClassDatabase(cldbStream);

        if (monoTempGenerator is not null)
        {
            manager.MonoTempGenerator = monoTempGenerator;
        }

        return manager;
    }

    public static Dictionary<long, MonoScriptIdentity> LoadGlobalMonoScripts(
        string dataDir,
        byte[] classDatabaseBytes)
    {
        var map = new Dictionary<long, MonoScriptIdentity>();
        var ggmAssetsPath = Path.Combine(dataDir, "globalgamemanagers.assets");
        if (!File.Exists(ggmAssetsPath))
        {
            return map;
        }

        var manager = CreateConfiguredManager(classDatabaseBytes);
        try
        {
            var inst = manager.LoadAssetsFile(ggmAssetsPath, false);
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoScript))
            {
                if (TryReadMonoScript(manager, inst, info, out var identity))
                {
                    map[info.PathId] = identity;
                }
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }

        return map;
    }

    public static BundleMonoScriptIndex BuildMonoScriptIndex(
        DetectedGame game,
        IReadOnlyList<CatalogBundleRecord> bundles,
        byte[] classDatabaseBytes,
        int jobs)
    {
        var globalScripts = LoadGlobalMonoScripts(game.DataDirectory, classDatabaseBytes);
        var cabScripts = new ConcurrentDictionary<(string CabName, long PathId), MonoScriptIdentity>();

        // Index MonoScripts across bundles so cross-bundle m_Script PPtrs resolve to true class names.
        Parallel.ForEach(
            bundles.Where(b => b.ExistsOnDisk),
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            bundle =>
            {
                var manager = CreateConfiguredManager(classDatabaseBytes);
                try
                {
                    var bunInst = manager.LoadBundleFile(bundle.FullPath, true);
                    for (var i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    {
                        var dirInfo = bunInst.file.BlockAndDirInfo.DirectoryInfos[i];
                        if (IsResourceDataEntry(dirInfo.Name))
                        {
                            continue;
                        }

                        var aInst = manager.LoadAssetsFileFromBundle(bunInst, i, false);
                        if (aInst is null)
                        {
                            continue;
                        }

                        var cabName = dirInfo.Name.ToLowerInvariant();
                        foreach (var info in aInst.file.GetAssetsOfType(AssetClassID.MonoScript))
                        {
                            if (TryReadMonoScript(manager, aInst, info, out var identity))
                            {
                                cabScripts[(cabName, info.PathId)] = identity;
                            }
                        }
                    }
                }
                catch
                {
                    // Individual bundle failures are reported during inventory/export passes.
                }
                finally
                {
                    manager.UnloadAll(true);
                }
            });

        return new BundleMonoScriptIndex(globalScripts, cabScripts);
    }

    public static bool IsResourceDataEntry(string entryName)
    {
        return entryName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
            || entryName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
    }

    public static Dictionary<long, string> ReadContainerMap(AssetsManager manager, AssetsFileInstance aInst)
    {
        var containerMap = new Dictionary<long, string>();
        foreach (var abInfo in aInst.file.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            var abField = manager.GetBaseField(aInst, abInfo);
            var arr = abField["m_Container.Array"];
            if (arr.IsDummy)
            {
                continue;
            }

            foreach (var item in arr)
            {
                var key = item["first"].AsString;
                var assetPtr = item["second.asset"];
                if (assetPtr.IsDummy)
                {
                    continue;
                }

                var fileId = assetPtr["m_FileID"].AsInt;
                var pathId = assetPtr["m_PathID"].AsLong;
                if (fileId == 0 && !string.IsNullOrEmpty(key) && !containerMap.ContainsKey(pathId))
                {
                    containerMap[pathId] = key;
                }
            }
        }

        return containerMap;
    }

    public static Dictionary<long, MonoScriptIdentity> ReadLocalMonoScripts(AssetsManager manager, AssetsFileInstance aInst)
    {
        var map = new Dictionary<long, MonoScriptIdentity>();
        foreach (var info in aInst.file.GetAssetsOfType(AssetClassID.MonoScript))
        {
            if (TryReadMonoScript(manager, aInst, info, out var identity))
            {
                map[info.PathId] = identity;
            }
        }

        return map;
    }

    public static bool TryReadMonoScript(
        AssetsManager manager,
        AssetsFileInstance aInst,
        AssetFileInfo info,
        out MonoScriptIdentity identity)
    {
        identity = default!;
        try
        {
            var field = manager.GetBaseField(aInst, info);
            var asm = field["m_AssemblyName"].AsString ?? string.Empty;
            var ns = field["m_Namespace"].AsString ?? string.Empty;
            var cls = field["m_ClassName"].AsString ?? string.Empty;
            if (string.IsNullOrEmpty(cls))
            {
                return false;
            }

            identity = new MonoScriptIdentity(asm, ns, cls);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static MonoScriptIdentity? ResolveMonoBehaviourScript(
        AssetsFile af,
        int scriptFileId,
        long scriptPathId,
        IReadOnlyDictionary<long, MonoScriptIdentity> localScripts,
        BundleMonoScriptIndex? externalIndex)
    {
        if (scriptPathId == 0)
        {
            return null;
        }

        if (scriptFileId == 0)
        {
            return localScripts.GetValueOrDefault(scriptPathId);
        }

        if (externalIndex is null)
        {
            return null;
        }

        var extIdx = scriptFileId - 1;
        if (extIdx < 0 || extIdx >= af.Metadata.Externals.Count)
        {
            return null;
        }

        var extPath = af.Metadata.Externals[extIdx].PathName;
        var fileName = Path.GetFileName(extPath.Replace('\\', '/')).ToLowerInvariant();
        if (fileName == "globalgamemanagers.assets")
        {
            return externalIndex.GlobalScripts.GetValueOrDefault(scriptPathId);
        }

        return externalIndex.CabScripts.GetValueOrDefault((fileName, scriptPathId));
    }

    public static Dictionary<long, string> ReadGameObjectNames(AssetsManager manager, AssetsFileInstance aInst)
    {
        var names = new Dictionary<long, string>();
        foreach (var goInfo in aInst.file.GetAssetsOfType(AssetClassID.GameObject))
        {
            try
            {
                var field = manager.GetBaseField(aInst, goInfo);
                var name = field["m_Name"].AsString;
                if (!string.IsNullOrEmpty(name))
                {
                    names[goInfo.PathId] = name;
                }
            }
            catch
            {
                // Ignore individual GameObject name lookup failures.
            }
        }

        return names;
    }

    /// <summary>
    /// Deserializes a MonoBehaviour using embedded TypeTree when present, or falls back to
    /// ClassDatabase + Cpp2IL template generation when TypeTree is stripped.
    /// </summary>
    public static (AssetTypeValueField Field, string TypeSource, MonoScriptIdentity? Script) DeserializeMonoBehaviour(
        AssetsManager manager,
        AssetsFileInstance aInst,
        AssetFileInfo info,
        IReadOnlyDictionary<long, MonoScriptIdentity> localScripts,
        BundleMonoScriptIndex? externalIndex,
        SafeCpp2IlGenerator? cpp2Il,
        string unityVersion)
    {
        var af = aInst.file;
        var scriptIndex = info.GetScriptIndex(af);
        var ttType = af.Metadata.TypeTreeEnabled
            ? af.Metadata.FindTypeTreeTypeByID(info.TypeId, scriptIndex)
            : null;

        if (af.Metadata.TypeTreeEnabled && ttType is not null && ttType.Nodes.Count > 0)
        {
            var field = manager.GetBaseField(aInst, info);
            var scriptFileId = field["m_Script.m_FileID"].AsInt;
            var scriptPathId = field["m_Script.m_PathID"].AsLong;
            var script = ResolveMonoBehaviourScript(af, scriptFileId, scriptPathId, localScripts, externalIndex);
            return (field, "TypeTree", script);
        }

        // No TypeTree: read base MonoBehaviour header first via built-in ClassDatabase.
        var baseTemplate = manager.GetTemplateBaseField(
            aInst,
            info,
            AssetReadFlags.ForceFromCldb | AssetReadFlags.SkipMonoBehaviourFields);
        if (baseTemplate is null)
        {
            throw new InvalidDataException($"缺少 MonoBehaviour 基础模板（PathId={info.PathId}）");
        }

        var (baseField, baseBytesConsumed) = ReadValueFromAsset(aInst, info, baseTemplate);
        var mbScriptFileId = baseField["m_Script.m_FileID"].AsInt;
        var mbScriptPathId = baseField["m_Script.m_PathID"].AsLong;
        var resolvedScript = ResolveMonoBehaviourScript(af, mbScriptFileId, mbScriptPathId, localScripts, externalIndex);

        if (cpp2Il is not null && resolvedScript is not null)
        {
            var version = new UnityVersion(
                string.IsNullOrWhiteSpace(af.Metadata.UnityVersion) ? unityVersion : af.Metadata.UnityVersion);
            AssetTypeTemplateField? generated;
            try
            {
                generated = cpp2Il.GetTemplateField(
                    baseTemplate,
                    resolvedScript.AssemblyName,
                    resolvedScript.Namespace,
                    resolvedScript.ClassName,
                    version);
            }
            catch when (baseBytesConsumed == info.ByteSize)
            {
                // Script class was compiled out of this platform/build (e.g. #if UNITY_IOS / #if DEBUG)
                // and the serialized MonoBehaviour contains only the 32-byte base header (0 trailing bytes).
                return (baseField, "BaseOnly", resolvedScript);
            }

            if (generated is not null)
            {
                var refMan = manager.GetRefTypeManager(aInst);
                var (fullField, _) = ReadValueFromAsset(aInst, info, generated, refMan);
                return (fullField, "Cpp2IL", resolvedScript);
            }
        }

        if (baseBytesConsumed < info.ByteSize)
        {
            throw new InvalidDataException(
                $"MonoBehaviour 缺少类型树且无法通过 IL2CPP 解析剩余 {info.ByteSize - baseBytesConsumed} 字节（PathId={info.PathId}, Script={resolvedScript?.FullName ?? "未知"}）");
        }

        return (baseField, "BaseOnly", resolvedScript);
    }

    private static (AssetTypeValueField Field, long BytesConsumed) ReadValueFromAsset(
        AssetsFileInstance aInst,
        AssetFileInfo info,
        AssetTypeTemplateField template,
        RefTypeManager? refMan = null)
    {
        using var ms = new MemoryStream((int)info.ByteSize);
        lock (aInst.LockReader)
        {
            var reader = aInst.file.Reader;
            reader.Position = info.GetAbsoluteByteOffset(aInst.file);
            var remaining = info.ByteSize;
            var buffer = new byte[Math.Min(remaining, 81920)];
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = reader.BaseStream.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                ms.Write(buffer, 0, read);
                remaining -= (uint)read;
            }
        }

        ms.Position = 0;
        var assetReader = new AssetsFileReader(ms);
        var value = template.MakeValue(assetReader, 0, refMan);
        return (value, assetReader.Position);
    }
}
