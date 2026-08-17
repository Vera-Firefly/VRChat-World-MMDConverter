using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    [Serializable]
    public sealed class PmxEvaluationOptions
    {
        public bool EnableAppendTransforms = true;
        public bool EnableIk = true;
        public bool EnableBoneMorphs = true;
        public bool ApplyFixedAxes = true;
        public bool ApplyLocalAxes;
        public bool EvaluateAfterPhysicsBones = true;
        public bool UseIncrementalWorldUpdates = true;
        public int MaximumIkIterations = 256;
        public float IkTolerance = 0.00001f;
        public VmdInterpolationOwner InterpolationOwner = VmdInterpolationOwner.DestinationKey;
    }

    public sealed class PmxMotionEvaluator
    {
        private readonly PmxModel _model;
        private readonly VmdMotion _motion;
        private readonly PmxEvaluationOptions _options;
        private readonly VmdBoneTrack[] _boneTracks;
        private readonly VmdMorphTrack[] _morphTracks;
        private readonly Vector3[] _restLocalPositions;
        private readonly int[] _transformOrder;
        private readonly float[] _morphWeights;
        private readonly bool[] _morphVisiting;
        private readonly byte[] _worldTransformStates;
        private readonly int[][] _boneChildren;
        private readonly int[] _subtreeStack;

        public PmxModel Model => _model;
        public VmdMotion Motion => _motion;

        public PmxMotionEvaluator(PmxModel model, VmdMotion motion, PmxEvaluationOptions options = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _motion = motion ?? throw new ArgumentNullException(nameof(motion));
            _options = options ?? new PmxEvaluationOptions();
            _model.ValidateHierarchy();
            _restLocalPositions = model.GetRestLocalPositions();
            _transformOrder = model.GetTransformOrder();
            _boneChildren = BuildBoneChildren(model);
            _subtreeStack = new int[model.Bones.Count];
            _boneTracks = new VmdBoneTrack[model.Bones.Count];
            for (int index = 0; index < model.Bones.Count; index++)
            {
                PmxBone bone = model.Bones[index];
                if (!motion.BoneTracks.TryGetValue(bone.NameJapanese, out _boneTracks[index]) &&
                    !string.IsNullOrEmpty(bone.NameEnglish))
                    motion.BoneTracks.TryGetValue(bone.NameEnglish, out _boneTracks[index]);
            }

            _morphTracks = new VmdMorphTrack[model.Morphs.Count];
            _morphWeights = new float[model.Morphs.Count];
            _morphVisiting = new bool[model.Morphs.Count];
            _worldTransformStates = new byte[model.Bones.Count];
            for (int index = 0; index < model.Morphs.Count; index++)
            {
                PmxMorphDefinition morph = model.Morphs[index];
                if (!motion.MorphTracks.TryGetValue(morph.NameJapanese, out _morphTracks[index]) &&
                    !string.IsNullOrEmpty(morph.NameEnglish))
                    motion.MorphTracks.TryGetValue(morph.NameEnglish, out _morphTracks[index]);
            }
        }

        public PmxPose Evaluate(float frame)
        {
            return Evaluate(frame, new PmxPose(_model.Bones.Count));
        }

        public PmxPose CreatePose()
        {
            return new PmxPose(_model.Bones.Count);
        }

        public PmxPose Evaluate(float frame, PmxPose pose)
        {
            if (pose == null) throw new ArgumentNullException(nameof(pose));
            if (pose.BoneCount != _model.Bones.Count)
                throw new ArgumentException("The reusable pose belongs to a different PMX model.", nameof(pose));
            InitializeMotion(frame, pose);
            if (_options.EnableBoneMorphs) ApplyBoneMorphs(frame, pose);
            RecomputeWorldTransforms(pose);

            for (int orderIndex = 0; orderIndex < _transformOrder.Length; orderIndex++)
            {
                int boneIndex = _transformOrder[orderIndex];
                PmxBone bone = _model.Bones[boneIndex];
                if (!_options.EvaluateAfterPhysicsBones && bone.HasFlag(PmxBoneFlags.AfterPhysics)) continue;

                if (_options.EnableAppendTransforms)
                {
                    if (ApplyAppendTransform(boneIndex, pose))
                        RecomputeChangedWorldTransforms(boneIndex, pose);
                }

                if (_options.EnableIk && bone.Ik != null && IsIkEnabled(bone, frame))
                    SolveIk(boneIndex, pose);
            }

            RecomputeWorldTransforms(pose);
            return pose;
        }

        private void InitializeMotion(float frame, PmxPose pose)
        {
            for (int index = 0; index < _model.Bones.Count; index++)
            {
                Vector3 translation = Vector3.zero;
                Quaternion rotation = Quaternion.identity;
                if (_boneTracks[index] != null)
                {
                    VmdTrackSampler.SampleBone(_boneTracks[index], frame,
                        out translation, out rotation, _options.InterpolationOwner);
                }

                PmxBone bone = _model.Bones[index];
                if (_options.ApplyLocalAxes && bone.HasFlag(PmxBoneFlags.LocalAxes))
                {
                    Quaternion basis = Quaternion.LookRotation(bone.LocalAxisZ, bone.LocalAxisY);
                    rotation = basis * rotation * Quaternion.Inverse(basis);
                    translation = basis * translation;
                }

                if (_options.ApplyFixedAxes && bone.HasFlag(PmxBoneFlags.FixedAxis))
                    rotation = ProjectToAxis(rotation, bone.FixedAxis);

                pose.MotionTranslations[index] = translation;
                pose.MotionRotations[index] = NormalizeSafe(rotation);
                pose.LocalPositions[index] = _restLocalPositions[index] + translation;
                pose.LocalRotations[index] = pose.MotionRotations[index];
            }
        }

        private void ApplyBoneMorphs(float frame, PmxPose pose)
        {
            Array.Clear(_morphWeights, 0, _morphWeights.Length);
            Array.Clear(_morphVisiting, 0, _morphVisiting.Length);
            for (int index = 0; index < _morphTracks.Length; index++)
            {
                if (_morphTracks[index] != null)
                {
                    float directWeight = VmdTrackSampler.SampleMorph(_morphTracks[index], frame);
                    _morphWeights[index] += directWeight;
                    if (Mathf.Abs(directWeight) > 1e-7f)
                        PropagateGroupMorph(index, directWeight, _morphVisiting, 0);
                }
            }

            for (int morphIndex = 0; morphIndex < _model.Morphs.Count; morphIndex++)
            {
                float weight = _morphWeights[morphIndex];
                if (Mathf.Abs(weight) <= 1e-7f) continue;
                PmxMorphDefinition morph = _model.Morphs[morphIndex];
                for (int offsetIndex = 0; offsetIndex < morph.BoneOffsets.Count; offsetIndex++)
                {
                    PmxBoneMorphOffset offset = morph.BoneOffsets[offsetIndex];
                    if (offset.BoneIndex < 0 || offset.BoneIndex >= pose.BoneCount) continue;
                    pose.MotionTranslations[offset.BoneIndex] += offset.Translation * weight;
                    pose.MotionRotations[offset.BoneIndex] = NormalizeSafe(
                        Quaternion.SlerpUnclamped(Quaternion.identity, offset.Rotation, weight) *
                        pose.MotionRotations[offset.BoneIndex]);
                    pose.LocalPositions[offset.BoneIndex] =
                        _restLocalPositions[offset.BoneIndex] + pose.MotionTranslations[offset.BoneIndex];
                    pose.LocalRotations[offset.BoneIndex] = pose.MotionRotations[offset.BoneIndex];
                }
            }
        }

        private void PropagateGroupMorph(int morphIndex, float sourceWeight, bool[] visiting, int depth)
        {
            if (morphIndex < 0 || morphIndex >= _model.Morphs.Count || depth > 32 || visiting[morphIndex]) return;
            visiting[morphIndex] = true;
            PmxMorphDefinition morph = _model.Morphs[morphIndex];
            for (int index = 0; index < morph.GroupOffsets.Count; index++)
            {
                PmxGroupMorphOffset child = morph.GroupOffsets[index];
                if (child.MorphIndex < 0 || child.MorphIndex >= _morphWeights.Length) continue;
                float contribution = sourceWeight * child.Weight;
                _morphWeights[child.MorphIndex] += contribution;
                PropagateGroupMorph(child.MorphIndex, contribution, visiting, depth + 1);
            }
            visiting[morphIndex] = false;
        }

        private bool ApplyAppendTransform(int boneIndex, PmxPose pose)
        {
            PmxBone bone = _model.Bones[boneIndex];
            int sourceIndex = bone.InheritParentIndex;
            if (sourceIndex < 0 || sourceIndex >= _model.Bones.Count || Mathf.Abs(bone.InheritWeight) < 1e-7f)
                return false;

            bool local = bone.HasFlag(PmxBoneFlags.LocalAppend);
            Quaternion inheritedRotation;
            Vector3 inheritedTranslation;
            if (local)
            {
                inheritedRotation = pose.LocalRotations[sourceIndex];
                inheritedTranslation = pose.LocalPositions[sourceIndex] - _restLocalPositions[sourceIndex];
            }
            else
            {
                Quaternion parentWorld = bone.ParentIndex >= 0
                    ? pose.WorldRotations[bone.ParentIndex]
                    : Quaternion.identity;
                // PMX grants inherit the source bone's deformation rotation, not
                // its absolute world orientation. Using a world quaternion here
                // also imports the source parent chain and breaks cancellation
                // helpers such as shoulder P/C and the arm-twist grants.
                inheritedRotation = pose.LocalRotations[sourceIndex];
                Vector3 worldDisplacement = pose.WorldPositions[sourceIndex] - _model.Bones[sourceIndex].Position;
                inheritedTranslation = Quaternion.Inverse(parentWorld) * worldDisplacement;
            }

            bool applied = false;
            if (bone.HasFlag(PmxBoneFlags.InheritTranslation))
            {
                pose.LocalPositions[boneIndex] += inheritedTranslation * bone.InheritWeight;
                applied = true;
            }
            if (bone.HasFlag(PmxBoneFlags.InheritRotation))
            {
                Quaternion grant = Quaternion.SlerpUnclamped(
                    Quaternion.identity, inheritedRotation, bone.InheritWeight);
                pose.LocalRotations[boneIndex] = NormalizeSafe(grant * pose.LocalRotations[boneIndex]);
                if (_options.ApplyFixedAxes && bone.HasFlag(PmxBoneFlags.FixedAxis))
                    pose.LocalRotations[boneIndex] = ProjectToAxis(pose.LocalRotations[boneIndex], bone.FixedAxis);
                applied = true;
            }
            return applied;
        }

        private bool IsIkEnabled(PmxBone bone, float frame)
        {
            if (_motion.TryGetIkState(bone.NameJapanese, frame, out bool enabled)) return enabled;
            if (!string.IsNullOrEmpty(bone.NameEnglish) &&
                _motion.TryGetIkState(bone.NameEnglish, frame, out enabled)) return enabled;
            return true;
        }

        private void SolveIk(int ikBoneIndex, PmxPose pose)
        {
            PmxIkDefinition ik = _model.Bones[ikBoneIndex].Ik;
            if (ik == null || ik.TargetBoneIndex < 0 || ik.TargetBoneIndex >= pose.BoneCount) return;
            int iterations = Mathf.Clamp(ik.IterationCount, 0, Math.Max(0, _options.MaximumIkIterations));
            float maximumAngle = Mathf.Clamp(Mathf.Abs(ik.AngleLimit), 0f, Mathf.PI);
            float toleranceSquared = Mathf.Max(0f, _options.IkTolerance);
            toleranceSquared *= toleranceSquared;

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                bool changed = false;
                for (int linkIndex = 0; linkIndex < ik.Links.Count; linkIndex++)
                {
                    PmxIkLink link = ik.Links[linkIndex];
                    int boneIndex = link.BoneIndex;
                    if (boneIndex < 0 || boneIndex >= pose.BoneCount || boneIndex == ikBoneIndex) continue;

                    Vector3 effectorVector = pose.WorldPositions[ik.TargetBoneIndex] - pose.WorldPositions[boneIndex];
                    Vector3 goalVector = pose.WorldPositions[ikBoneIndex] - pose.WorldPositions[boneIndex];
                    if (effectorVector.sqrMagnitude < 1e-14f || goalVector.sqrMagnitude < 1e-14f) continue;

                    Quaternion inverseWorld = Quaternion.Inverse(pose.WorldRotations[boneIndex]);
                    Vector3 effectorLocal = (inverseWorld * effectorVector).normalized;
                    Vector3 goalLocal = (inverseWorld * goalVector).normalized;
                    float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(effectorLocal, goalLocal), -1f, 1f));
                    if (angle < 1e-7f) continue;
                    angle = Mathf.Min(angle, maximumAngle);
                    Vector3 axis = Vector3.Cross(effectorLocal, goalLocal);
                    if (axis.sqrMagnitude < 1e-12f) continue;

                    Quaternion delta = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, axis.normalized);
                    Quaternion candidate = NormalizeSafe(pose.LocalRotations[boneIndex] * delta);
                    if (link.HasLimits) candidate = ClampIkRotation(candidate, link.MinimumAngle, link.MaximumAngle);
                    PmxBone linkBone = _model.Bones[boneIndex];
                    if (_options.ApplyFixedAxes && linkBone.HasFlag(PmxBoneFlags.FixedAxis))
                        candidate = ProjectToAxis(candidate, linkBone.FixedAxis);
                    pose.LocalRotations[boneIndex] = candidate;
                    RecomputeChangedWorldTransforms(boneIndex, pose);
                    changed = true;

                    if ((pose.WorldPositions[ik.TargetBoneIndex] - pose.WorldPositions[ikBoneIndex]).sqrMagnitude <=
                        toleranceSquared)
                        return;
                }
                if (!changed ||
                    (pose.WorldPositions[ik.TargetBoneIndex] - pose.WorldPositions[ikBoneIndex]).sqrMagnitude <=
                    toleranceSquared)
                    break;
            }
        }

        private void RecomputeWorldTransforms(PmxPose pose)
        {
            Array.Clear(_worldTransformStates, 0, _worldTransformStates.Length);
            for (int index = 0; index < pose.BoneCount; index++)
                ComputeWorldTransform(index, pose, _worldTransformStates);
        }

        private void RecomputeWorldSubtree(int rootBoneIndex, PmxPose pose)
        {
            int stackCount = 0;
            _subtreeStack[stackCount++] = rootBoneIndex;
            while (stackCount > 0)
            {
                int boneIndex = _subtreeStack[--stackCount];
                int parent = _model.Bones[boneIndex].ParentIndex;
                if (parent >= 0)
                {
                    pose.WorldRotations[boneIndex] = NormalizeSafe(
                        pose.WorldRotations[parent] * pose.LocalRotations[boneIndex]);
                    pose.WorldPositions[boneIndex] = pose.WorldPositions[parent] +
                                                     pose.WorldRotations[parent] * pose.LocalPositions[boneIndex];
                }
                else
                {
                    pose.WorldRotations[boneIndex] = NormalizeSafe(pose.LocalRotations[boneIndex]);
                    pose.WorldPositions[boneIndex] = pose.LocalPositions[boneIndex];
                }

                int[] children = _boneChildren[boneIndex];
                for (int childIndex = 0; childIndex < children.Length; childIndex++)
                    _subtreeStack[stackCount++] = children[childIndex];
            }
        }

        private void RecomputeChangedWorldTransforms(int rootBoneIndex, PmxPose pose)
        {
            if (_options.UseIncrementalWorldUpdates)
                RecomputeWorldSubtree(rootBoneIndex, pose);
            else
                RecomputeWorldTransforms(pose);
        }

        private static int[][] BuildBoneChildren(PmxModel model)
        {
            int boneCount = model.Bones.Count;
            var children = new List<int>[boneCount];
            for (int index = 0; index < boneCount; index++)
                children[index] = new List<int>();
            for (int index = 0; index < boneCount; index++)
            {
                int parent = model.Bones[index].ParentIndex;
                if (parent >= 0) children[parent].Add(index);
            }

            var result = new int[boneCount][];
            for (int index = 0; index < boneCount; index++)
                result[index] = children[index].ToArray();
            return result;
        }

        private void ComputeWorldTransform(int boneIndex, PmxPose pose, byte[] states)
        {
            if (states[boneIndex] == 2) return;
            if (states[boneIndex] == 1)
                throw new InvalidOperationException("The PMX bone hierarchy contains a cycle.");
            states[boneIndex] = 1;
            int parent = _model.Bones[boneIndex].ParentIndex;
            if (parent >= 0)
            {
                ComputeWorldTransform(parent, pose, states);
                pose.WorldRotations[boneIndex] = NormalizeSafe(
                    pose.WorldRotations[parent] * pose.LocalRotations[boneIndex]);
                pose.WorldPositions[boneIndex] = pose.WorldPositions[parent] +
                                                 pose.WorldRotations[parent] * pose.LocalPositions[boneIndex];
            }
            else
            {
                pose.WorldRotations[boneIndex] = NormalizeSafe(pose.LocalRotations[boneIndex]);
                pose.WorldPositions[boneIndex] = pose.LocalPositions[boneIndex];
            }
            states[boneIndex] = 2;
        }

        private static Quaternion ProjectToAxis(Quaternion rotation, Vector3 axis)
        {
            if (axis.sqrMagnitude < 1e-12f) return Quaternion.identity;
            rotation = NormalizeSafe(rotation);
            rotation.ToAngleAxis(out float angle, out Vector3 rotationAxis);
            if (float.IsNaN(angle) || rotationAxis.sqrMagnitude < 1e-12f) return Quaternion.identity;
            float sign = Mathf.Sign(Vector3.Dot(rotationAxis, axis));
            return Quaternion.AngleAxis(angle * sign, axis.normalized);
        }

        private static Quaternion ClampIkRotation(Quaternion rotation, Vector3 minimum, Vector3 maximum)
        {
            Vector3 min = Vector3.Min(minimum, maximum) * Mathf.Rad2Deg;
            Vector3 max = Vector3.Max(minimum, maximum) * Mathf.Rad2Deg;

            bool xOnly = Mathf.Abs(min.y) < 1e-5f && Mathf.Abs(max.y) < 1e-5f &&
                         Mathf.Abs(min.z) < 1e-5f && Mathf.Abs(max.z) < 1e-5f;
            if (xOnly)
            {
                float x = Mathf.Atan2(2f * (rotation.w * rotation.x + rotation.y * rotation.z),
                    1f - 2f * (rotation.x * rotation.x + rotation.y * rotation.y)) * Mathf.Rad2Deg;
                return Quaternion.AngleAxis(Mathf.Clamp(NormalizeDegrees(x), min.x, max.x), Vector3.right);
            }

            Vector3 euler = rotation.eulerAngles;
            euler.x = Mathf.Clamp(NormalizeDegrees(euler.x), min.x, max.x);
            euler.y = Mathf.Clamp(NormalizeDegrees(euler.y), min.y, max.y);
            euler.z = Mathf.Clamp(NormalizeDegrees(euler.z), min.z, max.z);
            return NormalizeSafe(Quaternion.Euler(euler));
        }

        private static float NormalizeDegrees(float value)
        {
            value %= 360f;
            if (value > 180f) value -= 360f;
            if (value < -180f) value += 360f;
            return value;
        }

        private static Quaternion NormalizeSafe(Quaternion value)
        {
            float length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (length < 1e-8f) return Quaternion.identity;
            float inverse = 1f / length;
            return new Quaternion(value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
        }
    }
}
