using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    public sealed class PmxPose
    {
        public readonly Vector3[] LocalPositions;
        public readonly Quaternion[] LocalRotations;
        public readonly Vector3[] WorldPositions;
        public readonly Quaternion[] WorldRotations;
        internal readonly Vector3[] MotionTranslations;
        internal readonly Quaternion[] MotionRotations;

        public int BoneCount => LocalPositions.Length;

        internal PmxPose(int boneCount)
        {
            LocalPositions = new Vector3[boneCount];
            LocalRotations = new Quaternion[boneCount];
            WorldPositions = new Vector3[boneCount];
            WorldRotations = new Quaternion[boneCount];
            MotionTranslations = new Vector3[boneCount];
            MotionRotations = new Quaternion[boneCount];
        }
    }
}
