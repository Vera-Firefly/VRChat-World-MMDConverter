using System;
using System.Collections.Generic;
using UnityEngine;

public enum VmdMotionConversionScheme
{
    EmbeddedHumanoidClip = 1,
    DirectExport = 2,
    ExternalHumanoidClip = 3
}

public sealed class VmdMotionConversionData : ScriptableObject
{
    public const int CurrentDataVersion = 3;

    [Serializable]
    public sealed class MorphFrame
    {
        public int frame;
        public float value;
    }

    [Serializable]
    public sealed class MorphRecord
    {
        public string name;
        public List<MorphFrame> frames = new List<MorphFrame>();
    }

    [Serializable]
    public sealed class MorphMapping
    {
        public string source;
        public string target;
        public string targetPath;
    }

    [Serializable]
    public sealed class ModelMapping
    {
        public string modelName;
        public string hierarchyPath;
        public List<MorphMapping> mappings = new List<MorphMapping>();
    }

    public int dataVersion;
    public bool isConverted;
    public VmdMotionConversionScheme conversionScheme;
    public string sourceVmdGuid = string.Empty;
    public string sourceVmdDependencyHash = string.Empty;
    public string sourceExpressionVmdGuid = string.Empty;
    public string sourceExpressionVmdDependencyHash = string.Empty;
    public AnimationClip sourceClip;
    public float poseRotationAngle;
    public List<MorphRecord> morphs = new List<MorphRecord>();
    public List<ModelMapping> modelMappings = new List<ModelMapping>();
}
