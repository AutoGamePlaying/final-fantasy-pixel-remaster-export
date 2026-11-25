using System.Collections.Concurrent;
using AssetsTools.NET;
using AssetsTools.NET.Cpp2IL;
using AssetsTools.NET.Extra;
using LibCpp2IL.Logging;

namespace FfprExport;

internal sealed record MonoScriptIdentity(string AssemblyName, string Namespace, string ClassName)
{
    public string FullName
    {
        get
        {
            var asm = AssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? AssemblyName[..^4]
                : AssemblyName;
            var qualified = string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";
            return string.IsNullOrEmpty(asm) ? qualified : $"{asm}:{qualified}";
        }
    }
}

internal static class UnityClassDatabase
{
    private const uint AlignFlag = 0x4000;

    /// <summary>
    /// Builds a self-contained in-memory ClassDatabaseFile serialized to bytes for Unity 2019.4.x
    /// covering the core types required to inspect and deserialize TypeTree-stripped *_Data files.
    /// </summary>
    public static byte[] CreateBuiltIn2019Bytes()
    {
        var db = CreateBuiltIn2019();
        using var ms = new MemoryStream();
        using var writer = new AssetsFileWriter(ms);
        db.Write(writer, ClassFileCompressionType.Uncompressed);
        return ms.ToArray();
    }

    public static ClassDatabaseFile CreateBuiltIn2019()
    {
        var stringTable = new ClassDatabaseStringTable { Strings = [] };
        var classes = new List<ClassDatabaseType>
        {
            BuildGameObject(stringTable),
            BuildTextAsset(stringTable),
            BuildMonoBehaviour(stringTable),
            BuildMonoScript(stringTable),
            BuildAssetBundle(stringTable),
        };

        return new ClassDatabaseFile
        {
            Header = new ClassDatabaseFileHeader
            {
                Magic = "CLDB",
                FileVersion = 1,
                Version = new UnityVersion("2019.4.26f1"),
                CompressionType = ClassFileCompressionType.Uncompressed,
                CompressedSize = 0,
                DecompressedSize = 0,
            },
            Classes = classes,
            StringTable = stringTable,
            CommonStringBufferIndices = [],
        };
    }

    public static string? TryReadAssetName(AssetsManager manager, AssetsFileInstance inst, AssetFileInfo info)
    {
        var typeId = info.TypeId;
        var af = inst.file;

        if (af.Metadata.TypeTreeEnabled)
        {
            try
            {
                var tt = af.Metadata.FindTypeTreeTypeByID(typeId, info.GetScriptIndex(af));
                if (tt is not null && tt.Nodes.Count > 1)
                {
                    var firstChildName = tt.Nodes[1].GetNameString(tt.StringBufferBytes);
                    if (firstChildName == "m_Name")
                    {
                        return ReadLeadingCountString(af, info);
                    }

                    if (typeId == (int)AssetClassID.GameObject)
                    {
                        var bf = manager.GetBaseField(inst, info);
                        return bf["m_Name"].AsString;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        // TypeTree-stripped *_Data files: read m_Name for known types.
        if (typeId == (int)AssetClassID.GameObject)
        {
            try
            {
                var bf = manager.GetBaseField(inst, info);
                return bf["m_Name"].AsString;
            }
            catch
            {
                return null;
            }
        }

        if (typeId == (int)AssetClassID.MonoBehaviour)
        {
            try
            {
                var bf = manager.GetBaseField(inst, info, AssetReadFlags.SkipMonoBehaviourFields);
                return bf["m_Name"].AsString;
            }
            catch
            {
                return null;
            }
        }

        if (HasLeadingNameField(typeId))
        {
            return ReadLeadingCountString(af, info);
        }

        return null;
    }

    private static bool HasLeadingNameField(int typeId)
    {
        return (AssetClassID)typeId is
            AssetClassID.TextAsset or
            AssetClassID.MonoScript or
            AssetClassID.AssetBundle or
            AssetClassID.Texture2D or
            AssetClassID.Sprite or
            AssetClassID.SpriteAtlas or
            AssetClassID.RenderTexture or
            AssetClassID.Material or
            AssetClassID.Shader or
            AssetClassID.Mesh or
            AssetClassID.AnimationClip or
            AssetClassID.AnimatorController or
            AssetClassID.AnimatorOverrideController or
            AssetClassID.AudioClip or
            AssetClassID.Font or
            AssetClassID.PreloadData;
    }

    private static string? ReadLeadingCountString(AssetsFile af, AssetFileInfo info)
    {
        try
        {
            var reader = af.Reader;
            var start = info.GetAbsoluteByteOffset(af);
            reader.Position = start;
            var len = reader.ReadInt32();
            if (len < 0 || len > 4096 || len + 4 > info.ByteSize)
            {
                return null;
            }

            reader.Position = start;
            return reader.ReadCountStringInt32();
        }
        catch
        {
            return null;
        }
    }

    private static ClassDatabaseTypeNode Node(
        ClassDatabaseStringTable st,
        string typeName,
        string fieldName,
        int byteSize,
        ushort version = 1,
        byte typeFlags = 0,
        uint metaFlag = 0,
        List<ClassDatabaseTypeNode>? children = null)
    {
        return new ClassDatabaseTypeNode
        {
            TypeName = st.AddString(typeName),
            FieldName = st.AddString(fieldName),
            ByteSize = byteSize,
            Version = version,
            TypeFlags = typeFlags,
            MetaFlag = metaFlag,
            Children = children ?? [],
        };
    }

    private static ClassDatabaseTypeNode StringNode(ClassDatabaseStringTable st, string fieldName)
    {
        return Node(st, "string", fieldName, -1, 1, 0, 0, [
            Node(st, "Array", "Array", -1, 1, 1, AlignFlag, [
                Node(st, "int", "size", 4),
                Node(st, "char", "data", 1),
            ]),
        ]);
    }

    private static ClassDatabaseTypeNode PPtrNode(ClassDatabaseStringTable st, string typeName, string fieldName)
    {
        return Node(st, typeName, fieldName, 12, 1, 0, 0, [
            Node(st, "int", "m_FileID", 4),
            Node(st, "SInt64", "m_PathID", 8),
        ]);
    }

    private static ClassDatabaseType BuildTextAsset(ClassDatabaseStringTable st)
    {
        var root = Node(st, "TextAsset", "Base", -1, 1, 0, 0, [
            StringNode(st, "m_Name"),
            StringNode(st, "m_Script"),
        ]);

        return new ClassDatabaseType
        {
            ClassId = (int)AssetClassID.TextAsset,
            Name = st.AddString("TextAsset"),
            BaseName = st.AddString("NamedObject"),
            Flags = ClassFileTypeFlags.HasReleaseRootNode,
            ReleaseRootNode = root,
        };
    }

    private static ClassDatabaseType BuildMonoBehaviour(ClassDatabaseStringTable st)
    {
        var root = Node(st, "MonoBehaviour", "Base", -1, 1, 0, 0, [
            PPtrNode(st, "PPtr<GameObject>", "m_GameObject"),
            Node(st, "UInt8", "m_Enabled", 1, 1, 0, AlignFlag),
            PPtrNode(st, "PPtr<MonoScript>", "m_Script"),
            StringNode(st, "m_Name"),
        ]);

        return new ClassDatabaseType
        {
            ClassId = (int)AssetClassID.MonoBehaviour,
            Name = st.AddString("MonoBehaviour"),
            BaseName = st.AddString("Behaviour"),
            Flags = ClassFileTypeFlags.HasReleaseRootNode,
            ReleaseRootNode = root,
        };
    }

    private static ClassDatabaseType BuildMonoScript(ClassDatabaseStringTable st)
    {
        var hashChildren = new List<ClassDatabaseTypeNode>(16);
        for (var i = 0; i < 16; i++)
        {
            hashChildren.Add(Node(st, "UInt8", $"bytes[{i}]", 1));
        }

        var root = Node(st, "MonoScript", "Base", -1, 5, 0, 0, [
            StringNode(st, "m_Name"),
            Node(st, "int", "m_ExecutionOrder", 4),
            Node(st, "Hash128", "m_PropertiesHash", 16, 1, 0, 0, hashChildren),
            StringNode(st, "m_ClassName"),
            StringNode(st, "m_Namespace"),
            StringNode(st, "m_AssemblyName"),
        ]);

        return new ClassDatabaseType
        {
            ClassId = (int)AssetClassID.MonoScript,
            Name = st.AddString("MonoScript"),
            BaseName = st.AddString("TextAsset"),
            Flags = ClassFileTypeFlags.HasReleaseRootNode,
            ReleaseRootNode = root,
        };
    }

    private static ClassDatabaseType BuildGameObject(ClassDatabaseStringTable st)
    {
        var root = Node(st, "GameObject", "Base", -1, 6, 0, 0, [
            Node(st, "vector", "m_Component", -1, 1, 0, 0, [
                Node(st, "Array", "Array", -1, 1, 1, AlignFlag, [
                    Node(st, "int", "size", 4),
                    Node(st, "ComponentPair", "data", 12, 1, 0, 0, [
                        PPtrNode(st, "PPtr<Component>", "component"),
                    ]),
                ]),
            ]),
            Node(st, "unsigned int", "m_Layer", 4),
            StringNode(st, "m_Name"),
            Node(st, "UInt16", "m_Tag", 2),
            Node(st, "bool", "m_IsActive", 1),
        ]);

        return new ClassDatabaseType
        {
            ClassId = (int)AssetClassID.GameObject,
            Name = st.AddString("GameObject"),
            BaseName = st.AddString("EditorExtension"),
            Flags = ClassFileTypeFlags.HasReleaseRootNode,
            ReleaseRootNode = root,
        };
    }

    private static ClassDatabaseType BuildAssetBundle(ClassDatabaseStringTable st)
    {
        var assetInfo = (string fieldName) => Node(st, "AssetInfo", fieldName, 20, 1, 0, 0, [
            Node(st, "int", "preloadIndex", 4),
            Node(st, "int", "preloadSize", 4),
            PPtrNode(st, "PPtr<Object>", "asset"),
        ]);

        var root = Node(st, "AssetBundle", "Base", -1, 3, 0, 0, [
            StringNode(st, "m_Name"),
            Node(st, "vector", "m_PreloadTable", -1, 1, 0, 0, [
                Node(st, "Array", "Array", -1, 1, 1, AlignFlag, [
                    Node(st, "int", "size", 4),
                    PPtrNode(st, "PPtr<Object>", "data"),
                ]),
            ]),
            Node(st, "map", "m_Container", -1, 1, 0, 0, [
                Node(st, "Array", "Array", -1, 1, 1, 0, [
                    Node(st, "int", "size", 4),
                    Node(st, "pair", "data", -1, 1, 0, 0, [
                        StringNode(st, "first"),
                        assetInfo("second"),
                    ]),
                ]),
            ]),
            assetInfo("m_MainAsset"),
            Node(st, "unsigned int", "m_RuntimeCompatibility", 4),
            StringNode(st, "m_AssetBundleName"),
            Node(st, "vector", "m_Dependencies", -1, 1, 0, 0, [
                Node(st, "Array", "Array", -1, 1, 1, AlignFlag, [
                    Node(st, "int", "size", 4),
                    StringNode(st, "data"),
                ]),
            ]),
        ]);

        return new ClassDatabaseType
        {
            ClassId = (int)AssetClassID.AssetBundle,
            Name = st.AddString("AssetBundle"),
            BaseName = st.AddString("NamedObject"),
            Flags = ClassFileTypeFlags.HasReleaseRootNode,
            ReleaseRootNode = root,
        };
    }
}

/// <summary>
/// Thread-safe, lazy wrapper around AssetsTools.NET.Cpp2IL's Cpp2IlTempGenerator.
/// </summary>
internal sealed class SafeCpp2IlGenerator : IMonoBehaviourTemplateGenerator, IDisposable
{
    private static readonly object StaticLibLock = new();
    private readonly string _globalMetadataPath;
    private readonly string _gameAssemblyPath;
    private readonly UnityVersion _unityVersion;
    private readonly ConcurrentDictionary<(string Assembly, string Namespace, string ClassName), AssetTypeTemplateField?> _cache = new();
    private Cpp2IlTempGenerator? _inner;
    private bool _disposed;

    public SafeCpp2IlGenerator(string globalMetadataPath, string gameAssemblyPath, string unityVersion)
    {
        _globalMetadataPath = globalMetadataPath;
        _gameAssemblyPath = gameAssemblyPath;
        _unityVersion = new UnityVersion(unityVersion);
    }

    public AssetTypeTemplateField? GetTemplateField(
        AssetTypeTemplateField baseField,
        string assemblyName,
        string nameSpace,
        string className,
        UnityVersion unityVersion)
    {
        var cleanAsm = assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? assemblyName[..^4]
            : assemblyName;
        var key = (cleanAsm, nameSpace, className);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached?.Clone();
        }

        lock (StaticLibLock)
        {
            if (_cache.TryGetValue(key, out cached))
            {
                return cached?.Clone();
            }

            EnsureInitializedLocked();
            var generated = _inner!.GetTemplateField(baseField.Clone(), cleanAsm, nameSpace, className, unityVersion);
            _cache[key] = generated;
            return generated?.Clone();
        }
    }

    private void EnsureInitializedLocked()
    {
        if (_inner is not null)
        {
            return;
        }

        // Silence LibCpp2IL console chatter so CLI stdout stays clean.
        LibLogger.Writer = new SilentLibCpp2IlLogWriter();
        var gen = new Cpp2IlTempGenerator(_globalMetadataPath, _gameAssemblyPath);
        gen.SetUnityVersion(_unityVersion.major, _unityVersion.minor, _unityVersion.patch);
        gen.InitializeCpp2IL();
        _inner = gen;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (StaticLibLock)
        {
            _inner?.Dispose();
            _inner = null;
        }
    }

    private sealed class SilentLibCpp2IlLogWriter : LogWriter
    {
        public override void Info(string message)
        {
        }

        public override void Warn(string message)
        {
        }

        public override void Error(string message)
        {
        }

        public override void Verbose(string message)
        {
        }
    }
}
