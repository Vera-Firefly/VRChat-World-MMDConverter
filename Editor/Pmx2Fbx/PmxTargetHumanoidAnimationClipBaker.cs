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
    internal static class PmxTargetHumanoidAnimationClipBaker
    {
        private static readonly bool[] CanonicalUpperLimbMuscles =
            BuildCanonicalUpperLimbMuscleMask();

        internal static PmxAnimationBakeReport Bake(
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            PmxAnimationClipBakerOptions options,
            PmxTargetHumanoidSourceRig rig,
            PmxHumanoidSourceRig canonicalRig,
            HumanPoseHandler canonicalPoseHandler,
            Action<float, string> progress = null)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (canonicalRig == null) throw new ArgumentNullException(nameof(canonicalRig));
            if (canonicalPoseHandler == null)
                throw new ArgumentNullException(nameof(canonicalPoseHandler));
            options = options ?? new PmxAnimationClipBakerOptions();
            int sampleCount = prepared.Samples.Length;
            float[] sampleTimes = PmxHumanoidAnimationClipBaker.CreateSampleTimes(
                sampleCount, prepared.Duration, options.SampleRate);
            var muscles = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator[HumanTrait.MuscleCount];
            for (int index = 0; index < muscles.Length; index++)
                muscles[index] = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootTx = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootTy = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootTz = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQx = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQy = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQz = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQw = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var humanPose = new HumanPose();
            var canonicalPose = new HumanPose();
            Quaternion previousBodyRotation = Quaternion.identity;
            bool hasPreviousBodyRotation = false;

            for (int sample = 0; sample < sampleCount; sample++)
            {
                float vmdFrame = PmxHumanoidAnimationClipBaker.CalculateVmdFrame(
                    sample, sampleCount, options.SampleRate,
                    prepared.Motion.MaximumFrame);
                if (!options.DebugTargetUpperLimbInverseSolve)
                {
                    canonicalRig.Apply(prepared.Samples[sample], options.PositionScale);
                    canonicalPoseHandler.GetHumanPose(ref canonicalPose);
                    PmxHumanoidAnimationClipBaker.ValidateHumanPose(canonicalPose, vmdFrame);
                }
                rig.Apply(prepared.Samples[sample], options.DebugTargetUpperLimbInverseSolve);
                rig.GetHumanPose(ref humanPose);
                PmxHumanoidAnimationClipBaker.ValidateHumanPose(humanPose, vmdFrame);
                Vector3 bodyPosition = PmxHumanoidAnimationClipBaker.ConvertHumanoidRootPosition(
                    humanPose.bodyPosition, options.CoordinateConversion);
                Quaternion bodyRotation = PmxHumanoidAnimationClipBaker.ConvertHumanoidRootRotation(
                    humanPose.bodyRotation, options.CoordinateConversion);
                if (hasPreviousBodyRotation && Quaternion.Dot(previousBodyRotation, bodyRotation) < 0f)
                    bodyRotation = new Quaternion(
                        -bodyRotation.x, -bodyRotation.y, -bodyRotation.z, -bodyRotation.w);
                previousBodyRotation = bodyRotation;
                hasPreviousBodyRotation = true;

                rootTx.Add(bodyPosition.x);
                rootTy.Add(bodyPosition.y);
                rootTz.Add(bodyPosition.z);
                rootQx.Add(bodyRotation.x);
                rootQy.Add(bodyRotation.y);
                rootQz.Add(bodyRotation.z);
                rootQw.Add(bodyRotation.w);
                for (int muscle = 0; muscle < muscles.Length; muscle++)
                {
                    // Humanoid upper-limb muscles are already a canonical, avatar-independent
                    // representation. Reconstructing target FK first and extracting the muscles
                    // again changes the rotation basis on A-pose/non-MMD arm axes and can flip
                    // arm/forearm twist to the opposite +/-2 branch. Keep the canonical PMX
                    // Humanoid solution for the complete upper limb; target calibration remains
                    // active for root, torso and legs where it corrects target proportions.
                    float value = !options.DebugTargetUpperLimbInverseSolve &&
                                  IsCanonicalUpperLimbMuscle(muscle)
                        ? canonicalPose.muscles[muscle]
                        : humanPose.muscles[muscle];
                    muscles[muscle].Add(value);
                }
                if ((sample & 15) == 0 || sample + 1 == sampleCount)
                    progress?.Invoke(0.8f * (sample + 1f) / Mathf.Max(1, sampleCount),
                        $"Calibrating Humanoid frame {sample + 1}/{sampleCount}");
            }

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(prepared.Motion.ModelName)
                    ? "MMD Target-calibrated Humanoid Motion"
                    : prepared.Motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };
            var accumulators = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator[7 + muscles.Length];
            accumulators[0] = rootTx;
            accumulators[1] = rootTy;
            accumulators[2] = rootTz;
            accumulators[3] = rootQx;
            accumulators[4] = rootQy;
            accumulators[5] = rootQz;
            accumulators[6] = rootQw;
            Array.Copy(muscles, 0, accumulators, 7, muscles.Length);
            var optimizedKeys = new Keyframe[accumulators.Length][];
            Parallel.For(0, accumulators.Length, index =>
            {
                bool keepConstant = index < 7;
                PmxHumanoidAnimationClipBaker.FloatCurveAccumulator accumulator = accumulators[index];
                if (!keepConstant && accumulator.IsApproximatelyZero()) return;
                optimizedKeys[index] = accumulator.ToLinearKeys(
                    options.CurveReductionError);
            });

            var properties = new string[optimizedKeys.Length];
            properties[0] = "RootT.x";
            properties[1] = "RootT.y";
            properties[2] = "RootT.z";
            properties[3] = "RootQ.x";
            properties[4] = "RootQ.y";
            properties[5] = "RootQ.z";
            properties[6] = "RootQ.w";
            for (int muscle = 0; muscle < muscles.Length; muscle++)
                properties[7 + muscle] =
                    PmxHumanoidAnimationClipBaker.GetAnimatorMusclePropertyName(muscle);
            PmxHumanoidAnimationClipBaker.WriteAnimatorCurves(clip, properties, optimizedKeys);
            clip.EnsureQuaternionContinuity();
            progress?.Invoke(0.95f, "Finalizing target-calibrated Humanoid animation");
            return new PmxAnimationBakeReport
            {
                Clip = clip,
                BoundBoneCount = rig.BindingCount,
                SampleCount = sampleCount
            };
        }

        private static bool[] BuildCanonicalUpperLimbMuscleMask()
        {
            var result = new bool[HumanTrait.MuscleCount];
            for (int index = 0; index < result.Length; index++)
            {
                string name = HumanTrait.MuscleName[index];
                if (string.IsNullOrEmpty(name)) continue;
                result[index] = IsUpperLimbMuscle(name, "Left ") ||
                                IsUpperLimbMuscle(name, "Right ");
            }
            return result;
        }

        internal static bool IsCanonicalUpperLimbMuscle(int muscleIndex)
        {
            return muscleIndex >= 0 &&
                   muscleIndex < CanonicalUpperLimbMuscles.Length &&
                   CanonicalUpperLimbMuscles[muscleIndex];
        }

        internal static void CopyCanonicalUpperLimbMuscles(
            HumanPose canonicalPose, HumanPose targetPose)
        {
            if (canonicalPose.muscles == null || targetPose.muscles == null ||
                canonicalPose.muscles.Length != HumanTrait.MuscleCount ||
                targetPose.muscles.Length != HumanTrait.MuscleCount)
                throw new InvalidOperationException(
                    "Cannot project an invalid Humanoid upper-limb pose.");
            for (int muscle = 0; muscle < CanonicalUpperLimbMuscles.Length; muscle++)
                if (CanonicalUpperLimbMuscles[muscle])
                    targetPose.muscles[muscle] = canonicalPose.muscles[muscle];
        }

        private static bool IsUpperLimbMuscle(string name, string side)
        {
            if (!name.StartsWith(side, StringComparison.Ordinal)) return false;
            string part = name.Substring(side.Length);
            return part.StartsWith("Shoulder ", StringComparison.Ordinal) ||
                   part.StartsWith("Arm ", StringComparison.Ordinal) ||
                   part.StartsWith("Forearm ", StringComparison.Ordinal) ||
                   part.StartsWith("Hand ", StringComparison.Ordinal) ||
                   part.StartsWith("Thumb ", StringComparison.Ordinal) ||
                   part.StartsWith("Index ", StringComparison.Ordinal) ||
                   part.StartsWith("Middle ", StringComparison.Ordinal) ||
                   part.StartsWith("Ring ", StringComparison.Ordinal) ||
                   part.StartsWith("Little ", StringComparison.Ordinal);
        }

        internal static PmxAnimationBakeReport BakeToAsset(string pmxPath, string vmdPath,
            GameObject targetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options = null, Action<float, string> progress = null)
        {
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            PmxAnimationClipBaker.ValidateAssetPath(outputAssetPath);
            options = options ?? new PmxAnimationClipBakerOptions();
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report;
            using (var session = new PmxHumanoidAnimationClipBaker.BatchSession(model, options))
            {
                PmxHumanoidAnimationClipBaker.PreparedMotion prepared = session.Prepare(motion);
                report = session.BakeTargetHumanoid(prepared, targetRoot, progress);
            }
            if (options.BakeMorphs)
                report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                    report.Clip, motion, targetRoot.transform);
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            progress?.Invoke(0.96f, "Writing target-calibrated Humanoid AnimationClip asset");
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            progress?.Invoke(1f, "Conversion complete");
            return report;
        }
    }
}
