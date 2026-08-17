using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[Serializable]
internal sealed class VmdCameraPage : ConverterFeaturePage
{
    [SerializeField] private List<DefaultAsset> cameraVmdAssets = new List<DefaultAsset>();
    [SerializeField] private Vector2 cameraVmdListScroll;
    [SerializeField] private Vector2 pageScroll;
    [SerializeField] private MotionStorageLocation cameraOutputStorageLocation;
    [SerializeField] private string customCameraOutputStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private float unitScale = 0.08f;

    internal void OnEnable(ConverterWindowContext context, DefaultAsset selected, bool acceptSelection)
    {
        Attach(context);
        if (cameraVmdAssets == null) cameraVmdAssets = new List<DefaultAsset>();
        if (acceptSelection && IsVmdAsset(selected))
            AddVmdAsset(cameraVmdAssets, selected);
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        if (cameraVmdAssets == null) cameraVmdAssets = new List<DefaultAsset>();
        cameraVmdAssets.RemoveAll(asset => asset == null);
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawCameraConverter();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        Attach(context);
        EditorGUILayout.Space(8f);
        List<string> sourcePaths = GetVmdAssetPaths(cameraVmdAssets);
        using (new EditorGUI.DisabledScope(
                   sourcePaths.Count == 0 || !TryGetCameraOutputStorageFolder(out _)))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                ConvertCameras(sourcePaths);
        }
    }

    private void DrawCameraConverter()
    {
        ConverterWindowGui.DrawSectionTitle(L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"));
        DrawVmdFileList(cameraVmdAssets, ref cameraVmdListScroll);

        ConverterWindowGui.DrawOutlinedSetting(() => DrawStorageLocationSetting(
            L("输出位置", "Output Location", "出力場所"),
            ref cameraOutputStorageLocation,
            ref customCameraOutputStorageFolder,
            L("输出存储位置", "Output Storage Location", "出力保存場所")));

        unitScale = Mathf.Max(0.000001f, EditorGUILayout.FloatField(
            new GUIContent(
                L("单位缩放", "Unit Scale", "単位スケール"),
                L("VRChat 常用 0.08；保留 MMD 原始单位时填写 1。",
                    "Use 0.08 for VRChat, or 1 for original MMD units.",
                    "VRChat は通常 0.08、MMD の元単位は 1。")),
            unitScale));
        int cameraFileCount = GetVmdAssetPaths(cameraVmdAssets).Count;
        EditorGUILayout.LabelField(
            L("输出", "Output", "出力"),
            L($"将生成 {cameraFileCount} 个镜头动画",
                $"{cameraFileCount} camera animations will be created",
                $"{cameraFileCount} 個のカメラアニメーションを生成します"));

        EditorGUILayout.HelpBox(
            L("输出动画已适配 MikuMikuDanceStation：根节点控制中心点与旋转，Distance 控制镜头距离，Distance/Camera 控制视野角与透视模式。",
                "The output is bound for MikuMikuDanceStation: the root controls center/rotation, Distance controls camera distance, and Distance/Camera controls lens settings.",
                "出力はMikuMikuDanceStation向けです。ルートが中心と回転、Distanceが距離、Distance/Cameraがレンズ設定を制御します。"),
            MessageType.Info);

    }

    private bool DrawVmdFileList(List<DefaultAsset> assets, ref Vector2 scroll)
    {
        bool changed = false;
        if (assets.Count > 0)
        {
            const float maxListHeight = 220f;
            float rowHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            bool useScrollView = assets.Count * rowHeight > maxListHeight;
            if (useScrollView)
            {
                scroll = EditorGUILayout.BeginScrollView(
                    scroll,
                    false,
                    false,
                    GUILayout.Height(maxListHeight));
            }

            try
            {
                for (int index = 0; index < assets.Count; index++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DefaultAsset next = (DefaultAsset)EditorGUILayout.ObjectField(
                            assets[index],
                            typeof(DefaultAsset),
                            false);
                        if (next != assets[index])
                        {
                            if (next == null || IsVmdAsset(next))
                            {
                                string nextPath = GetVmdAssetPath(next);
                                bool duplicate = !string.IsNullOrEmpty(nextPath) && assets
                                    .Where((_, otherIndex) => otherIndex != index)
                                    .Any(other => string.Equals(
                                        GetVmdAssetPath(other),
                                        nextPath,
                                        StringComparison.OrdinalIgnoreCase));
                                if (!duplicate)
                                {
                                    assets[index] = next;
                                    changed = true;
                                }
                            }
                            else
                            {
                                ShowDialog(
                                    L("VMD文件", "VMD Files", "VMDファイル"),
                                    L("请选择Assets目录中的.vmd文件。",
                                        "Select .vmd files inside Assets.",
                                        "Assets内の.vmdファイルを選択してください。"));
                            }
                        }

                        if (GUILayout.Button("−", GUILayout.Width(24f)))
                        {
                            assets.RemoveAt(index);
                            changed = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (useScrollView)
                    EditorGUILayout.EndScrollView();
            }
        }

        Rect dropRect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
        var dropStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
        GUI.Box(
            dropRect,
            L("拖入一个或多个VMD文件", "Drop one or more VMD files", "1つ以上のVMDファイルをドロップ"),
            dropStyle);
        Event currentEvent = Event.current;
        if (dropRect.Contains(currentEvent.mousePosition) &&
            (currentEvent.type == EventType.DragUpdated || currentEvent.type == EventType.DragPerform))
        {
            bool hasVmd = DragAndDrop.objectReferences
                .OfType<DefaultAsset>()
                .Any(IsVmdAsset);
            DragAndDrop.visualMode = hasVmd ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (currentEvent.type == EventType.DragPerform && hasVmd)
            {
                DragAndDrop.AcceptDrag();
                foreach (DefaultAsset asset in DragAndDrop.objectReferences.OfType<DefaultAsset>())
                    changed |= AddVmdAsset(assets, asset);
            }
            currentEvent.Use();
        }

        return changed;
    }

    private void ConvertCameras(IReadOnlyList<string> sourcePaths)
    {
        if (!TryGetCameraOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The camera output storage folder must be inside Assets.");
        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        string lastOutputPath = string.Empty;
        var writtenPaths = new List<string>(sourcePaths.Count);

        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            string outputPath = VmdCameraConverter.GetOutputPath(sourcePath, outputStorageFolder);
            try
            {
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath) != null &&
                    !EditorUtility.DisplayDialog(
                        L("覆盖镜头动画", "Overwrite Camera Animation", "カメラアニメーションを上書き"),
                        L("目标文件已经存在：\n", "The target already exists:\n", "対象ファイルは既に存在します:\n") +
                        outputPath,
                        L("覆盖", "Overwrite", "上書き"),
                        L("取消", "Cancel", "キャンセル")))
                {
                    skipped++;
                    continue;
                }

                double lastProgressUpdate = double.NegativeInfinity;
                string lastProgressStage = string.Empty;
                Action<float, string> progress = (value, message) =>
                {
                    double now = EditorApplication.timeSinceStartup;
                    string stage = GetProgressStage(message);
                    bool stageChanged = !string.Equals(stage, lastProgressStage, StringComparison.Ordinal);
                    if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.08d) return;
                    lastProgressUpdate = now;
                    lastProgressStage = stage;
                    float overall = (fileIndex + Mathf.Clamp01(value)) /
                                    Mathf.Max(1f, sourcePaths.Count);
                    bool cancelRequested = EditorUtility.DisplayCancelableProgressBar(
                        L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"),
                        $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)} — {message}",
                        overall);
                    if (cancelRequested && value < 0.96f)
                        throw new OperationCanceledException();
                };

                lastClip = VmdCameraConverter.ConvertToAsset(
                    sourcePath, outputPath, unitScale, progress, true);
                lastOutputPath = outputPath;
                writtenPaths.Add(outputPath);
                converted++;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add(Path.GetFileName(sourcePath) + ": " + exception.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        if (writtenPaths.Count > 0)
        {
            try
            {
                EditorUtility.DisplayProgressBar(
                    L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"),
                    L("正在保存批量镜头动画", "Saving camera animation batch", "カメラアニメーションを保存中"),
                    0.99f);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AnimationClip imported = AssetDatabase.LoadAssetAtPath<AnimationClip>(lastOutputPath);
                if (imported != null) lastClip = imported;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add("Batch asset write failed: " + exception.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        if (lastClip != null)
        {
            Selection.activeObject = lastClip;
            EditorGUIUtility.PingObject(lastClip);
        }
        ShowBatchResult(
            L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private bool TryGetCameraOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            cameraOutputStorageLocation, customCameraOutputStorageFolder, out folder);
    }
}
