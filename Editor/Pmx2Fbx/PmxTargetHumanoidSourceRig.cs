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
    internal sealed class PmxTargetHumanoidSourceRig : IDisposable
    {
        private List<PmxTargetFkAnimationClipBaker.TargetBinding> _bindings;
        private Transform[] _cloneTargets;
        private Transform[] _cloneByHumanBone;
        private PmxAnimationClipBakerOptions _sourceOptions;
        private HumanPoseHandler _poseHandler;
        private GameObject _cloneRoot;
        private int _rootMotionSourceBoneIndex = -1;
        private Vector3 _rootMotionSourceRestPosition;
        private float _rootMotionTargetScale = 1f;
        internal int BindingCount => _bindings != null ? _bindings.Count : 0;

        private PmxTargetHumanoidSourceRig()
        {
        }

        private void Build(PmxModel model, GameObject targetRoot,
            PmxAnimationClipBakerOptions options)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            Animator animator = targetRoot != null
                ? targetRoot.GetComponent<Animator>() ?? targetRoot.GetComponentInChildren<Animator>(true)
                : null;
            if (animator == null || animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman)
                throw new InvalidOperationException(
                    "Target-calibrated Humanoid output requires a valid Humanoid Animator.");

            _bindings = PmxTargetFkAnimationClipBaker.BuildBindings(
                model, animator.transform, animator);
            if (!_bindings.Any(binding => binding.HumanBone == HumanBodyBones.Hips))
                throw new InvalidOperationException(
                    "The PMX and target Avatar must both provide a Hips/lower-body mapping.");

            Dictionary<Transform, Transform> cloneMap = CloneHierarchy(
                animator, out _cloneRoot);
            _cloneTargets = new Transform[_bindings.Count];
            _cloneByHumanBone = new Transform[(int)HumanBodyBones.LastBone];
            for (int index = 0; index < _bindings.Count; index++)
            {
                PmxTargetFkAnimationClipBaker.TargetBinding binding = _bindings[index];
                if (!cloneMap.TryGetValue(binding.Target, out Transform clone))
                    throw new InvalidOperationException(
                        "Could not reproduce the target Avatar skeleton path: " + binding.Path);
                _cloneTargets[index] = clone;
                int humanBoneIndex = (int)binding.HumanBone;
                if (humanBoneIndex >= 0 && humanBoneIndex < _cloneByHumanBone.Length)
                    _cloneByHumanBone[humanBoneIndex] = clone;
                binding.RootToParentVector = clone.parent != null
                    ? clone.parent.worldToLocalMatrix * _cloneRoot.transform.localToWorldMatrix
                    : Matrix4x4.identity;
                if (binding.HumanBone == HumanBodyBones.Hips)
                {
                    _rootMotionSourceBoneIndex = PmxTargetFkAnimationClipBaker.FindRootMotionSourceBone(
                        model, binding.SourceBoneIndex);
                    _rootMotionSourceRestPosition =
                        model.Bones[_rootMotionSourceBoneIndex].Position;
                    _rootMotionTargetScale = PmxTargetFkAnimationClipBaker.CalculateRootMotionTargetScale(
                        _rootMotionSourceRestPosition,
                        options.PositionScale,
                        clone,
                        _cloneRoot.transform);
                }
            }
            _sourceOptions = new PmxAnimationClipBakerOptions
            {
                PositionScale = options.PositionScale,
                CoordinateConversion = options.CoordinateConversion
            };
            _poseHandler = new HumanPoseHandler(animator.avatar, _cloneRoot.transform);
        }

        internal static PmxTargetHumanoidSourceRig Create(PmxModel model, GameObject targetRoot,
            PmxAnimationClipBakerOptions options)
        {
            var rig = new PmxTargetHumanoidSourceRig();
            try
            {
                rig.Build(model, targetRoot, options ?? new PmxAnimationClipBakerOptions());
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        internal void Apply(PmxHumanoidPoseSample sample, bool applyUpperLimbInverseSolve)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (_poseHandler == null) throw new ObjectDisposedException(nameof(PmxTargetHumanoidSourceRig));
            for (int index = 0; index < _bindings.Count; index++)
            {
                PmxTargetFkAnimationClipBaker.TargetBinding binding = _bindings[index];
                // Target upper-limb reconstruction is an opt-in debug path. When it is
                // disabled, do not even apply upper-limb FK to the target clone; the
                // target-calibrated baker takes those muscles exclusively from the
                // canonical Humanoid solution.
                if (!applyUpperLimbInverseSolve && IsUpperLimb(binding.HumanBone)) continue;
                Transform target = _cloneTargets[index];
                bool suppressMmdWaistCancellation =
                    binding.HumanBone == HumanBodyBones.LeftUpperLeg ||
                    binding.HumanBone == HumanBodyBones.RightUpperLeg;
                target.localRotation = PmxTargetFkAnimationClipBaker.EvaluateLocalRotation(
                    sample, _bindings, index, _sourceOptions, suppressMmdWaistCancellation);
                if (binding.HumanBone == HumanBodyBones.Hips)
                    target.localPosition = PmxTargetFkAnimationClipBaker.EvaluateRootLocalPosition(
                        sample, binding, _rootMotionSourceBoneIndex,
                        _rootMotionSourceRestPosition, _sourceOptions, _rootMotionTargetScale);
            }
        }

        internal static bool IsUpperLimb(HumanBodyBones bone)
        {
            return IsArmChain(bone) ||
                   bone >= HumanBodyBones.LeftThumbProximal &&
                   bone <= HumanBodyBones.RightLittleDistal;
        }

        internal static bool IsArmChain(HumanBodyBones bone)
        {
            return bone >= HumanBodyBones.LeftShoulder && bone <= HumanBodyBones.RightHand;
        }

        internal void GetHumanPose(ref HumanPose pose)
        {
            if (_poseHandler == null) throw new ObjectDisposedException(nameof(PmxTargetHumanoidSourceRig));
            _poseHandler.GetHumanPose(ref pose);
        }

        internal void SetHumanPose(ref HumanPose pose)
        {
            if (_poseHandler == null) throw new ObjectDisposedException(nameof(PmxTargetHumanoidSourceRig));
            _poseHandler.SetHumanPose(ref pose);
        }

        internal void RoundTripHumanPose(ref HumanPose pose, float vmdFrame)
        {
            if (_poseHandler == null) throw new ObjectDisposedException(nameof(PmxTargetHumanoidSourceRig));
            _poseHandler.GetHumanPose(ref pose);
            PmxHumanoidAnimationClipBaker.ValidateHumanPose(pose, vmdFrame);
            _poseHandler.SetHumanPose(ref pose);
        }

        internal Quaternion GetSolvedLocalRotation(HumanBodyBones bone)
        {
            int index = (int)bone;
            if (_cloneByHumanBone == null || index < 0 || index >= _cloneByHumanBone.Length ||
                _cloneByHumanBone[index] == null)
                throw new InvalidOperationException("The target Avatar does not provide " + bone + ".");
            return _cloneByHumanBone[index].localRotation;
        }

        public void Dispose()
        {
            if (_poseHandler != null) _poseHandler.Dispose();
            if (_cloneRoot != null) UnityEngine.Object.DestroyImmediate(_cloneRoot);
            _poseHandler = null;
            _cloneRoot = null;
            _cloneTargets = null;
            _cloneByHumanBone = null;
        }

        private static Dictionary<Transform, Transform> CloneHierarchy(Animator animator,
            out GameObject cloneRoot)
        {
            Transform sourceRoot = animator.transform;
            cloneRoot = new GameObject(sourceRoot.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            cloneRoot.transform.localPosition = Vector3.zero;
            cloneRoot.transform.localRotation = Quaternion.identity;
            cloneRoot.transform.localScale = Vector3.one;
            var result = new Dictionary<Transform, Transform>
            {
                [sourceRoot] = cloneRoot.transform
            };
            Dictionary<string, SkeletonBone> skeleton =
                PmxTargetFkAnimationClipBaker.BuildSkeletonLookup(animator.avatar);
            HashSet<Transform> required = BuildRequiredTransforms(animator, skeleton);
            CloneChildren(sourceRoot, cloneRoot.transform, skeleton, required, result);
            return result;
        }

        private static HashSet<Transform> BuildRequiredTransforms(Animator animator,
            IReadOnlyDictionary<string, SkeletonBone> skeleton)
        {
            Transform root = animator.transform;
            var required = new HashSet<Transform> { root };
            for (int index = 0; index < (int)HumanBodyBones.LastBone; index++)
                AddWithAncestors(animator.GetBoneTransform((HumanBodyBones)index), root, required);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                if (skeleton.ContainsKey(transform.name))
                    AddWithAncestors(transform, root, required);
            return required;
        }

        private static void AddWithAncestors(Transform transform, Transform root,
            ISet<Transform> required)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                required.Add(current);
                if (current == root) break;
            }
        }

        private static void CloneChildren(Transform source, Transform cloneParent,
            IReadOnlyDictionary<string, SkeletonBone> skeleton,
            ISet<Transform> required,
            IDictionary<Transform, Transform> result)
        {
            for (int index = 0; index < source.childCount; index++)
            {
                Transform child = source.GetChild(index);
                if (!required.Contains(child)) continue;
                var cloneObject = new GameObject(child.name)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Transform clone = cloneObject.transform;
                clone.SetParent(cloneParent, false);
                if (skeleton.TryGetValue(child.name, out SkeletonBone rest))
                {
                    clone.localPosition = rest.position;
                    clone.localRotation = rest.rotation;
                    clone.localScale = rest.scale;
                }
                else
                {
                    clone.localPosition = child.localPosition;
                    clone.localRotation = child.localRotation;
                    clone.localScale = child.localScale;
                }
                result[child] = clone;
                CloneChildren(child, clone, skeleton, required, result);
            }
        }
    }
}
