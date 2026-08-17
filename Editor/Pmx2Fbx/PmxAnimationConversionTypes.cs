using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    public enum PmxCoordinateConversion
    {
        MmdToUnity,
        MmdToUnityAndRotateY180,
        RotateY180,
        None
    }

    /// <summary>Shared sampling and coordinate options for PMX/VMD conversion.</summary>
    [Serializable]
    public sealed class PmxAnimationClipBakerOptions
    {
        public float SampleRate = 30f;
        public float PositionScale = 1f;
        public PmxCoordinateConversion CoordinateConversion = PmxCoordinateConversion.RotateY180;
        public bool BakeMorphs = true;
        public bool EnforceHumanoidTPose = true;
        public float CurveReductionError = 0.0001f;

        /// <summary>
        /// Debug-only target Avatar upper-limb reconstruction for target-calibrated Humanoid
        /// output. Target FK has an independent option in
        /// <see cref="PmxDebugTargetFkBakeOptions.UpperLimbInverseSolve"/>.
        /// Standard Humanoid and Generic PMX conversion ignore this value.
        /// </summary>
        public bool DebugTargetUpperLimbInverseSolve;

        public PmxEvaluationOptions Evaluation = new PmxEvaluationOptions();
    }

    public sealed class PmxAnimationBakeReport
    {
        public AnimationClip Clip;
        public readonly List<string> MissingBones = new List<string>();
        public int BoundBoneCount;
        public int BoundMorphCount;
        public int SampleCount;
    }
}
