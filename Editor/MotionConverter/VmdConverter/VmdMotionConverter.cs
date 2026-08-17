using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using VRChat.MmdPreview.Pmx2Fbx;
using VRChat.MmdPreview.Pmx2Fbx.Editor;
using Debug = UnityEngine.Debug;

internal static class VmdMotionConverter
{
    internal const string DefaultModelPath =
        "Packages/com.vrchat.world.mmdconverter/Editor/MotionConverter/Model/_model.pmx";

    private const float VmdFrameRate = 30f;

    internal enum ModelParserMethod
    {
        BuiltInManaged,
        MoyuerAvatarToolkit
    }

    internal sealed class BlendShapeTarget
    {
        public string Name;
        public string Path;
        public string DisplayName;
    }

    internal sealed class ManagedPreparedConversion
    {
        internal PmxHumanoidAnimationClipBaker.PreparedMotion HumanoidMotion;
        internal List<VmdMotionConversionData.MorphRecord> Morphs;
    }

    /// <summary>
    /// Keeps PMX2FBX batch implementation details inside MotionConverter so feature pages do
    /// not depend on an individual PMX baker class.
    /// </summary>
    internal sealed class ManagedBatchSession : IDisposable
    {
        private PmxHumanoidAnimationClipBaker.BatchSession session;

        internal ManagedBatchSession(PmxModel model, PmxAnimationClipBakerOptions options)
        {
            session = new PmxHumanoidAnimationClipBaker.BatchSession(model, options);
        }

        internal PmxHumanoidAnimationClipBaker.PreparedMotion Prepare(
            VmdMotion motion, int maximumParallelism = 0,
            Action<int, int> progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureActive();
            return session.Prepare(
                motion, maximumParallelism, progress, cancellationToken);
        }

        internal PmxAnimationBakeReport Bake(
            VmdMotion motion, Transform morphTargetRoot = null,
            Action<float, string> progress = null)
        {
            EnsureActive();
            return session.Bake(motion, morphTargetRoot, progress);
        }

        internal PmxAnimationBakeReport Bake(
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            Transform morphTargetRoot = null,
            Action<float, string> progress = null)
        {
            EnsureActive();
            return session.Bake(prepared, morphTargetRoot, progress);
        }

        internal PmxAnimationBakeReport BakeTargetHumanoid(
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            GameObject targetRoot,
            Action<float, string> progress = null)
        {
            EnsureActive();
            return session.BakeTargetHumanoid(prepared, targetRoot, progress);
        }

        private void EnsureActive()
        {
            if (session == null) throw new ObjectDisposedException(nameof(ManagedBatchSession));
        }

        public void Dispose()
        {
            if (session == null) return;
            session.Dispose();
            session = null;
        }
    }

    internal static int ManagedDefaultParallelism =>
        PmxHumanoidAnimationClipBaker.DefaultParallelism;

    internal static string GetDataPath(string vmdPath, string destinationFolder = null)
    {
        return BuildDestinationPath(vmdPath, destinationFolder, "_MmdPreview.asset");
    }

    internal static string GetExternalClipPath(string vmdPath, string destinationFolder = null)
    {
        return BuildDestinationPath(vmdPath, destinationFolder, "_MmdPreview.anim");
    }

    internal static VmdMotionConversionData LoadData(string vmdPath, string destinationFolder = null)
    {
        if (string.IsNullOrEmpty(vmdPath)) return null;
        VmdMotionConversionData data =
            AssetDatabase.LoadAssetAtPath<VmdMotionConversionData>(
                GetDataPath(vmdPath, destinationFolder));
        if (data == null) return null;
        UpgradeLegacyData(data, vmdPath);
        return data;
    }

    internal static bool IsConvertedFor(
        VmdMotionConversionData data, string vmdPath, VmdMotionConversionScheme scheme,
        string expressionVmdPath = null)
    {
        if (data == null || !data.isConverted || scheme == VmdMotionConversionScheme.DirectExport ||
            data.conversionScheme != scheme)
            return false;
        string guid = AssetDatabase.AssetPathToGUID(vmdPath);
        if (!string.IsNullOrEmpty(data.sourceVmdGuid) &&
            !string.Equals(data.sourceVmdGuid, guid, StringComparison.Ordinal))
            return false;
        string hash = GetDependencyHash(vmdPath);
        if (!string.IsNullOrEmpty(data.sourceVmdDependencyHash) &&
            !string.Equals(data.sourceVmdDependencyHash, hash, StringComparison.Ordinal))
            return false;
        string expressionGuid = string.IsNullOrEmpty(expressionVmdPath)
            ? string.Empty
            : AssetDatabase.AssetPathToGUID(expressionVmdPath);
        string expressionHash = string.IsNullOrEmpty(expressionVmdPath)
            ? string.Empty
            : GetDependencyHash(expressionVmdPath);
        if (!string.Equals(data.sourceExpressionVmdGuid ?? string.Empty,
                expressionGuid, StringComparison.Ordinal) ||
            !string.Equals(data.sourceExpressionVmdDependencyHash ?? string.Empty,
                expressionHash, StringComparison.Ordinal))
            return false;
        if (scheme == VmdMotionConversionScheme.ExternalHumanoidClip && data.sourceClip == null)
            return false;
        return true;
    }

    internal static VmdMotionConversionData CreateTransientData(
        string vmdPath, string expressionVmdPath = null)
    {
        ValidateAssetPath(vmdPath, ".vmd", "VMD");
        var data = ScriptableObject.CreateInstance<VmdMotionConversionData>();
        data.name = Path.GetFileNameWithoutExtension(vmdPath) + "_Direct";
        data.dataVersion = VmdMotionConversionData.CurrentDataVersion;
        data.isConverted = false;
        data.conversionScheme = VmdMotionConversionScheme.DirectExport;
        SetSourceIdentity(data, vmdPath);
        SetExpressionSourceIdentity(data, expressionVmdPath);
        List<VmdMotionConversionData.MorphRecord> motionMorphs =
            ReadMorphs(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
        data.morphs = MergeExpressionMorphs(
            motionMorphs, vmdPath, expressionVmdPath, int.MaxValue);
        return data;
    }

    internal static VmdMotionConversionData Convert(
        string vmdPath,
        string modelPath,
        ModelParserMethod parserMethod,
        float curveReductionError,
        Func<string, string, string, bool> confirmOverwrite,
        VmdMotionConversionScheme conversionScheme,
        ManagedBatchSession batchSession = null,
        bool deferAssetImport = false,
        string destinationFolder = null,
        string expressionVmdPath = null)
    {
        ValidateAssetPath(vmdPath, ".vmd", "VMD");
        if (!IsModelPath(modelPath, parserMethod))
            throw new ArgumentException(
                parserMethod == ModelParserMethod.BuiltInManaged
                    ? "The built-in C# parser requires a .pmx model asset."
                    : "MMD model must be a .pmx or .pmd asset.",
                nameof(modelPath));
        if (parserMethod == ModelParserMethod.MoyuerAvatarToolkit && !IsMoyuerAvatarToolkitAvailable())
            throw new InvalidOperationException("Moyuer Avatar Toolkit is not installed or its VMD converter is unavailable.");
        ValidatePersistentScheme(conversionScheme);

        string dataPath = GetDataPath(vmdPath, destinationFolder);
        if (!ConfirmPersistentConversion(
                vmdPath, conversionScheme, confirmOverwrite, destinationFolder))
            return null;

        AnimationClip sourceClip = ConvertMotionClip(
            vmdPath,
            modelPath,
            parserMethod,
            Mathf.Clamp(curveReductionError, 0f, 0.001f),
            batchSession,
            out VmdMotion parsedMotion);
        List<VmdMotionConversionData.MorphRecord> motionMorphs = parsedMotion != null
            ? BuildMorphRecords(parsedMotion)
            : ReadMorphs(Path.GetFullPath(vmdPath));
        int maximumFrame = parsedMotion != null
            ? checked((int)parsedMotion.MaximumFrame)
            : Mathf.Max(0, Mathf.RoundToInt(sourceClip.length * VmdFrameRate));
        List<VmdMotionConversionData.MorphRecord> morphs = MergeExpressionMorphs(
            motionMorphs, vmdPath, expressionVmdPath, maximumFrame);
        return WriteConversionData(
            vmdPath, dataPath, sourceClip, morphs, conversionScheme, deferAssetImport,
            expressionVmdPath);
    }

    internal static bool ConfirmManagedConversion(
        string vmdPath,
        VmdMotionConversionScheme conversionScheme,
        Func<string, string, string, bool> confirmOverwrite,
        string destinationFolder = null)
    {
        ValidateAssetPath(vmdPath, ".vmd", "VMD");
        ValidatePersistentScheme(conversionScheme);
        return ConfirmPersistentConversion(
            vmdPath, conversionScheme, confirmOverwrite, destinationFolder);
    }

    internal static string GetPhysicalVmdPath(string vmdAssetPath)
    {
        ValidateAssetPath(vmdAssetPath, ".vmd", "VMD");
        return PmxAnimationClipBaker.ToPhysicalPath(vmdAssetPath);
    }

    internal static ManagedPreparedConversion PrepareManagedConversion(
        string physicalVmdPath,
        string physicalExpressionVmdPath,
        ManagedBatchSession batchSession,
        int maximumParallelism,
        Action<int, int> threadSafeProgress,
        CancellationToken cancellationToken)
    {
        if (batchSession == null) throw new ArgumentNullException(nameof(batchSession));
        cancellationToken.ThrowIfCancellationRequested();
        VmdMotion motion = VmdParser.Parse(physicalVmdPath);
        cancellationToken.ThrowIfCancellationRequested();
        List<VmdMotionConversionData.MorphRecord> motionMorphs = BuildMorphRecords(motion);
        string motionPath = Path.GetFullPath(physicalVmdPath);
        string expressionPath = string.IsNullOrEmpty(physicalExpressionVmdPath)
            ? null
            : Path.GetFullPath(physicalExpressionVmdPath);
        return new ManagedPreparedConversion
        {
            HumanoidMotion = batchSession.Prepare(
                motion, maximumParallelism, threadSafeProgress, cancellationToken),
            Morphs = MergeExpressionMorphs(
                motionMorphs,
                motionPath,
                expressionPath,
                checked((int)motion.MaximumFrame))
        };
    }

    internal static VmdMotionConversionData CommitManagedConversion(
        string vmdPath,
        ManagedPreparedConversion prepared,
        ManagedBatchSession batchSession,
        VmdMotionConversionScheme conversionScheme,
        bool deferAssetImport,
        Action<float, string> progress,
        string destinationFolder = null,
        string expressionVmdPath = null)
    {
        if (prepared == null) throw new ArgumentNullException(nameof(prepared));
        if (batchSession == null) throw new ArgumentNullException(nameof(batchSession));
        PmxAnimationBakeReport report = batchSession.Bake(prepared.HumanoidMotion, null, progress);
        report.Clip.name = Path.GetFileNameWithoutExtension(vmdPath);
        return WriteConversionData(
            vmdPath,
            GetDataPath(vmdPath, destinationFolder),
            report.Clip,
            prepared.Morphs,
            conversionScheme,
            deferAssetImport,
            expressionVmdPath);
    }

    internal static AnimationClip BakeManagedSourceClip(
        ManagedPreparedConversion prepared,
        ManagedBatchSession batchSession,
        Action<float, string> progress)
    {
        if (prepared == null) throw new ArgumentNullException(nameof(prepared));
        if (batchSession == null) throw new ArgumentNullException(nameof(batchSession));
        PmxAnimationBakeReport report = batchSession.Bake(prepared.HumanoidMotion, null, progress);
        return report.Clip;
    }

    internal static AnimationClip BakeManagedTargetHumanoidSourceClip(
        ManagedPreparedConversion prepared,
        ManagedBatchSession batchSession,
        GameObject targetModel,
        Action<float, string> progress)
    {
        if (prepared == null) throw new ArgumentNullException(nameof(prepared));
        if (batchSession == null) throw new ArgumentNullException(nameof(batchSession));
        if (targetModel == null) throw new ArgumentNullException(nameof(targetModel));
        PmxAnimationBakeReport report = batchSession.BakeTargetHumanoid(
            prepared.HumanoidMotion, targetModel, progress);
        return report.Clip;
    }

    internal static AnimationClip ConvertDirectSourceClip(
        string vmdPath,
        string expressionVmdPath,
        string modelPath,
        ModelParserMethod parserMethod,
        float curveReductionError,
        out List<VmdMotionConversionData.MorphRecord> morphs)
    {
        AnimationClip clip = ConvertMotionClip(
            vmdPath,
            modelPath,
            parserMethod,
            Mathf.Clamp(curveReductionError, 0f, 0.001f),
            null,
            out VmdMotion parsedMotion);
        List<VmdMotionConversionData.MorphRecord> motionMorphs = parsedMotion != null
            ? BuildMorphRecords(parsedMotion)
            : ReadMorphs(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
        int maximumFrame = parsedMotion != null
            ? checked((int)parsedMotion.MaximumFrame)
            : Mathf.Max(0, Mathf.RoundToInt(clip.length * VmdFrameRate));
        morphs = MergeExpressionMorphs(
            motionMorphs, vmdPath, expressionVmdPath, maximumFrame);
        return clip;
    }

    private static VmdMotionConversionData WriteConversionData(
        string vmdPath,
        string dataPath,
        AnimationClip sourceClip,
        List<VmdMotionConversionData.MorphRecord> morphs,
        VmdMotionConversionScheme conversionScheme,
        bool deferAssetImport,
        string expressionVmdPath = null)
    {
        ValidatePersistentScheme(conversionScheme);
        sourceClip.name = "Motion";
        AssetDatabase.DeleteAsset(dataPath);
        string externalClipPath = Path.ChangeExtension(dataPath, ".anim").Replace('\\', '/');
        EnsureAssetFolderExists(Path.GetDirectoryName(dataPath)?.Replace('\\', '/'));
        AssetDatabase.DeleteAsset(externalClipPath);
        var data = ScriptableObject.CreateInstance<VmdMotionConversionData>();
        data.name = Path.GetFileNameWithoutExtension(vmdPath) + "_MmdPreview";
        data.dataVersion = VmdMotionConversionData.CurrentDataVersion;
        data.isConverted = true;
        data.conversionScheme = conversionScheme;
        data.morphs = morphs;
        SetSourceIdentity(data, vmdPath);
        SetExpressionSourceIdentity(data, expressionVmdPath);
        if (conversionScheme == VmdMotionConversionScheme.ExternalHumanoidClip)
        {
            AssetDatabase.CreateAsset(sourceClip, externalClipPath);
            data.sourceClip = sourceClip;
            AssetDatabase.CreateAsset(data, dataPath);
        }
        else
        {
            AssetDatabase.CreateAsset(data, dataPath);
            AssetDatabase.AddObjectToAsset(sourceClip, data);
            data.sourceClip = sourceClip;
            EditorUtility.SetDirty(data);
        }
        if (deferAssetImport) return data;
        AssetDatabase.SaveAssets();
        if (conversionScheme == VmdMotionConversionScheme.ExternalHumanoidClip)
            AssetDatabase.ImportAsset(externalClipPath, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(dataPath, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<VmdMotionConversionData>(dataPath);
    }

    internal static ManagedBatchSession CreateManagedBatchSession(
        string modelAssetPath, float curveReductionError,
        bool targetUpperLimbInverseSolve = false)
    {
        if (string.IsNullOrEmpty(modelAssetPath) ||
            !modelAssetPath.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The built-in C# parser supports PMX models only.");
        PmxModel model = PmxModelCache.Load(modelAssetPath);
        return new ManagedBatchSession(
            model,
            CreateManagedBakeOptions(
                Mathf.Clamp(curveReductionError, 0f, 0.001f),
                targetUpperLimbInverseSolve));
    }

    internal static List<GameObject> FindPipelineModels()
    {
        var models = new List<GameObject>();
        foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject == null || !gameObject.scene.IsValid()) continue;
            Component[] components = gameObject.GetComponents<Component>();
            if (!components.Any(component =>
                    component != null &&
                    component.GetType().FullName == "VRC.Core.PipelineManager"))
                continue;
            models.Add(gameObject);
        }

        return models
            .Distinct()
            .OrderBy(GetHierarchyPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<BlendShapeTarget> GetBlendShapeTargets(GameObject model)
    {
        var targets = new List<BlendShapeTarget>();
        if (model == null) return targets;

        foreach (SkinnedMeshRenderer renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = renderer.sharedMesh;
            if (mesh == null) continue;
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, model.transform);
            for (int index = 0; index < mesh.blendShapeCount; index++)
            {
                string name = mesh.GetBlendShapeName(index);
                targets.Add(new BlendShapeTarget
                {
                    Name = name,
                    Path = path,
                    DisplayName = string.IsNullOrEmpty(path) ? name : name + "  (" + path + ")"
                });
            }
        }
        return targets;
    }

    internal static Dictionary<string, BlendShapeTarget> BuildSameNameTargetLookup(
        IReadOnlyList<BlendShapeTarget> targets)
    {
        var lookup = new Dictionary<string, BlendShapeTarget>(StringComparer.Ordinal);
        if (targets == null) return lookup;
        for (int index = 0; index < targets.Count; index++)
        {
            BlendShapeTarget target = targets[index];
            if (target == null || target.Name == null || lookup.ContainsKey(target.Name)) continue;
            // Preserve the previous FirstOrDefault behavior when multiple renderers expose
            // the same BlendShape name: the first hierarchy scan result remains authoritative.
            lookup.Add(target.Name, target);
        }
        return lookup;
    }

    internal static VmdMotionConversionData.ModelMapping GetOrCreateMapping(
        VmdMotionConversionData data,
        GameObject model,
        IReadOnlyList<BlendShapeTarget> targets,
        out bool created,
        Action<float, string> progress = null,
        IReadOnlyDictionary<string, BlendShapeTarget> sameNameTargets = null,
        bool deferAssetSave = false)
    {
        created = false;
        if (data == null || model == null) return null;

        string hierarchyPath = GetHierarchyPath(model);
        VmdMotionConversionData.ModelMapping mapping =
            data.modelMappings.FirstOrDefault(item => item.hierarchyPath == hierarchyPath);
        if (mapping != null) return mapping;

        mapping = new VmdMotionConversionData.ModelMapping
        {
            modelName = model.name,
            hierarchyPath = hierarchyPath
        };

        if (sameNameTargets == null)
            sameNameTargets = BuildSameNameTargetLookup(targets);
        progress?.Invoke(0f, "Preparing same-name morph matching");
        int morphCount = data.morphs.Count;
        for (int morphIndex = 0; morphIndex < data.morphs.Count; morphIndex++)
        {
            VmdMotionConversionData.MorphRecord morph = data.morphs[morphIndex];
            BlendShapeTarget exact = null;
            if (morph.name != null)
                sameNameTargets.TryGetValue(morph.name, out exact);
            mapping.mappings.Add(new VmdMotionConversionData.MorphMapping
            {
                source = morph.name,
                target = exact != null ? exact.Name : string.Empty,
                targetPath = exact != null ? exact.Path : string.Empty
            });
            int completed = morphIndex + 1;
            if ((completed & 31) == 0 || completed == morphCount)
                progress?.Invoke(
                    completed / (float)Mathf.Max(1, morphCount),
                    $"Matching morphs {completed}/{morphCount}");
        }

        data.modelMappings.Add(mapping);
        bool persistent = AssetDatabase.Contains(data);
        if (persistent) EditorUtility.SetDirty(data);
        created = true;
        if (persistent && !deferAssetSave) AssetDatabase.SaveAssets();
        progress?.Invoke(1f, "Same-name morph matching complete");
        return mapping;
    }

    internal static AnimationClip Export(
        VmdMotionConversionData data,
        GameObject model,
        VmdMotionConversionData.ModelMapping modelMapping,
        string vmdPath,
        Func<string, string, string, bool> confirmOverwrite,
        Action<float, string> progress,
        AnimationClip sourceClipOverride = null,
        string outputFolder = null,
        bool deferAssetImport = false)
    {
        if (data == null)
            throw new InvalidOperationException("Select a converted VMD first.");

        AnimationClip sourceClip = sourceClipOverride ?? ResolveSourceClip(data);
        if (sourceClip == null)
            throw new InvalidDataException("The converted motion clip is missing.");

        string modelName = SanitizeFileName(model != null ? model.name : "Humanoid");
        string outputPath = BuildDestinationPath(
            vmdPath, outputFolder, "_" + modelName + ".anim");
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath) != null &&
            confirmOverwrite != null &&
            !confirmOverwrite("Overwrite animation", outputPath, "Overwrite"))
            return null;

        progress?.Invoke(0.02f, "Preparing animation export");
        bool reuseTransientSource = sourceClipOverride != null &&
                                    !AssetDatabase.Contains(sourceClipOverride);
        AnimationClip clip;
        if (reuseTransientSource)
        {
            clip = sourceClipOverride;
        }
        else
        {
            clip = new AnimationClip();
            EditorUtility.CopySerialized(sourceClip, clip);
        }
        clip.name = Path.GetFileNameWithoutExtension(outputPath);
        progress?.Invoke(0.12f, reuseTransientSource
            ? "Reusing transient source animation"
            : "Copying source animation");

        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.orientationOffsetY = data.poseRotationAngle;
        settings.loopTime = false;
        settings.loopBlendOrientation = true;
        settings.loopBlendPositionY = true;
        settings.loopBlendPositionXZ = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        progress?.Invoke(0.2f, "Applying animation settings");

        var morphByName = data.morphs.ToDictionary(morph => morph.name, StringComparer.Ordinal);
        var exportMappings = modelMapping != null
            ? new List<VmdMotionConversionData.MorphMapping>(modelMapping.mappings)
            : new List<VmdMotionConversionData.MorphMapping>();
        var mappedSources = new HashSet<string>(
            exportMappings
                .Where(mapping => !string.IsNullOrEmpty(mapping.source) &&
                                  !string.IsNullOrEmpty(mapping.target))
                .Select(mapping => mapping.source),
            StringComparer.Ordinal);
        foreach (VmdMotionConversionData.MorphRecord morph in data.morphs)
        {
            if (string.IsNullOrEmpty(morph.name) || mappedSources.Contains(morph.name)) continue;
            // Preserve every VMD morph even when no target FBX is supplied or no same-name
            // BlendShape exists. A later mapping pass can retarget these root bindings.
            exportMappings.Add(new VmdMotionConversionData.MorphMapping
            {
                source = morph.name,
                target = morph.name,
                targetPath = string.Empty
            });
            mappedSources.Add(morph.name);
        }

        int mappingCount = exportMappings.Count;
        var morphBindings = new List<EditorCurveBinding>(mappingCount);
        var morphCurves = new List<AnimationCurve>(mappingCount);
        for (int mappingIndex = 0; mappingIndex < mappingCount; mappingIndex++)
        {
            VmdMotionConversionData.MorphMapping mapping = exportMappings[mappingIndex];
            if (string.IsNullOrEmpty(mapping.source) ||
                string.IsNullOrEmpty(mapping.target) ||
                !morphByName.TryGetValue(mapping.source, out VmdMotionConversionData.MorphRecord morph))
            {
                progress?.Invoke(
                    0.2f + 0.65f * (mappingIndex + 1f) / Mathf.Max(1, mappingCount),
                    $"Writing morph curves {mappingIndex + 1}/{mappingCount}");
                continue;
            }

            var keys = new Keyframe[morph.frames.Count];
            for (int index = 0; index < keys.Length; index++)
            {
                VmdMotionConversionData.MorphFrame frame = morph.frames[index];
                keys[index] = new Keyframe(frame.frame / VmdFrameRate, frame.value * 100f);
            }

            var binding = EditorCurveBinding.FloatCurve(
                mapping.targetPath ?? string.Empty,
                typeof(SkinnedMeshRenderer),
                "blendShape." + mapping.target);
            morphBindings.Add(binding);
            morphCurves.Add(new AnimationCurve(keys));
            progress?.Invoke(
                0.2f + 0.65f * (mappingIndex + 1f) / Mathf.Max(1, mappingCount),
                $"Writing morph curves {mappingIndex + 1}/{mappingCount}");
        }
        if (morphBindings.Count > 0)
            AnimationUtility.SetEditorCurves(
                clip, morphBindings.ToArray(), morphCurves.ToArray());

        progress?.Invoke(0.9f, "Saving AnimationClip asset");
        EnsureAssetFolderExists(Path.GetDirectoryName(outputPath)?.Replace('\\', '/'));
        AssetDatabase.DeleteAsset(outputPath);
        AssetDatabase.CreateAsset(clip, outputPath);
        if (deferAssetImport)
        {
            progress?.Invoke(1f, "Animation export queued for batch import");
            return clip;
        }
        AssetDatabase.SaveAssets();
        progress?.Invoke(0.96f, "Importing AnimationClip asset");
        AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport);
        AnimationClip result = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
        Selection.activeObject = result;
        EditorGUIUtility.PingObject(result);
        progress?.Invoke(1f, "Animation export complete");
        return result;
    }

    private static AnimationClip ResolveSourceClip(VmdMotionConversionData data)
    {
        if (data.sourceClip != null) return data.sourceClip;
        string dataPath = AssetDatabase.GetAssetPath(data);
        if (string.IsNullOrEmpty(dataPath)) return null;
        if (data.conversionScheme == VmdMotionConversionScheme.ExternalHumanoidClip)
        {
            string clipPath = Path.ChangeExtension(dataPath, ".anim");
            AnimationClip external = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (external != null) return external;
        }

        // Compatibility path for version-1 resources that embedded the clip without a direct reference.
        return AssetDatabase.LoadAllAssetsAtPath(dataPath).OfType<AnimationClip>().FirstOrDefault();
    }

    private static bool ConfirmPersistentConversion(
        string vmdPath,
        VmdMotionConversionScheme conversionScheme,
        Func<string, string, string, bool> confirmOverwrite,
        string destinationFolder)
    {
        string dataPath = GetDataPath(vmdPath, destinationFolder);
        string existingPath = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dataPath) != null
            ? dataPath
            : conversionScheme == VmdMotionConversionScheme.ExternalHumanoidClip &&
              AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                  GetExternalClipPath(vmdPath, destinationFolder)) != null
                ? GetExternalClipPath(vmdPath, destinationFolder)
                : string.Empty;
        return string.IsNullOrEmpty(existingPath) ||
               confirmOverwrite == null ||
               confirmOverwrite("Overwrite conversion data", existingPath, "Overwrite");
    }

    private static void ValidatePersistentScheme(VmdMotionConversionScheme conversionScheme)
    {
        if (conversionScheme != VmdMotionConversionScheme.EmbeddedHumanoidClip &&
            conversionScheme != VmdMotionConversionScheme.ExternalHumanoidClip)
            throw new ArgumentOutOfRangeException(
                nameof(conversionScheme), "Direct export does not create conversion data assets.");
    }

    private static void UpgradeLegacyData(VmdMotionConversionData data, string vmdPath)
    {
        if (data.dataVersion >= VmdMotionConversionData.CurrentDataVersion) return;
        bool predatesConversionSchemes = data.dataVersion < 2;
        data.dataVersion = VmdMotionConversionData.CurrentDataVersion;
        if (predatesConversionSchemes)
        {
            data.isConverted = true;
            data.conversionScheme = VmdMotionConversionScheme.EmbeddedHumanoidClip;
        }
        if (string.IsNullOrEmpty(data.sourceVmdGuid))
            data.sourceVmdGuid = AssetDatabase.AssetPathToGUID(vmdPath);
        EditorUtility.SetDirty(data);
    }

    private static void SetSourceIdentity(VmdMotionConversionData data, string vmdPath)
    {
        data.sourceVmdGuid = AssetDatabase.AssetPathToGUID(vmdPath);
        data.sourceVmdDependencyHash = GetDependencyHash(vmdPath);
    }

    private static void SetExpressionSourceIdentity(
        VmdMotionConversionData data, string expressionVmdPath)
    {
        data.sourceExpressionVmdGuid = string.IsNullOrEmpty(expressionVmdPath)
            ? string.Empty
            : AssetDatabase.AssetPathToGUID(expressionVmdPath);
        data.sourceExpressionVmdDependencyHash = string.IsNullOrEmpty(expressionVmdPath)
            ? string.Empty
            : GetDependencyHash(expressionVmdPath);
    }

    private static string GetDependencyHash(string assetPath)
    {
        return AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
    }

    private static string BuildDestinationPath(
        string sourcePath, string destinationFolder, string suffixAndExtension)
    {
        string fileName = Path.GetFileNameWithoutExtension(sourcePath) + suffixAndExtension;
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            string sourceFolder = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(sourceFolder) ? fileName : sourceFolder + "/" + fileName;
        }

        string folder = destinationFolder.Trim().Replace('\\', '/').TrimEnd('/');
        if (!string.Equals(folder, "Assets", StringComparison.Ordinal) &&
            !folder.StartsWith("Assets/", StringComparison.Ordinal))
            throw new ArgumentException("The storage folder must be inside Assets.", nameof(destinationFolder));
        return folder + "/" + fileName;
    }

    internal static void EnsureAssetFolderExists(string folder)
    {
        if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
        if (!string.Equals(folder, "Assets", StringComparison.Ordinal) &&
            !folder.StartsWith("Assets/", StringComparison.Ordinal))
            throw new ArgumentException("The storage folder must be inside Assets.", nameof(folder));

        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int index = 1; index < parts.Length; index++)
        {
            string next = current + "/" + parts[index];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[index]);
            current = next;
        }
    }

    internal static bool IsModelPath(string path)
    {
        return IsModelPath(path, ModelParserMethod.MoyuerAvatarToolkit);
    }

    internal static bool IsModelPath(string path, ModelParserMethod parserMethod)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)) return true;
        return parserMethod == ModelParserMethod.MoyuerAvatarToolkit &&
               path.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<ModelParserMethod> GetAvailableModelParsers()
    {
        var result = new List<ModelParserMethod> { ModelParserMethod.BuiltInManaged };
        if (IsMoyuerAvatarToolkitAvailable()) result.Add(ModelParserMethod.MoyuerAvatarToolkit);
        return result;
    }

    internal static bool IsMoyuerAvatarToolkitAvailable()
    {
        Type type = FindType("VRChatAvatarToolkit.Vmd2AnimUtils");
        return type?.GetMethod(
                   "GetAnimationClip",
                   BindingFlags.Public | BindingFlags.Static,
                   null,
                   new[] { typeof(string), typeof(string) },
                   null) != null;
    }

    private static AnimationClip ConvertMotionClip(
        string vmdAssetPath,
        string modelAssetPath,
        ModelParserMethod parserMethod,
        float curveReductionError,
        ManagedBatchSession batchSession,
        out VmdMotion parsedMotion)
    {
        parsedMotion = null;
        try
        {
            switch (parserMethod)
            {
                case ModelParserMethod.MoyuerAvatarToolkit:
                    return ConvertWithMoyuerAvatarToolkit(vmdAssetPath, modelAssetPath);
                default:
                    return ConvertWithManagedParser(
                        vmdAssetPath,
                        modelAssetPath,
                        curveReductionError,
                        batchSession,
                        out parsedMotion);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "VMD motion conversion failed. Verify the PMX/PMD and VMD files.\n" + exception.Message,
                exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static AnimationClip ConvertWithManagedParser(
        string vmdAssetPath,
        string modelAssetPath,
        float curveReductionError,
        ManagedBatchSession batchSession,
        out VmdMotion motion)
    {
        if (!modelAssetPath.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The built-in C# parser supports PMX models only.");

        EditorUtility.DisplayProgressBar("VMD Converter", "Loading PMX and VMD data...", 0.01f);
        motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdAssetPath));
        PmxAnimationClipBakerOptions options = CreateManagedBakeOptions(curveReductionError);

        double lastProgressUpdate = double.NegativeInfinity;
        string lastStage = string.Empty;
        Action<float, string> progress = (value, message) =>
        {
            double now = EditorApplication.timeSinceStartup;
            int separator = string.IsNullOrEmpty(message) ? -1 : message.IndexOf(' ');
            string stage = separator > 0 ? message.Substring(0, separator) : message ?? string.Empty;
            bool stageChanged = !string.Equals(stage, lastStage, StringComparison.Ordinal);
            if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.1d) return;
            lastProgressUpdate = now;
            lastStage = stage;
            if (EditorUtility.DisplayCancelableProgressBar("VMD Converter", message, value))
                throw new OperationCanceledException("VMD motion conversion was cancelled.");
        };

        PmxAnimationBakeReport report;
        if (batchSession != null)
        {
            report = batchSession.Bake(motion, null, progress);
        }
        else
        {
            PmxModel model = PmxModelCache.Load(modelAssetPath);
            report = PmxHumanoidAnimationClipBaker.Bake(
                model, motion, null, options, progress);
        }
        report.Clip.name = Path.GetFileNameWithoutExtension(vmdAssetPath);
        return report.Clip;
    }

    private static PmxAnimationClipBakerOptions CreateManagedBakeOptions(
        float curveReductionError, bool targetUpperLimbInverseSolve = false)
    {
        return new PmxAnimationClipBakerOptions
        {
            SampleRate = VmdFrameRate,
            PositionScale = 0.08f,
            CoordinateConversion = PmxCoordinateConversion.RotateY180,
            BakeMorphs = false,
            CurveReductionError = curveReductionError,
            DebugTargetUpperLimbInverseSolve = targetUpperLimbInverseSolve
        };
    }

    private static AnimationClip ConvertWithMoyuerAvatarToolkit(string vmdAssetPath, string modelAssetPath)
    {
        Type type = FindType("VRChatAvatarToolkit.Vmd2AnimUtils");
        MethodInfo method = type?.GetMethod(
            "GetAnimationClip",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(string) },
            null);
        if (method == null)
            throw new InvalidOperationException("Moyuer Avatar Toolkit VMD converter was not found.");
        try
        {
            var clip = method.Invoke(null, new object[] { vmdAssetPath, modelAssetPath }) as AnimationClip;
            if (clip == null) throw new InvalidOperationException("Moyuer Avatar Toolkit returned no animation clip.");
            return clip;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    private static Type FindType(string fullName)
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    private static List<VmdMotionConversionData.MorphRecord> ReadMorphs(string fullVmdPath)
    {
        using (FileStream stream = File.OpenRead(fullVmdPath))
        using (var reader = new BinaryReader(stream))
        {
            Encoding shiftJis = Encoding.GetEncoding("shift_jis");
            string header = ReadFixedString(reader, 30, Encoding.ASCII);
            if (!header.StartsWith("Vocaloid Motion Data", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid VMD header.");

            int modelNameLength = header.Contains("0002") ? 20 : 10;
            EnsureRemaining(stream, modelNameLength, "model name");
            reader.ReadBytes(modelNameLength);

            uint boneCount = ReadCount(reader, stream, "bone");
            SkipRecords(stream, boneCount, 111, "bone");

            uint morphCount = ReadCount(reader, stream, "morph");
            if (morphCount > 10000000)
                throw new InvalidDataException("The VMD morph count is invalid.");

            var records = new Dictionary<string, VmdMotionConversionData.MorphRecord>(StringComparer.Ordinal);
            for (uint index = 0; index < morphCount; index++)
            {
                EnsureRemaining(stream, 23, "morph");
                string name = ReadFixedString(reader, 15, shiftJis);
                int frame = checked((int)reader.ReadUInt32());
                float value = reader.ReadSingle();
                if (!records.TryGetValue(name, out VmdMotionConversionData.MorphRecord record))
                {
                    record = new VmdMotionConversionData.MorphRecord { name = name };
                    records.Add(name, record);
                }
                record.frames.Add(new VmdMotionConversionData.MorphFrame { frame = frame, value = value });
            }

            foreach (VmdMotionConversionData.MorphRecord record in records.Values)
                record.frames.Sort((left, right) => left.frame.CompareTo(right.frame));
            return records.Values.OrderBy(record => record.name, StringComparer.Ordinal).ToList();
        }
    }

    private static List<VmdMotionConversionData.MorphRecord> BuildMorphRecords(VmdMotion motion)
    {
        var records = new Dictionary<string, VmdMotionConversionData.MorphRecord>(StringComparer.Ordinal);
        foreach (VmdMorphKeyframe key in motion.MorphKeyframes)
        {
            if (!records.TryGetValue(key.MorphName, out VmdMotionConversionData.MorphRecord record))
            {
                record = new VmdMotionConversionData.MorphRecord { name = key.MorphName };
                records.Add(key.MorphName, record);
            }
            record.frames.Add(new VmdMotionConversionData.MorphFrame
            {
                frame = checked((int)key.Frame),
                value = key.Weight
            });
        }

        foreach (VmdMotionConversionData.MorphRecord record in records.Values)
            record.frames.Sort((left, right) => left.frame.CompareTo(right.frame));
        return records.Values.OrderBy(record => record.name, StringComparer.Ordinal).ToList();
    }

    private static List<VmdMotionConversionData.MorphRecord> MergeExpressionMorphs(
        List<VmdMotionConversionData.MorphRecord> motionMorphs,
        string motionVmdPath,
        string expressionVmdPath,
        int maximumFrame)
    {
        if (string.IsNullOrEmpty(expressionVmdPath) || string.Equals(
                Path.GetFullPath(motionVmdPath),
                Path.GetFullPath(expressionVmdPath),
                StringComparison.OrdinalIgnoreCase))
            return motionMorphs;

        List<VmdMotionConversionData.MorphRecord> expressionMorphs = ReadMorphs(
            PmxAnimationClipBaker.ToPhysicalPath(expressionVmdPath));
        var merged = new Dictionary<string, VmdMotionConversionData.MorphRecord>(
            motionMorphs.Count + expressionMorphs.Count,
            StringComparer.Ordinal);
        foreach (VmdMotionConversionData.MorphRecord record in motionMorphs)
            merged[record.name] = record;
        foreach (VmdMotionConversionData.MorphRecord source in expressionMorphs)
        {
            if (source.frames.Count > 0 &&
                source.frames[source.frames.Count - 1].frame <= maximumFrame)
            {
                merged[source.name] = source;
                continue;
            }
            var replacement = new VmdMotionConversionData.MorphRecord { name = source.name };
            for (int index = 0; index < source.frames.Count; index++)
            {
                VmdMotionConversionData.MorphFrame frame = source.frames[index];
                if (frame.frame > maximumFrame) break;
                replacement.frames.Add(new VmdMotionConversionData.MorphFrame
                {
                    frame = frame.frame,
                    value = frame.value
                });
            }
            if (replacement.frames.Count > 0)
                merged[source.name] = replacement;
            else
                merged.Remove(source.name);
        }
        return merged.Values.OrderBy(record => record.name, StringComparer.Ordinal).ToList();
    }

    private static void ValidateAssetPath(string path, string extension, string label)
    {
        if (string.IsNullOrEmpty(path) ||
            !path.StartsWith("Assets/", StringComparison.Ordinal) ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(label + " must be inside the project's Assets directory.");
    }

    private static string ReadFixedString(BinaryReader reader, int length, Encoding encoding)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        int count = Array.IndexOf(bytes, (byte)0);
        if (count < 0) count = bytes.Length;
        return encoding.GetString(bytes, 0, count);
    }

    private static uint ReadCount(BinaryReader reader, Stream stream, string section)
    {
        EnsureRemaining(stream, 4, section);
        return reader.ReadUInt32();
    }

    private static void SkipRecords(Stream stream, uint count, int recordSize, string section)
    {
        long bytes = checked((long)count * recordSize);
        EnsureRemaining(stream, bytes, section);
        stream.Position += bytes;
    }

    private static void EnsureRemaining(Stream stream, long bytes, string section)
    {
        if (bytes < 0 || stream.Length - stream.Position < bytes)
            throw new InvalidDataException("The VMD " + section + " data is incomplete.");
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

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Model" : value.Trim();
    }
}
