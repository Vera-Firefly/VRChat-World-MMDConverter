using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRChat.MmdPreview.Pmx2Fbx.Editor;

[Serializable]
internal sealed class PmxSettingsPage : ConverterFeaturePage
{
    [SerializeField] private Vector2 pageScroll;
    [SerializeField] private DefaultAsset debugPmxAsset;
    [SerializeField] private DefaultAsset debugVmdAsset;
    [SerializeField] private GameObject debugSkeletonRoot;
    [SerializeField] private GameObject debugMorphTargetRoot;
    [SerializeField] private BuiltInDebugOutput debugOutput = BuiltInDebugOutput.Humanoid;
    [SerializeField] private float debugSampleRate = 30f;
    [SerializeField] private float debugPositionScale = 1f;
    [SerializeField] private PmxCoordinateConversion debugCoordinateConversion =
        PmxCoordinateConversion.RotateY180;
    [SerializeField] private bool debugBakeMorphs = true;

    internal void OnEnable(ConverterWindowContext context)
    {
        Attach(context);
        NormalizeModelParser();
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawPmxSettings();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawPmxSettings()
    {
        NormalizeModelParser();
        ConverterWindowGui.DrawSectionTitle(L("PMX转换设置", "PMX Conversion Settings", "PMX変換設定"));

        IReadOnlyList<VmdMotionConverter.ModelParserMethod> parsers =
            VmdMotionConverter.GetAvailableModelParsers();
        string[] parserLabels = parsers.Select(GetModelParserLabel).ToArray();
        int currentIndex = Mathf.Max(0, parsers.ToList().IndexOf(modelParserMethod));
        int nextIndex;
        using (new EditorGUI.DisabledScope(IsTargetCalibratedHumanoid))
            nextIndex = EditorGUILayout.Popup(
                L("模型解析方式", "Model Parser", "モデル解析方式"),
                currentIndex,
                parserLabels);
        modelParserMethod = parsers[Mathf.Clamp(nextIndex, 0, parsers.Count - 1)];

        if (modelParserMethod != VmdMotionConverter.ModelParserMethod.BuiltInManaged)
            return;

        EditorGUILayout.Space(8f);
        Debug_DrawBuiltInSettings();
        EditorGUILayout.Space(14f);
        DrawPmxCacheManagement();
    }

    private void Debug_DrawBuiltInSettings()
    {
        ConverterWindowGui.DrawSectionTitle(L("内置解析方案调试", "Built-in Parser Debug", "内蔵解析方式のデバッグ"));
        debugPmxAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("PMX文件", "PMX File", "PMXファイル"),
            debugPmxAsset,
            typeof(DefaultAsset),
            false);
        debugVmdAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("VMD文件", "VMD File", "VMDファイル"),
            debugVmdAsset,
            typeof(DefaultAsset),
            false);

        BuiltInDebugOutput[] outputValues =
        {
            BuiltInDebugOutput.Humanoid,
            BuiltInDebugOutput.GenericTransform,
            BuiltInDebugOutput.TargetModelFkFidelity,
            BuiltInDebugOutput.TargetModelLowerBodyFkFidelity,
            BuiltInDebugOutput.TargetCalibratedHumanoid
        };
        string[] outputLabels =
        {
            "Humanoid",
            L("通用骨骼", "Generic Transform", "汎用ボーン"),
            L("目标模型 FK 保真模式", "Target-model FK Fidelity", "対象モデルFK忠実モード"),
            L("目标模型 FK 保真模式 (下肢)", "Target-model FK Fidelity (Lower Body)", "対象モデル FK 忠実モード（下半身）"),
            L("目标校准 Humanoid", "Target-calibrated Humanoid", "対象校正Humanoid")
        };
        int outputIndex = Array.IndexOf(outputValues, debugOutput);
        if (outputIndex < 0) outputIndex = 0;
        outputIndex = EditorGUILayout.Popup(
            L("输出类型", "Output", "出力タイプ"),
            outputIndex,
            outputLabels);
        debugOutput = outputValues[Mathf.Clamp(outputIndex, 0, outputValues.Length - 1)];
        if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
        {
            EditorGUILayout.HelpBox(
                L("下肢输出目标骨架逐骨 FK 曲线，其余骨骼由目标校准 Humanoid 姿态实体化。开启“目标上肢反解”时，上肢改用目标 FK 重建后经 HumanPose 反解的结果。",
                    "Writes per-bone target FK curves for the lower body and materializes the remaining bones from the target-calibrated Humanoid pose. When Target Upper-limb Inverse Solve is enabled, upper limbs use target FK reconstruction followed by HumanPose inverse solving.",
                    "下半身は対象骨格のボーン別 FK カーブを出力し、その他のボーンは対象校正 Humanoid 姿勢から実体化します。対象上肢の逆解析を有効にすると、上肢は対象 FK 再構築後の HumanPose 逆解析結果を使用します。"),
                MessageType.Info);
        }

        if (debugOutput == BuiltInDebugOutput.Humanoid)
        {
            EditorGUILayout.LabelField(
                L("源骨架", "Source Skeleton", "ソーススケルトン"),
                L("从 PMX 自动构建", "Built automatically from PMX", "PMXから自動構築"));
            debugMorphTargetRoot = (GameObject)EditorGUILayout.ObjectField(
                L("形态键目标根（可选）", "Morph Target Root (Optional)", "モーフ対象ルート（任意）"),
                debugMorphTargetRoot,
                typeof(GameObject),
                true);
        }
        else
        {
            debugSkeletonRoot = (GameObject)EditorGUILayout.ObjectField(
                debugOutput == BuiltInDebugOutput.TargetModelFkFidelity ||
                debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity ||
                debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid
                    ? L("目标 Humanoid 模型根", "Target Humanoid Model Root", "対象Humanoidモデルルート")
                    : L("现有PMX骨架根", "Existing PMX Skeleton Root", "既存PMXスケルトンルート"),
                debugSkeletonRoot,
                typeof(GameObject),
                true);
        }

        debugSampleRate = EditorGUILayout.Slider(
            L("采样率", "Sample Rate", "サンプルレート"),
            debugSampleRate,
            1f,
            120f);
        debugPositionScale = EditorGUILayout.FloatField(
            L("位置缩放", "Position Scale", "位置スケール"),
            debugPositionScale);
        debugCoordinateConversion = (PmxCoordinateConversion)EditorGUILayout.Popup(
            L("坐标转换", "Coordinates", "座標変換"),
            (int)debugCoordinateConversion,
            new[]
            {
                "MMD → Unity",
                "MMD → Unity + Y 180°",
                "Y 180°",
                L("无", "None", "なし")
            });
        debugBakeMorphs = EditorGUILayout.Toggle(
            L("烘焙同名形态键", "Bake Same-name Morphs", "同名モーフをベイク"),
            debugBakeMorphs);
        targetUpperLimbInverseSolve = EditorGUILayout.Toggle(
            new GUIContent(
                L("目标上肢反解", "Target Upper-limb Inverse Solve", "対象上肢の逆解析"),
                L("影响目标校准 Humanoid 与目标模型 FK 保真。启用后，上肢执行目标 FK 重建并由 HumanPose 反解；默认关闭。",
                    "Affects target-calibrated Humanoid and target-model FK fidelity. Reconstructs target upper-limb FK and inverse-solves it through HumanPose; disabled by default.",
                    "対象校正 Humanoid と対象モデル FK 保持に適用します。対象上肢 FK を再構築して HumanPose から逆解析します。既定では無効です。")),
            targetUpperLimbInverseSolve);
        if (targetUpperLimbInverseSolve)
        {
            EditorGUILayout.HelpBox(
                L("调试模式：会为每个采样帧增加上肢 HumanPose 往返求解，因此转换更慢；极端扭转仍可能发生分支翻转。",
                    "Debug mode: adds an upper-limb HumanPose round trip per sample and is slower; extreme twists may still flip branches.",
                    "デバッグモード：各サンプルで上肢 HumanPose の往復解析を追加するため遅くなります。極端なツイストでは分岐反転が残る場合があります。"),
                MessageType.Warning);
        }
        curveReductionError = EditorGUILayout.Slider(
            new GUIContent(
                "Curve Error",
                L("此项同时应用于VMD动作转换。0表示保留全部采样点。",
                    "This also applies to VMD Motion conversion. Zero keeps every sampled key.",
                    "この設定はVMDモーション変換にも適用されます。0では全サンプルキーを保持します。")),
            curveReductionError,
            0f,
            0.001f);
        if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity ||
            debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
        {
            fkRotationErrorDegrees = EditorGUILayout.Slider(
                L("FK旋转角误差 (度)", "FK Rotation Error (deg)", "FK回転角誤差（度）"),
                fkRotationErrorDegrees,
                0f,
                0.1f);
            fkPositionError = EditorGUILayout.Slider(
                L("FK根位置误差", "FK Root Position Error", "FKルート位置誤差"),
                fkPositionError,
                0f,
                0.001f);
        }

        string pmxPath = AssetDatabase.GetAssetPath(debugPmxAsset);
        string vmdPath = AssetDatabase.GetAssetPath(debugVmdAsset);
        bool canConvert = IsAssetExtension(pmxPath, ".pmx") &&
                          IsAssetExtension(vmdPath, ".vmd") &&
                          (debugOutput == BuiltInDebugOutput.Humanoid || debugSkeletonRoot != null);
        using (new EditorGUI.DisabledScope(!canConvert))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(28f)))
                Debug_ConvertBuiltIn(pmxPath, vmdPath);
        }
    }

    private void DrawPmxCacheManagement()
    {
        ConverterWindowGui.DrawSectionTitle(L("PMX解析缓存管理", "PMX Parse Cache", "PMX解析キャッシュ管理"));
        PmxModelCache.GetDiskCacheStats(out int cacheFiles, out long cacheBytes);
        EditorGUILayout.LabelField(
            L("PMX解析缓存", "PMX Parse Cache", "PMX解析キャッシュ"),
            L($"{cacheFiles} 个文件，{FormatBytes(cacheBytes)}",
                $"{cacheFiles} files, {FormatBytes(cacheBytes)}",
                $"{cacheFiles} ファイル、{FormatBytes(cacheBytes)}"));
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.TextField(
                L("缓存位置", "Cache Location", "キャッシュ場所"),
                PmxModelCache.DiskCacheDirectory);

        if (!GUILayout.Button(
                L("清理 PMX 解析缓存", "Clear PMX Parse Cache", "PMX解析キャッシュを削除"),
                GUILayout.Height(28f)))
            return;

        if (!EditorUtility.DisplayDialog(
                L("清理 PMX 解析缓存", "Clear PMX Parse Cache", "PMX解析キャッシュを削除"),
                L("将清除内存与磁盘中的 PMX 解析缓存。下次转换会重新解析 PMX。",
                    "This clears the PMX parse cache in memory and on disk. The next conversion will parse the PMX again.",
                    "メモリとディスク上の PMX 解析キャッシュを削除します。次回の変換時に PMX を再解析します。"),
                L("清理", "Clear", "削除"),
                L("取消", "Cancel", "キャンセル")))
            return;

        try
        {
            PmxModelCache.ClearAll();
            ShowDialog(
                L("PMX解析缓存", "PMX Parse Cache", "PMX解析キャッシュ"),
                L("缓存已清理。", "Cache cleared.", "キャッシュを削除しました。"));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(
                L("缓存清理失败", "Cache Cleanup Failed", "キャッシュ削除に失敗"),
                exception.Message);
        }
    }

    private void Debug_ConvertBuiltIn(string pmxPath, string vmdPath)
    {
        string defaultName = Path.GetFileNameWithoutExtension(vmdPath) + "_CSharp.anim";
        string outputPath = EditorUtility.SaveFilePanelInProject(
            L("保存MMD动画", "Save MMD Animation", "MMDアニメーションを保存"),
            defaultName,
            "anim",
            L("选择输出AnimationClip路径。",
                "Choose the output AnimationClip path.",
                "AnimationClipの出力先を選択してください。"));
        if (string.IsNullOrEmpty(outputPath)) return;

        try
        {
            var options = new PmxAnimationClipBakerOptions
            {
                SampleRate = debugSampleRate,
                PositionScale = debugPositionScale,
                CoordinateConversion = debugCoordinateConversion,
                BakeMorphs = debugBakeMorphs,
                CurveReductionError = curveReductionError,
                // Target-FK receives the same UI value through its own bake options below.
                // Do not leak the Humanoid inverse-solve flag into another output pipeline.
                DebugTargetUpperLimbInverseSolve =
                    debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid &&
                    targetUpperLimbInverseSolve
            };

            double lastProgressUpdate = double.NegativeInfinity;
            string lastStage = string.Empty;
            Action<float, string> progress = (value, message) =>
            {
                double now = EditorApplication.timeSinceStartup;
                string stage = GetProgressStage(message);
                bool stageChanged = !string.Equals(stage, lastStage, StringComparison.Ordinal);
                if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.1d) return;
                lastProgressUpdate = now;
                lastStage = stage;
                if (EditorUtility.DisplayCancelableProgressBar(
                        L("内置解析方案", "Built-in Parser", "内蔵解析方式"),
                        message,
                        value))
                    throw new OperationCanceledException();
            };

            PmxAnimationBakeReport report;
            if (debugOutput == BuiltInDebugOutput.Humanoid)
            {
                report = PmxAnimationConversion.Debug_ConvertHumanoidToAsset(
                    pmxPath,
                    vmdPath,
                    debugMorphTargetRoot != null ? debugMorphTargetRoot.transform : null,
                    outputPath,
                    options,
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid)
            {
                report = PmxAnimationConversion.Debug_ConvertTargetCalibratedHumanoidToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity)
            {
                report = PmxAnimationConversion.Debug_ConvertTargetFkToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    Debug_CreateFkBakeOptions(),
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetModelLowerBodyFkFidelity)
            {
                report = PmxAnimationConversion.Debug_ConvertTargetLowerBodyFkToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    Debug_CreateFkBakeOptions(),
                    progress);
            }
            else
            {
                report = PmxAnimationConversion.Debug_ConvertGenericPmxToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot.transform,
                    outputPath,
                    options,
                    progress);
            }

            Selection.activeObject = report.Clip;
            EditorGUIUtility.PingObject(report.Clip);
            Debug.Log(
                $"[VMD Converter] Baked {report.SampleCount} samples as " +
                $"{debugOutput}, {report.BoundBoneCount} mapped bones and " +
                $"{report.BoundMorphCount} morph curves.",
                report.Clip);
            ShowDialog(
                L("内置解析方案", "Built-in Parser", "内蔵解析方式"),
                L("转换完成：\n", "Conversion completed:\n", "変換が完了しました：\n") + outputPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(
                L("转换失败", "Conversion Failed", "変換に失敗"),
                exception.Message);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
