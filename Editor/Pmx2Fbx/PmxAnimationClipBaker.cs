using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    internal static class PmxAnimationClipBaker
    {
        internal static PmxAnimationBakeReport Debug_Bake(PmxModel model, VmdMotion motion, Transform root,
            PmxAnimationClipBakerOptions options = null, Action<float, string> progress = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            if (root == null) throw new ArgumentNullException(nameof(root));
            options = options ?? new PmxAnimationClipBakerOptions();
            if (options.SampleRate <= 0f || options.SampleRate > 240f)
                throw new ArgumentOutOfRangeException(nameof(options.SampleRate));

            var report = new PmxAnimationBakeReport();
            Transform[] bindings = ResolveBoneTransforms(model, root, report.MissingBones);
            var curves = new BoneCurves[model.Bones.Count];
            for (int index = 0; index < curves.Length; index++)
            {
                if (bindings[index] == null) continue;
                curves[index] = new BoneCurves
                {
                    Path = AnimationUtility.CalculateTransformPath(bindings[index], root),
                    RestPosition = bindings[index].localPosition,
                    RestRotation = bindings[index].localRotation
                };
                report.BoundBoneCount++;
            }

            if (report.BoundBoneCount == 0)
                throw new InvalidOperationException(
                    $"No PMX bones were found below '{root.name}'. " +
                    "Generic output requires a hierarchy whose transform names match the PMX bones. " +
                    "Use Humanoid output to build the source skeleton from the PMX automatically.");

            float duration = motion.MaximumFrame / 30f;
            int sampleCount = Math.Max(1, Mathf.CeilToInt(duration * options.SampleRate) + 1);
            report.SampleCount = sampleCount;
            var evaluator = new PmxMotionEvaluator(model, motion, options.Evaluation);
            PmxPose reusablePose = evaluator.CreatePose();
            Vector3[] restLocal = model.GetRestLocalPositions();
            Quaternion[] previousRotations = new Quaternion[model.Bones.Count];
            bool[] hasPreviousRotation = new bool[model.Bones.Count];

            for (int sample = 0; sample < sampleCount; sample++)
            {
                float time = sample == sampleCount - 1 ? duration : sample / options.SampleRate;
                float vmdFrame = Mathf.Min(motion.MaximumFrame, time * 30f);
                PmxPose pose = evaluator.Evaluate(vmdFrame, reusablePose);

                for (int boneIndex = 0; boneIndex < curves.Length; boneIndex++)
                {
                    BoneCurves target = curves[boneIndex];
                    if (target == null) continue;
                    Vector3 displacement = pose.LocalPositions[boneIndex] - restLocal[boneIndex];
                    displacement = ConvertPosition(displacement, options) * options.PositionScale;
                    Vector3 position = target.RestPosition + displacement;
                    Quaternion rotation = target.RestRotation * ConvertRotation(pose.LocalRotations[boneIndex], options);
                    rotation = NormalizeSafe(rotation);
                    if (hasPreviousRotation[boneIndex] && Quaternion.Dot(previousRotations[boneIndex], rotation) < 0f)
                        rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    previousRotations[boneIndex] = rotation;
                    hasPreviousRotation[boneIndex] = true;
                    target.Add(time, position, rotation);
                }

                progress?.Invoke((sample + 1f) / sampleCount, $"Baking frame {sample + 1}/{sampleCount}");
            }

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(motion.ModelName) ? "MMD Motion" : motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };
            for (int index = 0; index < curves.Length; index++)
            {
                if (curves[index] != null) curves[index].WriteTo(clip, options.CurveReductionError);
            }
            if (options.BakeMorphs)
                report.BoundMorphCount = BakeMorphCurves(clip, motion, root);

            clip.EnsureQuaternionContinuity();
            report.Clip = clip;
            return report;
        }

        internal static PmxAnimationBakeReport Debug_BakeToAsset(string pmxPath, string vmdPath, Transform root,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            ValidateAssetPath(outputAssetPath);
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report = Debug_Bake(model, motion, root, options, progress);
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            return report;
        }

        private static Transform[] ResolveBoneTransforms(PmxModel model, Transform root, List<string> missing)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var byName = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
            foreach (Transform transform in all)
            {
                if (!byName.TryGetValue(transform.name, out List<Transform> list))
                {
                    list = new List<Transform>();
                    byName.Add(transform.name, list);
                }
                list.Add(transform);
            }

            var result = new Transform[model.Bones.Count];
            var used = new HashSet<Transform>();
            for (int index = 0; index < model.Bones.Count; index++)
            {
                PmxBone bone = model.Bones[index];
                Transform parent = bone.ParentIndex >= 0 ? result[bone.ParentIndex] : null;
                Transform resolved = FindCandidate(byName, bone.NameJapanese, parent, used) ??
                                     FindCandidate(byName, bone.NameEnglish, parent, used);
                if (resolved == null)
                {
                    string displayName = string.IsNullOrEmpty(bone.NameJapanese) ? bone.NameEnglish : bone.NameJapanese;
                    missing.Add(displayName);
                    continue;
                }
                result[index] = resolved;
                used.Add(resolved);
            }
            return result;
        }

        private static Transform FindCandidate(Dictionary<string, List<Transform>> byName, string name,
            Transform expectedParent, HashSet<Transform> used)
        {
            if (string.IsNullOrEmpty(name) || !byName.TryGetValue(name, out List<Transform> candidates)) return null;
            Transform fallback = null;
            foreach (Transform candidate in candidates)
            {
                if (used.Contains(candidate)) continue;
                if (fallback == null) fallback = candidate;
                if (expectedParent == null || IsDescendantOf(candidate, expectedParent)) return candidate;
            }
            return fallback;
        }

        private static bool IsDescendantOf(Transform transform, Transform parent)
        {
            for (Transform current = transform.parent; current != null; current = current.parent)
                if (current == parent) return true;
            return false;
        }

        internal static int BakeMorphCurves(AnimationClip clip, VmdMotion motion, Transform root)
        {
            int count = 0;
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, root);
                for (int shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
                {
                    string name = mesh.GetBlendShapeName(shapeIndex);
                    if (!motion.MorphTracks.TryGetValue(name, out VmdMorphTrack track)) continue;
                    var keys = new Keyframe[track.Keys.Count];
                    for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
                    {
                        VmdMorphKeyframe source = track.Keys[keyIndex];
                        keys[keyIndex] = new Keyframe(source.Frame / 30f, source.Weight * 100f);
                    }
                    AnimationCurve curve = CreateLinearCurve(keys);
                    AnimationUtility.SetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name),
                        curve);
                    count++;
                }
            }
            return count;
        }

        internal static Vector3 ConvertPosition(Vector3 value, PmxAnimationClipBakerOptions options)
        {
            switch (options.CoordinateConversion)
            {
                case PmxCoordinateConversion.MmdToUnity:
                    return new Vector3(value.x, value.y, -value.z);
                case PmxCoordinateConversion.MmdToUnityAndRotateY180:
                    return new Vector3(-value.x, value.y, value.z);
                case PmxCoordinateConversion.RotateY180:
                    return new Vector3(-value.x, value.y, -value.z);
                default:
                    return value;
            }
        }

        internal static Quaternion ConvertRotation(Quaternion value, PmxAnimationClipBakerOptions options)
        {
            switch (options.CoordinateConversion)
            {
                case PmxCoordinateConversion.MmdToUnity:
                    return NormalizeSafe(new Quaternion(-value.x, -value.y, value.z, value.w));
                case PmxCoordinateConversion.MmdToUnityAndRotateY180:
                    return NormalizeSafe(new Quaternion(value.x, -value.y, -value.z, value.w));
                case PmxCoordinateConversion.RotateY180:
                    return NormalizeSafe(new Quaternion(-value.x, value.y, -value.z, value.w));
                default:
                    return NormalizeSafe(value);
            }
        }

        internal static string ToPhysicalPath(string assetOrPhysicalPath)
        {
            if (string.IsNullOrEmpty(assetOrPhysicalPath)) throw new ArgumentNullException(nameof(assetOrPhysicalPath));
            if (Path.IsPathRooted(assetOrPhysicalPath)) return assetOrPhysicalPath;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetOrPhysicalPath));
        }

        internal static void ValidateAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output must be an .anim path inside Assets.", nameof(path));
        }

        internal static Quaternion NormalizeSafe(Quaternion value)
        {
            float length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (length < 1e-8f) return Quaternion.identity;
            float inverse = 1f / length;
            return new Quaternion(value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
        }

        internal static AnimationCurve CreateLinearCurve(Keyframe[] keys, float reductionError = 0f)
        {
            return new AnimationCurve(CreateLinearKeys(keys, reductionError));
        }

        internal static Keyframe[] CreateLinearKeys(Keyframe[] keys, float reductionError = 0f)
        {
            if (keys == null || keys.Length == 0) return Array.Empty<Keyframe>();
            keys = ReduceLinearKeys(keys, reductionError);
            for (int index = 0; index < keys.Length; index++)
            {
                float inTangent = index > 0
                    ? CalculateSlope(keys[index - 1], keys[index])
                    : keys.Length > 1 ? CalculateSlope(keys[0], keys[1]) : 0f;
                float outTangent = index + 1 < keys.Length
                    ? CalculateSlope(keys[index], keys[index + 1])
                    : keys.Length > 1 ? CalculateSlope(keys[keys.Length - 2], keys[keys.Length - 1]) : 0f;
                Keyframe key = keys[index];
                key.inTangent = inTangent;
                key.outTangent = outTangent;
                key.inWeight = 0f;
                key.outWeight = 0f;
                key.weightedMode = WeightedMode.None;
                keys[index] = key;
            }
            return keys;
        }

        internal static Keyframe[] CreateLinearKeys(
            float[] times, float[] values, int count, float reductionError = 0f)
        {
            if (times == null) throw new ArgumentNullException(nameof(times));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (count < 0 || count > times.Length || count > values.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return Array.Empty<Keyframe>();

            bool[] keep = null;
            int keptCount = count;
            if (count > 2 && reductionError > 0f)
            {
                keep = new bool[count];
                keep[0] = true;
                keep[count - 1] = true;
                var ranges = new Stack<int>();
                ranges.Push(0);
                ranges.Push(count - 1);
                while (ranges.Count > 0)
                {
                    int end = ranges.Pop();
                    int start = ranges.Pop();
                    float duration = times[end] - times[start];
                    float largestError = -1f;
                    int split = -1;
                    for (int index = start + 1; index < end; index++)
                    {
                        float factor = Mathf.Abs(duration) > 1e-12f
                            ? (times[index] - times[start]) / duration
                            : 0f;
                        float expected = Mathf.LerpUnclamped(
                            values[start], values[end], factor);
                        float error = Mathf.Abs(values[index] - expected);
                        if (error <= largestError) continue;
                        largestError = error;
                        split = index;
                    }
                    if (split < 0 || largestError <= reductionError) continue;
                    keep[split] = true;
                    ranges.Push(start);
                    ranges.Push(split);
                    ranges.Push(split);
                    ranges.Push(end);
                }

                keptCount = 0;
                for (int index = 0; index < count; index++)
                    if (keep[index]) keptCount++;
            }

            var keys = new Keyframe[keptCount];
            int destination = 0;
            for (int source = 0; source < count; source++)
            {
                if (keep != null && !keep[source]) continue;
                keys[destination++] = new Keyframe(times[source], values[source]);
            }
            for (int index = 0; index < keys.Length; index++)
            {
                float inTangent = index > 0
                    ? CalculateSlope(keys[index - 1], keys[index])
                    : keys.Length > 1 ? CalculateSlope(keys[0], keys[1]) : 0f;
                float outTangent = index + 1 < keys.Length
                    ? CalculateSlope(keys[index], keys[index + 1])
                    : keys.Length > 1
                        ? CalculateSlope(keys[keys.Length - 2], keys[keys.Length - 1])
                        : 0f;
                Keyframe key = keys[index];
                key.inTangent = inTangent;
                key.outTangent = outTangent;
                key.inWeight = 0f;
                key.outWeight = 0f;
                key.weightedMode = WeightedMode.None;
                keys[index] = key;
            }
            return keys;
        }

        internal static Keyframe[] ReduceLinearKeys(Keyframe[] keys, float maximumError)
        {
            if (keys == null || keys.Length <= 2 || maximumError <= 0f) return keys;
            var keep = new bool[keys.Length];
            keep[0] = true;
            keep[keys.Length - 1] = true;
            var ranges = new Stack<int>();
            ranges.Push(0);
            ranges.Push(keys.Length - 1);

            while (ranges.Count > 0)
            {
                int end = ranges.Pop();
                int start = ranges.Pop();
                float duration = keys[end].time - keys[start].time;
                float largestError = -1f;
                int split = -1;
                for (int index = start + 1; index < end; index++)
                {
                    float factor = Mathf.Abs(duration) > 1e-12f
                        ? (keys[index].time - keys[start].time) / duration
                        : 0f;
                    float expected = Mathf.LerpUnclamped(keys[start].value, keys[end].value, factor);
                    float error = Mathf.Abs(keys[index].value - expected);
                    if (error > largestError)
                    {
                        largestError = error;
                        split = index;
                    }
                }

                if (split < 0 || largestError <= maximumError) continue;
                keep[split] = true;
                ranges.Push(start);
                ranges.Push(split);
                ranges.Push(split);
                ranges.Push(end);
            }

            var reduced = new List<Keyframe>();
            for (int index = 0; index < keys.Length; index++)
                if (keep[index]) reduced.Add(keys[index]);
            return reduced.ToArray();
        }

        private static float CalculateSlope(Keyframe left, Keyframe right)
        {
            float duration = right.time - left.time;
            return Mathf.Abs(duration) > 1e-12f ? (right.value - left.value) / duration : 0f;
        }

        private sealed class BoneCurves
        {
            internal string Path;
            internal Vector3 RestPosition;
            internal Quaternion RestRotation;
            private readonly List<Keyframe> _positionX = new List<Keyframe>();
            private readonly List<Keyframe> _positionY = new List<Keyframe>();
            private readonly List<Keyframe> _positionZ = new List<Keyframe>();
            private readonly List<Keyframe> _rotationX = new List<Keyframe>();
            private readonly List<Keyframe> _rotationY = new List<Keyframe>();
            private readonly List<Keyframe> _rotationZ = new List<Keyframe>();
            private readonly List<Keyframe> _rotationW = new List<Keyframe>();

            internal void Add(float time, Vector3 position, Quaternion rotation)
            {
                _positionX.Add(new Keyframe(time, position.x));
                _positionY.Add(new Keyframe(time, position.y));
                _positionZ.Add(new Keyframe(time, position.z));
                _rotationX.Add(new Keyframe(time, rotation.x));
                _rotationY.Add(new Keyframe(time, rotation.y));
                _rotationZ.Add(new Keyframe(time, rotation.z));
                _rotationW.Add(new Keyframe(time, rotation.w));
            }

            internal void WriteTo(AnimationClip clip, float reductionError)
            {
                Write(clip, "m_LocalPosition.x", _positionX, reductionError);
                Write(clip, "m_LocalPosition.y", _positionY, reductionError);
                Write(clip, "m_LocalPosition.z", _positionZ, reductionError);
                Write(clip, "m_LocalRotation.x", _rotationX, reductionError);
                Write(clip, "m_LocalRotation.y", _rotationY, reductionError);
                Write(clip, "m_LocalRotation.z", _rotationZ, reductionError);
                Write(clip, "m_LocalRotation.w", _rotationW, reductionError);
            }

            private void Write(AnimationClip clip, string property, List<Keyframe> keys, float reductionError)
            {
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(Path, typeof(Transform), property),
                    CreateLinearCurve(keys.ToArray(), reductionError));
            }
        }
    }
}
