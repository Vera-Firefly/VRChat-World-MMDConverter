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
    internal static class PmxHumanoidAnimationClipBaker
    {
        public static PmxAnimationBakeReport Bake(PmxModel model, VmdMotion motion,
            Transform morphTargetRoot = null, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            options = options ?? new PmxAnimationClipBakerOptions();
            using (var session = new BatchSession(model, options))
                return session.Bake(motion, morphTargetRoot, progress);
        }

        internal sealed class BatchSession : IDisposable
        {
            private readonly PmxModel _model;
            private readonly PmxAnimationClipBakerOptions _options;
            private PmxHumanoidSourceRig _rig;
            private HumanPoseHandler _poseHandler;
            private PmxTargetHumanoidSourceRig _targetHumanoidRig;
            private int _targetHumanoidRootId;
            private bool _disposed;

            internal BatchSession(PmxModel model, PmxAnimationClipBakerOptions options,
                bool createHumanoidRig = true)
            {
                _model = model ?? throw new ArgumentNullException(nameof(model));
                _options = options ?? new PmxAnimationClipBakerOptions();
                if (_options.SampleRate <= 0f || _options.SampleRate > 240f)
                    throw new ArgumentOutOfRangeException(nameof(options.SampleRate));
                if (!createHumanoidRig) return;

                try
                {
                    _rig = PmxHumanoidSourceRig.Create(_model, _options);
                    if (_rig.Avatar == null || !_rig.Avatar.isValid || !_rig.Avatar.isHuman)
                        throw new InvalidOperationException(
                            "Unity could not create a valid Humanoid Avatar from the PMX skeleton. " +
                            "Required MMD humanoid bones may be missing or the PMX rest pose may be invalid.");
                    _poseHandler = new HumanPoseHandler(_rig.Avatar, _rig.Root.transform);
                }
                catch
                {
                    if (_poseHandler != null) _poseHandler.Dispose();
                    if (_rig != null) _rig.Dispose();
                    _poseHandler = null;
                    _rig = null;
                    throw;
                }
            }

            internal PmxAnimationBakeReport Bake(VmdMotion motion, Transform morphTargetRoot = null,
                Action<float, string> progress = null)
            {
                if (motion == null) throw new ArgumentNullException(nameof(motion));
                if (_rig == null || _poseHandler == null)
                    throw new ObjectDisposedException(nameof(BatchSession));
                progress?.Invoke(0f, "Preparing MMD bone and IK samples");
                PreparedMotion prepared = Prepare(motion);
                return BakePrepared(_model, prepared, morphTargetRoot, _options, progress, _rig, _poseHandler);
            }

            internal PreparedMotion Prepare(VmdMotion motion, int maximumParallelism = 0,
                Action<int, int> threadSafeProgress = null, CancellationToken cancellationToken = default)
            {
                if (motion == null) throw new ArgumentNullException(nameof(motion));
                if (_disposed)
                    throw new ObjectDisposedException(nameof(BatchSession));
                return PrepareMotion(
                    _model, motion, _options, maximumParallelism, threadSafeProgress, cancellationToken);
            }

            internal PmxAnimationBakeReport Bake(PreparedMotion prepared, Transform morphTargetRoot = null,
                Action<float, string> progress = null)
            {
                if (prepared == null) throw new ArgumentNullException(nameof(prepared));
                if (_rig == null || _poseHandler == null)
                    throw new ObjectDisposedException(nameof(BatchSession));
                return BakePrepared(_model, prepared, morphTargetRoot, _options, progress, _rig, _poseHandler);
            }

            internal PmxAnimationBakeReport Debug_BakeTargetFk(PreparedMotion prepared, GameObject targetRoot,
                PmxDebugTargetFkBakeOptions fkOptions, Action<float, string> progress = null)
            {
                if (prepared == null) throw new ArgumentNullException(nameof(prepared));
                if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
                if (_disposed)
                    throw new ObjectDisposedException(nameof(BatchSession));
                return PmxTargetFkAnimationClipBaker.Debug_Bake(
                    _model, prepared, targetRoot, _options, fkOptions, progress);
            }

            internal PmxAnimationBakeReport BakeTargetHumanoid(PreparedMotion prepared,
                GameObject targetRoot, Action<float, string> progress = null)
            {
                if (prepared == null) throw new ArgumentNullException(nameof(prepared));
                if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
                if (_disposed) throw new ObjectDisposedException(nameof(BatchSession));
                if (_rig == null || _poseHandler == null)
                    throw new InvalidOperationException(
                        "Target-calibrated Humanoid output requires the canonical Humanoid source rig.");
                int targetId = targetRoot.GetInstanceID();
                if (_targetHumanoidRig == null || _targetHumanoidRootId != targetId)
                {
                    if (_targetHumanoidRig != null) _targetHumanoidRig.Dispose();
                    _targetHumanoidRig = null;
                    _targetHumanoidRootId = 0;
                    PmxTargetHumanoidSourceRig targetRig = PmxTargetHumanoidSourceRig.Create(
                        _model, targetRoot, _options);
                    _targetHumanoidRig = targetRig;
                    _targetHumanoidRootId = targetId;
                }
                return PmxTargetHumanoidAnimationClipBaker.Bake(
                    prepared, _options, _targetHumanoidRig, _rig, _poseHandler, progress);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_poseHandler != null) _poseHandler.Dispose();
                }
                finally
                {
                    if (_targetHumanoidRig != null) _targetHumanoidRig.Dispose();
                    if (_rig != null) _rig.Dispose();
                    _poseHandler = null;
                    _rig = null;
                    _targetHumanoidRig = null;
                    _targetHumanoidRootId = 0;
                }
            }
        }

        internal sealed class PreparedMotion
        {
            internal readonly VmdMotion Motion;
            internal readonly float Duration;
            internal readonly PmxHumanoidPoseSample[] Samples;

            internal PreparedMotion(VmdMotion motion, float duration, PmxHumanoidPoseSample[] samples)
            {
                Motion = motion;
                Duration = duration;
                Samples = samples;
            }
        }

        private sealed class PoseEvaluationWorker
        {
            internal readonly PmxMotionEvaluator Evaluator;
            internal readonly PmxPose Pose;

            internal PoseEvaluationWorker(PmxModel model, VmdMotion motion, PmxEvaluationOptions options)
            {
                Evaluator = new PmxMotionEvaluator(model, motion, options);
                Pose = Evaluator.CreatePose();
            }
        }

        internal static int DefaultParallelism => Math.Max(1, Environment.ProcessorCount - 1);

        private static PreparedMotion PrepareMotion(PmxModel model, VmdMotion motion,
            PmxAnimationClipBakerOptions options, int maximumParallelism,
            Action<int, int> threadSafeProgress, CancellationToken cancellationToken)
        {
            model.ValidateHierarchy();
            model.GetRestLocalPositions();
            model.GetTransformOrder();
            motion.PrepareForConcurrentSampling();

            float duration = motion.MaximumFrame / 30f;
            int sampleCount = Math.Max(1, Mathf.CeilToInt(duration * options.SampleRate) + 1);
            var samples = new PmxHumanoidPoseSample[sampleCount];
            int completed = 0;
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maximumParallelism > 0
                    ? maximumParallelism
                    : DefaultParallelism
            };

            Parallel.For(
                0,
                sampleCount,
                parallelOptions,
                () => new PoseEvaluationWorker(model, motion, options.Evaluation),
                (sample, _, worker) =>
                {
                    float time = sample == sampleCount - 1 ? duration : sample / options.SampleRate;
                    float vmdFrame = CalculateVmdFrame(
                        sample, sampleCount, options.SampleRate, motion.MaximumFrame);
                    PmxPose pose = worker.Evaluator.Evaluate(vmdFrame, worker.Pose);
                    samples[sample] = new PmxHumanoidPoseSample(pose);
                    int current = Interlocked.Increment(ref completed);
                    threadSafeProgress?.Invoke(current, sampleCount);
                    return worker;
                },
                _ => { });

            return new PreparedMotion(motion, duration, samples);
        }

        private static PmxAnimationBakeReport BakePrepared(PmxModel model, PreparedMotion prepared,
            Transform morphTargetRoot, PmxAnimationClipBakerOptions options,
            Action<float, string> progress, PmxHumanoidSourceRig rig, HumanPoseHandler poseHandler)
        {
            VmdMotion motion = prepared.Motion;
            var report = new PmxAnimationBakeReport
            {
                BoundBoneCount = rig.MappedHumanBoneCount
            };
            report.MissingBones.AddRange(rig.MissingRequiredBones);

            float duration = prepared.Duration;
            int sampleCount = prepared.Samples.Length;
            report.SampleCount = sampleCount;
            float[] sampleTimes = CreateSampleTimes(sampleCount, duration, options.SampleRate);

            var muscles = new FloatCurveAccumulator[HumanTrait.MuscleCount];
            for (int index = 0; index < muscles.Length; index++)
                muscles[index] = new FloatCurveAccumulator(sampleTimes);
            var rootTx = new FloatCurveAccumulator(sampleTimes);
            var rootTy = new FloatCurveAccumulator(sampleTimes);
            var rootTz = new FloatCurveAccumulator(sampleTimes);
            var rootQx = new FloatCurveAccumulator(sampleTimes);
            var rootQy = new FloatCurveAccumulator(sampleTimes);
            var rootQz = new FloatCurveAccumulator(sampleTimes);
            var rootQw = new FloatCurveAccumulator(sampleTimes);

            var humanPose = new HumanPose();
            Quaternion previousBodyRotation = Quaternion.identity;
            bool hasPreviousBodyRotation = false;

            for (int sample = 0; sample < sampleCount; sample++)
            {
                float vmdFrame = CalculateVmdFrame(
                    sample, sampleCount, options.SampleRate, motion.MaximumFrame);
                rig.Apply(prepared.Samples[sample], options.PositionScale);
                poseHandler.GetHumanPose(ref humanPose);

                ValidateHumanPose(humanPose, vmdFrame);
                Vector3 bodyPosition = ConvertHumanoidRootPosition(
                    humanPose.bodyPosition, options.CoordinateConversion);
                Quaternion bodyRotation = ConvertHumanoidRootRotation(
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
                    muscles[muscle].Add(humanPose.muscles[muscle]);

                if ((sample & 15) == 0 || sample + 1 == sampleCount)
                    progress?.Invoke(0.8f * (sample + 1f) / sampleCount,
                        $"Baking Humanoid frame {sample + 1}/{sampleCount}");
            }

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(motion.ModelName) ? "MMD Humanoid Motion" : motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };
            progress?.Invoke(0.81f, "Optimizing Humanoid root curves");
            var accumulators = new FloatCurveAccumulator[7 + muscles.Length];
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
                FloatCurveAccumulator accumulator = accumulators[index];
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
            {
                properties[7 + muscle] = GetAnimatorMusclePropertyName(muscle);
                progress?.Invoke(0.82f + 0.12f * (muscle + 1f) / muscles.Length,
                    $"Optimizing Humanoid curves {muscle + 1}/{muscles.Length}");
            }
            WriteAnimatorCurves(clip, properties, optimizedKeys);

            if (options.BakeMorphs && morphTargetRoot != null)
                report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                    clip, motion, morphTargetRoot);

            clip.EnsureQuaternionContinuity();
            progress?.Invoke(0.95f, "Finalizing Humanoid animation");
            report.Clip = clip;
            return report;
        }

        public static PmxAnimationBakeReport BakeToAsset(string pmxPath, string vmdPath,
            Transform morphTargetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options = null, Action<float, string> progress = null)
        {
            PmxAnimationClipBaker.ValidateAssetPath(outputAssetPath);
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report = Bake(model, motion, morphTargetRoot, options, progress);
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            progress?.Invoke(0.96f, "Writing AnimationClip asset");
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            progress?.Invoke(0.99f, "Importing AnimationClip asset");
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            progress?.Invoke(1f, "Conversion complete");
            return report;
        }

        internal static void WriteAnimatorCurves(AnimationClip clip,
            IReadOnlyList<string> properties, IReadOnlyList<Keyframe[]> keys)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (properties == null) throw new ArgumentNullException(nameof(properties));
            if (keys == null || properties.Count != keys.Count)
                throw new ArgumentException("Animator curve properties and keys must have equal lengths.");
            var bindings = new List<EditorCurveBinding>(properties.Count);
            var curves = new List<AnimationCurve>(properties.Count);
            for (int index = 0; index < properties.Count; index++)
            {
                if (string.IsNullOrEmpty(properties[index]) || keys[index] == null) continue;
                bindings.Add(EditorCurveBinding.FloatCurve(
                    string.Empty, typeof(Animator), properties[index]));
                curves.Add(new AnimationCurve(keys[index]));
            }
            if (bindings.Count > 0)
                AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
        }

        internal static string GetAnimatorMusclePropertyName(int muscleIndex)
        {
            string muscleName = HumanTrait.MuscleName[muscleIndex];
            if (string.IsNullOrEmpty(muscleName)) return muscleName;

            // HumanTrait exposes finger labels such as "Left Thumb 1 Stretched",
            // but imported Humanoid clips bind them through Animator's canonical
            // hand namespace: "LeftHand.Thumb.1 Stretched". The display label is
            // not a valid substitute for that serialized binding name.
            string hand;
            string remainder;
            if (muscleName.StartsWith("Left ", StringComparison.Ordinal))
            {
                hand = "LeftHand.";
                remainder = muscleName.Substring(5);
            }
            else if (muscleName.StartsWith("Right ", StringComparison.Ordinal))
            {
                hand = "RightHand.";
                remainder = muscleName.Substring(6);
            }
            else
            {
                return muscleName;
            }

            string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            foreach (string finger in fingers)
            {
                if (!remainder.StartsWith(finger + " ", StringComparison.Ordinal)) continue;
                string channel = remainder.Substring(finger.Length + 1);
                return hand + finger + "." + channel;
            }
            return muscleName;
        }

        internal static void ValidateHumanPose(HumanPose pose, float frame)
        {
            if (!IsFinite(pose.bodyPosition.x) || !IsFinite(pose.bodyPosition.y) ||
                !IsFinite(pose.bodyPosition.z) || !IsFinite(pose.bodyRotation.x) ||
                !IsFinite(pose.bodyRotation.y) || !IsFinite(pose.bodyRotation.z) ||
                !IsFinite(pose.bodyRotation.w))
                throw new InvalidOperationException($"Humanoid root pose became non-finite at VMD frame {frame}.");
            if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
                throw new InvalidOperationException("Unity returned an invalid Humanoid muscle pose.");
            for (int index = 0; index < pose.muscles.Length; index++)
                if (!IsFinite(pose.muscles[index]))
                    throw new InvalidOperationException(
                        $"Humanoid muscle {index} became non-finite at VMD frame {frame}.");
        }

        internal static float CalculateVmdFrame(int sampleIndex, int sampleCount,
            float sampleRate, uint maximumFrame)
        {
            if (sampleIndex == sampleCount - 1) return maximumFrame;
            if (Mathf.Abs(sampleRate - 30f) <= 1e-6f)
                return Mathf.Min(maximumFrame, sampleIndex);
            float time = sampleIndex / sampleRate;
            return Mathf.Min(maximumFrame, time * 30f);
        }

        internal static float[] CreateSampleTimes(int sampleCount, float duration, float sampleRate)
        {
            var result = new float[sampleCount];
            for (int sample = 0; sample < sampleCount; sample++)
                result[sample] = sample == sampleCount - 1 ? duration : sample / sampleRate;
            return result;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static Vector3 ConvertHumanoidRootPosition(Vector3 value, PmxCoordinateConversion target)
        {
            // The Humanoid proxy always uses a 180-degree Y source basis so semantic left/right
            // and forward match Unity. Convert its root back to the requested clip coordinate basis.
            switch (target)
            {
                case PmxCoordinateConversion.MmdToUnity:
                    return new Vector3(-value.x, value.y, value.z);
                case PmxCoordinateConversion.MmdToUnityAndRotateY180:
                    return new Vector3(value.x, value.y, -value.z);
                case PmxCoordinateConversion.None:
                    return new Vector3(-value.x, value.y, -value.z);
                default:
                    return value;
            }
        }

        internal static Quaternion ConvertHumanoidRootRotation(Quaternion value,
            PmxCoordinateConversion target)
        {
            switch (target)
            {
                case PmxCoordinateConversion.MmdToUnity:
                    return PmxAnimationClipBaker.NormalizeSafe(
                        new Quaternion(value.x, -value.y, -value.z, value.w));
                case PmxCoordinateConversion.MmdToUnityAndRotateY180:
                    return PmxAnimationClipBaker.NormalizeSafe(
                        new Quaternion(-value.x, -value.y, value.z, value.w));
                case PmxCoordinateConversion.None:
                    return PmxAnimationClipBaker.NormalizeSafe(
                        new Quaternion(-value.x, value.y, -value.z, value.w));
                default:
                    return PmxAnimationClipBaker.NormalizeSafe(value);
            }
        }

        internal sealed class FloatCurveAccumulator
        {
            private readonly float[] _times;
            private readonly float[] _values;
            private int _count;
            private float _maximumAbsoluteValue;

            internal FloatCurveAccumulator(float[] sharedTimes)
            {
                _times = sharedTimes ?? throw new ArgumentNullException(nameof(sharedTimes));
                _values = new float[sharedTimes.Length];
            }

            internal void Add(float value)
            {
                if (_count >= _values.Length)
                    throw new InvalidOperationException("Humanoid curve sample capacity was exceeded.");
                _values[_count] = value;
                _count++;
                _maximumAbsoluteValue = Mathf.Max(_maximumAbsoluteValue, Mathf.Abs(value));
            }

            internal bool IsApproximatelyZero()
            {
                return _maximumAbsoluteValue < 1e-6f;
            }

            internal Keyframe[] ToLinearKeys(float reductionError)
            {
                return PmxAnimationClipBaker.CreateLinearKeys(
                    _times, _values, _count, reductionError);
            }
        }
    }
}
