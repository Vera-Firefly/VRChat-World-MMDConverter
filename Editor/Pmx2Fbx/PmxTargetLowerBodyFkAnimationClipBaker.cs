using System;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    /// <summary>
    /// Debug target-Transform output that keeps direct PMX FK only for hips and legs.
    /// All remaining Transform curves are materialized from the target-calibrated HumanPose.
    /// </summary>
    internal static class PmxTargetLowerBodyFkAnimationClipBaker
    {
        internal static PmxAnimationBakeReport Debug_BakeToAsset(
            string pmxPath,
            string vmdPath,
            GameObject targetRoot,
            string outputAssetPath,
            PmxAnimationClipBakerOptions options = null,
            PmxDebugTargetFkBakeOptions fkOptions = null,
            Action<float, string> progress = null)
        {
            return PmxTargetFkAnimationClipBaker.Debug_BakeToAsset(
                pmxPath,
                vmdPath,
                targetRoot,
                outputAssetPath,
                options,
                fkOptions,
                PmxTargetFkBakeMode.LowerBody,
                progress);
        }
    }
}
