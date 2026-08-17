using System;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    /// <summary>
    /// Public entry points for PMX/VMD animation conversion. Feature pages should call this
    /// facade instead of depending on an individual baker implementation.
    /// </summary>
    public static class PmxAnimationConversion
    {
        /// <summary>
        /// Converts VMD motion to a standard Humanoid Root/Muscle AnimationClip asset.
        /// This is the normal VMD Motion conversion path.
        /// </summary>
        public static PmxAnimationBakeReport ConvertVmdHumanoidToAsset(
            string pmxPath, string vmdPath, Transform morphTargetRoot,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            return PmxHumanoidAnimationClipBaker.BakeToAsset(
                pmxPath, vmdPath, morphTargetRoot, outputAssetPath, options, progress);
        }

        /// <summary>
        /// Converts VMD motion to target-calibrated Humanoid Root/Muscle curves.
        /// Target upper-limb HumanPose inverse solving occurs only when
        /// <see cref="PmxAnimationClipBakerOptions.DebugTargetUpperLimbInverseSolve"/> is true.
        /// </summary>
        public static PmxAnimationBakeReport ConvertVmdTargetCalibratedHumanoidToAsset(
            string pmxPath, string vmdPath, GameObject targetAvatar,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            return PmxTargetHumanoidAnimationClipBaker.BakeToAsset(
                pmxPath, vmdPath, targetAvatar, outputAssetPath, options, progress);
        }

        /// <summary>
        /// Debug PMX conversion that writes Transform curves onto a hierarchy whose names
        /// match PMX bones. This is not used by the normal VMD Motion conversion page.
        /// </summary>
        public static PmxAnimationBakeReport Debug_ConvertGenericPmxToAsset(
            string pmxPath, string vmdPath, Transform targetRoot,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            return PmxAnimationClipBaker.Debug_BakeToAsset(
                pmxPath, vmdPath, targetRoot, outputAssetPath, options, progress);
        }

        /// <summary>
        /// Debug conversion that writes target-Avatar Transform/FK curves. When the explicit
        /// <see cref="PmxDebugTargetFkBakeOptions.UpperLimbInverseSolve"/> option is enabled,
        /// shoulder/arm curves use a target HumanPose round trip; the remaining bindings keep
        /// the parallel FK path.
        /// </summary>
        public static PmxAnimationBakeReport Debug_ConvertTargetFkToAsset(
            string pmxPath, string vmdPath, GameObject targetAvatar,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            PmxDebugTargetFkBakeOptions fkOptions = null,
            Action<float, string> progress = null)
        {
            return PmxTargetFkAnimationClipBaker.Debug_BakeToAsset(
                pmxPath, vmdPath, targetAvatar, outputAssetPath, options, fkOptions, progress);
        }

        /// <summary>
        /// Debug conversion that preserves target-FK curves for the hips and legs while
        /// materializing the remaining target Transform curves from the same HumanPose used by
        /// target-calibrated Humanoid output. The FK upper-limb inverse option remains explicit.
        /// </summary>
        public static PmxAnimationBakeReport Debug_ConvertTargetLowerBodyFkToAsset(
            string pmxPath, string vmdPath, GameObject targetAvatar,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            PmxDebugTargetFkBakeOptions fkOptions = null,
            Action<float, string> progress = null)
        {
            return PmxTargetLowerBodyFkAnimationClipBaker.Debug_BakeToAsset(
                pmxPath, vmdPath, targetAvatar, outputAssetPath, options, fkOptions, progress);
        }

        /// <summary>
        /// Debug entry point for comparing standard Humanoid output from PMX settings.
        /// </summary>
        public static PmxAnimationBakeReport Debug_ConvertHumanoidToAsset(
            string pmxPath, string vmdPath, Transform morphTargetRoot,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            return ConvertVmdHumanoidToAsset(
                pmxPath, vmdPath, morphTargetRoot, outputAssetPath, options, progress);
        }

        /// <summary>
        /// Debug entry point for comparing target-calibrated Humanoid output from PMX settings.
        /// The upper-limb inverse-solve option remains explicit in <paramref name="options"/>.
        /// </summary>
        public static PmxAnimationBakeReport Debug_ConvertTargetCalibratedHumanoidToAsset(
            string pmxPath, string vmdPath, GameObject targetAvatar,
            string outputAssetPath, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            return ConvertVmdTargetCalibratedHumanoidToAsset(
                pmxPath, vmdPath, targetAvatar, outputAssetPath, options, progress);
        }
    }
}
