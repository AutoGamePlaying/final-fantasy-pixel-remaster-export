using AssetsTools.NET.Extra;

namespace FfprExport;

internal sealed record AssetClassification(
    string Decision,
    string Category,
    string Reason);

internal static class AssetClassifier
{
    public const string DecisionExportText = "export_text";
    public const string DecisionExportJson = "export_json";
    public const string DecisionSkipImage = "skip_image";
    public const string DecisionSkipAudio = "skip_audio";
    public const string DecisionSkipVideo = "skip_video";
    public const string DecisionSkipFont = "skip_font";
    public const string DecisionSkipShader = "skip_shader";
    public const string DecisionSkipAnimation = "skip_animation";
    public const string DecisionSkipSceneObject = "skip_scene_object";
    public const string DecisionSkipOther = "skip_other";

    private const string SerialPrefix = "Assets/GameAssets/Serial/";
    private const string GameAssetsPrefix = "Assets/GameAssets/";
    private const string AssetsPrefix = "Assets/";

    public static bool IsExportDecision(string decision)
    {
        return decision is DecisionExportText or DecisionExportJson;
    }

    public static bool IsAudioTextAssetPath(string? containerPath)
    {
        if (string.IsNullOrEmpty(containerPath))
        {
            return false;
        }

        var normalized = containerPath.Replace('\\', '/');
        return normalized.Contains("/Res/Audio/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".mab.bytes", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".sab.bytes", StringComparison.OrdinalIgnoreCase);
    }

    public static AssetClassification Classify(int typeId, string? containerPath = null)
    {
        var classId = (AssetClassID)typeId;
        switch (classId)
        {
            case AssetClassID.TextAsset:
                if (IsAudioTextAssetPath(containerPath))
                {
                    return new AssetClassification(
                        DecisionSkipAudio,
                        "audio",
                        "音频二进制资源（Square Enix MAB/SAB，封装为 TextAsset）");
                }

                return new AssetClassification(
                    DecisionExportText,
                    "text_asset",
                    "文本/JSON/CSV 结构化资源，按容器路径原样导出");

            case AssetClassID.MonoBehaviour:
                return new AssetClassification(
                    DecisionExportJson,
                    "monobehaviour",
                    "MonoBehaviour/ScriptableObject，通过 TypeTree 或 IL2CPP 元数据导出为 JSON");

            case AssetClassID.Texture2D:
            case AssetClassID.Sprite:
            case AssetClassID.SpriteAtlas:
            case AssetClassID.RenderTexture:
            case AssetClassID.Cubemap:
            case AssetClassID.Texture3D:
            case AssetClassID.Texture2DArray:
            case AssetClassID.SpriteRenderer:
            case AssetClassID.CanvasRenderer:
            case AssetClassID.Mesh:
            case AssetClassID.MeshFilter:
            case AssetClassID.MeshRenderer:
            case AssetClassID.SkinnedMeshRenderer:
            case AssetClassID.TrailRenderer:
            case AssetClassID.ParticleSystem:
            case AssetClassID.ParticleSystemRenderer:
            case AssetClassID.ParticleSystemForceField:
            case AssetClassID.Cloth:
            case AssetClassID.Light:
            case AssetClassID.Flare:
            case AssetClassID.Projector:
            case AssetClassID.LineRenderer:
            case AssetClassID.BillboardAsset:
            case AssetClassID.BillboardRenderer:
                return new AssetClassification(
                    DecisionSkipImage,
                    "image_rendering",
                    "图像、精灵、网格或视觉渲染组件");

            case AssetClassID.Shader:
            case AssetClassID.ComputeShader:
            case AssetClassID.ShaderVariantCollection:
            case AssetClassID.Material:
            case AssetClassID.PhysicMaterial:
            case AssetClassID.PhysicsMaterial2D:
                return new AssetClassification(
                    DecisionSkipShader,
                    "shader_material",
                    "着色器、材质或物理材质");

            case AssetClassID.AudioClip:
            case AssetClassID.AudioMixer:
            case AssetClassID.AudioMixerController:
            case AssetClassID.AudioMixerGroup:
            case AssetClassID.AudioMixerSnapshot:
            case AssetClassID.AudioSource:
            case AssetClassID.AudioListener:
            case AssetClassID.AudioManager:
                return new AssetClassification(
                    DecisionSkipAudio,
                    "audio",
                    "音频资源或音频组件");

            case AssetClassID.VideoClip:
            case AssetClassID.VideoPlayer:
            case AssetClassID.MovieTexture:
                return new AssetClassification(
                    DecisionSkipVideo,
                    "video",
                    "视频资源或视频组件");

            case AssetClassID.Font:
            case AssetClassID.TextMesh:
                return new AssetClassification(
                    DecisionSkipFont,
                    "font",
                    "字体资源或文本网格");

            case AssetClassID.Animation:
            case AssetClassID.AnimationClip:
            case AssetClassID.AnimatorController:
            case AssetClassID.AnimatorOverrideController:
            case AssetClassID.Animator:
            case AssetClassID.Avatar:
                return new AssetClassification(
                    DecisionSkipAnimation,
                    "animation",
                    "动画片段与动画状态机（视觉表现）");

            case AssetClassID.GameObject:
            case AssetClassID.Transform:
            case AssetClassID.RectTransform:
            case AssetClassID.Canvas:
            case AssetClassID.CanvasGroup:
            case AssetClassID.Camera:
            case AssetClassID.BoxCollider2D:
            case AssetClassID.CircleCollider2D:
            case AssetClassID.PolygonCollider2D:
            case AssetClassID.MeshCollider:
            case AssetClassID.SphereCollider:
            case AssetClassID.Rigidbody2D:
            case AssetClassID.PositionConstraint:
            case AssetClassID.RenderSettings:
            case AssetClassID.LightmapSettings:
            case AssetClassID.NavMeshSettings:
            case AssetClassID.PreloadData:
            case AssetClassID.AssetBundle:
            case AssetClassID.MonoScript:
            case AssetClassID.PlayableDirector:
            case AssetClassID.SortingGroup:
            case AssetClassID.SpriteMask:
            case AssetClassID.Tilemap:
            case AssetClassID.TilemapRenderer:
            case AssetClassID.Grid:
            case AssetClassID.LightProbes:
            case AssetClassID.ReflectionProbe:
            case AssetClassID.OcclusionCullingSettings:
            case AssetClassID.FlareLayer:
                return new AssetClassification(
                    DecisionSkipSceneObject,
                    "unity_runtime",
                    "Unity 场景/预制体/资源包骨架对象（容器与脚本元数据已整合进索引与 MonoBehaviour 输出）");

            default:
                return new AssetClassification(
                    DecisionSkipOther,
                    "other",
                    "其他引擎内部类型");
        }
    }

    public static string GetTypeName(int typeId)
    {
        var cls = (AssetClassID)typeId;
        return Enum.IsDefined(cls) ? cls.ToString() : $"UnknownType_{typeId}";
    }

    /// <summary>
    /// Maps a Unity Addressables container path to a safe relative output path matching legacy FF5 layout:
    /// Assets/GameAssets/Serial/Res/... -> Res/...
    /// Assets/GameAssets/Serial/Data/... -> Data/...
    /// Assets/GameAssets/Common/... -> Common/...
    /// Assets/GameAssets/AssetsPath.json -> AssetsPath.json
    /// </summary>
    public static string MapContainerPathToRelativeOutput(string containerPath)
    {
        var normalized = containerPath.Replace('\\', '/').TrimStart('/');
        string relative;
        var serialIdx = normalized.IndexOf(SerialPrefix, StringComparison.OrdinalIgnoreCase);
        if (serialIdx >= 0)
        {
            relative = normalized[(serialIdx + SerialPrefix.Length)..];
        }
        else
        {
            var gaIdx = normalized.IndexOf(GameAssetsPrefix, StringComparison.OrdinalIgnoreCase);
            if (gaIdx >= 0)
            {
                relative = normalized[(gaIdx + GameAssetsPrefix.Length)..];
            }
            else if (normalized.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = normalized[AssetsPrefix.Length..];
            }
            else
            {
                relative = normalized;
            }
        }

        return SanitizeRelativePath(relative, containerPath);
    }

    public static string SanitizeRelativePath(string relativePath, string originalForError)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidDataException($"容器路径映射后为空：{originalForError}");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg is "." or "..")
            {
                throw new InvalidDataException($"容器路径包含非法越界片段：{originalForError}");
            }
        }

        return string.Join('/', segments);
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "unnamed" : result;
    }

    public static string ExtractDirectoryBucket(string? containerPath, string sourceKind, string sourceFile)
    {
        if (!string.IsNullOrEmpty(containerPath))
        {
            var normalized = containerPath.Replace('\\', '/').TrimStart('/');
            var serialIdx = normalized.IndexOf(SerialPrefix, StringComparison.OrdinalIgnoreCase);
            if (serialIdx >= 0)
            {
                var rel = normalized[(serialIdx + SerialPrefix.Length)..];
                var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : parts[0];
            }

            var gaIdx = normalized.IndexOf(GameAssetsPrefix, StringComparison.OrdinalIgnoreCase);
            if (gaIdx >= 0)
            {
                var rel = normalized[(gaIdx + GameAssetsPrefix.Length)..];
                var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 ? $"{parts[0]}/{parts[1]}/{parts[2]}"
                    : parts.Length >= 2 ? $"{parts[0]}/{parts[1]}"
                    : parts[0];
            }

            var fallbackParts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return fallbackParts.Length >= 2 ? $"{fallbackParts[0]}/{fallbackParts[1]}" : fallbackParts[0];
        }

        return sourceKind == "data"
            ? $"_data/{sourceFile}"
            : "_bundle_internal";
    }
}
