using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    public enum PmxModelCacheHit
    {
        Parsed,
        Memory,
        Disk
    }

    /// <summary>
    /// Two-level cache for the immutable PMX data used by animation conversion.
    /// Memory entries live until the editor domain reloads; disk entries live in
    /// Library and are keyed by PMX content plus the binary cache format version.
    /// </summary>
    public static class PmxModelCache
    {
        private const int CacheFormatVersion = 2;
        private const int MaximumCachedElements = 10000000;
        private const uint CacheMagic = 0x31434D50; // PMC1

        private static readonly Dictionary<string, PmxModel> ModelsByKey =
            new Dictionary<string, PmxModel>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PathFingerprint> FingerprintsByPath =
            new Dictionary<string, PathFingerprint>(StringComparer.OrdinalIgnoreCase);

        public static PmxModel Load(string path)
        {
            return Load(path, out _);
        }

        public static PmxModel Load(string path, out PmxModelCacheHit hit)
        {
            string physicalPath = NormalizePath(path);
            CacheIdentity identity = GetIdentity(physicalPath);
            if (ModelsByKey.TryGetValue(identity.Key, out PmxModel memoryModel))
            {
                hit = PmxModelCacheHit.Memory;
                Debug.Log($"[PMX2FBX C#] PMX memory cache hit: {Path.GetFileName(physicalPath)}");
                return memoryModel;
            }

            string cachePath = GetDiskCachePath(identity.Key);
            if (TryRead(cachePath, identity.Hash, out PmxModel diskModel))
            {
                ModelsByKey[identity.Key] = diskModel;
                hit = PmxModelCacheHit.Disk;
                Debug.Log($"[PMX2FBX C#] PMX disk cache hit: {Path.GetFileName(physicalPath)}");
                return diskModel;
            }

            PmxModel parsed = PmxParser.Parse(physicalPath);
            WarmModel(parsed);
            ModelsByKey[identity.Key] = parsed;
            TryWrite(cachePath, identity.Hash, parsed);
            hit = PmxModelCacheHit.Parsed;
            Debug.Log($"[PMX2FBX C#] Parsed PMX and updated cache: {Path.GetFileName(physicalPath)}");
            return parsed;
        }

        public static void ClearMemory()
        {
            ModelsByKey.Clear();
            FingerprintsByPath.Clear();
        }

        public static string DiskCacheDirectory => GetDiskCacheDirectory();

        public static void GetDiskCacheStats(out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            string directory = GetDiskCacheDirectory();
            if (!Directory.Exists(directory)) return;

            foreach (string file in Directory.GetFiles(directory, "*.bin", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                fileCount++;
                totalBytes += info.Length;
            }
        }

        public static void ClearAll()
        {
            ClearMemory();
            string directory = GetDiskCacheDirectory();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string libraryRoot = Path.GetFullPath(Path.Combine(projectRoot, "Library"));
            string libraryPrefix = libraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                   Path.DirectorySeparatorChar;
            if (!directory.StartsWith(libraryPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The PMX cache path is outside the project Library directory.");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static void WarmModel(PmxModel model)
        {
            model.ValidateHierarchy();
            model.GetTransformOrder();
            model.GetRestLocalPositions();
            if (model.Bones.Count > 0)
                model.FindBone(model.Bones[0].NameJapanese);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            string physicalPath = PmxAnimationClipBaker.ToPhysicalPath(path);
            physicalPath = Path.GetFullPath(physicalPath);
            if (!File.Exists(physicalPath)) throw new FileNotFoundException("The PMX file does not exist.", physicalPath);
            return physicalPath;
        }

        private static CacheIdentity GetIdentity(string physicalPath)
        {
            var info = new FileInfo(physicalPath);
            var current = new PathFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, string.Empty);
            if (FingerprintsByPath.TryGetValue(physicalPath, out PathFingerprint known) &&
                known.Length == current.Length && known.LastWriteTicks == current.LastWriteTicks)
                return new CacheIdentity(known.Hash, BuildKey(known.Hash));

            string hash;
            using (var sha256 = SHA256.Create())
            using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       1024 * 1024, FileOptions.SequentialScan))
                hash = ToHex(sha256.ComputeHash(stream));

            FingerprintsByPath[physicalPath] =
                new PathFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, hash);
            return new CacheIdentity(hash, BuildKey(hash));
        }

        private static string BuildKey(string hash)
        {
            return $"v{CacheFormatVersion}-{hash}";
        }

        private static string GetDiskCachePath(string key)
        {
            return Path.Combine(GetDiskCacheDirectory(), key + ".bin");
        }

        private static string GetDiskCacheDirectory()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, "Library", "PMX2FBXCache"));
        }

        private static bool TryRead(string cachePath, string expectedHash, out PmxModel model)
        {
            model = null;
            if (!File.Exists(cachePath)) return false;
            try
            {
                using (var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                           256 * 1024, FileOptions.SequentialScan))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
                {
                    if (reader.ReadUInt32() != CacheMagic || reader.ReadInt32() != CacheFormatVersion)
                        return false;
                    if (!string.Equals(reader.ReadString(), expectedHash, StringComparison.Ordinal))
                        return false;
                    model = ReadModel(reader);
                    if (stream.Position != stream.Length)
                        throw new InvalidDataException("The PMX cache contains trailing data.");
                    WarmModel(model);
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException ||
                                               exception is InvalidDataException ||
                                               exception is ArgumentException ||
                                               exception is EndOfStreamException ||
                                               exception is InvalidOperationException ||
                                               exception is IndexOutOfRangeException)
            {
                Debug.LogWarning($"[PMX2FBX C#] Ignoring invalid PMX cache '{cachePath}': {exception.Message}");
                model = null;
                return false;
            }
        }

        private static void TryWrite(string cachePath, string hash, PmxModel model)
        {
            string directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            string temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 256 * 1024, FileOptions.SequentialScan))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
                {
                    writer.Write(CacheMagic);
                    writer.Write(CacheFormatVersion);
                    writer.Write(hash);
                    WriteModel(writer, model);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(cachePath)) File.Replace(temporaryPath, cachePath, null);
                else File.Move(temporaryPath, cachePath);
            }
            catch (Exception exception) when (exception is IOException ||
                                               exception is UnauthorizedAccessException ||
                                               exception is ArgumentException)
            {
                Debug.LogWarning($"[PMX2FBX C#] Could not write PMX cache: {exception.Message}");
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void WriteModel(BinaryWriter writer, PmxModel model)
        {
            writer.Write(model.Version);
            writer.Write(model.NameJapanese ?? string.Empty);
            writer.Write(model.NameEnglish ?? string.Empty);
            writer.Write(model.CommentJapanese ?? string.Empty);
            writer.Write(model.CommentEnglish ?? string.Empty);
            writer.Write(model.Bones.Count);
            foreach (PmxBone bone in model.Bones)
            {
                writer.Write(bone.Index);
                writer.Write(bone.NameJapanese ?? string.Empty);
                writer.Write(bone.NameEnglish ?? string.Empty);
                WriteVector3(writer, bone.Position);
                writer.Write(bone.ParentIndex);
                writer.Write(bone.TransformLayer);
                writer.Write((ushort)bone.Flags);
                writer.Write(bone.TailBoneIndex);
                WriteVector3(writer, bone.TailOffset);
                writer.Write(bone.InheritParentIndex);
                writer.Write(bone.InheritWeight);
                WriteVector3(writer, bone.FixedAxis);
                WriteVector3(writer, bone.LocalAxisX);
                WriteVector3(writer, bone.LocalAxisY);
                WriteVector3(writer, bone.LocalAxisZ);
                writer.Write(bone.ExternalParentKey);
                writer.Write(bone.Ik != null);
                if (bone.Ik == null) continue;
                writer.Write(bone.Ik.TargetBoneIndex);
                writer.Write(bone.Ik.IterationCount);
                writer.Write(bone.Ik.AngleLimit);
                writer.Write(bone.Ik.Links.Count);
                foreach (PmxIkLink link in bone.Ik.Links)
                {
                    writer.Write(link.BoneIndex);
                    writer.Write(link.HasLimits);
                    WriteVector3(writer, link.MinimumAngle);
                    WriteVector3(writer, link.MaximumAngle);
                }
            }

            writer.Write(model.Morphs.Count);
            foreach (PmxMorphDefinition morph in model.Morphs)
            {
                writer.Write(morph.NameJapanese ?? string.Empty);
                writer.Write(morph.NameEnglish ?? string.Empty);
                writer.Write(morph.Panel);
                writer.Write((byte)morph.Type);
                writer.Write(morph.OffsetCount);
                writer.Write(morph.GroupOffsets.Count);
                foreach (PmxGroupMorphOffset offset in morph.GroupOffsets)
                {
                    writer.Write(offset.MorphIndex);
                    writer.Write(offset.Weight);
                }
                writer.Write(morph.BoneOffsets.Count);
                foreach (PmxBoneMorphOffset offset in morph.BoneOffsets)
                {
                    writer.Write(offset.BoneIndex);
                    WriteVector3(writer, offset.Translation);
                    WriteQuaternion(writer, offset.Rotation);
                }
            }
        }

        private static PmxModel ReadModel(BinaryReader reader)
        {
            var model = new PmxModel
            {
                Version = reader.ReadSingle(),
                NameJapanese = reader.ReadString(),
                NameEnglish = reader.ReadString(),
                CommentJapanese = reader.ReadString(),
                CommentEnglish = reader.ReadString()
            };
            int boneCount = ReadCount(reader, "bone");
            model.Bones.Capacity = boneCount;
            for (int index = 0; index < boneCount; index++)
            {
                var bone = new PmxBone
                {
                    Index = reader.ReadInt32(),
                    NameJapanese = reader.ReadString(),
                    NameEnglish = reader.ReadString(),
                    Position = ReadVector3(reader),
                    ParentIndex = reader.ReadInt32(),
                    TransformLayer = reader.ReadInt32(),
                    Flags = (PmxBoneFlags)reader.ReadUInt16(),
                    TailBoneIndex = reader.ReadInt32(),
                    TailOffset = ReadVector3(reader),
                    InheritParentIndex = reader.ReadInt32(),
                    InheritWeight = reader.ReadSingle(),
                    FixedAxis = ReadVector3(reader),
                    LocalAxisX = ReadVector3(reader),
                    LocalAxisY = ReadVector3(reader),
                    LocalAxisZ = ReadVector3(reader),
                    ExternalParentKey = reader.ReadInt32()
                };
                if (reader.ReadBoolean())
                {
                    bone.Ik = new PmxIkDefinition
                    {
                        TargetBoneIndex = reader.ReadInt32(),
                        IterationCount = reader.ReadInt32(),
                        AngleLimit = reader.ReadSingle()
                    };
                    int linkCount = ReadCount(reader, "IK link");
                    for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
                        bone.Ik.Links.Add(new PmxIkLink
                        {
                            BoneIndex = reader.ReadInt32(),
                            HasLimits = reader.ReadBoolean(),
                            MinimumAngle = ReadVector3(reader),
                            MaximumAngle = ReadVector3(reader)
                        });
                }
                model.Bones.Add(bone);
            }

            int morphCount = ReadCount(reader, "morph");
            model.Morphs.Capacity = morphCount;
            for (int index = 0; index < morphCount; index++)
            {
                var morph = new PmxMorphDefinition
                {
                    NameJapanese = reader.ReadString(),
                    NameEnglish = reader.ReadString(),
                    Panel = reader.ReadByte(),
                    Type = (PmxMorphType)reader.ReadByte(),
                    OffsetCount = ReadCount(reader, "morph offset")
                };
                int groupCount = ReadCount(reader, "group morph offset");
                for (int group = 0; group < groupCount; group++)
                    morph.GroupOffsets.Add(new PmxGroupMorphOffset
                    {
                        MorphIndex = reader.ReadInt32(),
                        Weight = reader.ReadSingle()
                    });
                int boneOffsetCount = ReadCount(reader, "bone morph offset");
                for (int boneOffset = 0; boneOffset < boneOffsetCount; boneOffset++)
                    morph.BoneOffsets.Add(new PmxBoneMorphOffset
                    {
                        BoneIndex = reader.ReadInt32(),
                        Translation = ReadVector3(reader),
                        Rotation = ReadQuaternion(reader)
                    });
                model.Morphs.Add(morph);
            }
            return model;
        }

        private static int ReadCount(BinaryReader reader, string label)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumCachedElements)
                throw new InvalidDataException($"The cached PMX {label} count is invalid: {count}.");
            return count;
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader reader)
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++) builder.Append(bytes[index].ToString("x2"));
            return builder.ToString();
        }

        private readonly struct CacheIdentity
        {
            internal readonly string Hash;
            internal readonly string Key;

            internal CacheIdentity(string hash, string key)
            {
                Hash = hash;
                Key = key;
            }
        }

        private readonly struct PathFingerprint
        {
            internal readonly long Length;
            internal readonly long LastWriteTicks;
            internal readonly string Hash;

            internal PathFingerprint(long length, long lastWriteTicks, string hash)
            {
                Length = length;
                LastWriteTicks = lastWriteTicks;
                Hash = hash;
            }
        }
    }
}
