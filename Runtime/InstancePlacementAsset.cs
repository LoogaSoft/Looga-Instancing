using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Compact, lossless placement data. Containers copy this data before runtime edits.</summary>
    [CreateAssetMenu(menuName = "Looga/Instancing/Instance Placements", fileName = "InstancePlacements")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstancePlacementAsset")]
    public sealed class InstancePlacementAsset : ScriptableObject
    {
        // Retain the original field so existing authored assets still load.
        [SerializeField, HideInInspector] private InstanceContainer.Placement[] _placements = Array.Empty<InstanceContainer.Placement>();
        [SerializeField, HideInInspector] private byte[] _packed = Array.Empty<byte>();
        [SerializeField, HideInInspector] private int _count;
        private const int Magic = 0x4C495031;
        private const int MaximumCount = 1000000;
        private const int MaximumIdBytes = 512;

        /// <summary>Number of stored local-space placements.</summary>
        public int Count => _packed != null && _packed.Length > 0 ? _count : _placements?.Length ?? 0;
        /// <summary>Bytes in the compact payload, excluding Unity asset metadata.</summary>
        public int PackedBytes => _packed?.Length ?? 0;

        /// <summary>Replace the snapshot with validated lossless TRS data and unchanged IDs.</summary>
        public void SetPlacements(InstanceContainer.Placement[] placements)
        {
            var copy = InstanceContainer.CopyValidated(placements);
            if (copy.Length > MaximumCount)
            {
                throw new ArgumentException("A placement snapshot supports at most one million records.");
            }
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Magic);
            writer.Write(copy.Length);
            foreach (var item in copy)
            {
                byte[] id = Encoding.UTF8.GetBytes(item.Id);
                if (id.Length > MaximumIdBytes)
                {
                    throw new ArgumentException("A placement ID must fit in 512 UTF-8 bytes.");
                }
                writer.Write((ushort)id.Length);
                writer.Write(id);
                Write(writer, item.Position);
                Write(writer, item.EulerAngles);
                Write(writer, item.Scale);
            }
            _packed = stream.ToArray();
            _count = copy.Length;
            _placements = Array.Empty<InstanceContainer.Placement>();
        }

        /// <summary>Return an independent snapshot. Legacy array assets remain readable.</summary>
        public InstanceContainer.Placement[] CopyPlacements()
        {
            if (_packed == null || _packed.Length == 0) return InstanceContainer.CopyValidated(_placements);
            return Decode(_packed);
        }

        /// <summary>Export portable bytes. The caller owns disk storage and source identity.</summary>
        public byte[] ExportBinary()
        {
            if (_packed == null || _packed.Length == 0)
            {
                SetPlacements(_placements);
            }
            return (byte[])_packed.Clone();
        }

        /// <summary>Import only after complete validation. Invalid payloads leave the asset unchanged.</summary>
        public void ImportBinary(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }
            var copy = (byte[])bytes.Clone();
            var placements = Decode(copy);
            _packed = copy;
            _count = placements.Length;
            _placements = Array.Empty<InstanceContainer.Placement>();
        }

        private static InstanceContainer.Placement[] Decode(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
            if (bytes.Length < 8 || reader.ReadInt32() != Magic)
            {
                throw new InvalidDataException("Placement snapshot header is invalid.");
            }
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumCount || count > (bytes.Length - 8) / 39)
            {
                throw new InvalidDataException("Placement count exceeds the payload or limit.");
            }
            var encoding = new UTF8Encoding(false, true);
            var result = new InstanceContainer.Placement[count];
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadUInt16();
                if (length < 1 || length > MaximumIdBytes || length + 36 > stream.Length - stream.Position)
                {
                    throw new InvalidDataException("Placement ID length is invalid.");
                }
                string id = encoding.GetString(reader.ReadBytes(length));
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidDataException("Placement IDs must not be empty or whitespace.");
                }
                result[i] = new InstanceContainer.Placement
                {
                    Id = id,
                    Position = Read(reader), EulerAngles = Read(reader), Scale = Read(reader)
                };
            }
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Placement snapshot contains trailing bytes.");
            }
            return InstanceContainer.CopyValidated(result);
        }

        private static Vector3 Read(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static void Write(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }
    }
}
