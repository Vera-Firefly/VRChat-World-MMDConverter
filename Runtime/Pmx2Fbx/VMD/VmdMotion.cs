using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    [Serializable]
    public sealed class VmdBoneInterpolation
    {
        public MmdBezierCurve X = MmdBezierCurve.Linear;
        public MmdBezierCurve Y = MmdBezierCurve.Linear;
        public MmdBezierCurve Z = MmdBezierCurve.Linear;
        public MmdBezierCurve Rotation = MmdBezierCurve.Linear;
    }

    [Serializable]
    public sealed class VmdBoneKeyframe
    {
        public string BoneName = string.Empty;
        public uint Frame;
        public Vector3 Translation;
        public Quaternion Rotation = Quaternion.identity;
        public VmdBoneInterpolation Interpolation = new VmdBoneInterpolation();
    }

    [Serializable]
    public sealed class VmdMorphKeyframe
    {
        public string MorphName = string.Empty;
        public uint Frame;
        public float Weight;
    }

    [Serializable]
    public sealed class VmdCameraKeyframe
    {
        public uint Frame;
        public float Distance;
        public Vector3 Position;
        public Vector3 RotationRadians;
        public readonly byte[] Interpolation = new byte[24];
        public uint ViewAngle;
        public bool Perspective;
    }

    [Serializable]
    public sealed class VmdLightKeyframe
    {
        public uint Frame;
        public Color Color;
        public Vector3 Position;
    }

    [Serializable]
    public sealed class VmdShadowKeyframe
    {
        public uint Frame;
        public byte Mode;
        public float Distance;
    }

    [Serializable]
    public sealed class VmdIkState
    {
        public string Name = string.Empty;
        public bool Enabled;
    }

    [Serializable]
    public sealed class VmdIkKeyframe
    {
        public uint Frame;
        public bool ModelVisible;
        public readonly List<VmdIkState> States = new List<VmdIkState>();
    }

    public sealed class VmdBoneTrack
    {
        public string Name { get; internal set; }
        public IReadOnlyList<VmdBoneKeyframe> Keys => _keys;
        private readonly List<VmdBoneKeyframe> _keys = new List<VmdBoneKeyframe>();

        internal void Add(VmdBoneKeyframe key) => _keys.Add(key);
        internal void SortAndDeduplicate() => TrackUtility.SortAndDeduplicate(_keys, key => key.Frame);
    }

    public sealed class VmdMorphTrack
    {
        public string Name { get; internal set; }
        public IReadOnlyList<VmdMorphKeyframe> Keys => _keys;
        private readonly List<VmdMorphKeyframe> _keys = new List<VmdMorphKeyframe>();

        internal void Add(VmdMorphKeyframe key) => _keys.Add(key);
        internal void SortAndDeduplicate() => TrackUtility.SortAndDeduplicate(_keys, key => key.Frame);
    }

    public sealed class VmdMotion
    {
        private struct IkTrackKey
        {
            internal uint Frame;
            internal bool Enabled;
        }

        public string Header = string.Empty;
        public string ModelName = string.Empty;
        public readonly List<VmdBoneKeyframe> BoneKeyframes = new List<VmdBoneKeyframe>();
        public readonly List<VmdMorphKeyframe> MorphKeyframes = new List<VmdMorphKeyframe>();
        public readonly List<VmdCameraKeyframe> CameraKeyframes = new List<VmdCameraKeyframe>();
        public readonly List<VmdLightKeyframe> LightKeyframes = new List<VmdLightKeyframe>();
        public readonly List<VmdShadowKeyframe> ShadowKeyframes = new List<VmdShadowKeyframe>();
        public readonly List<VmdIkKeyframe> IkKeyframes = new List<VmdIkKeyframe>();

        private Dictionary<string, VmdBoneTrack> _boneTracks;
        private Dictionary<string, VmdMorphTrack> _morphTracks;
        private Dictionary<string, List<IkTrackKey>> _ikTracks;
        private uint? _maximumFrame;

        public IReadOnlyDictionary<string, VmdBoneTrack> BoneTracks
        {
            get { EnsureTracks(); return _boneTracks; }
        }

        public IReadOnlyDictionary<string, VmdMorphTrack> MorphTracks
        {
            get { EnsureTracks(); return _morphTracks; }
        }

        public uint MaximumFrame
        {
            get
            {
                if (_maximumFrame.HasValue) return _maximumFrame.Value;
                uint maximum = 0;
                foreach (VmdBoneKeyframe key in BoneKeyframes) maximum = Math.Max(maximum, key.Frame);
                foreach (VmdMorphKeyframe key in MorphKeyframes) maximum = Math.Max(maximum, key.Frame);
                foreach (VmdCameraKeyframe key in CameraKeyframes) maximum = Math.Max(maximum, key.Frame);
                foreach (VmdLightKeyframe key in LightKeyframes) maximum = Math.Max(maximum, key.Frame);
                foreach (VmdShadowKeyframe key in ShadowKeyframes) maximum = Math.Max(maximum, key.Frame);
                foreach (VmdIkKeyframe key in IkKeyframes) maximum = Math.Max(maximum, key.Frame);
                _maximumFrame = maximum;
                return maximum;
            }
        }

        public bool IsIkEnabled(string ikName, float frame)
        {
            return !TryGetIkState(ikName, frame, out bool enabled) || enabled;
        }

        public bool TryGetIkState(string ikName, float frame, out bool enabled)
        {
            enabled = true;
            if (string.IsNullOrEmpty(ikName)) return false;
            EnsureIkTracks();
            if (!_ikTracks.TryGetValue(ikName, out List<IkTrackKey> track) || track.Count == 0)
                return false;

            int low = 0;
            int high = track.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (track[middle].Frame <= frame) low = middle + 1;
                else high = middle;
            }
            if (low == 0) return false;
            enabled = track[low - 1].Enabled;
            return true;
        }

        internal void FinalizeData()
        {
            IkKeyframes.Sort((left, right) => left.Frame.CompareTo(right.Frame));
            CameraKeyframes.Sort((left, right) => left.Frame.CompareTo(right.Frame));
            LightKeyframes.Sort((left, right) => left.Frame.CompareTo(right.Frame));
            ShadowKeyframes.Sort((left, right) => left.Frame.CompareTo(right.Frame));
            EnsureTracks();
            EnsureIkTracks();
        }

        public void PrepareForConcurrentSampling()
        {
            EnsureTracks();
            EnsureIkTracks();
            _ = MaximumFrame;
        }

        private void EnsureTracks()
        {
            if (_boneTracks != null) return;
            _boneTracks = new Dictionary<string, VmdBoneTrack>(StringComparer.Ordinal);
            _morphTracks = new Dictionary<string, VmdMorphTrack>(StringComparer.Ordinal);
            foreach (VmdBoneKeyframe key in BoneKeyframes)
            {
                if (!_boneTracks.TryGetValue(key.BoneName, out VmdBoneTrack track))
                {
                    track = new VmdBoneTrack { Name = key.BoneName };
                    _boneTracks.Add(key.BoneName, track);
                }
                track.Add(key);
            }
            foreach (VmdMorphKeyframe key in MorphKeyframes)
            {
                if (!_morphTracks.TryGetValue(key.MorphName, out VmdMorphTrack track))
                {
                    track = new VmdMorphTrack { Name = key.MorphName };
                    _morphTracks.Add(key.MorphName, track);
                }
                track.Add(key);
            }
            foreach (VmdBoneTrack track in _boneTracks.Values) track.SortAndDeduplicate();
            foreach (VmdMorphTrack track in _morphTracks.Values) track.SortAndDeduplicate();
        }

        private void EnsureIkTracks()
        {
            if (_ikTracks != null) return;
            _ikTracks = new Dictionary<string, List<IkTrackKey>>(StringComparer.Ordinal);
            foreach (VmdIkKeyframe keyframe in IkKeyframes)
            {
                foreach (VmdIkState state in keyframe.States)
                {
                    if (!_ikTracks.TryGetValue(state.Name, out List<IkTrackKey> track))
                    {
                        track = new List<IkTrackKey>();
                        _ikTracks.Add(state.Name, track);
                    }
                    track.Add(new IkTrackKey { Frame = keyframe.Frame, Enabled = state.Enabled });
                }
            }
        }
    }

    internal static class TrackUtility
    {
        internal static void SortAndDeduplicate<T>(List<T> keys, Func<T, uint> getFrame)
        {
            bool sorted = true;
            for (int index = 1; index < keys.Count; index++)
            {
                if (getFrame(keys[index - 1]) <= getFrame(keys[index])) continue;
                sorted = false;
                break;
            }
            if (!sorted)
                keys.Sort((left, right) => getFrame(left).CompareTo(getFrame(right)));
            for (int index = keys.Count - 1; index > 0; index--)
            {
                if (getFrame(keys[index - 1]) == getFrame(keys[index]))
                    keys.RemoveAt(index - 1);
            }
        }
    }
}
