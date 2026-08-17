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
    internal sealed class PmxHumanoidSourceRig : IDisposable
    {
        private static readonly PmxAnimationClipBakerOptions SourceCoordinates =
            new PmxAnimationClipBakerOptions
            {
                CoordinateConversion = PmxCoordinateConversion.RotateY180
            };

        internal sealed class HumanBoneRule
        {
            internal readonly HumanBodyBones HumanBone;
            internal readonly string[] Candidates;

            internal HumanBoneRule(HumanBodyBones humanBone, params string[] candidates)
            {
                HumanBone = humanBone;
                Candidates = candidates;
            }
        }

        internal static readonly HumanBoneRule[] HumanBoneRules =
        {
            // Standard MMD waist is the common ancestor of upper body and lower body/legs.
            // Mapping 下半身 directly would put Spine outside the Humanoid Hips hierarchy.
            Rule(HumanBodyBones.Hips, "下半身", "lower body", "LowerBody", "腰", "waist", "Waist", "hips", "Hips"),
            Rule(HumanBodyBones.Spine, "上半身", "upper body", "UpperBody", "spine", "Spine"),
            Rule(HumanBodyBones.Chest, "上半身2", "上半身２", "upper body2", "UpperBody2", "chest", "Chest"),
            Rule(HumanBodyBones.UpperChest, "上半身3", "上半身３", "upper body3", "UpperBody3", "upper chest", "UpperChest"),
            Rule(HumanBodyBones.Neck, "首", "neck", "Neck"),
            Rule(HumanBodyBones.Head, "頭", "head", "Head"),
            Rule(HumanBodyBones.LeftEye, "左目", "left eye", "LeftEye"),
            Rule(HumanBodyBones.RightEye, "右目", "right eye", "RightEye"),
            Rule(HumanBodyBones.Jaw, "あご", "顎", "jaw", "Jaw"),
            Rule(HumanBodyBones.LeftShoulder, "左肩", "left shoulder", "LeftShoulder"),
            Rule(HumanBodyBones.LeftUpperArm, "左腕", "left arm", "LeftArm", "LeftUpperArm"),
            Rule(HumanBodyBones.LeftLowerArm, "左ひじ", "左肘", "left elbow", "LeftElbow", "LeftLowerArm"),
            Rule(HumanBodyBones.LeftHand, "左手首", "left wrist", "LeftWrist", "LeftHand"),
            Rule(HumanBodyBones.RightShoulder, "右肩", "right shoulder", "RightShoulder"),
            Rule(HumanBodyBones.RightUpperArm, "右腕", "right arm", "RightArm", "RightUpperArm"),
            Rule(HumanBodyBones.RightLowerArm, "右ひじ", "右肘", "right elbow", "RightElbow", "RightLowerArm"),
            Rule(HumanBodyBones.RightHand, "右手首", "right wrist", "RightWrist", "RightHand"),
            Rule(HumanBodyBones.LeftUpperLeg, "左足D", "左足Ｄ", "左足", "left leg", "LeftLeg", "LeftUpperLeg"),
            Rule(HumanBodyBones.LeftLowerLeg, "左ひざD", "左膝D", "左ひざＤ", "左ひざ", "左膝", "left knee", "LeftKnee", "LeftLowerLeg"),
            Rule(HumanBodyBones.LeftFoot, "左足首D", "左足首Ｄ", "左足首", "left ankle", "LeftAnkle", "LeftFoot"),
            Rule(HumanBodyBones.LeftToes, "左つま先EX", "左つま先", "left toe", "LeftToe", "LeftToes"),
            Rule(HumanBodyBones.RightUpperLeg, "右足D", "右足Ｄ", "右足", "right leg", "RightLeg", "RightUpperLeg"),
            Rule(HumanBodyBones.RightLowerLeg, "右ひざD", "右膝D", "右ひざＤ", "右ひざ", "右膝", "right knee", "RightKnee", "RightLowerLeg"),
            Rule(HumanBodyBones.RightFoot, "右足首D", "右足首Ｄ", "右足首", "right ankle", "RightAnkle", "RightFoot"),
            Rule(HumanBodyBones.RightToes, "右つま先EX", "右つま先", "right toe", "RightToe", "RightToes"),
            Rule(HumanBodyBones.LeftThumbIntermediate, "左親指1", "左親指１", "left thumb1", "LeftThumb1"),
            Rule(HumanBodyBones.LeftThumbDistal, "左親指2", "左親指２", "left thumb2", "LeftThumb2"),
            Rule(HumanBodyBones.LeftIndexProximal, "左人指1", "左人指１", "left index1", "LeftIndex1"),
            Rule(HumanBodyBones.LeftIndexIntermediate, "左人指2", "左人指２", "left index2", "LeftIndex2"),
            Rule(HumanBodyBones.LeftIndexDistal, "左人指3", "左人指３", "left index3", "LeftIndex3"),
            Rule(HumanBodyBones.LeftMiddleProximal, "左中指1", "左中指１", "left middle1", "LeftMiddle1"),
            Rule(HumanBodyBones.LeftMiddleIntermediate, "左中指2", "左中指２", "left middle2", "LeftMiddle2"),
            Rule(HumanBodyBones.LeftMiddleDistal, "左中指3", "左中指３", "left middle3", "LeftMiddle3"),
            Rule(HumanBodyBones.LeftRingProximal, "左薬指1", "左薬指１", "left ring1", "LeftRing1"),
            Rule(HumanBodyBones.LeftRingIntermediate, "左薬指2", "左薬指２", "left ring2", "LeftRing2"),
            Rule(HumanBodyBones.LeftRingDistal, "左薬指3", "左薬指３", "left ring3", "LeftRing3"),
            Rule(HumanBodyBones.LeftLittleProximal, "左小指1", "左小指１", "left little1", "LeftLittle1"),
            Rule(HumanBodyBones.LeftLittleIntermediate, "左小指2", "左小指２", "left little2", "LeftLittle2"),
            Rule(HumanBodyBones.LeftLittleDistal, "左小指3", "左小指３", "left little3", "LeftLittle3"),
            Rule(HumanBodyBones.RightThumbIntermediate, "右親指1", "右親指１", "right thumb1", "RightThumb1"),
            Rule(HumanBodyBones.RightThumbDistal, "右親指2", "右親指２", "right thumb2", "RightThumb2"),
            Rule(HumanBodyBones.RightIndexProximal, "右人指1", "右人指１", "right index1", "RightIndex1"),
            Rule(HumanBodyBones.RightIndexIntermediate, "右人指2", "右人指２", "right index2", "RightIndex2"),
            Rule(HumanBodyBones.RightIndexDistal, "右人指3", "右人指３", "right index3", "RightIndex3"),
            Rule(HumanBodyBones.RightMiddleProximal, "右中指1", "右中指１", "right middle1", "RightMiddle1"),
            Rule(HumanBodyBones.RightMiddleIntermediate, "右中指2", "右中指２", "right middle2", "RightMiddle2"),
            Rule(HumanBodyBones.RightMiddleDistal, "右中指3", "右中指３", "right middle3", "RightMiddle3"),
            Rule(HumanBodyBones.RightRingProximal, "右薬指1", "右薬指１", "right ring1", "RightRing1"),
            Rule(HumanBodyBones.RightRingIntermediate, "右薬指2", "右薬指２", "right ring2", "RightRing2"),
            Rule(HumanBodyBones.RightRingDistal, "右薬指3", "右薬指３", "right ring3", "RightRing3"),
            Rule(HumanBodyBones.RightLittleProximal, "右小指1", "右小指１", "right little1", "RightLittle1"),
            Rule(HumanBodyBones.RightLittleIntermediate, "右小指2", "右小指２", "right little2", "RightLittle2"),
            Rule(HumanBodyBones.RightLittleDistal, "右小指3", "右小指３", "right little3", "RightLittle3")
        };

        internal GameObject Root { get; private set; }
        internal Avatar Avatar { get; private set; }
        internal int MappedHumanBoneCount { get; private set; }
        internal readonly List<string> MissingRequiredBones = new List<string>();

        private PmxModel _model;
        private Transform[] _bones;
        private Vector3[] _restLocalPositions;
        private readonly Dictionary<HumanBodyBones, int> _humanBoneIndices =
            new Dictionary<HumanBodyBones, int>();
        private readonly Dictionary<HumanBodyBones, Transform> _humanBoneTransforms =
            new Dictionary<HumanBodyBones, Transform>();
        private readonly List<int> _humanBoneWorldOrder = new List<int>();
        private readonly HashSet<int> _thumbLocalMotionBoneIndices = new HashSet<int>();
        private readonly Dictionary<int, int> _thumbMotionParentIndices = new Dictionary<int, int>();

        internal static PmxHumanoidSourceRig Create(PmxModel model, PmxAnimationClipBakerOptions options)
        {
            var rig = new PmxHumanoidSourceRig();
            try
            {
                rig.Build(model, options);
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        internal void Apply(PmxHumanoidPoseSample pose, float positionScale)
        {
            foreach (int index in _humanBoneWorldOrder)
            {
                if (_thumbLocalMotionBoneIndices.Contains(index)) continue;
                _bones[index].position =
                    PmxAnimationClipBaker.ConvertPosition(pose.WorldPositions[index], SourceCoordinates) * positionScale;
                _bones[index].rotation =
                    PmxAnimationClipBaker.ConvertRotation(pose.WorldRotations[index], SourceCoordinates);
            }

            // Native PMX2FBX inserts a static Thumb0M proxy between the wrist and
            // the original Thumb1/Thumb2 bones. Those two bones therefore retain
            // their own local VMD rotations and must not inherit the animated PMX
            // Thumb0 world rotation.
            foreach (int index in _humanBoneWorldOrder)
            {
                if (!_thumbLocalMotionBoneIndices.Contains(index)) continue;
                Quaternion localRotation = pose.LocalRotations[index];
                if (_thumbMotionParentIndices.TryGetValue(index, out int originalThumbRoot))
                {
                    // Thumb1 used to be a child of the animated PMX Thumb0. Once
                    // Thumb0 is replaced by the static Mecanim proxy, preserve the
                    // original world motion by folding Thumb0 into Thumb1.
                    localRotation = pose.LocalRotations[originalThumbRoot] * localRotation;
                }
                _bones[index].localRotation =
                    PmxAnimationClipBaker.ConvertRotation(localRotation, SourceCoordinates);
            }
        }

        public void Dispose()
        {
            if (Avatar != null) UnityEngine.Object.DestroyImmediate(Avatar);
            if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
            Avatar = null;
            Root = null;
        }

        private void Build(PmxModel model, PmxAnimationClipBakerOptions options)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _restLocalPositions = model.GetRestLocalPositions();
            Root = new GameObject("__PMX_Humanoid_Source__")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _bones = new Transform[model.Bones.Count];
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < model.Bones.Count; index++)
            {
                PmxBone bone = model.Bones[index];
                string name = string.IsNullOrEmpty(bone.NameJapanese) ? bone.NameEnglish : bone.NameJapanese;
                if (string.IsNullOrEmpty(name)) name = "PMX Bone " + index;
                string uniqueName = name;
                for (int suffix = 1; !usedNames.Add(uniqueName); suffix++)
                    uniqueName = name + "__" + suffix;
                var gameObject = new GameObject(uniqueName) { hideFlags = HideFlags.HideAndDontSave };
                _bones[index] = gameObject.transform;
            }
            for (int index = 0; index < model.Bones.Count; index++)
            {
                int parent = model.Bones[index].ParentIndex;
                _bones[index].SetParent(parent >= 0 ? _bones[parent] : Root.transform, false);
                _bones[index].localPosition =
                    PmxAnimationClipBaker.ConvertPosition(_restLocalPositions[index], SourceCoordinates) * options.PositionScale;
                _bones[index].localRotation = Quaternion.identity;
                _bones[index].localScale = Vector3.one;
            }

            HumanBone[] human = BuildHumanBones();
            if (MissingRequiredBones.Count > 0)
                throw new InvalidOperationException(
                    "PMX is missing required Humanoid mappings: " + string.Join(", ", MissingRequiredBones));
            BuildHumanoidProxyHierarchy();
            SkeletonBone[] referenceSkeleton = null;
            if (options.EnforceHumanoidTPose)
                TryBuildUnityHumanoidReferenceSkeleton(human, out referenceSkeleton);
            if (referenceSkeleton == null)
            {
                if (options.EnforceHumanoidTPose)
                    ForceHumanoidReferencePose(PmxCoordinateConversion.RotateY180);
                referenceSkeleton = BuildSkeletonBones(Root.transform);
            }

            var description = new HumanDescription
            {
                human = human,
                skeleton = referenceSkeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };
            Avatar = AvatarBuilder.BuildHumanAvatar(Root, description);
            Avatar.name = string.IsNullOrEmpty(model.NameJapanese)
                ? "PMX Humanoid Source Avatar"
                : model.NameJapanese + " Humanoid Source Avatar";
        }

        private HumanBone[] BuildHumanBones()
        {
            var result = new List<HumanBone>();
            var mappedPmxBones = new HashSet<int>();
            foreach (HumanBoneRule rule in HumanBoneRules)
            {
                int boneIndex = FindCandidate(rule.Candidates);
                if (boneIndex < 0 || !mappedPmxBones.Add(boneIndex))
                {
                    int humanIndex = (int)rule.HumanBone;
                    if (humanIndex >= 0 && humanIndex < HumanTrait.BoneCount && HumanTrait.RequiredBone(humanIndex))
                        MissingRequiredBones.Add(HumanTrait.BoneName[humanIndex]);
                    continue;
                }

                result.Add(new HumanBone
                {
                    boneName = _bones[boneIndex].name,
                    humanName = HumanTrait.BoneName[(int)rule.HumanBone],
                    limit = new HumanLimit { useDefaultValues = true }
                });
                _humanBoneIndices[rule.HumanBone] = boneIndex;
                _humanBoneTransforms[rule.HumanBone] = _bones[boneIndex];
            }

            AddSyntheticThumbProximal(result, true);
            AddSyntheticThumbProximal(result, false);
            MappedHumanBoneCount = result.Count;
            return result.ToArray();
        }

        private void AddSyntheticThumbProximal(List<HumanBone> humanBones, bool left)
        {
            HumanBodyBones handBone = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            HumanBodyBones proximalBone = left
                ? HumanBodyBones.LeftThumbProximal
                : HumanBodyBones.RightThumbProximal;
            HumanBodyBones intermediateBone = left
                ? HumanBodyBones.LeftThumbIntermediate
                : HumanBodyBones.RightThumbIntermediate;
            HumanBodyBones distalBone = left
                ? HumanBodyBones.LeftThumbDistal
                : HumanBodyBones.RightThumbDistal;
            if (!_humanBoneTransforms.TryGetValue(handBone, out Transform hand) ||
                !_humanBoneTransforms.TryGetValue(intermediateBone, out Transform intermediate) ||
                !_humanBoneIndices.TryGetValue(intermediateBone, out int intermediateIndex))
                return;

            string name = left ? "__LeftThumb0M__" : "__RightThumb0M__";
            var proxyObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            Transform proxy = proxyObject.transform;
            proxy.SetParent(hand, false);
            proxy.position = Vector3.Lerp(hand.position, intermediate.position, 0.5f);
            proxy.localRotation = Quaternion.identity;
            proxy.localScale = Vector3.one;
            intermediate.SetParent(proxy, true);

            humanBones.Add(new HumanBone
            {
                boneName = name,
                humanName = HumanTrait.BoneName[(int)proximalBone],
                limit = new HumanLimit { useDefaultValues = true }
            });
            _humanBoneTransforms[proximalBone] = proxy;
            _thumbLocalMotionBoneIndices.Add(intermediateIndex);
            int originalThumbRoot = FindCandidate(left
                ? new[] { "\u5de6\u89aa\u6307\uff10", "\u5de6\u89aa\u63070", "left thumb0", "LeftThumb0" }
                : new[] { "\u53f3\u89aa\u6307\uff10", "\u53f3\u89aa\u63070", "right thumb0", "RightThumb0" });
            if (originalThumbRoot >= 0 && originalThumbRoot != intermediateIndex)
                _thumbMotionParentIndices[intermediateIndex] = originalThumbRoot;
            if (_humanBoneIndices.TryGetValue(distalBone, out int distalIndex))
                _thumbLocalMotionBoneIndices.Add(distalIndex);
        }

        private void BuildHumanoidProxyHierarchy()
        {
            Reparent(HumanBodyBones.Hips, null);
            Reparent(HumanBodyBones.Spine, HumanBodyBones.Hips);
            Reparent(HumanBodyBones.Chest, HumanBodyBones.Spine);
            Reparent(HumanBodyBones.UpperChest, FirstMapped(HumanBodyBones.Chest, HumanBodyBones.Spine));
            HumanBodyBones torso = FirstMapped(HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine);
            Reparent(HumanBodyBones.Neck, torso);
            Reparent(HumanBodyBones.Head, FirstMapped(HumanBodyBones.Neck, torso));
            Reparent(HumanBodyBones.LeftEye, HumanBodyBones.Head);
            Reparent(HumanBodyBones.RightEye, HumanBodyBones.Head);
            Reparent(HumanBodyBones.Jaw, HumanBodyBones.Head);

            BuildArmHierarchy(true, torso);
            BuildArmHierarchy(false, torso);
            BuildLegHierarchy(true);
            BuildLegHierarchy(false);

            _humanBoneWorldOrder.Clear();
            _humanBoneWorldOrder.AddRange(_humanBoneIndices.Values
                .Distinct()
                .OrderBy(index => GetHierarchyDepth(_bones[index])));
        }

        private void BuildArmHierarchy(bool left, HumanBodyBones torso)
        {
            HumanBodyBones shoulder = left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder;
            HumanBodyBones upper = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            HumanBodyBones lower = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            HumanBodyBones hand = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            Reparent(shoulder, torso);
            Reparent(upper, FirstMapped(shoulder, torso));
            Reparent(lower, upper);
            Reparent(hand, lower);

            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal,
                left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate,
                left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal,
                left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate,
                left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal,
                left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate,
                left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal,
                left ? HumanBodyBones.LeftRingIntermediate : HumanBodyBones.RightRingIntermediate,
                left ? HumanBodyBones.LeftRingDistal : HumanBodyBones.RightRingDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal,
                left ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate,
                left ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal);
        }

        private void BuildFingerHierarchy(HumanBodyBones hand, HumanBodyBones proximal,
            HumanBodyBones intermediate, HumanBodyBones distal)
        {
            Reparent(proximal, hand);
            Reparent(intermediate, proximal);
            Reparent(distal, intermediate);
        }

        private void BuildLegHierarchy(bool left)
        {
            HumanBodyBones upper = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
            HumanBodyBones lower = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
            HumanBodyBones foot = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
            HumanBodyBones toes = left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
            Reparent(upper, HumanBodyBones.Hips);
            Reparent(lower, upper);
            Reparent(foot, lower);
            Reparent(toes, foot);
        }

        private void Reparent(HumanBodyBones child, HumanBodyBones? parent)
        {
            if (!_humanBoneTransforms.TryGetValue(child, out Transform childTransform)) return;
            Transform parentTransform = Root.transform;
            if (parent.HasValue && _humanBoneTransforms.TryGetValue(parent.Value, out Transform mappedParent))
                parentTransform = mappedParent;
            childTransform.SetParent(parentTransform, true);
        }

        private HumanBodyBones FirstMapped(params HumanBodyBones[] candidates)
        {
            foreach (HumanBodyBones candidate in candidates)
                if (_humanBoneTransforms.ContainsKey(candidate)) return candidate;
            return candidates[candidates.Length - 1];
        }

        private static int GetHierarchyDepth(Transform transform)
        {
            int depth = 0;
            for (Transform current = transform.parent; current != null; current = current.parent) depth++;
            return depth;
        }

        private void ForceHumanoidReferencePose(PmxCoordinateConversion coordinates)
        {
            bool swapsHorizontalSides = coordinates == PmxCoordinateConversion.MmdToUnityAndRotateY180 ||
                                        coordinates == PmxCoordinateConversion.RotateY180;
            Vector3 left = swapsHorizontalSides ? Vector3.left : Vector3.right;
            Vector3 right = -left;

            AlignChain(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, left);
            AlignChain(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, left);

            AlignChain(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, right);
            AlignChain(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, right);
        }

        private void AlignChain(HumanBodyBones parentBone, HumanBodyBones childBone, Vector3 desiredDirection)
        {
            if (!_humanBoneTransforms.TryGetValue(parentBone, out Transform parent) ||
                !_humanBoneTransforms.TryGetValue(childBone, out Transform child)) return;
            Vector3 currentDirection = child.position - parent.position;
            if (currentDirection.sqrMagnitude < 1e-8f) return;
            Quaternion correction = Quaternion.FromToRotation(currentDirection.normalized, desiredDirection.normalized);
            parent.rotation = correction * parent.rotation;
        }

        private bool TryBuildUnityHumanoidReferenceSkeleton(HumanBone[] humanBones,
            out SkeletonBone[] skeleton)
        {
            skeleton = null;
            GameObject poseRoot = null;
            try
            {
                // ModelImporter runs AvatarSetupTool.MakePoseValid before it builds a
                // Humanoid Avatar. That pass is important for fingers: it straightens
                // the terminal phalanges and creates the thumb reference rotations.
                // BuildHumanAvatar does not perform that editor-side setup by itself.
                Type setupTool = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AvatarSetupTool");
                if (setupTool == null) return false;

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo getModelBones = setupTool.GetMethods(flags).FirstOrDefault(method =>
                    method.Name == "GetModelBones" && method.GetParameters().Length == 3);
                MethodInfo getHumanBones = setupTool.GetMethods(flags).FirstOrDefault(method =>
                {
                    if (method.Name != "GetHumanBones") return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(Dictionary<string, string>);
                });
                MethodInfo makePoseValid = setupTool.GetMethods(flags).FirstOrDefault(method =>
                    method.Name == "MakePoseValid" && method.GetParameters().Length == 1);
                if (getModelBones == null || getHumanBones == null || makePoseValid == null)
                    return false;

                poseRoot = UnityEngine.Object.Instantiate(Root);
                poseRoot.name = Root.name;
                poseRoot.hideFlags = HideFlags.HideAndDontSave;

                object modelBones = getModelBones.Invoke(null,
                    new object[] { poseRoot.transform, false, null });
                var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (HumanBone humanBone in humanBones)
                    mapping[humanBone.humanName] = humanBone.boneName;
                object wrappers = getHumanBones.Invoke(null, new[] { (object)mapping, modelBones });
                if (wrappers == null) return false;

                makePoseValid.Invoke(null, new[] { wrappers });
                skeleton = BuildSkeletonBones(poseRoot.transform);
                return skeleton != null && skeleton.Length > 0;
            }
            catch (Exception)
            {
                skeleton = null;
                return false;
            }
            finally
            {
                if (poseRoot != null) UnityEngine.Object.DestroyImmediate(poseRoot);
            }
        }

        private int FindCandidate(string[] names)
        {
            foreach (string name in names)
            {
                int index = _model.FindBone(name);
                if (index >= 0) return index;
            }
            return -1;
        }

        private static SkeletonBone[] BuildSkeletonBones(Transform root)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            var result = new SkeletonBone[transforms.Length];
            for (int index = 0; index < transforms.Length; index++)
            {
                Transform transform = transforms[index];
                result[index] = new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale
                };
            }
            return result;
        }

        private static HumanBoneRule Rule(HumanBodyBones bone, params string[] candidates)
        {
            return new HumanBoneRule(bone, candidates);
        }
    }
}
