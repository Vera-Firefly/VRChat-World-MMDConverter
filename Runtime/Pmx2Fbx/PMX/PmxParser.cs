using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    public static class PmxParser
    {
        private const int MaximumElementCount = 100000000;

        public static PmxModel Parse(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            using (FileStream stream = File.OpenRead(path))
                return Parse(stream, true);
        }

        public static PmxModel Parse(Stream stream, bool leaveOpen = false)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("The PMX stream is not readable.", nameof(stream));

            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen))
            {
                byte[] signature = ReadBytes(reader, 4, "signature");
                if (signature[0] != (byte)'P' || signature[1] != (byte)'M' ||
                    signature[2] != (byte)'X' || signature[3] != (byte)' ')
                    throw new InvalidDataException("The file is not a PMX model.");

                var model = new PmxModel { Version = reader.ReadSingle() };
                if (model.Version < 1.99f || model.Version > 2.11f)
                    throw new InvalidDataException($"Unsupported PMX version {model.Version}.");

                int globalCount = reader.ReadByte();
                if (globalCount < 8 || globalCount > 64)
                    throw new InvalidDataException("The PMX global header is invalid.");
                byte[] globals = ReadBytes(reader, globalCount, "global header");
                var context = new ReaderContext(
                    reader,
                    globals[0] == 0 ? Encoding.Unicode : Encoding.UTF8,
                    globals[1], globals[2], globals[3], globals[4], globals[5], globals[6], globals[7]);
                context.ValidateIndexSizes();

                model.NameJapanese = context.ReadString("model name");
                model.NameEnglish = context.ReadString("model English name");
                model.CommentJapanese = context.ReadString("model comment");
                model.CommentEnglish = context.ReadString("model English comment");

                SkipVertices(context);
                SkipFaces(context);
                SkipTextures(context);
                SkipMaterials(context);
                ReadBones(context, model);
                ReadMorphs(context, model);
                model.ValidateHierarchy();
                return model;
            }
        }

        private static void SkipVertices(ReaderContext context)
        {
            int count = context.ReadCount("vertex");
            for (int index = 0; index < count; index++)
            {
                context.ReadVector3();
                context.ReadVector3();
                context.ReadVector2();
                for (int uv = 0; uv < context.AdditionalUvCount; uv++) context.ReadVector4();

                byte weightType = context.Reader.ReadByte();
                switch (weightType)
                {
                    case 0: // BDEF1
                        context.ReadBoneIndex();
                        break;
                    case 1: // BDEF2
                        context.ReadBoneIndex();
                        context.ReadBoneIndex();
                        context.Reader.ReadSingle();
                        break;
                    case 2: // BDEF4
                    case 4: // QDEF
                        for (int bone = 0; bone < 4; bone++) context.ReadBoneIndex();
                        for (int weight = 0; weight < 4; weight++) context.Reader.ReadSingle();
                        break;
                    case 3: // SDEF
                        context.ReadBoneIndex();
                        context.ReadBoneIndex();
                        context.Reader.ReadSingle();
                        context.ReadVector3();
                        context.ReadVector3();
                        context.ReadVector3();
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported PMX vertex weight type {weightType}.");
                }
                context.Reader.ReadSingle(); // edge scale
            }
        }

        private static void SkipFaces(ReaderContext context)
        {
            int count = context.ReadCount("surface index");
            for (int index = 0; index < count; index++) context.ReadVertexIndex();
        }

        private static void SkipTextures(ReaderContext context)
        {
            int count = context.ReadCount("texture");
            for (int index = 0; index < count; index++) context.ReadString("texture path");
        }

        private static void SkipMaterials(ReaderContext context)
        {
            int count = context.ReadCount("material");
            for (int index = 0; index < count; index++)
            {
                context.ReadString("material name");
                context.ReadString("material English name");
                context.ReadVector4(); // diffuse
                context.ReadVector3(); // specular
                context.Reader.ReadSingle();
                context.ReadVector3(); // ambient
                context.Reader.ReadByte();
                context.ReadVector4(); // edge color
                context.Reader.ReadSingle();
                context.ReadTextureIndex();
                context.ReadTextureIndex();
                context.Reader.ReadByte();
                byte sharedToon = context.Reader.ReadByte();
                if (sharedToon == 0) context.ReadTextureIndex();
                else if (sharedToon == 1) context.Reader.ReadByte();
                else throw new InvalidDataException("The PMX shared toon flag is invalid.");
                context.ReadString("material memo");
                context.Reader.ReadInt32();
            }
        }

        private static void ReadBones(ReaderContext context, PmxModel model)
        {
            int count = context.ReadCount("bone");
            model.Bones.Capacity = count;
            for (int index = 0; index < count; index++)
            {
                var bone = new PmxBone
                {
                    Index = index,
                    NameJapanese = context.ReadString("bone name"),
                    NameEnglish = context.ReadString("bone English name"),
                    Position = context.ReadVector3(),
                    ParentIndex = context.ReadBoneIndex(),
                    TransformLayer = context.Reader.ReadInt32(),
                    Flags = (PmxBoneFlags)context.Reader.ReadUInt16()
                };

                if (bone.HasFlag(PmxBoneFlags.TailUsesBoneIndex))
                    bone.TailBoneIndex = context.ReadBoneIndex();
                else
                    bone.TailOffset = context.ReadVector3();

                if (bone.HasFlag(PmxBoneFlags.InheritRotation) ||
                    bone.HasFlag(PmxBoneFlags.InheritTranslation))
                {
                    bone.InheritParentIndex = context.ReadBoneIndex();
                    bone.InheritWeight = context.Reader.ReadSingle();
                }

                if (bone.HasFlag(PmxBoneFlags.FixedAxis))
                    bone.FixedAxis = SafeNormalize(context.ReadVector3(), Vector3.right);

                if (bone.HasFlag(PmxBoneFlags.LocalAxes))
                {
                    bone.LocalAxisX = SafeNormalize(context.ReadVector3(), Vector3.right);
                    bone.LocalAxisZ = SafeNormalize(context.ReadVector3(), Vector3.forward);
                    bone.LocalAxisY = SafeNormalize(Vector3.Cross(bone.LocalAxisZ, bone.LocalAxisX), Vector3.up);
                    bone.LocalAxisZ = SafeNormalize(Vector3.Cross(bone.LocalAxisX, bone.LocalAxisY), bone.LocalAxisZ);
                }

                if (bone.HasFlag(PmxBoneFlags.ExternalParent))
                    bone.ExternalParentKey = context.Reader.ReadInt32();

                if (bone.HasFlag(PmxBoneFlags.Ik))
                {
                    var ik = new PmxIkDefinition
                    {
                        TargetBoneIndex = context.ReadBoneIndex(),
                        IterationCount = context.Reader.ReadInt32(),
                        AngleLimit = context.Reader.ReadSingle()
                    };
                    int linkCount = context.ReadCount("IK link");
                    for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
                    {
                        var link = new PmxIkLink
                        {
                            BoneIndex = context.ReadBoneIndex(),
                            HasLimits = context.Reader.ReadByte() != 0
                        };
                        if (link.HasLimits)
                        {
                            link.MinimumAngle = context.ReadVector3();
                            link.MaximumAngle = context.ReadVector3();
                        }
                        ik.Links.Add(link);
                    }
                    bone.Ik = ik;
                }
                model.Bones.Add(bone);
            }
        }

        private static void ReadMorphs(ReaderContext context, PmxModel model)
        {
            int count = context.ReadCount("morph");
            model.Morphs.Capacity = count;
            for (int index = 0; index < count; index++)
            {
                var morph = new PmxMorphDefinition
                {
                    NameJapanese = context.ReadString("morph name"),
                    NameEnglish = context.ReadString("morph English name"),
                    Panel = context.Reader.ReadByte(),
                    Type = (PmxMorphType)context.Reader.ReadByte(),
                    OffsetCount = context.ReadCount("morph offset")
                };

                for (int offset = 0; offset < morph.OffsetCount; offset++)
                    ReadMorphOffset(context, morph);
                model.Morphs.Add(morph);
            }
        }

        private static void ReadMorphOffset(ReaderContext context, PmxMorphDefinition morph)
        {
            switch (morph.Type)
            {
                case PmxMorphType.Group:
                case PmxMorphType.Flip:
                    morph.GroupOffsets.Add(new PmxGroupMorphOffset
                    {
                        MorphIndex = context.ReadMorphIndex(),
                        Weight = context.Reader.ReadSingle()
                    });
                    break;
                case PmxMorphType.Vertex:
                    context.ReadVertexIndex();
                    context.ReadVector3();
                    break;
                case PmxMorphType.Bone:
                    morph.BoneOffsets.Add(new PmxBoneMorphOffset
                    {
                        BoneIndex = context.ReadBoneIndex(),
                        Translation = context.ReadVector3(),
                        Rotation = context.ReadQuaternion()
                    });
                    break;
                case PmxMorphType.Uv:
                case PmxMorphType.AdditionalUv1:
                case PmxMorphType.AdditionalUv2:
                case PmxMorphType.AdditionalUv3:
                case PmxMorphType.AdditionalUv4:
                    context.ReadVertexIndex();
                    context.ReadVector4();
                    break;
                case PmxMorphType.Material:
                    context.ReadMaterialIndex();
                    context.Reader.ReadByte();
                    context.ReadVector4();
                    context.ReadVector3();
                    context.Reader.ReadSingle();
                    context.ReadVector3();
                    context.ReadVector4();
                    context.Reader.ReadSingle();
                    context.ReadVector4();
                    context.ReadVector4();
                    context.ReadVector4();
                    break;
                case PmxMorphType.Impulse:
                    context.ReadRigidBodyIndex();
                    context.Reader.ReadByte();
                    context.ReadVector3();
                    context.ReadVector3();
                    break;
                default:
                    throw new InvalidDataException($"Unsupported PMX morph type {(byte)morph.Type}.");
            }
        }

        private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        {
            return value.sqrMagnitude > 1e-12f ? value.normalized : fallback;
        }

        private static byte[] ReadBytes(BinaryReader reader, int count, string label)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException($"The PMX {label} is incomplete.");
            return bytes;
        }

        private sealed class ReaderContext
        {
            internal readonly BinaryReader Reader;
            internal readonly Encoding TextEncoding;
            internal readonly int AdditionalUvCount;
            private readonly int _vertexIndexSize;
            private readonly int _textureIndexSize;
            private readonly int _materialIndexSize;
            private readonly int _boneIndexSize;
            private readonly int _morphIndexSize;
            private readonly int _rigidBodyIndexSize;

            internal ReaderContext(BinaryReader reader, Encoding textEncoding, int additionalUvCount,
                int vertexIndexSize, int textureIndexSize, int materialIndexSize,
                int boneIndexSize, int morphIndexSize, int rigidBodyIndexSize)
            {
                Reader = reader;
                TextEncoding = textEncoding;
                AdditionalUvCount = additionalUvCount;
                _vertexIndexSize = vertexIndexSize;
                _textureIndexSize = textureIndexSize;
                _materialIndexSize = materialIndexSize;
                _boneIndexSize = boneIndexSize;
                _morphIndexSize = morphIndexSize;
                _rigidBodyIndexSize = rigidBodyIndexSize;
            }

            internal void ValidateIndexSizes()
            {
                if (AdditionalUvCount < 0 || AdditionalUvCount > 4)
                    throw new InvalidDataException("The PMX additional UV count is invalid.");
                ValidateIndexSize(_vertexIndexSize, "vertex");
                ValidateIndexSize(_textureIndexSize, "texture");
                ValidateIndexSize(_materialIndexSize, "material");
                ValidateIndexSize(_boneIndexSize, "bone");
                ValidateIndexSize(_morphIndexSize, "morph");
                ValidateIndexSize(_rigidBodyIndexSize, "rigid body");
            }

            internal int ReadCount(string label)
            {
                int count = Reader.ReadInt32();
                if (count < 0 || count > MaximumElementCount)
                    throw new InvalidDataException($"The PMX {label} count is invalid: {count}.");
                return count;
            }

            internal string ReadString(string label)
            {
                int byteCount = Reader.ReadInt32();
                if (byteCount < 0 || byteCount > MaximumElementCount)
                    throw new InvalidDataException($"The PMX {label} length is invalid: {byteCount}.");
                return TextEncoding.GetString(ReadBytes(Reader, byteCount, label));
            }

            internal Vector2 ReadVector2()
            {
                return new Vector2(Reader.ReadSingle(), Reader.ReadSingle());
            }

            internal Vector3 ReadVector3()
            {
                return new Vector3(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle());
            }

            internal Vector4 ReadVector4()
            {
                return new Vector4(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle());
            }

            internal Quaternion ReadQuaternion()
            {
                return new Quaternion(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle());
            }

            internal int ReadVertexIndex() => ReadIndex(_vertexIndexSize, false);
            internal int ReadTextureIndex() => ReadIndex(_textureIndexSize, true);
            internal int ReadMaterialIndex() => ReadIndex(_materialIndexSize, true);
            internal int ReadBoneIndex() => ReadIndex(_boneIndexSize, true);
            internal int ReadMorphIndex() => ReadIndex(_morphIndexSize, true);
            internal int ReadRigidBodyIndex() => ReadIndex(_rigidBodyIndexSize, true);

            private int ReadIndex(int size, bool signed)
            {
                switch (size)
                {
                    case 1: return signed ? Reader.ReadSByte() : (int)Reader.ReadByte();
                    case 2: return signed ? Reader.ReadInt16() : (int)Reader.ReadUInt16();
                    case 4: return Reader.ReadInt32();
                    default: throw new InvalidDataException($"Unsupported PMX index size {size}.");
                }
            }

            private static void ValidateIndexSize(int size, string label)
            {
                if (size != 1 && size != 2 && size != 4)
                    throw new InvalidDataException($"The PMX {label} index size is invalid: {size}.");
            }
        }
    }
}
