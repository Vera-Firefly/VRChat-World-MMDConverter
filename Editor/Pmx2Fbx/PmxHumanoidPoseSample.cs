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
    internal sealed class PmxHumanoidPoseSample
    {
        internal readonly Vector3[] WorldPositions;
        internal readonly Quaternion[] WorldRotations;
        internal readonly Quaternion[] LocalRotations;

        internal PmxHumanoidPoseSample(PmxPose pose)
        {
            int count = pose.BoneCount;
            WorldPositions = new Vector3[count];
            WorldRotations = new Quaternion[count];
            LocalRotations = new Quaternion[count];
            Array.Copy(pose.WorldPositions, WorldPositions, count);
            Array.Copy(pose.WorldRotations, WorldRotations, count);
            Array.Copy(pose.LocalRotations, LocalRotations, count);
        }
    }
}
