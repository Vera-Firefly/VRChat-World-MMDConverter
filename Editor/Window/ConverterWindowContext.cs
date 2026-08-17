using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRChat.MmdPreview.Pmx2Fbx.Editor;

internal enum ConverterEditorLanguage
{
    SimplifiedChinese,
    English,
    Japanese
}

internal enum BuiltInDebugOutput
{
    Humanoid = 0,
    GenericTransform = 1,
    TargetModelFkFidelity = 2,
    TargetCalibratedHumanoid = 3,
    TargetModelLowerBodyFkFidelity = 4
}

internal enum MotionOutputType
{
    Humanoid,
    TargetCalibratedHumanoid
}

internal enum MotionStorageLocation
{
    FollowSource,
    Default,
    Custom
}

[Serializable]
internal sealed class ConverterWindowContext
{
    private const string LanguagePreference = "VmdConverter.Language";

    [SerializeField] private VmdMotionConverter.ModelParserMethod modelParserMethod;
    [SerializeField] private float curveReductionError = 0.0001f;
    [SerializeField] private MotionOutputType motionOutputType;
    [SerializeField] private float fkRotationErrorDegrees = 0.01f;
    [SerializeField] private float fkPositionError = 0.00005f;
    [SerializeField] private bool targetUpperLimbInverseSolve;
    internal VmdMotionConverter.ModelParserMethod ModelParserMethod
    {
        get => modelParserMethod;
        set => modelParserMethod = value;
    }
    internal float CurveReductionError
    {
        get => curveReductionError;
        set => curveReductionError = value;
    }
    internal MotionOutputType MotionOutputType
    {
        get => motionOutputType;
        set => motionOutputType = value;
    }
    internal float FkRotationErrorDegrees
    {
        get => fkRotationErrorDegrees;
        set => fkRotationErrorDegrees = value;
    }
    internal float FkPositionError
    {
        get => fkPositionError;
        set => fkPositionError = value;
    }
    internal bool TargetUpperLimbInverseSolve
    {
        get => targetUpperLimbInverseSolve;
        set => targetUpperLimbInverseSolve = value;
    }
    internal ConverterEditorLanguage Language
    {
        get => (ConverterEditorLanguage)EditorPrefs.GetInt(
            LanguagePreference,
            (int)ConverterEditorLanguage.SimplifiedChinese);
        set => EditorPrefs.SetInt(LanguagePreference, (int)value);
    }

    internal string L(string chinese, string english, string japanese)
    {
        switch (Language)
        {
            case ConverterEditorLanguage.English:
                return english;
            case ConverterEditorLanguage.Japanese:
                return japanese;
            default:
                return chinese;
        }
    }
}

[Serializable]
internal abstract class ConverterFeaturePage
{
    protected const float VmdFrameRate = 30f;
    protected const string DefaultMotionStorageFolder =
        "Assets/VeraFirefly/VMD Converter Output";
    protected static readonly Quaternion MmdToUnityRotation = Quaternion.Euler(0f, 180f, 0f);

    [NonSerialized] private ConverterWindowContext attachedContext;

    protected ConverterWindowContext Context =>
        attachedContext ?? throw new InvalidOperationException("The converter page is not attached.");
    protected ConverterEditorLanguage Language => Context.Language;
    protected VmdMotionConverter.ModelParserMethod modelParserMethod
    {
        get => Context.ModelParserMethod;
        set => Context.ModelParserMethod = value;
    }
    protected float curveReductionError
    {
        get => Context.CurveReductionError;
        set => Context.CurveReductionError = value;
    }
    protected MotionOutputType motionOutputType
    {
        get => Context.MotionOutputType;
        set => Context.MotionOutputType = value;
    }
    protected float fkRotationErrorDegrees
    {
        get => Context.FkRotationErrorDegrees;
        set => Context.FkRotationErrorDegrees = value;
    }
    protected float fkPositionError
    {
        get => Context.FkPositionError;
        set => Context.FkPositionError = value;
    }
    protected bool targetUpperLimbInverseSolve
    {
        get => Context.TargetUpperLimbInverseSolve;
        set => Context.TargetUpperLimbInverseSolve = value;
    }
    protected bool IsTargetCalibratedHumanoid =>
        motionOutputType == MotionOutputType.TargetCalibratedHumanoid;

    internal virtual void Attach(ConverterWindowContext context)
    {
        attachedContext = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected virtual void OnStorageSettingChanged()
    {
    }

    protected bool ConfirmOverwrite(string title, string path, string confirm)
    {
        return EditorUtility.DisplayDialog(
            L("覆盖现有文件", title, "既存ファイルを上書き"),
            L("目标文件已经存在：\n", "The target already exists:\n", "対象ファイルは既に存在します:\n") +
            path,
            L("覆盖", confirm, "上書き"),
            L("取消", "Cancel", "キャンセル"));
    }

    protected static void SelectExportedClip(AnimationClip clip)
    {
        if (clip == null) return;
        Selection.activeObject = clip;
        EditorGUIUtility.PingObject(clip);
    }

    protected void DrawStorageLocationSetting(
        string label,
        ref MotionStorageLocation location,
        ref string customFolder,
        string customLabel)
    {
        string[] labels =
        {
            L("跟随文件: 文件所在位置", "Follow File: File Location", "ファイルに従う: ファイルの場所"),
            L("默认: " + EscapePopupPath(DefaultMotionStorageFolder + "/"),
                "Default: " + EscapePopupPath(DefaultMotionStorageFolder + "/"),
                "既定: " + EscapePopupPath(DefaultMotionStorageFolder + "/")),
            L("自定义", "Custom", "カスタム")
        };
        EditorGUI.BeginChangeCheck();
        location = (MotionStorageLocation)EditorGUILayout.Popup(label, (int)location, labels);
        if (EditorGUI.EndChangeCheck()) OnStorageSettingChanged();
        if (location != MotionStorageLocation.Custom) return;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            customFolder = EditorGUILayout.TextField(customLabel, customFolder ?? string.Empty);
            if (EditorGUI.EndChangeCheck()) OnStorageSettingChanged();
            GUIContent folderIcon = EditorGUIUtility.IconContent("Folder Icon");
            folderIcon.tooltip = L("选择Assets内的文件夹", "Select a folder inside Assets", "Assets内のフォルダーを選択");
            if (GUILayout.Button(folderIcon, GUILayout.Width(28f), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                string selectedFolder = EditorUtility.OpenFolderPanel(
                    customLabel,
                    AssetFolderToPhysicalPath(customFolder),
                    string.Empty);
                if (!string.IsNullOrEmpty(selectedFolder))
                {
                    if (TryPhysicalFolderToAssetPath(selectedFolder, out string assetFolder))
                    {
                        customFolder = assetFolder;
                        OnStorageSettingChanged();
                        GUI.FocusControl(null);
                    }
                    else
                    {
                        ShowDialog(
                            customLabel,
                            L("请选择项目Assets目录内的文件夹。",
                                "Select a folder inside the project's Assets directory.",
                                "プロジェクトのAssetsフォルダー内を選択してください。"));
                    }
                }
            }
        }

        if (!TryNormalizeAssetFolder(customFolder, out _))
            EditorGUILayout.LabelField(
                L("路径必须位于Assets目录内。",
                    "The path must be inside Assets.",
                    "パスはAssets内である必要があります。"),
                EditorStyles.miniLabel);
    }

    protected PmxDebugTargetFkBakeOptions Debug_CreateFkBakeOptions()
    {
        return new PmxDebugTargetFkBakeOptions
        {
            RotationErrorDegrees = Mathf.Clamp(fkRotationErrorDegrees, 0f, 0.1f),
            PositionError = Mathf.Clamp(fkPositionError, 0f, 0.001f),
            BakeRootTranslation = true,
            UpperLimbInverseSolve = targetUpperLimbInverseSolve
        };
    }

    protected static bool IsAssetExtension(string path, string extension)
    {
        return !string.IsNullOrEmpty(path) &&
               path.StartsWith("Assets/", StringComparison.Ordinal) &&
               path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    protected static bool HasValidHumanoidAnimator(GameObject model)
    {
        if (model == null) return false;
        Animator animator = model.GetComponent<Animator>() ?? model.GetComponentInChildren<Animator>(true);
        return animator != null && animator.avatar != null &&
               animator.avatar.isValid && animator.avatar.isHuman;
    }

    protected static string GetProgressStage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        int separator = message.IndexOf(' ');
        return separator > 0 ? message.Substring(0, separator) : message;
    }

    protected void NormalizeModelParser()
    {
        IReadOnlyList<VmdMotionConverter.ModelParserMethod> parsers =
            VmdMotionConverter.GetAvailableModelParsers();
        if (!parsers.Contains(modelParserMethod))
            modelParserMethod = VmdMotionConverter.ModelParserMethod.BuiltInManaged;
    }

    protected string GetModelParserLabel(VmdMotionConverter.ModelParserMethod parser)
    {
        switch (parser)
        {
            case VmdMotionConverter.ModelParserMethod.MoyuerAvatarToolkit:
                return "Moyuer Avatar Toolkit (PMX2FBX)";
            default:
                return L("内置解析方案", "Built-in Parser", "内蔵解析方式");
        }
    }

    protected static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024L) return (bytes / 1024d).ToString("0.##") + " KB";
        return (bytes / (1024d * 1024d)).ToString("0.##") + " MB";
    }

    protected string L(string chinese, string english, string japanese)
    {
        return Context.L(chinese, english, japanese);
    }

    protected void ShowDialog(string title, string message)
    {
        EditorUtility.DisplayDialog(title, message, L("确定", "OK", "OK"));
    }

    protected void ShowBatchResult(
        string title,
        int succeeded,
        int skipped,
        IReadOnlyList<string> errors,
        bool cancelled)
    {
        string message = L(
            $"成功：{succeeded}\n跳过：{skipped}\n失败：{errors.Count}",
            $"Succeeded: {succeeded}\nSkipped: {skipped}\nFailed: {errors.Count}",
            $"成功：{succeeded}\nスキップ：{skipped}\n失敗：{errors.Count}");
        if (cancelled)
            message += "\n" + L("批处理已取消。", "Batch operation cancelled.", "バッチ処理をキャンセルしました。");
        if (errors.Count > 0)
        {
            message += "\n\n" + L("错误：", "Errors:", "エラー：") + "\n" +
                       string.Join("\n", errors.Take(6));
            if (errors.Count > 6)
                message += $"\n... (+{errors.Count - 6})";
        }
        ShowDialog(title, message);
    }

    protected static bool AddVmdAsset(List<DefaultAsset> assets, DefaultAsset asset)
    {
        string path = GetVmdAssetPath(asset);
        if (string.IsNullOrEmpty(path)) return false;
        if (assets.Any(existing => string.Equals(
                GetVmdAssetPath(existing),
                path,
                StringComparison.OrdinalIgnoreCase)))
            return false;
        assets.Add(asset);
        return true;
    }

    protected static List<string> GetVmdAssetPaths(IEnumerable<DefaultAsset> assets)
    {
        return assets
            .Select(GetVmdAssetPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    protected string GetVmdDisplayName(DefaultAsset asset)
    {
        string path = GetVmdAssetPath(asset);
        return string.IsNullOrEmpty(path)
            ? L("（空）", "(Empty)", "（空）")
            : Path.GetFileNameWithoutExtension(path);
    }

    protected static bool IsVmdAsset(DefaultAsset asset)
    {
        return !string.IsNullOrEmpty(GetVmdAssetPath(asset));
    }

    protected static string GetVmdAssetPath(DefaultAsset asset)
    {
        if (asset == null) return string.Empty;
        string path = AssetDatabase.GetAssetPath(asset);
        return path.StartsWith("Assets/", StringComparison.Ordinal) &&
               path.EndsWith(".vmd", StringComparison.OrdinalIgnoreCase)
            ? path
            : string.Empty;
    }

    protected static bool TryResolveStorageFolder(
        MotionStorageLocation location, string customFolder, out string folder)
    {
        switch (location)
        {
            case MotionStorageLocation.FollowSource:
                folder = null;
                return true;
            case MotionStorageLocation.Default:
                folder = DefaultMotionStorageFolder;
                return true;
            case MotionStorageLocation.Custom:
                return TryNormalizeAssetFolder(customFolder, out folder);
            default:
                folder = null;
                return false;
        }
    }

    protected static bool TryNormalizeAssetFolder(string folder, out string normalized)
    {
        normalized = (folder ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalized, "Assets", StringComparison.Ordinal) ||
               normalized.StartsWith("Assets/", StringComparison.Ordinal);
    }

    protected static string EscapePopupPath(string path)
    {
        // Popup does not support escaping its '/' submenu separator. Use backslashes for the
        // displayed path only; stored Unity Asset paths continue to use forward slashes.
        return (path ?? string.Empty).Replace("/", "\\");
    }

    protected static string AssetFolderToPhysicalPath(string assetFolder)
    {
        if (!TryNormalizeAssetFolder(assetFolder, out string normalized))
            return Application.dataPath;
        if (string.Equals(normalized, "Assets", StringComparison.Ordinal))
            return Application.dataPath;
        return Path.Combine(
            Application.dataPath,
            normalized.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar));
    }

    protected static bool TryPhysicalFolderToAssetPath(string physicalFolder, out string assetFolder)
    {
        string assetsRoot = Path.GetFullPath(Application.dataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string selected = Path.GetFullPath(physicalFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(selected, assetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            assetFolder = "Assets";
            return true;
        }

        string assetsPrefix = assetsRoot + Path.DirectorySeparatorChar;
        if (!selected.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            assetFolder = string.Empty;
            return false;
        }

        assetFolder = "Assets/" + selected.Substring(assetsPrefix.Length).Replace('\\', '/');
        return true;
    }
}
