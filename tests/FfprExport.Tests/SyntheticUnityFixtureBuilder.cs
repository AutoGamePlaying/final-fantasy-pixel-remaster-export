using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FfprExport.Tests;

internal sealed record SyntheticAssetSpec(
    long PathId,
    int TypeId,
    int TypeIndex,
    ushort ScriptTypeIndex,
    byte[] RawData);

internal static class SyntheticUnityFixtureBuilder
{
    public const string DefaultUnityVersion = "2019.4.26f1";

    public static byte[] BuildTextAssetBytes(string name, byte[] scriptBytes)
    {
        using var ms = new MemoryStream();
        using var w = new AssetsFileWriter(ms);
        WriteCountString(w, name);
        WriteByteString(w, scriptBytes);
        return ms.ToArray();
    }

    public static byte[] BuildMonoScriptBytes(string name, string className, string nameSpace, string assemblyName)
    {
        using var ms = new MemoryStream();
        using var w = new AssetsFileWriter(ms);
        WriteCountString(w, name);
        w.Write(0); // m_ExecutionOrder
        w.Write(new byte[16]); // m_PropertiesHash
        WriteCountString(w, className);
        WriteCountString(w, nameSpace);
        WriteCountString(w, assemblyName);
        return ms.ToArray();
    }

    public static byte[] BuildGameObjectBytes(string name, params long[] componentPathIds)
    {
        using var ms = new MemoryStream();
        using var w = new AssetsFileWriter(ms);
        w.Write(componentPathIds.Length);
        foreach (var pid in componentPathIds)
        {
            w.Write(0); // m_FileID
            w.Write(pid); // m_PathID
        }

        w.Write(0u); // m_Layer
        WriteCountString(w, name);
        w.Write((ushort)0); // m_Tag
        w.Write((byte)1); // m_IsActive
        return ms.ToArray();
    }

    public static byte[] BuildSampleMonoBehaviourBytes(
        long gameObjectPathId,
        long scriptPathId,
        string name,
        int level,
        string label,
        int[] values)
    {
        using var ms = new MemoryStream();
        using var w = new AssetsFileWriter(ms);
        w.Write(0); // m_GameObject.m_FileID
        w.Write(gameObjectPathId); // m_GameObject.m_PathID
        w.Write((byte)1); // m_Enabled
        w.Align();
        w.Write(0); // m_Script.m_FileID
        w.Write(scriptPathId); // m_Script.m_PathID
        WriteCountString(w, name);
        w.Write(level);
        WriteCountString(w, label);
        w.Write(values.Length);
        foreach (var v in values)
        {
            w.Write(v);
        }

        return ms.ToArray();
    }

    public static byte[] BuildBaseOnlyMonoBehaviourBytes(long gameObjectPathId, int scriptFileId, long scriptPathId, string name)
    {
        using var ms = new MemoryStream();
        using var w = new AssetsFileWriter(ms);
        w.Write(0);
        w.Write(gameObjectPathId);
        w.Write((byte)1);
        w.Align();
        w.Write(scriptFileId);
        w.Write(scriptPathId);
        WriteCountString(w, name);
        return ms.ToArray();
    }

    public static byte[] BuildAssetBundleObjectBytes(string bundleName, IReadOnlyList<(string ContainerPath, long PathId)> entries)
    {
        using var ms = new MemoryStream();
        using var w = new AssetsFileWriter(ms);
        WriteCountString(w, bundleName);
        w.Write(0); // m_PreloadTable count
        w.Write(entries.Count); // m_Container count
        foreach (var (containerPath, pathId) in entries)
        {
            WriteCountString(w, containerPath);
            w.Write(0); // preloadIndex
            w.Write(0); // preloadSize
            w.Write(0); // m_FileID
            w.Write(pathId); // m_PathID
        }

        // m_MainAsset
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0L);
        w.Write(5u); // m_RuntimeCompatibility
        WriteCountString(w, bundleName);
        w.Write(0); // m_Dependencies count
        return ms.ToArray();
    }

    public static TypeTreeType BuildTypeTreeFromClassDb(ClassDatabaseFile db, int classId)
    {
        var cldbType = db.FindAssetClassByID(classId)
            ?? throw new InvalidOperationException($"内置类库缺少 ClassId={classId}");
        var root = cldbType.GetPreferredNode()!;
        var nodes = new List<TypeTreeNode>();
        var stringBuf = new MemoryStream();
        var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);

        uint AddString(string s)
        {
            if (stringOffsets.TryGetValue(s, out var existing))
            {
                return existing;
            }

            var offset = (uint)stringBuf.Position;
            var bytes = Encoding.UTF8.GetBytes(s);
            stringBuf.Write(bytes, 0, bytes.Length);
            stringBuf.WriteByte(0);
            stringOffsets[s] = offset;
            return offset;
        }

        void Visit(ClassDatabaseTypeNode node, byte level)
        {
            nodes.Add(new TypeTreeNode
            {
                Version = node.Version,
                Level = level,
                TypeFlags = (TypeTreeNodeFlags)node.TypeFlags,
                TypeStrOffset = AddString(db.GetString(node.TypeName)),
                NameStrOffset = AddString(db.GetString(node.FieldName)),
                ByteSize = node.ByteSize,
                Index = (uint)nodes.Count,
                MetaFlags = node.MetaFlag,
                RefTypeHash = 0,
            });
            foreach (var child in node.Children)
            {
                Visit(child, (byte)(level + 1));
            }
        }

        Visit(root, 0);
        return CreateTypeTreeType(classId, 0xFFFF, nodes, stringBuf.ToArray());
    }

    private static TypeTreeType CreateTypeTreeType(
        int typeId,
        ushort scriptTypeIndex,
        List<TypeTreeNode> nodes,
        byte[] stringBufferBytes)
    {
        return new TypeTreeType
        {
            TypeId = typeId,
            IsStrippedType = false,
            ScriptTypeIndex = scriptTypeIndex,
            ScriptIdHash = Hash128.NewBlankHash(),
            TypeHash = Hash128.NewBlankHash(),
            TypeBlobIsDefinition = true,
            TypeBlob = new TypeTreeBlob
            {
                Nodes = nodes,
                StringBufferBytes = stringBufferBytes,
            },
            TypeDependencies = [],
        };
    }

    public static TypeTreeType BuildSampleMonoBehaviourTypeTree(ushort scriptTypeIndex)
    {
        var stringBuf = new MemoryStream();
        var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var nodes = new List<TypeTreeNode>();

        uint AddString(string s)
        {
            if (stringOffsets.TryGetValue(s, out var existing))
            {
                return existing;
            }

            var offset = (uint)stringBuf.Position;
            var bytes = Encoding.UTF8.GetBytes(s);
            stringBuf.Write(bytes, 0, bytes.Length);
            stringBuf.WriteByte(0);
            stringOffsets[s] = offset;
            return offset;
        }

        void Add(byte level, string type, string name, int size, TypeTreeNodeFlags flags = 0, uint meta = 0)
        {
            nodes.Add(new TypeTreeNode
            {
                Version = 1,
                Level = level,
                TypeFlags = flags,
                TypeStrOffset = AddString(type),
                NameStrOffset = AddString(name),
                ByteSize = size,
                Index = (uint)nodes.Count,
                MetaFlags = meta,
                RefTypeHash = 0,
            });
        }

        void AddStringField(byte level, string name)
        {
            Add(level, "string", name, -1);
            Add((byte)(level + 1), "Array", "Array", -1, TypeTreeNodeFlags.Array, 0x4000);
            Add((byte)(level + 2), "int", "size", 4);
            Add((byte)(level + 2), "char", "data", 1);
        }

        Add(0, "MonoBehaviour", "Base", -1);
        Add(1, "PPtr<GameObject>", "m_GameObject", 12);
        Add(2, "int", "m_FileID", 4);
        Add(2, "SInt64", "m_PathID", 8);
        Add(1, "UInt8", "m_Enabled", 1, 0, 0x4000);
        Add(1, "PPtr<MonoScript>", "m_Script", 12);
        Add(2, "int", "m_FileID", 4);
        Add(2, "SInt64", "m_PathID", 8);
        AddStringField(1, "m_Name");
        Add(1, "int", "level", 4);
        AddStringField(1, "label");
        Add(1, "vector", "values", -1);
        Add(2, "Array", "Array", -1, TypeTreeNodeFlags.Array, 0x4000);
        Add(3, "int", "size", 4);
        Add(3, "int", "data", 4);

        return CreateTypeTreeType((int)AssetClassID.MonoBehaviour, scriptTypeIndex, nodes, stringBuf.ToArray());
    }

    public static byte[] BuildAssetsFile(
        IReadOnlyList<TypeTreeType> typeTreeTypes,
        IReadOnlyList<SyntheticAssetSpec> assets,
        bool typeTreeEnabled = true,
        string unityVersion = DefaultUnityVersion)
    {
        var af = new AssetsFile
        {
            Header = new AssetsFileHeader
            {
                Version = 21,
                Endianness = false,
            },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = unityVersion,
                TargetPlatform = 19,
                TypeTreeEnabled = typeTreeEnabled,
                TypeTreeTypes = [.. typeTreeTypes],
                AssetInfos = assets.Select(a =>
                {
                    var info = new AssetFileInfo
                    {
                        PathId = a.PathId,
                        TypeIdOrIndex = a.TypeIndex,
                        TypeId = a.TypeId,
                        ScriptTypeIndex = a.ScriptTypeIndex,
                    };
                    info.SetNewData(a.RawData);
                    return info;
                }).ToList(),
                ScriptTypes = [],
                Externals = [],
                RefTypes = [],
                UserInformation = string.Empty,
            },
        };

        using var ms = new MemoryStream();
        using var writer = new AssetsFileWriter(ms);
        af.Write(writer);
        return ms.ToArray();
    }

    public static byte[] BuildBundleFile(string cabName, byte[] assetsFileBytes, string unityVersion = DefaultUnityVersion)
    {
        var bundle = new AssetBundleFile
        {
            Header = new AssetBundleHeader
            {
                Signature = "UnityFS",
                Version = 7,
                GenerationVersion = "5.x.x",
                EngineVersion = unityVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    Flags = AssetBundleFSHeaderFlags.BlockAndDirAtEnd,
                },
            },
            BlockAndDirInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = Hash128.NewBlankHash(),
                BlockInfos = [],
                DirectoryInfos =
                [
                    new AssetBundleDirectoryInfo
                    {
                        Offset = 0,
                        DecompressedSize = assetsFileBytes.Length,
                        Flags = 4,
                        Name = cabName,
                        Replacer = new ContentReplacerFromBuffer(assetsFileBytes),
                    },
                ],
            },
            DataIsCompressed = false,
        };

        using var ms = new MemoryStream();
        using var writer = new AssetsFileWriter(ms);
        bundle.Write(writer);
        return ms.ToArray();
    }

    public static string BuildCatalogJson(IReadOnlyList<(string BundleFileName, IReadOnlyList<string> AssetPaths)> bundles)
    {
        var internalIds = new List<string>();
        var entries = new List<(int InternalIdIdx, int ProviderIdx, int DepKeyIdx, int PrimaryKeyIdx, int ResTypeIdx)>();
        var bucketDefs = new List<(string Key, int[] EntryIndices)>();

        // 1. Add bundle entries first and create a dependency bucket per bundle.
        var bundleBucketIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (bundleName, _) in bundles)
        {
            var idIdx = internalIds.Count;
            internalIds.Add($"{{UnityEngine.AddressableAssets.Addressables.RuntimePath}}/StandaloneWindows64/{bundleName}");
            var entryIdx = entries.Count;
            var bIdx = bucketDefs.Count;
            bucketDefs.Add((bundleName, [entryIdx]));
            bundleBucketIdx[bundleName] = bIdx;
            entries.Add((idIdx, 0, -1, bIdx, 0));
        }

        // 2. Add asset entries linked to their owning bundle via dependencyKeyIndex.
        foreach (var (bundleName, assetPaths) in bundles)
        {
            var depBucket = bundleBucketIdx[bundleName];
            foreach (var assetPath in assetPaths)
            {
                var idIdx = internalIds.Count;
                internalIds.Add(assetPath);
                var entryIdx = entries.Count;
                var pkBucket = bucketDefs.Count;
                bucketDefs.Add((assetPath, [entryIdx]));
                entries.Add((idIdx, 1, depBucket, pkBucket, 1));
            }
        }

        // Encode m_KeyDataString & m_BucketDataString
        using var keyMs = new MemoryStream();
        WriteInt32LE(keyMs, bucketDefs.Count);
        var keyOffsets = new int[bucketDefs.Count];
        for (var i = 0; i < bucketDefs.Count; i++)
        {
            keyOffsets[i] = (int)keyMs.Position - 4;
            keyMs.WriteByte(0); // AsciiString
            var ascii = Encoding.ASCII.GetBytes(bucketDefs[i].Key);
            WriteInt32LE(keyMs, ascii.Length);
            keyMs.Write(ascii, 0, ascii.Length);
        }

        using var bucketMs = new MemoryStream();
        WriteInt32LE(bucketMs, bucketDefs.Count);
        for (var i = 0; i < bucketDefs.Count; i++)
        {
            WriteInt32LE(bucketMs, keyOffsets[i]);
            WriteInt32LE(bucketMs, bucketDefs[i].EntryIndices.Length);
            foreach (var ei in bucketDefs[i].EntryIndices)
            {
                WriteInt32LE(bucketMs, ei);
            }
        }

        using var entryMs = new MemoryStream();
        WriteInt32LE(entryMs, entries.Count);
        foreach (var e in entries)
        {
            WriteInt32LE(entryMs, e.InternalIdIdx);
            WriteInt32LE(entryMs, e.ProviderIdx);
            WriteInt32LE(entryMs, e.DepKeyIdx);
            WriteInt32LE(entryMs, 0); // depHash
            WriteInt32LE(entryMs, -1); // dataIndex
            WriteInt32LE(entryMs, e.PrimaryKeyIdx);
            WriteInt32LE(entryMs, e.ResTypeIdx);
        }

        var catalogObj = new
        {
            m_LocatorId = "AddressablesMainContentCatalog",
            m_ProviderIds = new[]
            {
                "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
                "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider",
            },
            m_InternalIds = internalIds,
            m_KeyDataString = Convert.ToBase64String(keyMs.ToArray()),
            m_BucketDataString = Convert.ToBase64String(bucketMs.ToArray()),
            m_EntryDataString = Convert.ToBase64String(entryMs.ToArray()),
            m_ExtraDataString = string.Empty,
            m_resourceTypes = new[]
            {
                new { m_AssemblyName = "Unity.ResourceManager", m_ClassName = "UnityEngine.ResourceManagement.ResourceProviders.IAssetBundleResource" },
                new { m_AssemblyName = "UnityEngine.CoreModule", m_ClassName = "UnityEngine.TextAsset" },
            },
            m_InternalIdPrefixes = Array.Empty<string>(),
        };

        return JsonSerializer.Serialize(catalogObj, FieldJsonWriter.SerializerOptions);
    }

    public static string BuildSyntheticSteamGame(
        string rootDir,
        GameSpec spec,
        string steamBuildId = "99001122",
        bool includeCorruptedBundle = false,
        bool includeCorruptedTextAsset = false,
        string? customCommonFolderName = null)
    {
        var steamAppsDir = Path.Combine(rootDir, "steamapps");
        var folderName = customCommonFolderName ?? $"{spec.Title} PR";
        var gameDir = Path.Combine(steamAppsDir, "common", folderName);
        var dataDir = Path.Combine(gameDir, spec.DataDirName);
        var aaDir = Path.Combine(dataDir, "StreamingAssets", "aa");
        var standaloneDir = Path.Combine(aaDir, "StandaloneWindows64");
        Directory.CreateDirectory(standaloneDir);

        // Create steamapps/appmanifest_<appid>.acf
        var acf = $$"""
            "AppState"
            {
            	"appid"		"{{spec.AppId}}"
            	"name"		"{{spec.Title}}"
            	"installdir"		"{{folderName}}"
            	"buildid"		"{{steamBuildId}}"
            }
            """;
        File.WriteAllText(Path.Combine(steamAppsDir, $"appmanifest_{spec.AppId}.acf"), acf, OutputGuard.Utf8WithoutBom);

        // Create dummy exe
        File.WriteAllBytes(Path.Combine(gameDir, spec.ExeName), [0x4D, 0x5A]);

        var db = UnityClassDatabase.CreateBuiltIn2019();
        var ttAb = BuildTypeTreeFromClassDb(db, (int)AssetClassID.AssetBundle);
        var ttText = BuildTypeTreeFromClassDb(db, (int)AssetClassID.TextAsset);
        var ttGo = BuildTypeTreeFromClassDb(db, (int)AssetClassID.GameObject);
        var ttMs = BuildTypeTreeFromClassDb(db, (int)AssetClassID.MonoScript);
        var ttMb = BuildSampleMonoBehaviourTypeTree(0);
        var ttTex = CreateTypeTreeType((int)AssetClassID.Texture2D, 0xFFFF, [], []);

        // Bundle 1: TextAssets (CSV, Tiled JSON, AssetsPath.json, skipped audio .mab.bytes, skipped Texture2D)
        var csvBytes = Encoding.UTF8.GetBytes("id,name\n1,Potion\n");
        var mapBytes = Encoding.UTF8.GetBytes("{\"width\":16,\"height\":16,\"layers\":[]}");
        var indexBytes = Encoding.UTF8.GetBytes("{\"entries\":[\"a\",\"b\"]}");
        var audioBytes = Encoding.ASCII.GetBytes("mabf\x02\x01SYNTH_AUDIO");

        var bundle1Container = new List<(string, long)>
        {
            ("Assets/GameAssets/Serial/Data/Master/item.csv", 2),
            ("Assets/GameAssets/Serial/Res/Map/Map_00001/Map_00001_1/tilemap.json", 3),
            ("Assets/GameAssets/AssetsPath.json", 4),
            ("Assets/GameAssets/Serial/Res/Audio/bgm_01.mab.bytes", 5),
            ("Assets/GameAssets/Serial/Res/UI/icon.png", 6),
        };

        var bundle1Assets = new List<SyntheticAssetSpec>
        {
            new(1, (int)AssetClassID.AssetBundle, 0, 0xFFFF, BuildAssetBundleObjectBytes("master_bundle", bundle1Container)),
            new(2, (int)AssetClassID.TextAsset, 1, 0xFFFF, BuildTextAssetBytes("item", csvBytes)),
            new(3, (int)AssetClassID.TextAsset, 1, 0xFFFF, BuildTextAssetBytes("tilemap", mapBytes)),
            new(4, (int)AssetClassID.TextAsset, 1, 0xFFFF, BuildTextAssetBytes("AssetsPath", indexBytes)),
            new(5, (int)AssetClassID.TextAsset, 1, 0xFFFF, BuildTextAssetBytes("bgm_01.mab", audioBytes)),
            new(6, (int)AssetClassID.Texture2D, 2, 0xFFFF, [0x01, 0x02, 0x03, 0x04]),
        };

        if (includeCorruptedTextAsset)
        {
            // Invalid length prefix (claims 999999 bytes on a 4-byte payload) triggers single-asset read failure.
            bundle1Container.Add(("Assets/GameAssets/Serial/Data/Master/broken.csv", 7));
            bundle1Assets[0] = new(1, (int)AssetClassID.AssetBundle, 0, 0xFFFF, BuildAssetBundleObjectBytes("master_bundle", bundle1Container));
            bundle1Assets.Add(new(7, (int)AssetClassID.TextAsset, 1, 0xFFFF, [0xFF, 0xFF, 0x0F, 0x00]));
        }

        var af1 = BuildAssetsFile([ttAb, ttText, ttTex], bundle1Assets, typeTreeEnabled: true);
        var bun1 = BuildBundleFile("CAB-synthetic0001", af1);
        File.WriteAllBytes(Path.Combine(standaloneDir, "master_assets_all_0001.bundle"), bun1);

        // Bundle 2: Standalone container MonoBehaviour + Prefab GameObject with component MonoBehaviour
        var bundle2Container = new List<(string, long)>
        {
            ("Assets/GameAssets/Serial/Res/Config/demo_config.asset", 4),
            ("Assets/GameAssets/Serial/Res/UI/sample_panel.prefab", 3),
        };

        var bundle2Assets = new List<SyntheticAssetSpec>
        {
            new(1, (int)AssetClassID.AssetBundle, 0, 0xFFFF, BuildAssetBundleObjectBytes("ui_bundle", bundle2Container)),
            new(2, (int)AssetClassID.MonoScript, 1, 0xFFFF, BuildMonoScriptBytes("DemoComponent", "DemoComponent", "Last.Demo", "Assembly-CSharp.dll")),
            new(3, (int)AssetClassID.GameObject, 2, 0xFFFF, BuildGameObjectBytes("SamplePanelRoot", 5)),
            new(4, (int)AssetClassID.MonoBehaviour, 3, 0, BuildSampleMonoBehaviourBytes(0, 2, "DemoStandalone", 42, "独立配置", [10, 20, 30])),
            new(5, (int)AssetClassID.MonoBehaviour, 3, 0, BuildSampleMonoBehaviourBytes(3, 2, "DemoChildComp", 7, "面板组件", [1, 2])),
        };

        var af2 = BuildAssetsFile([ttAb, ttMs, ttGo, ttMb], bundle2Assets, typeTreeEnabled: true);
        var bun2 = BuildBundleFile("CAB-synthetic0002", af2);
        File.WriteAllBytes(Path.Combine(standaloneDir, "ui_assets_all_0002.bundle"), bun2);

        var catalogBundles = new List<(string, IReadOnlyList<string>)>
        {
            ("master_assets_all_0001.bundle", bundle1Container.Select(x => x.Item1).ToArray()),
            ("ui_assets_all_0002.bundle", bundle2Container.Select(x => x.Item1).ToArray()),
        };

        if (includeCorruptedBundle)
        {
            File.WriteAllBytes(Path.Combine(standaloneDir, "corrupted_assets_all_9999.bundle"), Encoding.ASCII.GetBytes("NOT_A_VALID_UNITYFS_BUNDLE"));
            catalogBundles.Add(("corrupted_assets_all_9999.bundle", ["Assets/GameAssets/Serial/Res/Broken/bad.json"]));
        }

        File.WriteAllText(Path.Combine(aaDir, "catalog.json"), BuildCatalogJson(catalogBundles), OutputGuard.Utf8WithoutBom);

        // Synthetic *_Data files (TypeTree stripped, matching real game layout):
        // sharedassets0.assets with 1 TextAsset + level0 with 1 base-only MonoBehaviour
        var strippedTextType = CreateTypeTreeType((int)AssetClassID.TextAsset, 0xFFFF, [], []);
        var shared0Bytes = BuildAssetsFile(
            [strippedTextType],
            [new(10, (int)AssetClassID.TextAsset, 0, 0xFFFF, BuildTextAssetBytes("sead.win.config", Encoding.UTF8.GetBytes("{\"audio\":true}")))],
            typeTreeEnabled: false);
        File.WriteAllBytes(Path.Combine(dataDir, "sharedassets0.assets"), shared0Bytes);

        var strippedMbType = CreateTypeTreeType((int)AssetClassID.MonoBehaviour, 0, [], []);
        var level0Bytes = BuildAssetsFile(
            [strippedMbType],
            [new(20, (int)AssetClassID.MonoBehaviour, 0, 0, BuildBaseOnlyMonoBehaviourBytes(0, 0, 0, string.Empty))],
            typeTreeEnabled: false);
        File.WriteAllBytes(Path.Combine(dataDir, "level0"), level0Bytes);

        // Encrypted-looking font bundle in StreamingAssets root (must be noted as font bundle and skipped)
        File.WriteAllBytes(Path.Combine(dataDir, "StreamingAssets", "font_en.bundle"), [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);

        return gameDir;
    }

    private static void WriteCountString(AssetsFileWriter w, string value)
    {
        WriteByteString(w, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteByteString(AssetsFileWriter w, byte[] bytes)
    {
        w.Write(bytes.Length);
        w.Write(bytes);
        w.Align();
    }

    private static void WriteInt32LE(Stream stream, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        stream.Write(buf);
    }
}
