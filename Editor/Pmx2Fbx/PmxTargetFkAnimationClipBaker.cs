using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    internal enum PmxTargetFkBakeMode
    {
        FullBody,
        LowerBody
    }

    [Serializable]
    public sealed class PmxDebugTargetFkBakeOptions
    {
        // Quaternion curves are reduced as a unit and measured in degrees.
        public float RotationErrorDegrees = 0.01f;
        // Root translation is reduced as a Vector3 and measured in Unity units.
        public float PositionError = 0.00005f;
        public bool BakeRootTranslation = true;

        /// <summary>
        /// Debug-only target Avatar upper-limb reconstruction for target-FK output.
        /// When false, every upper-limb Transform curve is built directly by the FK path
        /// and no HumanPose round trip is performed.
        /// </summary>
        public bool UpperLimbInverseSolve;
    }

    internal static class PmxTargetFkAnimationClipBaker
    {
        internal sealed class TargetBinding
        {
            internal HumanBodyBones HumanBone;
            internal int SourceBoneIndex;
            internal Transform Target;
            internal string Path;
            internal Quaternion RestLocalRotation;
            internal Quaternion RestWorldRotation;
            internal Quaternion RestParentWorldRotation;
            internal Vector3 RestLocalPosition;
            internal Matrix4x4 RootToParentVector;
            internal int ParentBindingIndex = -1;
        }

        private sealed class TargetCurveData
        {
            internal string Path;
            internal Keyframe[][] Rotation;
            internal Keyframe[][] Position;
        }

        internal static PmxAnimationBakeReport Debug_Bake(PmxModel model,
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared, GameObject targetRoot,
            PmxAnimationClipBakerOptions options, PmxDebugTargetFkBakeOptions fkOptions,
            Action<float, string> progress = null)
        {
            return Debug_Bake(
                model, prepared, targetRoot, options, fkOptions,
                PmxTargetFkBakeMode.FullBody, progress);
        }

        internal static PmxAnimationBakeReport Debug_Bake(PmxModel model,
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared, GameObject targetRoot,
            PmxAnimationClipBakerOptions options, PmxDebugTargetFkBakeOptions fkOptions,
            PmxTargetFkBakeMode bakeMode, Action<float, string> progress = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            options = options ?? new PmxAnimationClipBakerOptions();
            fkOptions = fkOptions ?? new PmxDebugTargetFkBakeOptions();

            Animator animator = targetRoot.GetComponent<Animator>() ??
                                targetRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman)
                throw new InvalidOperationException(
                    "Target-model FK fidelity output requires a valid Humanoid Animator.");

            List<TargetBinding> bindings = BuildBindings(model, targetRoot.transform, animator);
            TargetBinding hipsBinding = bindings.FirstOrDefault(
                binding => binding.HumanBone == HumanBodyBones.Hips);
            if (hipsBinding == null)
                throw new InvalidOperationException(
                    "The PMX and target Avatar must both provide a Hips/lower-body mapping.");

            int rootMotionSourceBoneIndex = FindRootMotionSourceBone(
                model, hipsBinding.SourceBoneIndex);
            Vector3 rootMotionSourceRestPosition =
                model.Bones[rootMotionSourceBoneIndex].Position;
            float rootMotionTargetScale = CalculateRootMotionTargetScale(
                rootMotionSourceRestPosition,
                options.PositionScale,
                hipsBinding.Target,
                animator.transform);
            bool suppressLeftBakedFkWaistCancellation =
                ShouldSuppressBakedFkWaistCancellation(model, prepared.Motion, true);
            bool suppressRightBakedFkWaistCancellation =
                ShouldSuppressBakedFkWaistCancellation(model, prepared.Motion, false);

            int sampleCount = prepared.Samples.Length;
            var curveData = new TargetCurveData[bindings.Count];
            var sampleTimes = new float[sampleCount];
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                sampleTimes[sampleIndex] = sampleIndex == sampleCount - 1
                    ? prepared.Duration
                    : sampleIndex / options.SampleRate;
            progress?.Invoke(0f, "Preparing target-model FK curves");
            // Keep this separate from the target-calibrated Humanoid option. Target-FK
            // upper-limb inverse solving must be enabled explicitly for this output type.
            bool solveTargetUpperLimb = fkOptions.UpperLimbInverseSolve;
            bool lowerBodyOnly = bakeMode == PmxTargetFkBakeMode.LowerBody;
            Parallel.For(0, bindings.Count, bindingIndex =>
            {
                HumanBodyBones humanBone = bindings[bindingIndex].HumanBone;
                if (lowerBodyOnly && !IsLowerBody(humanBone))
                    return;
                if (!lowerBodyOnly && solveTargetUpperLimb &&
                    PmxTargetHumanoidSourceRig.IsArmChain(bindings[bindingIndex].HumanBone))
                    return;
                curveData[bindingIndex] = BuildCurves(
                    prepared, sampleTimes, bindings, bindingIndex, options, fkOptions,
                    rootMotionSourceBoneIndex, rootMotionSourceRestPosition,
                    rootMotionTargetScale, suppressLeftBakedFkWaistCancellation,
                    suppressRightBakedFkWaistCancellation);
            });
            if (lowerBodyOnly)
                BuildTargetHumanoidProjectedCurves(
                    model, prepared, targetRoot, sampleTimes, bindings,
                    curveData, options, fkOptions, solveTargetUpperLimb, progress);
            else if (solveTargetUpperLimb)
                BuildInverseSolvedUpperLimbCurves(
                    model, prepared, targetRoot, sampleTimes, bindings,
                    curveData, options, fkOptions, progress);

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(prepared.Motion.ModelName)
                    ? "MMD Target FK Motion"
                    : prepared.Motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };

            int animatedBoneCount = 0;
            for (int index = 0; index < curveData.Length; index++)
            {
                TargetCurveData data = curveData[index];
                if (data == null) continue;
                bool wrote = WriteVectorCurves(clip, data.Path, "m_LocalPosition", data.Position);
                wrote |= WriteQuaternionCurves(clip, data.Path, data.Rotation);
                if (wrote) animatedBoneCount++;
                progress?.Invoke(
                    0.75f + 0.18f * (index + 1f) / Mathf.Max(1, curveData.Length),
                    $"Writing target FK curves {index + 1}/{curveData.Length}");
            }

            var report = new PmxAnimationBakeReport
            {
                Clip = clip,
                BoundBoneCount = animatedBoneCount,
                SampleCount = sampleCount
            };
            if (options.BakeMorphs)
                report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                    clip, prepared.Motion, targetRoot.transform);
            clip.EnsureQuaternionContinuity();
            progress?.Invoke(0.95f, "Finalizing target-model FK animation");
            return report;
        }

        internal static bool IsLowerBody(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Hips:
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.LeftToes:
                case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.RightLowerLeg:
                case HumanBodyBones.RightFoot:
                case HumanBodyBones.RightToes:
                    return true;
                default:
                    return false;
            }
        }

        private static void BuildTargetHumanoidProjectedCurves(
            PmxModel model,
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            GameObject targetRoot,
            float[] sampleTimes,
            IReadOnlyList<TargetBinding> bindings,
            TargetCurveData[] curveData,
            PmxAnimationClipBakerOptions options,
            PmxDebugTargetFkBakeOptions fkOptions,
            bool solveTargetUpperLimb,
            Action<float, string> progress)
        {
            int[] projectedBindingIndices = Enumerable.Range(0, bindings.Count)
                .Where(index => !IsLowerBody(bindings[index].HumanBone))
                .ToArray();
            if (projectedBindingIndices.Length == 0) return;

            var rotations = new Quaternion[projectedBindingIndices.Length][];
            var previous = new Quaternion[projectedBindingIndices.Length];
            var hasPrevious = new bool[projectedBindingIndices.Length];
            for (int projected = 0; projected < rotations.Length; projected++)
                rotations[projected] = new Quaternion[prepared.Samples.Length];

            PmxHumanoidSourceRig canonicalRig = null;
            HumanPoseHandler canonicalPoseHandler = null;
            try
            {
                if (!solveTargetUpperLimb)
                {
                    canonicalRig = PmxHumanoidSourceRig.Create(model, options);
                    canonicalPoseHandler = new HumanPoseHandler(
                        canonicalRig.Avatar, canonicalRig.Root.transform);
                }

                using (PmxTargetHumanoidSourceRig targetRig =
                       PmxTargetHumanoidSourceRig.Create(model, targetRoot, options))
                {
                    var targetPose = new HumanPose();
                    var canonicalPose = new HumanPose();
                    for (int sampleIndex = 0;
                         sampleIndex < prepared.Samples.Length;
                         sampleIndex++)
                    {
                        float vmdFrame = Mathf.Min(
                            prepared.Motion.MaximumFrame,
                            sampleTimes[sampleIndex] * 30f);
                        if (!solveTargetUpperLimb)
                        {
                            canonicalRig.Apply(prepared.Samples[sampleIndex], options.PositionScale);
                            canonicalPoseHandler.GetHumanPose(ref canonicalPose);
                            PmxHumanoidAnimationClipBaker.ValidateHumanPose(
                                canonicalPose, vmdFrame);
                        }

                        targetRig.Apply(
                            prepared.Samples[sampleIndex], solveTargetUpperLimb);
                        targetRig.GetHumanPose(ref targetPose);
                        PmxHumanoidAnimationClipBaker.ValidateHumanPose(targetPose, vmdFrame);
                        if (!solveTargetUpperLimb)
                            PmxTargetHumanoidAnimationClipBaker.CopyCanonicalUpperLimbMuscles(
                                canonicalPose, targetPose);
                        targetRig.SetHumanPose(ref targetPose);

                        for (int projected = 0;
                             projected < projectedBindingIndices.Length;
                             projected++)
                        {
                            TargetBinding binding = bindings[projectedBindingIndices[projected]];
                            Quaternion local = PmxAnimationClipBaker.NormalizeSafe(
                                targetRig.GetSolvedLocalRotation(binding.HumanBone));
                            if (hasPrevious[projected] &&
                                Quaternion.Dot(previous[projected], local) < 0f)
                                local = new Quaternion(-local.x, -local.y, -local.z, -local.w);
                            rotations[projected][sampleIndex] = local;
                            previous[projected] = local;
                            hasPrevious[projected] = true;
                        }

                        if ((sampleIndex & 31) == 0 ||
                            sampleIndex + 1 == prepared.Samples.Length)
                            progress?.Invoke(
                                0.05f + 0.65f * (sampleIndex + 1f) /
                                Mathf.Max(1, prepared.Samples.Length),
                                $"Projecting target Humanoid curves " +
                                $"{sampleIndex + 1}/{prepared.Samples.Length}");
                    }
                }
            }
            finally
            {
                if (canonicalPoseHandler != null) canonicalPoseHandler.Dispose();
                if (canonicalRig != null) canonicalRig.Dispose();
            }

            Parallel.For(0, projectedBindingIndices.Length, projected =>
            {
                int bindingIndex = projectedBindingIndices[projected];
                TargetBinding binding = bindings[bindingIndex];
                Keyframe[][] rotationCurves = BuildQuaternionCurves(
                    sampleTimes,
                    rotations[projected],
                    binding.RestLocalRotation,
                    Mathf.Max(0f, fkOptions.RotationErrorDegrees));
                if (rotationCurves == null) return;
                curveData[bindingIndex] = new TargetCurveData
                {
                    Path = binding.Path,
                    Rotation = rotationCurves
                };
            });
        }

        private static void BuildInverseSolvedUpperLimbCurves(
            PmxModel model,
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            GameObject targetRoot,
            float[] sampleTimes,
            IReadOnlyList<TargetBinding> bindings,
            TargetCurveData[] curveData,
            PmxAnimationClipBakerOptions options,
            PmxDebugTargetFkBakeOptions fkOptions,
            Action<float, string> progress)
        {
            int[] upperBindingIndices = Enumerable.Range(0, bindings.Count)
                .Where(index => PmxTargetHumanoidSourceRig.IsArmChain(
                    bindings[index].HumanBone))
                .ToArray();
            if (upperBindingIndices.Length == 0) return;

            var rotations = new Quaternion[upperBindingIndices.Length][];
            var previous = new Quaternion[upperBindingIndices.Length];
            var hasPrevious = new bool[upperBindingIndices.Length];
            for (int upper = 0; upper < rotations.Length; upper++)
                rotations[upper] = new Quaternion[prepared.Samples.Length];

            using (PmxTargetHumanoidSourceRig rig =
                   PmxTargetHumanoidSourceRig.Create(model, targetRoot, options))
            {
                var pose = new HumanPose();
                for (int sampleIndex = 0; sampleIndex < prepared.Samples.Length; sampleIndex++)
                {
                    rig.Apply(prepared.Samples[sampleIndex], true);
                    rig.RoundTripHumanPose(
                        ref pose,
                        Mathf.Min(prepared.Motion.MaximumFrame, sampleTimes[sampleIndex] * 30f));
                    for (int upper = 0; upper < upperBindingIndices.Length; upper++)
                    {
                        TargetBinding binding = bindings[upperBindingIndices[upper]];
                        Quaternion local = PmxAnimationClipBaker.NormalizeSafe(
                            rig.GetSolvedLocalRotation(binding.HumanBone));
                        if (hasPrevious[upper] && Quaternion.Dot(previous[upper], local) < 0f)
                            local = new Quaternion(-local.x, -local.y, -local.z, -local.w);
                        rotations[upper][sampleIndex] = local;
                        previous[upper] = local;
                        hasPrevious[upper] = true;
                    }

                    if ((sampleIndex & 31) == 0 || sampleIndex + 1 == prepared.Samples.Length)
                        progress?.Invoke(
                            0.05f + 0.65f * (sampleIndex + 1f) /
                            Mathf.Max(1, prepared.Samples.Length),
                            $"Inverse-solving target upper limbs {sampleIndex + 1}/{prepared.Samples.Length}");
                }
            }

            Parallel.For(0, upperBindingIndices.Length, upper =>
            {
                int bindingIndex = upperBindingIndices[upper];
                TargetBinding binding = bindings[bindingIndex];
                Keyframe[][] rotationCurves = BuildQuaternionCurves(
                    sampleTimes,
                    rotations[upper],
                    binding.RestLocalRotation,
                    Mathf.Max(0f, fkOptions.RotationErrorDegrees));
                if (rotationCurves == null) return;
                curveData[bindingIndex] = new TargetCurveData
                {
                    Path = binding.Path,
                    Rotation = rotationCurves
                };
            });
        }

        internal static PmxAnimationBakeReport Debug_BakeToAsset(string pmxPath, string vmdPath,
            GameObject targetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options = null, PmxDebugTargetFkBakeOptions fkOptions = null,
            Action<float, string> progress = null)
        {
            return Debug_BakeToAsset(
                pmxPath, vmdPath, targetRoot, outputAssetPath, options, fkOptions,
                PmxTargetFkBakeMode.FullBody, progress);
        }

        internal static PmxAnimationBakeReport Debug_BakeToAsset(string pmxPath, string vmdPath,
            GameObject targetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options, PmxDebugTargetFkBakeOptions fkOptions,
            PmxTargetFkBakeMode bakeMode, Action<float, string> progress = null)
        {
            PmxAnimationClipBaker.ValidateAssetPath(outputAssetPath);
            options = options ?? new PmxAnimationClipBakerOptions();
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report;
            using (var session = new PmxHumanoidAnimationClipBaker.BatchSession(model, options, false))
            {
                PmxHumanoidAnimationClipBaker.PreparedMotion prepared = session.Prepare(motion);
                report = Debug_Bake(
                    model, prepared, targetRoot, options, fkOptions, bakeMode, progress);
            }
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            progress?.Invoke(0.96f, "Writing target FK AnimationClip asset");
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            progress?.Invoke(1f, "Conversion complete");
            return report;
        }

        internal static List<TargetBinding> BuildBindings(PmxModel model, Transform targetRoot,
            Animator animator)
        {
            Dictionary<string, SkeletonBone> skeleton = BuildSkeletonLookup(animator.avatar);
            var restWorldRotations = new Dictionary<Transform, Quaternion>();
            var result = new List<TargetBinding>();
            var usedSources = new HashSet<int>();
            var usedTargets = new HashSet<Transform>();

            foreach (PmxHumanoidSourceRig.HumanBoneRule rule in PmxHumanoidSourceRig.HumanBoneRules)
            {
                Transform target = animator.GetBoneTransform(rule.HumanBone);
                if (target == null || !target.IsChildOf(targetRoot) || !usedTargets.Add(target)) continue;
                int sourceIndex = FindSourceBone(model, rule.Candidates, usedSources);
                if (sourceIndex < 0) continue;
                usedSources.Add(sourceIndex);

                Quaternion restLocalRotation = skeleton.TryGetValue(target.name, out SkeletonBone restBone)
                    ? restBone.rotation
                    : target.localRotation;
                Vector3 restLocalPosition = skeleton.TryGetValue(target.name, out restBone)
                    ? restBone.position
                    : target.localPosition;
                Quaternion restWorldRotation = GetRestWorldRotation(
                    target, targetRoot, skeleton, restWorldRotations);
                Quaternion restParentWorldRotation = target.parent != null
                    ? GetRestWorldRotation(target.parent, targetRoot, skeleton, restWorldRotations)
                    : Quaternion.identity;
                Matrix4x4 rootToParent = target.parent != null
                    ? target.parent.worldToLocalMatrix * targetRoot.localToWorldMatrix
                    : Matrix4x4.identity;

                result.Add(new TargetBinding
                {
                    HumanBone = rule.HumanBone,
                    SourceBoneIndex = sourceIndex,
                    Target = target,
                    Path = AnimationUtility.CalculateTransformPath(target, targetRoot),
                    RestLocalRotation = PmxAnimationClipBaker.NormalizeSafe(restLocalRotation),
                    RestWorldRotation = PmxAnimationClipBaker.NormalizeSafe(restWorldRotation),
                    RestParentWorldRotation = PmxAnimationClipBaker.NormalizeSafe(restParentWorldRotation),
                    RestLocalPosition = restLocalPosition,
                    RootToParentVector = rootToParent
                });
            }

            var byTarget = new Dictionary<Transform, int>();
            for (int index = 0; index < result.Count; index++) byTarget[result[index].Target] = index;
            for (int index = 0; index < result.Count; index++)
            {
                for (Transform parent = result[index].Target.parent;
                     parent != null && parent != targetRoot;
                     parent = parent.parent)
                {
                    if (!byTarget.TryGetValue(parent, out int parentIndex)) continue;
                    result[index].ParentBindingIndex = parentIndex;
                    break;
                }
            }
            return result;
        }

        private static TargetCurveData BuildCurves(
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            float[] times,
            IReadOnlyList<TargetBinding> bindings, int bindingIndex,
            PmxAnimationClipBakerOptions options, PmxDebugTargetFkBakeOptions fkOptions,
            int rootMotionSourceBoneIndex, Vector3 rootMotionSourceRestPosition,
            float rootMotionTargetScale, bool suppressLeftBakedFkWaistCancellation,
            bool suppressRightBakedFkWaistCancellation)
        {
            TargetBinding binding = bindings[bindingIndex];
            int count = prepared.Samples.Length;
            var rotations = new Quaternion[count];
            Vector3[] positions = binding.HumanBone == HumanBodyBones.Hips && fkOptions.BakeRootTranslation
                ? new Vector3[count]
                : null;
            Quaternion previous = Quaternion.identity;
            bool hasPrevious = false;

            for (int sampleIndex = 0; sampleIndex < count; sampleIndex++)
            {
                PmxHumanoidPoseSample sample = prepared.Samples[sampleIndex];
                bool suppressMmdWaistCancellation =
                    binding.HumanBone == HumanBodyBones.LeftUpperLeg
                        ? suppressLeftBakedFkWaistCancellation
                        : binding.HumanBone == HumanBodyBones.RightUpperLeg &&
                          suppressRightBakedFkWaistCancellation;
                Quaternion local = EvaluateLocalRotation(
                    sample, bindings, bindingIndex, options, suppressMmdWaistCancellation);
                if (hasPrevious && Quaternion.Dot(previous, local) < 0f)
                    local = new Quaternion(-local.x, -local.y, -local.z, -local.w);
                rotations[sampleIndex] = local;
                previous = local;
                hasPrevious = true;

                if (positions == null) continue;
                positions[sampleIndex] = EvaluateRootLocalPosition(
                    sample, binding, rootMotionSourceBoneIndex,
                    rootMotionSourceRestPosition, options, rootMotionTargetScale);
            }

            Keyframe[][] rotationCurves = BuildQuaternionCurves(
                times, rotations, binding.RestLocalRotation,
                Mathf.Max(0f, fkOptions.RotationErrorDegrees));
            Keyframe[][] positionCurves = positions != null
                ? BuildVectorCurves(times, positions, binding.RestLocalPosition,
                    Mathf.Max(0f, fkOptions.PositionError))
                : null;
            return rotationCurves == null && positionCurves == null
                ? null
                : new TargetCurveData
                {
                    Path = binding.Path,
                    Rotation = rotationCurves,
                    Position = positionCurves
                };
        }

        private static Keyframe[][] BuildQuaternionCurves(float[] times, Quaternion[] values,
            Quaternion rest, float maximumErrorDegrees)
        {
            if (values.Length == 0) return null;
            bool differsFromRest = false;
            bool changes = false;
            for (int index = 0; index < values.Length; index++)
            {
                differsFromRest |= Quaternion.Angle(rest, values[index]) > 0.00001f;
                if (index > 0) changes |= Quaternion.Angle(values[0], values[index]) > 0.00001f;
            }
            if (!differsFromRest) return null;
            int[] kept = changes
                ? ReduceQuaternionSamples(times, values, maximumErrorDegrees)
                : new[] { 0 };
            var result = new Keyframe[4][];
            for (int component = 0; component < 4; component++)
            {
                var keys = new Keyframe[kept.Length];
                for (int keyIndex = 0; keyIndex < kept.Length; keyIndex++)
                {
                    int source = kept[keyIndex];
                    Quaternion value = values[source];
                    float componentValue = component == 0 ? value.x :
                        component == 1 ? value.y : component == 2 ? value.z : value.w;
                    keys[keyIndex] = new Keyframe(times[source], componentValue);
                }
                result[component] = PmxAnimationClipBaker.CreateLinearKeys(keys);
            }
            return result;
        }

        private static Keyframe[][] BuildVectorCurves(float[] times, Vector3[] values,
            Vector3 rest, float maximumError)
        {
            if (values.Length == 0) return null;
            bool differsFromRest = false;
            bool changes = false;
            for (int index = 0; index < values.Length; index++)
            {
                differsFromRest |= (values[index] - rest).sqrMagnitude > 1e-14f;
                if (index > 0) changes |= (values[index] - values[0]).sqrMagnitude > 1e-14f;
            }
            if (!differsFromRest) return null;
            int[] kept = changes ? ReduceVectorSamples(times, values, maximumError) : new[] { 0 };
            var result = new Keyframe[3][];
            for (int component = 0; component < 3; component++)
            {
                var keys = new Keyframe[kept.Length];
                for (int keyIndex = 0; keyIndex < kept.Length; keyIndex++)
                {
                    int source = kept[keyIndex];
                    Vector3 value = values[source];
                    keys[keyIndex] = new Keyframe(
                        times[source], component == 0 ? value.x : component == 1 ? value.y : value.z);
                }
                result[component] = PmxAnimationClipBaker.CreateLinearKeys(keys);
            }
            return result;
        }

        private static int[] ReduceQuaternionSamples(float[] times, Quaternion[] values,
            float maximumErrorDegrees)
        {
            if (values.Length <= 2 || maximumErrorDegrees <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            double radians = maximumErrorDegrees * Math.PI / 180.0;
            double maximumDotError = 1.0 - Math.Cos(radians * 0.5);
            return ReduceSamples(times, values.Length, (start, end, index) =>
            {
                float factor = GetFactor(times, start, end, index);
                Quaternion expected = Quaternion.LerpUnclamped(values[start], values[end], factor);
                Quaternion actual = values[index];
                double dot = Math.Abs(
                    (double)expected.x * actual.x +
                    (double)expected.y * actual.y +
                    (double)expected.z * actual.z +
                    (double)expected.w * actual.w);
                return 1.0 - Math.Min(1.0, dot);
            }, maximumDotError);
        }

        private static int[] ReduceVectorSamples(float[] times, Vector3[] values, float maximumError)
        {
            if (values.Length <= 2 || maximumError <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            double maximumSquaredError = (double)maximumError * maximumError;
            return ReduceSamples(times, values.Length, (start, end, index) =>
            {
                float factor = GetFactor(times, start, end, index);
                Vector3 difference =
                    Vector3.LerpUnclamped(values[start], values[end], factor) - values[index];
                return (double)difference.x * difference.x +
                       (double)difference.y * difference.y +
                       (double)difference.z * difference.z;
            }, maximumSquaredError);
        }

        private static int[] ReduceSamples(float[] times, int count,
            Func<int, int, int, double> getError, double maximumError)
        {
            var keep = new bool[count];
            keep[0] = true;
            keep[count - 1] = true;
            var ranges = new Stack<int>();
            ranges.Push(0);
            ranges.Push(count - 1);
            while (ranges.Count > 0)
            {
                int end = ranges.Pop();
                int start = ranges.Pop();
                double largest = -1.0;
                int split = -1;
                for (int index = start + 1; index < end; index++)
                {
                    double error = getError(start, end, index);
                    if (error <= largest) continue;
                    largest = error;
                    split = index;
                }
                if (split < 0 || largest <= maximumError) continue;
                keep[split] = true;
                ranges.Push(start);
                ranges.Push(split);
                ranges.Push(split);
                ranges.Push(end);
            }
            var result = new List<int>(count);
            for (int index = 0; index < count; index++) if (keep[index]) result.Add(index);
            return result.ToArray();
        }

        private static float GetFactor(float[] times, int start, int end, int index)
        {
            float duration = times[end] - times[start];
            return Mathf.Abs(duration) > 1e-12f ? (times[index] - times[start]) / duration : 0f;
        }

        private static bool WriteQuaternionCurves(AnimationClip clip, string path, Keyframe[][] curves)
        {
            if (curves == null) return false;
            string[] properties =
            {
                "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w"
            };
            for (int index = 0; index < properties.Length; index++)
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(path, typeof(Transform), properties[index]),
                    new AnimationCurve(curves[index]));
            return true;
        }

        private static bool WriteVectorCurves(AnimationClip clip, string path, string property,
            Keyframe[][] curves)
        {
            if (curves == null) return false;
            for (int index = 0; index < 3; index++)
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(
                        path, typeof(Transform), property + "." + (index == 0 ? "x" : index == 1 ? "y" : "z")),
                    new AnimationCurve(curves[index]));
            return true;
        }

        internal static Quaternion EvaluateLocalRotation(PmxHumanoidPoseSample sample,
            IReadOnlyList<TargetBinding> bindings, int bindingIndex,
            PmxAnimationClipBakerOptions options,
            bool suppressUnmappedParentRotation = false)
        {
            TargetBinding binding = bindings[bindingIndex];
            Quaternion sourceWorld = PmxAnimationClipBaker.ConvertRotation(
                sample.WorldRotations[binding.SourceBoneIndex], options);
            Quaternion sourceParentWorld = Quaternion.identity;
            if (binding.ParentBindingIndex >= 0)
            {
                TargetBinding parentBinding = bindings[binding.ParentBindingIndex];
                sourceParentWorld = PmxAnimationClipBaker.ConvertRotation(
                    sample.WorldRotations[parentBinding.SourceBoneIndex], options);
            }
            if (suppressUnmappedParentRotation && binding.ParentBindingIndex >= 0)
            {
                // Standard MMD waist rigs place a helper with -1 waist rotation
                // between LowerBody and each upper leg. Reproducing that helper in
                // Humanoid requires roughly 180 degrees of counter-twist in each hip
                // during a turn, which lies far outside the muscle range. Preserve the
                // leg's own FK rotation but let it inherit the Humanoid root rotation.
                Quaternion sourceLocal = PmxAnimationClipBaker.ConvertRotation(
                    sample.LocalRotations[binding.SourceBoneIndex], options);
                sourceWorld = PmxAnimationClipBaker.NormalizeSafe(
                    sourceParentWorld * sourceLocal);
            }
            // PMX rotations are evaluated in the source model/parent basis. Preserve
            // that world-space motion on the target reference axes, then derive the
            // local value against the animated mapped ancestor. Directly appending the
            // relative PMX quaternion to RestLocalRotation rotates around the target's
            // FBX-local axes instead, which is especially wrong for rigs whose hips and
            // legs have 90/180-degree reference rotations.
            Quaternion desiredWorld = PmxAnimationClipBaker.NormalizeSafe(
                sourceWorld * binding.RestWorldRotation);
            Quaternion desiredParentWorld = binding.RestParentWorldRotation;
            if (binding.ParentBindingIndex >= 0)
                desiredParentWorld = PmxAnimationClipBaker.NormalizeSafe(
                    sourceParentWorld * binding.RestParentWorldRotation);
            return PmxAnimationClipBaker.NormalizeSafe(
                Quaternion.Inverse(desiredParentWorld) * desiredWorld);
        }

        internal static Vector3 EvaluateRootLocalPosition(PmxHumanoidPoseSample sample,
            TargetBinding binding, Vector3 sourceRestPosition,
            PmxAnimationClipBakerOptions options)
        {
            return EvaluateRootLocalPosition(sample, binding, binding.SourceBoneIndex,
                sourceRestPosition, options, 1f);
        }

        internal static Vector3 EvaluateRootLocalPosition(PmxHumanoidPoseSample sample,
            TargetBinding binding, int sourceBoneIndex, Vector3 sourceRestPosition,
            PmxAnimationClipBakerOptions options, float targetScale)
        {
            Vector3 sourceDisplacement =
                sample.WorldPositions[sourceBoneIndex] - sourceRestPosition;
            Vector3 rootDisplacement = PmxAnimationClipBaker.ConvertPosition(
                sourceDisplacement, options) * (options.PositionScale * targetScale);
            return binding.RestLocalPosition +
                   binding.RootToParentVector.MultiplyVector(rootDisplacement);
        }

        internal static int FindRootMotionSourceBone(PmxModel model, int hipsSourceBoneIndex)
        {
            int current = hipsSourceBoneIndex;
            while (current >= 0 && current < model.Bones.Count)
            {
                PmxBone bone = model.Bones[current];
                if (string.Equals(bone.NameJapanese, "\u8170", StringComparison.Ordinal) ||
                    string.Equals(bone.NameEnglish, "waist", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bone.NameEnglish, "hips", StringComparison.OrdinalIgnoreCase))
                    return current;
                current = bone.ParentIndex;
            }
            return hipsSourceBoneIndex;
        }

        internal static float CalculateRootMotionTargetScale(
            Vector3 sourceRestPosition, float positionScale,
            Transform targetHips, Transform targetAvatarRoot)
        {
            if (targetHips == null || targetAvatarRoot == null) return 1f;
            float sourceReferenceHeight = Mathf.Abs(sourceRestPosition.y * positionScale);
            float targetReferenceHeight = Mathf.Abs(
                targetHips.position.y - targetAvatarRoot.position.y);
            return sourceReferenceHeight > 1e-5f && targetReferenceHeight > 1e-5f
                ? targetReferenceHeight / sourceReferenceHeight
                : 1f;
        }

        internal static int FindSourceBone(PmxModel model, IEnumerable<string> candidates,
            ISet<int> usedSources)
        {
            foreach (string candidate in candidates)
            {
                int index = model.FindBone(candidate);
                if (index >= 0 && (usedSources == null || !usedSources.Contains(index))) return index;
            }
            return -1;
        }

        private static bool ShouldSuppressBakedFkWaistCancellation(
            PmxModel model, VmdMotion motion, bool left)
        {
            if (model == null || motion == null || MotionModelMatchesPmx(model, motion))
                return false;

            string[] upperLegNames = left
                ? new[] { "左足", "left leg", "LeftLeg", "LeftUpperLeg" }
                : new[] { "右足", "right leg", "RightLeg", "RightUpperLeg" };
            string[] lowerLegNames = left
                ? new[] { "左ひざ", "左膝", "left knee", "LeftKnee", "LeftLowerLeg" }
                : new[] { "右ひざ", "右膝", "right knee", "RightKnee", "RightLowerLeg" };
            string[] footNames = left
                ? new[] { "左足首", "left ankle", "LeftAnkle", "LeftFoot" }
                : new[] { "右足首", "right ankle", "RightAnkle", "RightFoot" };
            string[] ikNames = left
                ? new[] { "左足ＩＫ", "左足IK", "left leg IK", "LeftLegIK" }
                : new[] { "右足ＩＫ", "右足IK", "right leg IK", "RightLegIK" };

            return HasAnimatedBoneTrack(motion, upperLegNames) &&
                   HasAnimatedBoneTrack(motion, lowerLegNames) &&
                   HasAnimatedBoneTrack(motion, footNames) &&
                   IsIkExplicitlyDisabledThroughout(motion, ikNames);
        }

        private static bool HasAnimatedBoneTrack(VmdMotion motion, IEnumerable<string> names)
        {
            foreach (string name in names)
                if (motion.BoneTracks.TryGetValue(name, out VmdBoneTrack track) &&
                    track.Keys.Count > 1)
                    return true;
            return false;
        }

        private static bool IsIkExplicitlyDisabledThroughout(
            VmdMotion motion, IEnumerable<string> names)
        {
            var candidates = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            bool found = false;
            foreach (VmdIkKeyframe keyframe in motion.IkKeyframes)
            {
                foreach (VmdIkState state in keyframe.States)
                {
                    if (!candidates.Contains(state.Name)) continue;
                    found = true;
                    if (state.Enabled) return false;
                }
            }
            return found;
        }

        private static bool MotionModelMatchesPmx(PmxModel model, VmdMotion motion)
        {
            string motionName = NormalizeModelName(motion.ModelName);
            if (string.IsNullOrEmpty(motionName)) return true;
            foreach (string sourceName in new[] { model.NameJapanese, model.NameEnglish })
            {
                string modelName = NormalizeModelName(sourceName);
                if (string.IsNullOrEmpty(modelName)) continue;
                if (string.Equals(motionName, modelName, StringComparison.OrdinalIgnoreCase) ||
                    motionName.Length >= 3 && modelName.IndexOf(
                        motionName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    modelName.Length >= 3 && motionName.IndexOf(
                        modelName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string NormalizeModelName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return new string(value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());
        }

        internal static Dictionary<string, SkeletonBone> BuildSkeletonLookup(Avatar avatar)
        {
            var result = new Dictionary<string, SkeletonBone>(StringComparer.Ordinal);
            try
            {
                foreach (SkeletonBone bone in avatar.humanDescription.skeleton)
                    if (!string.IsNullOrEmpty(bone.name) && !result.ContainsKey(bone.name))
                        result.Add(bone.name, bone);
            }
            catch (Exception)
            {
                // Scene-local transforms remain a valid fallback for generated or unusual Avatars.
            }
            return result;
        }

        private static Quaternion GetRestWorldRotation(Transform transform, Transform targetRoot,
            IReadOnlyDictionary<string, SkeletonBone> skeleton,
            IDictionary<Transform, Quaternion> cache)
        {
            if (transform == null || transform == targetRoot) return Quaternion.identity;
            if (cache.TryGetValue(transform, out Quaternion cached)) return cached;
            Quaternion parent = GetRestWorldRotation(transform.parent, targetRoot, skeleton, cache);
            Quaternion local = skeleton.TryGetValue(transform.name, out SkeletonBone bone)
                ? bone.rotation
                : transform.localRotation;
            Quaternion result = PmxAnimationClipBaker.NormalizeSafe(parent * local);
            cache[transform] = result;
            return result;
        }
    }
}
