using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    public static class VmdParser
    {
        private const uint MaximumRecordCount = 10000000;

        public static VmdMotion Parse(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            using (FileStream stream = File.OpenRead(path))
                return Parse(stream, true);
        }

        public static VmdMotion Parse(Stream stream, bool leaveOpen = false)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The VMD stream must be readable and seekable.", nameof(stream));

            Encoding shiftJis = Encoding.GetEncoding(932);
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen))
            {
                var motion = new VmdMotion
                {
                    Header = ReadFixedString(reader, stream, 30, Encoding.ASCII, "header")
                };
                if (!motion.Header.StartsWith("Vocaloid Motion Data", StringComparison.Ordinal))
                    throw new InvalidDataException("The file is not a VMD motion.");

                int modelNameLength = motion.Header.IndexOf("0002", StringComparison.Ordinal) >= 0 ? 20 : 10;
                motion.ModelName = ReadFixedString(reader, stream, modelNameLength, shiftJis, "model name");
                var fixedNames = new FixedStringCache(20, shiftJis);

                ReadBoneFrames(reader, stream, fixedNames, motion);
                ReadMorphFrames(reader, stream, fixedNames, motion);
                if (stream.Position < stream.Length) ReadCameraFrames(reader, stream, motion);
                if (stream.Position < stream.Length) ReadLightFrames(reader, stream, motion);
                if (stream.Position < stream.Length) ReadShadowFrames(reader, stream, motion);
                if (stream.Position < stream.Length) ReadIkFrames(reader, stream, fixedNames, motion);
                motion.FinalizeData();
                return motion;
            }
        }

        private static void ReadBoneFrames(BinaryReader reader, Stream stream,
            FixedStringCache names, VmdMotion motion)
        {
            uint count = ReadCount(reader, stream, "bone");
            motion.BoneKeyframes.Capacity = CheckedCapacity(count);
            var interpolation = new byte[64];
            for (uint index = 0; index < count; index++)
            {
                EnsureRemaining(stream, 111, "bone frame");
                var key = new VmdBoneKeyframe
                {
                    BoneName = names.Read(reader, stream, 15, "bone name"),
                    Frame = reader.ReadUInt32(),
                    Translation = ReadVector3(reader),
                    Rotation = NormalizeSafe(ReadQuaternion(reader))
                };
                ReadExactly(reader, interpolation);
                key.Interpolation = DecodeBoneInterpolation(interpolation);
                motion.BoneKeyframes.Add(key);
            }
        }

        private static void ReadMorphFrames(BinaryReader reader, Stream stream,
            FixedStringCache names, VmdMotion motion)
        {
            uint count = ReadCount(reader, stream, "morph");
            motion.MorphKeyframes.Capacity = CheckedCapacity(count);
            for (uint index = 0; index < count; index++)
            {
                EnsureRemaining(stream, 23, "morph frame");
                motion.MorphKeyframes.Add(new VmdMorphKeyframe
                {
                    MorphName = names.Read(reader, stream, 15, "morph name"),
                    Frame = reader.ReadUInt32(),
                    Weight = reader.ReadSingle()
                });
            }
        }

        private static void ReadCameraFrames(BinaryReader reader, Stream stream, VmdMotion motion)
        {
            uint count = ReadCount(reader, stream, "camera");
            motion.CameraKeyframes.Capacity = CheckedCapacity(count);
            var interpolation = new byte[24];
            for (uint index = 0; index < count; index++)
            {
                EnsureRemaining(stream, 61, "camera frame");
                var key = new VmdCameraKeyframe
                {
                    Frame = reader.ReadUInt32(),
                    Distance = reader.ReadSingle(),
                    Position = ReadVector3(reader),
                    RotationRadians = ReadVector3(reader)
                };
                ReadExactly(reader, interpolation);
                Array.Copy(interpolation, key.Interpolation, interpolation.Length);
                key.ViewAngle = reader.ReadUInt32();
                key.Perspective = reader.ReadByte() == 0;
                motion.CameraKeyframes.Add(key);
            }
        }

        private static void ReadLightFrames(BinaryReader reader, Stream stream, VmdMotion motion)
        {
            uint count = ReadCount(reader, stream, "light");
            motion.LightKeyframes.Capacity = CheckedCapacity(count);
            for (uint index = 0; index < count; index++)
            {
                EnsureRemaining(stream, 28, "light frame");
                uint frame = reader.ReadUInt32();
                Vector3 color = ReadVector3(reader);
                motion.LightKeyframes.Add(new VmdLightKeyframe
                {
                    Frame = frame,
                    Color = new Color(color.x, color.y, color.z, 1f),
                    Position = ReadVector3(reader)
                });
            }
        }

        private static void ReadShadowFrames(BinaryReader reader, Stream stream, VmdMotion motion)
        {
            uint count = ReadCount(reader, stream, "shadow");
            motion.ShadowKeyframes.Capacity = CheckedCapacity(count);
            for (uint index = 0; index < count; index++)
            {
                EnsureRemaining(stream, 9, "shadow frame");
                motion.ShadowKeyframes.Add(new VmdShadowKeyframe
                {
                    Frame = reader.ReadUInt32(),
                    Mode = reader.ReadByte(),
                    Distance = reader.ReadSingle()
                });
            }
        }

        private static void ReadIkFrames(BinaryReader reader, Stream stream,
            FixedStringCache names, VmdMotion motion)
        {
            uint count = ReadCount(reader, stream, "IK");
            motion.IkKeyframes.Capacity = CheckedCapacity(count);
            for (uint index = 0; index < count; index++)
            {
                EnsureRemaining(stream, 9, "IK frame");
                var key = new VmdIkKeyframe
                {
                    Frame = reader.ReadUInt32(),
                    ModelVisible = reader.ReadByte() != 0
                };
                uint stateCount = ReadCount(reader, stream, "IK state");
                for (uint stateIndex = 0; stateIndex < stateCount; stateIndex++)
                {
                    EnsureRemaining(stream, 21, "IK state");
                    key.States.Add(new VmdIkState
                    {
                        Name = names.Read(reader, stream, 20, "IK name"),
                        Enabled = reader.ReadByte() != 0
                    });
                }
                motion.IkKeyframes.Add(key);
            }
        }

        private static VmdBoneInterpolation DecodeBoneInterpolation(byte[] data)
        {
            if (data == null || data.Length != 64)
                throw new InvalidDataException("The VMD bone interpolation block is invalid.");
            return new VmdBoneInterpolation
            {
                X = DecodeCurve(data, 0),
                Y = DecodeCurve(data, 1),
                Z = DecodeCurve(data, 2),
                Rotation = DecodeCurve(data, 3)
            };
        }

        private static MmdBezierCurve DecodeCurve(byte[] data, int channel)
        {
            const float scale = 1f / 127f;
            return new MmdBezierCurve(
                data[channel] * scale,
                data[channel + 4] * scale,
                data[channel + 8] * scale,
                data[channel + 12] * scale);
        }

        private static int CheckedCapacity(uint count)
        {
            return count <= int.MaxValue ? (int)count : throw new InvalidDataException("The VMD record count is too large.");
        }

        private static uint ReadCount(BinaryReader reader, Stream stream, string section)
        {
            EnsureRemaining(stream, 4, section + " count");
            uint count = reader.ReadUInt32();
            if (count > MaximumRecordCount)
                throw new InvalidDataException($"The VMD {section} count is invalid: {count}.");
            return count;
        }

        private static string ReadFixedString(BinaryReader reader, Stream stream, int length,
            Encoding encoding, string section)
        {
            EnsureRemaining(stream, length, section);
            byte[] bytes = reader.ReadBytes(length);
            int zero = Array.IndexOf(bytes, (byte)0);
            if (zero < 0) zero = bytes.Length;
            return encoding.GetString(bytes, 0, zero);
        }

        private static void ReadExactly(BinaryReader reader, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = reader.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private readonly struct FixedByteKey : IEquatable<FixedByteKey>
        {
            private readonly ulong _first;
            private readonly ulong _second;
            private readonly ulong _third;
            private readonly int _length;

            internal FixedByteKey(byte[] bytes, int length)
            {
                _first = Pack(bytes, 0, Math.Min(8, length));
                _second = Pack(bytes, 8, Math.Min(8, Math.Max(0, length - 8)));
                _third = Pack(bytes, 16, Math.Min(8, Math.Max(0, length - 16)));
                _length = length;
            }

            public bool Equals(FixedByteKey other)
            {
                return _length == other._length && _first == other._first &&
                       _second == other._second && _third == other._third;
            }

            public override bool Equals(object obj)
            {
                return obj is FixedByteKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _length;
                    hash = hash * 397 ^ _first.GetHashCode();
                    hash = hash * 397 ^ _second.GetHashCode();
                    return hash * 397 ^ _third.GetHashCode();
                }
            }

            private static ulong Pack(byte[] bytes, int offset, int count)
            {
                ulong value = 0;
                for (int index = 0; index < count; index++)
                    value |= (ulong)bytes[offset + index] << (index * 8);
                return value;
            }
        }

        private sealed class FixedStringCache
        {
            private readonly byte[] _buffer;
            private readonly Encoding _encoding;
            private readonly Dictionary<FixedByteKey, string> _values =
                new Dictionary<FixedByteKey, string>();

            internal FixedStringCache(int maximumLength, Encoding encoding)
            {
                _buffer = new byte[maximumLength];
                _encoding = encoding;
            }

            internal string Read(BinaryReader reader, Stream stream, int length, string section)
            {
                if (length > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
                EnsureRemaining(stream, length, section);
                int offset = 0;
                while (offset < length)
                {
                    int read = reader.Read(_buffer, offset, length - offset);
                    if (read <= 0) throw new EndOfStreamException();
                    offset += read;
                }
                int count = Array.IndexOf(_buffer, (byte)0, 0, length);
                if (count < 0) count = length;
                var key = new FixedByteKey(_buffer, count);
                if (_values.TryGetValue(key, out string value)) return value;
                value = _encoding.GetString(_buffer, 0, count);
                _values.Add(key, value);
                return value;
            }
        }

        private static void EnsureRemaining(Stream stream, long byteCount, string section)
        {
            if (byteCount < 0 || stream.Length - stream.Position < byteCount)
                throw new EndOfStreamException($"The VMD {section} is incomplete.");
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static Quaternion ReadQuaternion(BinaryReader reader)
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
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
