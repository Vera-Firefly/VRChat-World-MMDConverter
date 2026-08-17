using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    [Flags]
    public enum PmxBoneFlags : ushort
    {
        None = 0,
        TailUsesBoneIndex = 0x0001,
        Rotatable = 0x0002,
        Movable = 0x0004,
        Visible = 0x0008,
        Enabled = 0x0010,
        Ik = 0x0020,
        LocalAppend = 0x0080,
        InheritRotation = 0x0100,
        InheritTranslation = 0x0200,
        FixedAxis = 0x0400,
        LocalAxes = 0x0800,
        AfterPhysics = 0x1000,
        ExternalParent = 0x2000
    }

    public enum PmxMorphType : byte
    {
        Group = 0,
        Vertex = 1,
        Bone = 2,
        Uv = 3,
        AdditionalUv1 = 4,
        AdditionalUv2 = 5,
        AdditionalUv3 = 6,
        AdditionalUv4 = 7,
        Material = 8,
        Flip = 9,
        Impulse = 10
    }

    [Serializable]
    public sealed class PmxIkLink
    {
        public int BoneIndex = -1;
        public bool HasLimits;
        public Vector3 MinimumAngle;
        public Vector3 MaximumAngle;
    }

    [Serializable]
    public sealed class PmxIkDefinition
    {
        public int TargetBoneIndex = -1;
        public int IterationCount;
        public float AngleLimit;
        public readonly List<PmxIkLink> Links = new List<PmxIkLink>();
    }

    [Serializable]
    public sealed class PmxBone
    {
        public int Index;
        public string NameJapanese = string.Empty;
        public string NameEnglish = string.Empty;
        public Vector3 Position;
        public int ParentIndex = -1;
        public int TransformLayer;
        public PmxBoneFlags Flags;

        public int TailBoneIndex = -1;
        public Vector3 TailOffset;

        public int InheritParentIndex = -1;
        public float InheritWeight;

        public Vector3 FixedAxis;
        public Vector3 LocalAxisX = Vector3.right;
        public Vector3 LocalAxisY = Vector3.up;
        public Vector3 LocalAxisZ = Vector3.forward;
        public int ExternalParentKey;
        public PmxIkDefinition Ik;

        public bool HasFlag(PmxBoneFlags flag)
        {
            return (Flags & flag) == flag;
        }
    }

    [Serializable]
    public sealed class PmxBoneMorphOffset
    {
        public int BoneIndex = -1;
        public Vector3 Translation;
        public Quaternion Rotation = Quaternion.identity;
    }

    [Serializable]
    public sealed class PmxGroupMorphOffset
    {
        public int MorphIndex = -1;
        public float Weight;
    }

    [Serializable]
    public sealed class PmxMorphDefinition
    {
        public string NameJapanese = string.Empty;
        public string NameEnglish = string.Empty;
        public byte Panel;
        public PmxMorphType Type;
        public int OffsetCount;
        public readonly List<PmxGroupMorphOffset> GroupOffsets = new List<PmxGroupMorphOffset>();
        public readonly List<PmxBoneMorphOffset> BoneOffsets = new List<PmxBoneMorphOffset>();
    }

    [Serializable]
    public sealed class PmxModel
    {
        public float Version;
        public string NameJapanese = string.Empty;
        public string NameEnglish = string.Empty;
        public string CommentJapanese = string.Empty;
        public string CommentEnglish = string.Empty;
        public readonly List<PmxBone> Bones = new List<PmxBone>();
        public readonly List<PmxMorphDefinition> Morphs = new List<PmxMorphDefinition>();

        private Dictionary<string, int> _boneLookup;
        private int[] _transformOrder;
        private Vector3[] _restLocalPositions;

        public int FindBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            EnsureBoneLookup();
            return _boneLookup.TryGetValue(name, out int index) ? index : -1;
        }

        public int[] GetTransformOrder()
        {
            if (_transformOrder != null) return _transformOrder;
            _transformOrder = new int[Bones.Count];
            for (int index = 0; index < _transformOrder.Length; index++)
                _transformOrder[index] = index;
            Array.Sort(_transformOrder, CompareTransformOrder);
            return _transformOrder;
        }

        public Vector3[] GetRestLocalPositions()
        {
            if (_restLocalPositions != null) return _restLocalPositions;
            _restLocalPositions = new Vector3[Bones.Count];
            for (int index = 0; index < Bones.Count; index++)
            {
                PmxBone bone = Bones[index];
                _restLocalPositions[index] = bone.ParentIndex >= 0 && bone.ParentIndex < Bones.Count
                    ? bone.Position - Bones[bone.ParentIndex].Position
                    : bone.Position;
            }
            return _restLocalPositions;
        }

        public void ValidateHierarchy()
        {
            for (int index = 0; index < Bones.Count; index++)
            {
                int parent = Bones[index].ParentIndex;
                if (parent < -1 || parent >= Bones.Count)
                    throw new InvalidOperationException($"PMX bone {index} has an invalid parent index {parent}.");

                int current = parent;
                int hops = 0;
                while (current >= 0)
                {
                    if (current == index)
                        throw new InvalidOperationException($"PMX bone hierarchy contains a cycle at bone {index}.");
                    current = Bones[current].ParentIndex;
                    if (++hops > Bones.Count)
                        throw new InvalidOperationException("PMX bone hierarchy contains a cycle.");
                }
            }
        }

        private int CompareTransformOrder(int left, int right)
        {
            PmxBone a = Bones[left];
            PmxBone b = Bones[right];
            int physics = a.HasFlag(PmxBoneFlags.AfterPhysics).CompareTo(b.HasFlag(PmxBoneFlags.AfterPhysics));
            if (physics != 0) return physics;
            int layer = a.TransformLayer.CompareTo(b.TransformLayer);
            return layer != 0 ? layer : left.CompareTo(right);
        }

        private void EnsureBoneLookup()
        {
            if (_boneLookup != null) return;
            _boneLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < Bones.Count; index++)
            {
                AddBoneName(Bones[index].NameJapanese, index);
                AddBoneName(Bones[index].NameEnglish, index);
            }
        }

        private void AddBoneName(string name, int index)
        {
            if (!string.IsNullOrEmpty(name) && !_boneLookup.ContainsKey(name))
                _boneLookup.Add(name, index);
        }
    }
}
