using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    public enum VmdInterpolationOwner
    {
        DestinationKey,
        SourceKey
    }

    public static class VmdTrackSampler
    {
        public static void SampleBone(VmdBoneTrack track, float frame,
            out Vector3 translation, out Quaternion rotation,
            VmdInterpolationOwner interpolationOwner = VmdInterpolationOwner.DestinationKey)
        {
            if (track == null || track.Keys.Count == 0)
            {
                translation = Vector3.zero;
                rotation = Quaternion.identity;
                return;
            }

            IReadOnlyList<VmdBoneKeyframe> keys = track.Keys;
            int rightIndex = FindRightKey(keys, frame);
            if (rightIndex <= 0)
            {
                translation = keys[0].Translation;
                rotation = keys[0].Rotation;
                return;
            }
            if (rightIndex >= keys.Count)
            {
                VmdBoneKeyframe last = keys[keys.Count - 1];
                translation = last.Translation;
                rotation = last.Rotation;
                return;
            }
            if (keys[rightIndex].Frame == frame)
            {
                VmdBoneKeyframe exact = keys[rightIndex];
                translation = exact.Translation;
                rotation = exact.Rotation;
                return;
            }

            VmdBoneKeyframe left = keys[rightIndex - 1];
            VmdBoneKeyframe right = keys[rightIndex];
            float duration = right.Frame - left.Frame;
            float time = duration > 0f ? Mathf.Clamp01((frame - left.Frame) / duration) : 1f;
            VmdBoneInterpolation interpolation = interpolationOwner == VmdInterpolationOwner.DestinationKey
                ? right.Interpolation
                : left.Interpolation;

            translation = new Vector3(
                Mathf.LerpUnclamped(left.Translation.x, right.Translation.x, interpolation.X.Evaluate(time)),
                Mathf.LerpUnclamped(left.Translation.y, right.Translation.y, interpolation.Y.Evaluate(time)),
                Mathf.LerpUnclamped(left.Translation.z, right.Translation.z, interpolation.Z.Evaluate(time)));
            rotation = NormalizeSafe(Quaternion.SlerpUnclamped(
                left.Rotation, right.Rotation, interpolation.Rotation.Evaluate(time)));
        }

        public static float SampleMorph(VmdMorphTrack track, float frame)
        {
            if (track == null || track.Keys.Count == 0) return 0f;
            IReadOnlyList<VmdMorphKeyframe> keys = track.Keys;
            int rightIndex = FindRightKey(keys, frame);
            if (rightIndex <= 0) return keys[0].Weight;
            if (rightIndex >= keys.Count) return keys[keys.Count - 1].Weight;
            if (keys[rightIndex].Frame == frame) return keys[rightIndex].Weight;
            VmdMorphKeyframe left = keys[rightIndex - 1];
            VmdMorphKeyframe right = keys[rightIndex];
            float duration = right.Frame - left.Frame;
            float time = duration > 0f ? Mathf.Clamp01((frame - left.Frame) / duration) : 1f;
            return Mathf.LerpUnclamped(left.Weight, right.Weight, time);
        }

        private static int FindRightKey(IReadOnlyList<VmdBoneKeyframe> keys, float frame)
        {
            int low = 0;
            int high = keys.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (keys[middle].Frame < frame) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static int FindRightKey(IReadOnlyList<VmdMorphKeyframe> keys, float frame)
        {
            int low = 0;
            int high = keys.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (keys[middle].Frame < frame) low = middle + 1;
                else high = middle;
            }
            return low;
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
