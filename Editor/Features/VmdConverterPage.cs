using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

[Serializable]
internal sealed class VmdConverterPage : ConverterFeaturePage
{
    private static readonly Color[] DarkMotionEntryColors =
    {
        new Color(0.38f, 0.78f, 1f),
        new Color(1f, 0.62f, 0.38f),
        new Color(0.55f, 0.88f, 0.48f),
        new Color(0.85f, 0.58f, 1f),
        new Color(1f, 0.78f, 0.30f),
        new Color(0.40f, 0.88f, 0.82f)
    };
    private static readonly Color[] LightMotionEntryColors =
    {
        new Color(0.05f, 0.42f, 0.72f),
        new Color(0.72f, 0.25f, 0.05f),
        new Color(0.16f, 0.52f, 0.12f),
        new Color(0.52f, 0.20f, 0.68f),
        new Color(0.65f, 0.42f, 0.02f),
        new Color(0.02f, 0.48f, 0.43f)
    };

    [SerializeField] private List<DefaultAsset> motionVmdAssets = new List<DefaultAsset>();
    [SerializeField] private List<DefaultAsset> motionExpressionVmdAssets = new List<DefaultAsset>();
    [SerializeField] private List<bool> motionVmdFileFoldouts = new List<bool>();
    [SerializeField] private Vector2 motionVmdListScroll;
    [SerializeField] private Vector2 pageScroll;
    [SerializeField] private DefaultAsset motionModelAsset;
    [SerializeField] private VmdMotionConversionScheme motionConversionScheme =
        VmdMotionConversionScheme.DirectExport;
    [SerializeField] private GameObject targetModelAsset;
    [SerializeField] private bool motionConversionSettingsExpanded = true;
    [SerializeField] private MotionStorageLocation motionAssetStorageLocation =
        MotionStorageLocation.FollowSource;
    [SerializeField] private string customMotionAssetStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private MotionStorageLocation motionOutputStorageLocation;
    [SerializeField] private string customMotionOutputStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private bool retargetModel;
    [SerializeField] private int selectedModelIndex;

    [NonSerialized] private List<GameObject> pipelineModels = new List<GameObject>();
    [NonSerialized] private List<ConvertedMotionEntry> convertedMotionEntries;
    [NonSerialized] private bool convertedMotionResourcesDirty = true;
    [NonSerialized] private Dictionary<string, bool> mappingFoldouts;
    [NonSerialized] private Dictionary<string, int> mappingPageIndices;
    [NonSerialized] private GameObject cachedMappingModel;
    [NonSerialized] private List<VmdMotionConverter.BlendShapeTarget> cachedMappingTargets;
    [NonSerialized] private string[] cachedMappingTargetLabels;
    [NonSerialized] private Dictionary<string, int> cachedMappingTargetIndices;
    [NonSerialized] private Dictionary<string, VmdMotionConverter.BlendShapeTarget> cachedSameNameTargets;
    [NonSerialized] private Dictionary<string, VmdMotionConversionData> transientMotionData;
    [NonSerialized] private ConverterEditorLanguage cachedMappingLanguage;

    private sealed class ManagedPreparationJob
    {
        internal int Ordinal;
        internal string AssetPath;
        internal string PhysicalPath;
        internal string ExpressionAssetPath;
        internal string ExpressionPhysicalPath;
        internal volatile int CompletedSamples;
        internal volatile int TotalSamples;
        internal Task<VmdMotionConverter.ManagedPreparedConversion> Work;
    }

    private sealed class ConvertedMotionEntry
    {
        public string Path;
        public string DisplayName;
        public VmdMotionConversionData Data;
        public bool IsTransient;
    }

    internal void OnEnable(ConverterWindowContext context, DefaultAsset selected, bool acceptSelection)
    {
        Attach(context);
        if (pipelineModels == null) pipelineModels = new List<GameObject>();
        EnsureMotionVmdEntryState();
        if (convertedMotionEntries == null) convertedMotionEntries = new List<ConvertedMotionEntry>();
        if (mappingFoldouts == null) mappingFoldouts = new Dictionary<string, bool>();
        if (mappingPageIndices == null) mappingPageIndices = new Dictionary<string, int>();
        // The public UI intentionally exposes direct export only. Keep the conversion
        // scheme enum and converter interfaces intact for future formats/migrations.
        motionConversionScheme = VmdMotionConversionScheme.DirectExport;
        if (motionOutputType != MotionOutputType.Humanoid &&
            motionOutputType != MotionOutputType.TargetCalibratedHumanoid)
            motionOutputType = MotionOutputType.Humanoid;
        convertedMotionResourcesDirty = true;
        ScanPipelineModels();
        NormalizeModelParser();
        if (acceptSelection && IsVmdAsset(selected))
        {
            AddMotionVmdAsset(selected);
            convertedMotionResourcesDirty = true;
        }
    }

    internal void OnHierarchyChange()
    {
        InvalidateMappingTargetCache();
        ScanPipelineModels();
    }

    internal void OnProjectChange()
    {
        InvalidateMappingTargetCache();
        convertedMotionResourcesDirty = true;
        ScanPipelineModels();
    }

    internal void OnDisable()
    {
        DestroyTransientMotionData();
    }

    internal void OnLanguageChanged()
    {
        InvalidateMappingTargetCache();
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        if (pipelineModels == null) pipelineModels = new List<GameObject>();
        EnsurePipelineModelsAreAlive();
        EnsureMotionVmdEntryState();
        RemoveMissingMotionEntries();
        if (convertedMotionEntries == null) convertedMotionEntries = new List<ConvertedMotionEntry>();
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawMotionConverter();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        Attach(context);
        EnsurePipelineModelsAreAlive();
        EditorGUILayout.Space(8f);
        EnsureConvertedMotionResourcesLoaded();
        List<string> sourcePaths = GetVmdAssetPaths(motionVmdAssets);
        if (motionConversionScheme == VmdMotionConversionScheme.DirectExport)
        {
            bool outputStorageValid = TryGetMotionOutputStorageFolder(out _);
            string directModelPath = retargetModel
                ? AssetDatabase.GetAssetPath(motionModelAsset)
                : VmdMotionConverter.DefaultModelPath;
            GameObject directTargetModel = IsTargetCalibratedHumanoid ? targetModelAsset : null;
            bool canDirectConvert = sourcePaths.Count > 0 &&
                                    outputStorageValid &&
                                    (!IsTargetCalibratedHumanoid ||
                                     (directTargetModel != null &&
                                      modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged &&
                                      HasValidHumanoidAnimator(directTargetModel))) &&
                                    (!retargetModel || VmdMotionConverter.IsModelPath(
                                        directModelPath, modelParserMethod));
            using (new EditorGUI.DisabledScope(!canDirectConvert))
            {
                if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                    ConvertDirectMotions(sourcePaths, directModelPath, directTargetModel);
            }
            return;
        }

        var convertedPaths = new HashSet<string>(
            convertedMotionEntries.Select(entry => entry.Path),
            StringComparer.OrdinalIgnoreCase);
        List<string> pendingPaths = sourcePaths
            .Where(path => !convertedPaths.Contains(path))
            .ToList();
        if (pendingPaths.Count > 0 || sourcePaths.Count == 0)
        {
            bool assetStorageValid = TryGetMotionAssetStorageFolder(out _);
            string modelPath = retargetModel
                ? AssetDatabase.GetAssetPath(motionModelAsset)
                : VmdMotionConverter.DefaultModelPath;
            bool canConvert = pendingPaths.Count > 0 &&
                              assetStorageValid &&
                              (!retargetModel || VmdMotionConverter.IsModelPath(modelPath, modelParserMethod));
            using (new EditorGUI.DisabledScope(!canConvert))
            {
                if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                    ConvertMotions(pendingPaths, modelPath);
            }
            return;
        }

        bool canExport = pipelineModels.Count > 0 && sourcePaths.Count > 0 &&
                         TryGetMotionOutputStorageFolder(out _);
        using (new EditorGUI.DisabledScope(!canExport))
        {
            if (!GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                return;
            selectedModelIndex = Mathf.Clamp(selectedModelIndex, 0, pipelineModels.Count - 1);
            ExportMotions(pipelineModels[selectedModelIndex], sourcePaths);
        }
    }

    protected override void OnStorageSettingChanged()
    {
        convertedMotionResourcesDirty = true;
    }

    private void DrawMotionConverter()
    {
        ConverterWindowGui.DrawSectionTitle(L("MMD动作转换", "MMD Motion Conversion", "MMDモーション変換"));

        if (DrawMotionVmdFileList())
            convertedMotionResourcesDirty = true;

        DrawMotionConversionSettings();
        // Morph mapping data and APIs remain available internally. The mapping editor is
        // intentionally hidden; direct export writes every source morph automatically.
    }

    private void DrawMotionConversionSettings()
    {
        ConverterWindowGui.DrawOutlinedSetting(() =>
        {
            motionConversionSettingsExpanded = EditorGUILayout.Foldout(
                motionConversionSettingsExpanded,
                L("转换设置", "Conversion Settings", "変換設定"),
                true);
            if (!motionConversionSettingsExpanded) return;

            EditorGUILayout.Space(5f);
            DrawMotionOutputTypeSettings();
            EditorGUILayout.Space(5f);
            DrawStorageLocationSetting(
                L("输出位置", "Output Location", "出力場所"),
                ref motionOutputStorageLocation,
                ref customMotionOutputStorageFolder,
                L("输出存储位置", "Output Storage Location", "出力保存場所"));
            EditorGUILayout.Space(5f);
            DrawMotionConversionModelSettings();
        });
    }

    private void DrawMotionOutputTypeSettings()
    {
        EditorGUI.BeginChangeCheck();
        MotionOutputType next = (MotionOutputType)EditorGUILayout.Popup(
            L("输出类型", "Output Type", "出力タイプ"),
            (int)motionOutputType,
            new[]
            {
                "Humanoid",
                L("目标校准 Humanoid", "Target-calibrated Humanoid", "対象校正Humanoid")
            });
        if (EditorGUI.EndChangeCheck())
        {
            motionOutputType = next;
            DestroyTransientMotionData();
            convertedMotionResourcesDirty = true;
            if (IsTargetCalibratedHumanoid)
            {
                motionConversionScheme = VmdMotionConversionScheme.DirectExport;
                modelParserMethod = VmdMotionConverter.ModelParserMethod.BuiltInManaged;
            }
        }
        if (!IsTargetCalibratedHumanoid) return;

        DrawTargetModelField();

        EditorGUILayout.HelpBox(
            L("在目标Avatar骨架上校准后输出标准HumanoidRoot/Muscle曲线, 该方案会额外经过目标 Avatar 的 HumanPoseHandler 校准, 因此转换通常更慢",
                "Outputs standard Humanoid Root/Muscle curves after calibration on the target Avatar skeleton. This adds a target Avatar HumanPoseHandler calibration pass, so conversion is usually slower.",
                "対象Avatar骨格で校正後、標準Humanoid Root/Muscleカーブを出力します。対象AvatarのHumanPoseHandler校正処理が追加されるため、通常は変換が遅くなります。"),
            MessageType.Info);
    }

    private void DrawTargetModelField()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            GameObject next = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent(
                    L("目标模型", "Target Model", "対象モデル"),
                    L("选择项目中的 Humanoid FBX 模型。",
                        "Select a Humanoid FBX model in the project.",
                        "プロジェクト内のHumanoid FBXモデルを選択します。")),
                targetModelAsset,
                typeof(GameObject),
                false);
            if (EditorGUI.EndChangeCheck())
            {
                string path = next != null ? AssetDatabase.GetAssetPath(next) : string.Empty;
                if (next != null && !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    ShowDialog(
                        L("目标模型", "Target Model", "対象モデル"),
                        L("请选择项目中的 FBX 模型。",
                            "Select an FBX model in the project.",
                            "プロジェクト内のFBXモデルを選択してください。"));
                }
                else
                {
                    targetModelAsset = next;
                    InvalidateMappingTargetCache();
                    convertedMotionResourcesDirty = true;
                }
            }

            using (new EditorGUI.DisabledScope(targetModelAsset == null))
            {
                if (GUILayout.Button("×", GUILayout.Width(24f)))
                {
                    targetModelAsset = null;
                    InvalidateMappingTargetCache();
                    convertedMotionResourcesDirty = true;
                    GUI.FocusControl(null);
                }
            }
        }
    }

    private void DrawMotionConversionModelSettings()
    {
        retargetModel = EditorGUILayout.Toggle(
            L("重定向模型", "Retarget Model", "モデルを置換"),
            retargetModel);
        if (!retargetModel) return;

        EditorGUI.BeginChangeCheck();
        DefaultAsset nextModel = (DefaultAsset)EditorGUILayout.ObjectField(
            L("MMD模型", "MMD Model", "MMDモデル"),
            motionModelAsset,
            typeof(DefaultAsset),
            false);
        if (!EditorGUI.EndChangeCheck()) return;

        string modelPath = nextModel != null ? AssetDatabase.GetAssetPath(nextModel) : string.Empty;
        if (nextModel != null && !VmdMotionConverter.IsModelPath(modelPath, modelParserMethod))
        {
            ShowDialog(
                L("MMD模型", "MMD Model", "MMDモデル"),
                modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged
                    ? L("内置解析方案仅支持.pmx模型。",
                        "The built-in parser supports .pmx models only.",
                        "内蔵解析方式は.pmxモデルのみ対応しています。")
                    : L("请选择.pmx或.pmd模型。",
                        "Select a .pmx or .pmd model.",
                        ".pmxまたは.pmdモデルを選択してください。"));
            return;
        }
        motionModelAsset = nextModel;
    }

    private void DrawModelMappings()
    {
        EnsurePipelineModelsAreAlive();
        if (pipelineModels.Count == 0)
        {
            EditorGUILayout.LabelField(
                L("模型选择", "Model", "モデル"),
                L("未找到 Pipeline Manager 模型",
                    "No Pipeline Manager model found",
                    "Pipeline Manager モデルが見つかりません"));
            return;
        }

        selectedModelIndex = Mathf.Clamp(selectedModelIndex, 0, pipelineModels.Count - 1);
        string[] modelNames = pipelineModels.Select(GetHierarchyPath).ToArray();
        selectedModelIndex = EditorGUILayout.Popup(
            L("模型选择", "Model", "モデル"),
            selectedModelIndex,
            modelNames);

        GameObject model = pipelineModels[selectedModelIndex];
        GetMappingTargetCache(
            model,
            out List<VmdMotionConverter.BlendShapeTarget> targets,
            out string[] targetLabels,
            out Dictionary<string, int> targetIndices,
            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);
        string modelHierarchyPath = GetHierarchyPath(model);
        bool createdMapping = false;
        for (int entryIndex = 0; entryIndex < convertedMotionEntries.Count; entryIndex++)
        {
            createdMapping |= DrawMotionMappingEntry(
                convertedMotionEntries[entryIndex],
                entryIndex,
                model,
                modelHierarchyPath,
                targets,
                targetLabels,
                targetIndices,
                sameNameTargets);
        }
        if (createdMapping) AssetDatabase.SaveAssets();
    }

    private bool DrawMotionMappingEntry(
        ConvertedMotionEntry entry,
        int entryIndex,
        GameObject model,
        string modelHierarchyPath,
        List<VmdMotionConverter.BlendShapeTarget> targets,
        string[] targetLabels,
        Dictionary<string, int> targetIndices,
        IReadOnlyDictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets)
    {
        VmdMotionConversionData.ModelMapping modelMapping;
        bool mappingCreated;
        try
        {
            modelMapping = VmdMotionConverter.GetOrCreateMapping(
                entry.Data,
                model,
                targets,
                out mappingCreated,
                (value, message) => EditorUtility.DisplayProgressBar(
                    L("自动匹配同名形态键", "Matching Same-name Morphs", "同名モーフを自動照合"),
                    $"[{entryIndex + 1}/{convertedMotionEntries.Count}] {entry.DisplayName} — {message}",
                    (entryIndex + Mathf.Clamp01(value)) /
                    Mathf.Max(1f, convertedMotionEntries.Count)),
                sameNameTargets,
                true);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Rect mappingBorder = EditorGUILayout.BeginVertical();
        try
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(6f);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    float angle = EditorGUILayout.FloatField(
                        L("姿势旋转角度", "Pose Rotation Angle", "ポーズ回転角度"),
                        entry.Data.poseRotationAngle);
                    if (EditorGUI.EndChangeCheck())
                    {
                        entry.Data.poseRotationAngle = angle;
                        if (!entry.IsTransient) EditorUtility.SetDirty(entry.Data);
                    }

                    string foldoutKey = entry.Path + "|" + modelHierarchyPath;
                    bool expanded = GetMappingFoldout(
                        foldoutKey,
                        convertedMotionEntries.Count == 1 || entryIndex == 0);
                    expanded = EditorGUILayout.Foldout(
                        expanded,
                        entry.DisplayName + L("-映射列表", "-Mapping List", "-マッピング一覧"),
                        true);
                    SetMappingFoldout(foldoutKey, expanded);
                    if (expanded)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(L("源形态键", "Source Morph", "ソースモーフ"), EditorStyles.boldLabel);
                            GUILayout.Label(L("模型形态键", "Model BlendShape", "モデルモーフ"), EditorStyles.boldLabel);
                        }

                        const int pageSize = 32;
                        int mappingCount = modelMapping.mappings.Count;
                        int pageCount = Mathf.Max(1, Mathf.CeilToInt(mappingCount / (float)pageSize));
                        int pageIndex = Mathf.Clamp(GetMappingPageIndex(foldoutKey), 0, pageCount - 1);
                        if (pageCount > 1)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                using (new EditorGUI.DisabledScope(pageIndex <= 0))
                                    if (GUILayout.Button("‹", GUILayout.Width(28f))) pageIndex--;
                                GUILayout.FlexibleSpace();
                                GUILayout.Label(
                                    $"{pageIndex + 1} / {pageCount}  ({mappingCount})",
                                    EditorStyles.centeredGreyMiniLabel);
                                GUILayout.FlexibleSpace();
                                using (new EditorGUI.DisabledScope(pageIndex >= pageCount - 1))
                                    if (GUILayout.Button("›", GUILayout.Width(28f))) pageIndex++;
                            }
                        }
                        SetMappingPageIndex(foldoutKey, pageIndex);
                        int firstVisible = pageIndex * pageSize;
                        int lastVisible = Mathf.Min(mappingCount, firstVisible + pageSize);
                        EditorGUI.BeginChangeCheck();
                        for (int mappingIndex = firstVisible; mappingIndex < lastVisible; mappingIndex++)
                        {
                            VmdMotionConversionData.MorphMapping mapping = modelMapping.mappings[mappingIndex];
                            string targetKey = GetBlendShapeTargetKey(mapping.target, mapping.targetPath);
                            int currentIndex = targetIndices.TryGetValue(targetKey, out int foundIndex)
                                ? foundIndex
                                : 0;
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.LabelField(mapping.source, GUILayout.MinWidth(120f));
                                int nextIndex = EditorGUILayout.Popup(currentIndex, targetLabels);
                                if (nextIndex == currentIndex) continue;
                                if (nextIndex <= 0)
                                {
                                    mapping.target = string.Empty;
                                    mapping.targetPath = string.Empty;
                                }
                                else
                                {
                                    VmdMotionConverter.BlendShapeTarget target = targets[nextIndex - 1];
                                    mapping.target = target.Name;
                                    mapping.targetPath = target.Path;
                                }
                            }
                        }
                        if (EditorGUI.EndChangeCheck())
                            if (!entry.IsTransient) EditorUtility.SetDirty(entry.Data);
                    }
                }
                GUILayout.Space(6f);
            }
            EditorGUILayout.Space(4f);
        }
        finally
        {
            EditorGUILayout.EndVertical();
        }
        ConverterWindowGui.DrawOutline(mappingBorder);
        EditorGUILayout.Space(6f);
        return mappingCreated && !entry.IsTransient;
    }

    private bool DrawMotionVmdFileList()
    {
        EnsureMotionVmdEntryState();
        bool changed = false;
        if (motionVmdAssets.Count > 0)
        {
            const float maxListHeight = 220f;
            float rowHeight = EditorGUIUtility.singleLineHeight +
                              EditorGUIUtility.standardVerticalSpacing;
            float contentHeight = 0f;
            for (int index = 0; index < motionVmdAssets.Count; index++)
                contentHeight += rowHeight * (motionVmdFileFoldouts[index] ? 3f : 1f) +
                                 (index > 0 ? 5f : 0f);
            bool useScrollView = contentHeight > maxListHeight;
            if (useScrollView)
            {
                motionVmdListScroll = EditorGUILayout.BeginScrollView(
                    motionVmdListScroll,
                    false,
                    false,
                    GUILayout.Height(maxListHeight));
            }

            try
            {
                for (int index = 0; index < motionVmdAssets.Count; index++)
                {
                if (index > 0) ConverterWindowGui.DrawEntrySeparator();
                DefaultAsset displayAsset = motionVmdAssets[index] != null
                    ? motionVmdAssets[index]
                    : motionExpressionVmdAssets[index];
                Color entryColor = GetMotionEntryColor(index);
                bool removeEntry = false;
                Color previousContentColor = GUI.contentColor;
                try
                {
                    GUI.contentColor = entryColor;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string arrow = motionVmdFileFoldouts[index] ? "▼ " : "▶ ";
                        if (GUILayout.Button(
                                arrow + GetVmdDisplayName(displayAsset),
                                EditorStyles.boldLabel,
                                GUILayout.ExpandWidth(true)))
                            motionVmdFileFoldouts[index] = !motionVmdFileFoldouts[index];
                        removeEntry = GUILayout.Button(
                            "×", EditorStyles.miniButton, GUILayout.Width(24f));
                    }
                }
                finally
                {
                    GUI.contentColor = previousContentColor;
                }
                if (removeEntry)
                {
                    motionVmdAssets.RemoveAt(index);
                    motionExpressionVmdAssets.RemoveAt(index);
                    motionVmdFileFoldouts.RemoveAt(index);
                    changed = true;
                    break;
                }
                if (!motionVmdFileFoldouts[index]) continue;

                int previousIndentLevel = EditorGUI.indentLevel;
                try
                {
                    EditorGUI.indentLevel = previousIndentLevel + 1;
                    if (DrawMotionVmdDataRow(
                            L("动作数据", "Motion Data", "モーションデータ"),
                            motionVmdAssets[index],
                            out DefaultAsset nextMotion))
                    {
                        string nextPath = GetVmdAssetPath(nextMotion);
                        bool duplicate = !string.IsNullOrEmpty(nextPath) && motionVmdAssets
                            .Where((_, otherIndex) => otherIndex != index)
                            .Any(other => string.Equals(
                                GetVmdAssetPath(other),
                                nextPath,
                                StringComparison.OrdinalIgnoreCase));
                        if (duplicate)
                        {
                            ShowDialog(
                                L("VMD文件", "VMD Files", "VMDファイル"),
                                L("该动作VMD已在列表中。",
                                    "That motion VMD is already in the list.",
                                    "そのモーションVMDはすでにリストにあります。"));
                        }
                        else
                        {
                            motionVmdAssets[index] = nextMotion;
                            changed = true;
                        }
                    }

                    if (DrawMotionVmdDataRow(
                            L("表情数据", "Expression Data", "表情データ"),
                            motionExpressionVmdAssets[index],
                            out DefaultAsset nextExpression))
                    {
                        motionExpressionVmdAssets[index] = nextExpression;
                        changed = true;
                    }
                }
                finally
                {
                    EditorGUI.indentLevel = previousIndentLevel;
                }

                if (motionVmdAssets[index] != null || motionExpressionVmdAssets[index] != null)
                    continue;
                motionVmdAssets.RemoveAt(index);
                motionExpressionVmdAssets.RemoveAt(index);
                motionVmdFileFoldouts.RemoveAt(index);
                changed = true;
                break;
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
            DragAndDrop.visualMode = hasVmd
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;
            if (currentEvent.type == EventType.DragPerform && hasVmd)
            {
                DragAndDrop.AcceptDrag();
                foreach (DefaultAsset asset in DragAndDrop.objectReferences.OfType<DefaultAsset>())
                    changed |= AddMotionVmdAsset(asset);
            }
            currentEvent.Use();
        }

        return changed;
    }

    private static Color GetMotionEntryColor(int index)
    {
        Color[] palette = EditorGUIUtility.isProSkin
            ? DarkMotionEntryColors
            : LightMotionEntryColors;
        return palette[Mathf.Abs(index) % palette.Length];
    }

    private bool DrawMotionVmdDataRow(string label, DefaultAsset current, out DefaultAsset next)
    {
        next = current;
        using (new EditorGUILayout.HorizontalScope())
        {
            DefaultAsset selected = (DefaultAsset)EditorGUILayout.ObjectField(
                label,
                current,
                typeof(DefaultAsset),
                false);
            if (selected != current)
            {
                if (selected == null || IsVmdAsset(selected))
                {
                    next = selected;
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
            if (GUILayout.Button("×", GUILayout.Width(24f))) next = null;
        }
        return next != current;
    }

    private void ConvertMotions(IReadOnlyList<string> sourcePaths, string modelPath)
    {
        if (!TryGetMotionAssetStorageFolder(out string assetStorageFolder))
            throw new InvalidOperationException("The Asset storage folder must be inside Assets.");
        if (modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            ConvertManagedMotionsParallel(sourcePaths, modelPath);
            return;
        }

        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        VmdMotionConversionData lastData = null;
        VmdMotionConverter.ManagedBatchSession batchSession = null;
        bool assetEditing = false;
        if (sourcePaths.Count > 1 &&
            modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            try
            {
                batchSession = VmdMotionConverter.CreateManagedBatchSession(
                    modelPath, curveReductionError, targetUpperLimbInverseSolve);
                AssetDatabase.StartAssetEditing();
                assetEditing = true;
            }
            catch (Exception exception)
            {
                if (batchSession != null) batchSession.Dispose();
                batchSession = null;
                Debug.LogException(exception);
                errors.Add("Batch initialization failed: " + exception.Message);
            }
        }
        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            try
            {
                EditorUtility.DisplayProgressBar(
                    L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                    $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)}",
                    fileIndex / (float)Mathf.Max(1, sourcePaths.Count));
                VmdMotionConversionData data = VmdMotionConverter.Convert(
                    sourcePath,
                    modelPath,
                    modelParserMethod,
                    curveReductionError,
                    ConfirmOverwrite,
                    motionConversionScheme,
                    batchSession,
                    assetEditing,
                    assetStorageFolder,
                    GetExpressionVmdPath(sourcePath));
                if (data == null)
                {
                    skipped++;
                    continue;
                }
                converted++;
                lastData = data;
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

        if (assetEditing)
        {
            try
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add("Batch asset write failed: " + exception.Message);
            }
        }
        if (batchSession != null) batchSession.Dispose();

        convertedMotionResourcesDirty = true;
        EnsureConvertedMotionResourcesLoaded();
        if (lastData != null)
        {
            Selection.activeObject = lastData;
            EditorGUIUtility.PingObject(lastData);
        }
        ShowBatchResult(
            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private void ConvertDirectMotions(
        IReadOnlyList<string> sourcePaths, string modelPath, GameObject targetModel)
    {
        if (!TryGetMotionOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The output storage folder must be inside Assets.");
        if (modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            ConvertManagedMotionsParallel(sourcePaths, modelPath, targetModel);
            return;
        }

        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        GetMappingTargetCache(
            targetModel,
            out List<VmdMotionConverter.BlendShapeTarget> targets,
            out _,
            out _,
            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);

        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            AnimationClip sourceClip = null;
            try
            {
                ConvertedMotionEntry entry = convertedMotionEntries.FirstOrDefault(item =>
                    string.Equals(item.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidOperationException("The transient VMD mapping data is missing.");
                sourceClip = VmdMotionConverter.ConvertDirectSourceClip(
                    sourcePath,
                    GetExpressionVmdPath(sourcePath),
                    modelPath,
                    modelParserMethod,
                    curveReductionError,
                    out List<VmdMotionConversionData.MorphRecord> morphs);
                entry.Data.morphs = morphs;
                VmdMotionConversionData.ModelMapping mapping =
                    VmdMotionConverter.GetOrCreateMapping(
                        entry.Data,
                        targetModel,
                        targets,
                        out _,
                        null,
                        sameNameTargets,
                        true);

                double lastProgressUpdate = double.NegativeInfinity;
                Action<float, string> progress = (value, message) =>
                {
                    double now = EditorApplication.timeSinceStartup;
                    if (value < 1f && now - lastProgressUpdate < 0.08d) return;
                    lastProgressUpdate = now;
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                            $"[{fileIndex + 1}/{sourcePaths.Count}] " +
                            Path.GetFileName(sourcePath) + " — " + message,
                            (fileIndex + Mathf.Clamp01(value)) / Mathf.Max(1f, sourcePaths.Count)))
                        throw new OperationCanceledException();
                };
                AnimationClip clip = VmdMotionConverter.Export(
                    entry.Data,
                    targetModel,
                    mapping,
                    sourcePath,
                    ConfirmOverwrite,
                    progress,
                    sourceClip,
                    outputStorageFolder);
                if (clip == null)
                {
                    skipped++;
                    continue;
                }
                converted++;
                lastClip = clip;
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
                if (sourceClip != null && !AssetDatabase.Contains(sourceClip))
                    UnityEngine.Object.DestroyImmediate(sourceClip);
                EditorUtility.ClearProgressBar();
            }
        }

        SelectExportedClip(lastClip);
        ShowBatchResult(
            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private void ConvertManagedMotionsParallel(
        IReadOnlyList<string> sourcePaths, string modelPath, GameObject directTargetModel = null)
    {
        bool directExport = motionConversionScheme == VmdMotionConversionScheme.DirectExport;
        bool targetCalibratedExport = directExport && IsTargetCalibratedHumanoid;
        string assetStorageFolder = null;
        string outputStorageFolder = null;
        if (directExport)
        {
            if (!TryGetMotionOutputStorageFolder(out outputStorageFolder))
                throw new InvalidOperationException("The output storage folder must be inside Assets.");
        }
        else if (!TryGetMotionAssetStorageFolder(out assetStorageFolder))
        {
            throw new InvalidOperationException("The Asset storage folder must be inside Assets.");
        }
        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        var pending = new List<ManagedPreparationJob>();
        VmdMotionConversionData lastData = null;
        AnimationClip lastClip = null;
        string lastClipPath = null;

        for (int index = 0; index < sourcePaths.Count; index++)
        {
            string sourcePath = sourcePaths[index];
            try
            {
                if (!directExport && !VmdMotionConverter.ConfirmManagedConversion(
                        sourcePath,
                        motionConversionScheme,
                        ConfirmOverwrite,
                        assetStorageFolder))
                {
                    skipped++;
                    continue;
                }
                pending.Add(new ManagedPreparationJob
                {
                    Ordinal = pending.Count,
                    AssetPath = sourcePath,
                    PhysicalPath = VmdMotionConverter.GetPhysicalVmdPath(sourcePath),
                    ExpressionAssetPath = GetExpressionVmdPath(sourcePath),
                    ExpressionPhysicalPath = GetExpressionPhysicalVmdPath(sourcePath)
                });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add(Path.GetFileName(sourcePath) + ": " + exception.Message);
            }
        }

        if (pending.Count == 0)
        {
            ShowBatchResult(
                L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                converted, skipped, errors, false);
            return;
        }

        VmdMotionConverter.ManagedBatchSession batchSession = null;
        var cancellation = new CancellationTokenSource();
        bool assetEditing = false;
        var active = new List<ManagedPreparationJob>();
        try
        {
            batchSession = VmdMotionConverter.CreateManagedBatchSession(
                modelPath, curveReductionError, targetUpperLimbInverseSolve);
            if (pending.Count > 1)
            {
                AssetDatabase.StartAssetEditing();
                assetEditing = true;
            }

            int availableWorkers = VmdMotionConverter.ManagedDefaultParallelism;
            int fileParallelism = Math.Min(pending.Count, Math.Min(4, availableWorkers));
            int frameParallelism = Math.Max(
                1, (availableWorkers + fileParallelism - 1) / Math.Max(1, fileParallelism));
            int nextJob = 0;
            int completedPreparationJobs = 0;

            Action startNext = () =>
            {
                if (nextJob >= pending.Count || cancellation.IsCancellationRequested) return;
                ManagedPreparationJob job = pending[nextJob++];
                job.Work = Task.Run(
                    () => VmdMotionConverter.PrepareManagedConversion(
                        job.PhysicalPath,
                        job.ExpressionPhysicalPath,
                        batchSession,
                        frameParallelism,
                        (completedSamples, totalSamples) =>
                        {
                            job.TotalSamples = totalSamples;
                            job.CompletedSamples = completedSamples;
                        },
                        cancellation.Token),
                    cancellation.Token);
                active.Add(job);
            };

            for (int index = 0; index < fileParallelism; index++) startNext();

            while (active.Count > 0 && !cancelled)
            {
                int readyIndex = active.FindIndex(job => job.Work.IsCompleted);
                if (readyIndex < 0)
                {
                    float partial = completedPreparationJobs;
                    foreach (ManagedPreparationJob job in active)
                    {
                        int total = job.TotalSamples;
                        if (total > 0)
                            partial += Mathf.Clamp01(job.CompletedSamples / (float)total);
                    }
                    ManagedPreparationJob displayJob = active[0];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                            $"Preparing [{displayJob.Ordinal + 1}/{pending.Count}] " +
                            Path.GetFileName(displayJob.AssetPath),
                            partial / Mathf.Max(1f, pending.Count)))
                    {
                        cancelled = true;
                        cancellation.Cancel();
                        break;
                    }
                    Thread.Sleep(15);
                    continue;
                }

                ManagedPreparationJob completedJob = active[readyIndex];
                active.RemoveAt(readyIndex);
                VmdMotionConverter.ManagedPreparedConversion prepared;
                try
                {
                    prepared = completedJob.Work.GetAwaiter().GetResult();
                    completedPreparationJobs++;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    cancellation.Cancel();
                    break;
                }
                catch (Exception exception)
                {
                    completedPreparationJobs++;
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(completedJob.AssetPath) + ": " + exception.Message);
                    startNext();
                    continue;
                }

                // Refill the background queue before the main-thread Humanoid pass so both stages overlap.
                startNext();
                try
                {
                    double lastProgressUpdate = double.NegativeInfinity;
                    string lastStage = string.Empty;
                    Action<float, string> progress = (value, message) =>
                    {
                        double now = EditorApplication.timeSinceStartup;
                        string stage = GetProgressStage(message);
                        bool stageChanged = !string.Equals(stage, lastStage, StringComparison.Ordinal);
                        if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.08d) return;
                        lastProgressUpdate = now;
                        lastStage = stage;
                        if (EditorUtility.DisplayCancelableProgressBar(
                                L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                                $"[{completedJob.Ordinal + 1}/{pending.Count}] " +
                                Path.GetFileName(completedJob.AssetPath) + " — " + message,
                                (completedJob.Ordinal + Mathf.Clamp01(value)) /
                                Mathf.Max(1f, pending.Count)))
                            throw new OperationCanceledException("VMD motion conversion was cancelled.");
                    };

                    if (directExport)
                    {
                        ConvertedMotionEntry entry = convertedMotionEntries.FirstOrDefault(
                            item => string.Equals(
                                item.Path, completedJob.AssetPath, StringComparison.OrdinalIgnoreCase));
                        if (entry == null)
                            throw new InvalidOperationException("The transient VMD mapping data is missing.");
                        entry.Data.morphs = prepared.Morphs;
                        VmdMotionConversionData.ModelMapping mapping = null;
                        if (directTargetModel != null)
                        {
                            GetMappingTargetCache(
                                directTargetModel,
                                out List<VmdMotionConverter.BlendShapeTarget> targets,
                                out _,
                                out _,
                                out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);
                            mapping = VmdMotionConverter.GetOrCreateMapping(
                                entry.Data,
                                directTargetModel,
                                targets,
                                out _,
                                null,
                                sameNameTargets,
                                true);
                        }
                        AnimationClip sourceClip = null;
                        try
                        {
                            sourceClip = targetCalibratedExport
                                ? VmdMotionConverter.BakeManagedTargetHumanoidSourceClip(
                                    prepared,
                                    batchSession,
                                    directTargetModel,
                                    (value, message) => progress(value * 0.72f, message))
                                : VmdMotionConverter.BakeManagedSourceClip(
                                    prepared,
                                    batchSession,
                                    (value, message) => progress(value * 0.72f, message));
                            AnimationClip clip = VmdMotionConverter.Export(
                                entry.Data,
                                directTargetModel,
                                mapping,
                                completedJob.AssetPath,
                                ConfirmOverwrite,
                                (value, message) => progress(0.72f + value * 0.28f, message),
                                sourceClip,
                                outputStorageFolder,
                                assetEditing);
                            if (clip == null)
                            {
                                skipped++;
                                continue;
                            }
                            converted++;
                            lastClip = clip;
                            lastClipPath = AssetDatabase.GetAssetPath(clip);
                        }
                        finally
                        {
                            if (sourceClip != null && !AssetDatabase.Contains(sourceClip))
                                UnityEngine.Object.DestroyImmediate(sourceClip);
                        }
                    }
                    else
                    {
                        VmdMotionConversionData data = VmdMotionConverter.CommitManagedConversion(
                            completedJob.AssetPath,
                            prepared,
                            batchSession,
                            motionConversionScheme,
                            assetEditing,
                            progress,
                            assetStorageFolder,
                            completedJob.ExpressionAssetPath);
                        converted++;
                        lastData = data;
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(completedJob.AssetPath) + ": " + exception.Message);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            if (cancelled && active.Count > 0)
            {
                while (active.Any(job => !job.Work.IsCompleted))
                {
                    EditorUtility.DisplayProgressBar(
                        L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                        "Cancelling background conversion tasks...",
                        1f);
                    Thread.Sleep(15);
                }
                foreach (ManagedPreparationJob job in active)
                {
                    try { job.Work.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        errors.Add(Path.GetFileName(job.AssetPath) + ": " + exception.Message);
                    }
                }
                active.Clear();
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            errors.Add("Parallel batch initialization failed: " + exception.Message);
        }
        finally
        {
            cancellation.Cancel();
            foreach (ManagedPreparationJob job in active)
            {
                if (job.Work == null) continue;
                try { job.Work.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(job.AssetPath) + ": " + exception.Message);
                }
            }
            active.Clear();
            cancellation.Dispose();
            EditorUtility.ClearProgressBar();
            if (assetEditing)
            {
                try
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add("Batch asset write failed: " + exception.Message);
                }
            }
            if (batchSession != null) batchSession.Dispose();
        }

        if (!directExport)
        {
            convertedMotionResourcesDirty = true;
            EnsureConvertedMotionResourcesLoaded();
        }
        if (lastData != null)
        {
            Selection.activeObject = lastData;
            EditorGUIUtility.PingObject(lastData);
        }
        else if (lastClip != null)
        {
            if (!string.IsNullOrEmpty(lastClipPath))
                lastClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(lastClipPath) ?? lastClip;
            SelectExportedClip(lastClip);
        }
        ShowBatchResult(
            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private void ExportMotions(GameObject model, IReadOnlyList<string> sourcePaths)
    {
        if (!TryGetMotionAssetStorageFolder(out string assetStorageFolder))
            throw new InvalidOperationException("The Asset storage folder must be inside Assets.");
        if (!TryGetMotionOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The output storage folder must be inside Assets.");
        int exported = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        GetMappingTargetCache(
            model,
            out List<VmdMotionConverter.BlendShapeTarget> targets,
            out _,
            out _,
            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);
        bool unsavedMappings = false;

        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            try
            {
                VmdMotionConversionData data = VmdMotionConverter.LoadData(
                    sourcePath, assetStorageFolder);
                if (data == null)
                    throw new InvalidOperationException("The VMD conversion data is missing.");
                VmdMotionConversionData.ModelMapping modelMapping =
                    VmdMotionConverter.GetOrCreateMapping(
                        data,
                        model,
                        targets,
                        out bool mappingCreated,
                        (value, message) => EditorUtility.DisplayProgressBar(
                            L("自动匹配同名形态键", "Matching Same-name Morphs", "同名モーフを自動照合"),
                            $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)} — {message}",
                            (fileIndex + Mathf.Clamp01(value)) /
                            Mathf.Max(1f, sourcePaths.Count)),
                        sameNameTargets,
                        true);
                unsavedMappings |= mappingCreated;

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
                        L("导出动画", "Export Animation", "アニメーションを書き出す"),
                        $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)} — {message}",
                        overall);
                    if (cancelRequested && value < 0.9f)
                        throw new OperationCanceledException();
                };

                AnimationClip clip = VmdMotionConverter.Export(
                    data,
                    model,
                    modelMapping,
                    sourcePath,
                    ConfirmOverwrite,
                    progress,
                    null,
                    outputStorageFolder);
                if (clip == null)
                {
                    skipped++;
                    continue;
                }
                exported++;
                lastClip = clip;
                // Export saves every dirty asset, including mappings created earlier in this batch.
                unsavedMappings = false;
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

        if (unsavedMappings) AssetDatabase.SaveAssets();

        SelectExportedClip(lastClip);
        ShowBatchResult(
            L("导出动画", "Export Animation", "アニメーションを書き出す"),
            exported,
            skipped,
            errors,
            cancelled);
    }

    private void EnsureConvertedMotionResourcesLoaded()
    {
        if (!convertedMotionResourcesDirty && convertedMotionEntries != null) return;
        convertedMotionResourcesDirty = false;
        if (convertedMotionEntries == null)
            convertedMotionEntries = new List<ConvertedMotionEntry>();
        else
            convertedMotionEntries.Clear();

        List<string> sourcePaths = GetVmdAssetPaths(motionVmdAssets);
        string assetStorageFolder = null;
        if (motionConversionScheme != VmdMotionConversionScheme.DirectExport &&
            !TryGetMotionAssetStorageFolder(out assetStorageFolder))
            return;
        try
        {
            if (motionConversionScheme == VmdMotionConversionScheme.DirectExport)
            {
                if (transientMotionData == null)
                    transientMotionData = new Dictionary<string, VmdMotionConversionData>(
                        StringComparer.OrdinalIgnoreCase);
                var activePaths = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
                foreach (string stalePath in transientMotionData.Keys
                             .Where(path => !activePaths.Contains(path)).ToArray())
                {
                    UnityEngine.Object.DestroyImmediate(transientMotionData[stalePath]);
                    transientMotionData.Remove(stalePath);
                }
            }

            for (int index = 0; index < sourcePaths.Count; index++)
            {
                string path = sourcePaths[index];
                string expressionPath = GetExpressionVmdPath(path);
                EditorUtility.DisplayProgressBar(
                    L("加载已转换资源", "Loading Converted Resources", "変換済みリソースを読み込み"),
                    $"[{index + 1}/{sourcePaths.Count}] {Path.GetFileName(path)}",
                    (index + 1f) / Mathf.Max(1, sourcePaths.Count));
                if (motionConversionScheme == VmdMotionConversionScheme.DirectExport)
                {
                    string dependencyHash = GetMotionEntryDependencyHash(path, expressionPath);
                    if (!transientMotionData.TryGetValue(path, out VmdMotionConversionData transient) ||
                        transient == null ||
                        !string.Equals(
                            transient.sourceVmdDependencyHash, dependencyHash, StringComparison.Ordinal))
                    {
                        if (transient != null) UnityEngine.Object.DestroyImmediate(transient);
                        transient = VmdMotionConverter.CreateTransientData(path, expressionPath);
                        transient.sourceVmdDependencyHash = dependencyHash;
                        transientMotionData[path] = transient;
                    }
                    convertedMotionEntries.Add(new ConvertedMotionEntry
                    {
                        Path = path,
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        Data = transient,
                        IsTransient = true
                    });
                    continue;
                }

                VmdMotionConversionData data = VmdMotionConverter.LoadData(
                    path, assetStorageFolder);
                if (!VmdMotionConverter.IsConvertedFor(
                        data, path, motionConversionScheme, expressionPath)) continue;
                convertedMotionEntries.Add(new ConvertedMotionEntry
                {
                    Path = path,
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                    Data = data,
                    IsTransient = false
                });
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void DestroyTransientMotionData()
    {
        if (transientMotionData == null) return;
        foreach (VmdMotionConversionData data in transientMotionData.Values)
            if (data != null) UnityEngine.Object.DestroyImmediate(data);
        transientMotionData.Clear();
    }

    private string GetExpressionVmdPath(string motionPath)
    {
        if (string.IsNullOrEmpty(motionPath)) return null;
        EnsureMotionVmdEntryState();
        for (int index = 0; index < motionVmdAssets.Count; index++)
        {
            if (!string.Equals(GetVmdAssetPath(motionVmdAssets[index]), motionPath,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            string expressionPath = GetVmdAssetPath(motionExpressionVmdAssets[index]);
            return string.Equals(expressionPath, motionPath, StringComparison.OrdinalIgnoreCase)
                ? null
                : expressionPath;
        }
        return null;
    }

    private string GetExpressionPhysicalVmdPath(string motionPath)
    {
        string expressionPath = GetExpressionVmdPath(motionPath);
        return string.IsNullOrEmpty(expressionPath)
            ? null
            : VmdMotionConverter.GetPhysicalVmdPath(expressionPath);
    }

    private static string GetMotionEntryDependencyHash(
        string motionPath, string expressionPath)
    {
        string motionHash = AssetDatabase.GetAssetDependencyHash(motionPath).ToString();
        if (string.IsNullOrEmpty(expressionPath)) return motionHash + "|";
        return motionHash + "|" + expressionPath + ":" +
               AssetDatabase.GetAssetDependencyHash(expressionPath);
    }

    private void ScanPipelineModels()
    {
        if (pipelineModels == null) pipelineModels = new List<GameObject>();
        GameObject selectedModel = selectedModelIndex >= 0 && selectedModelIndex < pipelineModels.Count
            ? pipelineModels[selectedModelIndex]
            : null;
        pipelineModels.Clear();
        pipelineModels.AddRange(VmdMotionConverter.FindPipelineModels());
        selectedModelIndex = selectedModel != null ? pipelineModels.IndexOf(selectedModel) : 0;
        if (selectedModelIndex < 0) selectedModelIndex = 0;
    }

    private void EnsurePipelineModelsAreAlive()
    {
        if (pipelineModels == null)
        {
            pipelineModels = new List<GameObject>();
            ScanPipelineModels();
            return;
        }

        bool containsInvalidModel = pipelineModels.Any(model =>
            model == null || !model.scene.IsValid());
        if (!containsInvalidModel) return;

        InvalidateMappingTargetCache();
        ScanPipelineModels();
    }

    private static string GetHierarchyPath(GameObject gameObject)
    {
        if (gameObject == null) return string.Empty;
        var names = new List<string>();
        for (Transform current = gameObject.transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private void EnsureMotionVmdEntryState()
    {
        if (motionVmdAssets == null)
            motionVmdAssets = new List<DefaultAsset>();
        if (motionExpressionVmdAssets == null)
            motionExpressionVmdAssets = new List<DefaultAsset>();
        if (motionVmdFileFoldouts == null)
            motionVmdFileFoldouts = new List<bool>();
        while (motionExpressionVmdAssets.Count < motionVmdAssets.Count)
            motionExpressionVmdAssets.Add(motionVmdAssets[motionExpressionVmdAssets.Count]);
        while (motionExpressionVmdAssets.Count > motionVmdAssets.Count)
            motionExpressionVmdAssets.RemoveAt(motionExpressionVmdAssets.Count - 1);
        while (motionVmdFileFoldouts.Count < motionVmdAssets.Count)
            motionVmdFileFoldouts.Add(false);
        while (motionVmdFileFoldouts.Count > motionVmdAssets.Count)
            motionVmdFileFoldouts.RemoveAt(motionVmdFileFoldouts.Count - 1);
    }

    private void RemoveMissingMotionEntries()
    {
        EnsureMotionVmdEntryState();
        for (int index = motionVmdAssets.Count - 1; index >= 0; index--)
        {
            if (motionVmdAssets[index] != null ||
                motionExpressionVmdAssets[index] != null)
                continue;

            motionVmdAssets.RemoveAt(index);
            motionExpressionVmdAssets.RemoveAt(index);
            motionVmdFileFoldouts.RemoveAt(index);
        }
    }

    private bool GetMappingFoldout(string key, bool defaultExpanded)
    {
        if (mappingFoldouts == null) mappingFoldouts = new Dictionary<string, bool>();
        return mappingFoldouts.TryGetValue(key, out bool expanded) ? expanded : defaultExpanded;
    }

    private void SetMappingFoldout(string key, bool expanded)
    {
        if (mappingFoldouts == null) mappingFoldouts = new Dictionary<string, bool>();
        mappingFoldouts[key] = expanded;
    }

    private int GetMappingPageIndex(string key)
    {
        if (mappingPageIndices == null)
            mappingPageIndices = new Dictionary<string, int>();
        return mappingPageIndices.TryGetValue(key, out int pageIndex)
            ? pageIndex
            : 0;
    }

    private void SetMappingPageIndex(string key, int pageIndex)
    {
        if (mappingPageIndices == null)
            mappingPageIndices = new Dictionary<string, int>();
        mappingPageIndices[key] = Mathf.Max(0, pageIndex);
    }

    private static string GetBlendShapeTargetKey(string name, string path)
    {
        return (name ?? string.Empty) + "\u001f" + (path ?? string.Empty);
    }

    private void GetMappingTargetCache(
        GameObject model,
        out List<VmdMotionConverter.BlendShapeTarget> targets,
        out string[] labels,
        out Dictionary<string, int> indices,
        out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets)
    {
        if (cachedMappingModel != model ||
            cachedMappingTargets == null ||
            cachedMappingTargetLabels == null ||
            cachedMappingTargetIndices == null ||
            cachedSameNameTargets == null ||
            cachedMappingLanguage != Language)
        {
            cachedMappingModel = model;
            cachedMappingLanguage = Language;
            cachedMappingTargets = VmdMotionConverter.GetBlendShapeTargets(model);
            cachedSameNameTargets = VmdMotionConverter.BuildSameNameTargetLookup(cachedMappingTargets);
            cachedMappingTargetLabels = new[] { L("(未映射)", "(Unmapped)", "(未設定)") }
                .Concat(cachedMappingTargets.Select(target => target.DisplayName))
                .ToArray();
            cachedMappingTargetIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int targetIndex = 0; targetIndex < cachedMappingTargets.Count; targetIndex++)
            {
                VmdMotionConverter.BlendShapeTarget target = cachedMappingTargets[targetIndex];
                string key = GetBlendShapeTargetKey(target.Name, target.Path);
                if (!cachedMappingTargetIndices.ContainsKey(key))
                    cachedMappingTargetIndices.Add(key, targetIndex + 1);
            }
        }

        targets = cachedMappingTargets;
        labels = cachedMappingTargetLabels;
        indices = cachedMappingTargetIndices;
        sameNameTargets = cachedSameNameTargets;
    }

    private void InvalidateMappingTargetCache()
    {
        cachedMappingModel = null;
        cachedMappingTargets = null;
        cachedMappingTargetLabels = null;
        cachedMappingTargetIndices = null;
        cachedSameNameTargets = null;
    }

    private bool AddMotionVmdAsset(DefaultAsset asset)
    {
        EnsureMotionVmdEntryState();
        if (!AddVmdAsset(motionVmdAssets, asset)) return false;
        motionExpressionVmdAssets.Add(asset);
        motionVmdFileFoldouts.Add(false);
        return true;
    }

    private bool TryGetMotionAssetStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            motionAssetStorageLocation, customMotionAssetStorageFolder, out folder);
    }

    private bool TryGetMotionOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            motionOutputStorageLocation, customMotionOutputStorageFolder, out folder);
    }
}
