using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "VegetationPageRecord")]
    public readonly struct VegetationPageRecord
    {
        public readonly string Id;
        public readonly int Prototype;
        public readonly Matrix4x4 LocalTransform;
        public readonly InstanceAppearance Appearance;
        public VegetationPageRecord(int prototype, Matrix4x4 localTransform, InstanceAppearance appearance, string id = null)
        { Id = id; Prototype = prototype; LocalTransform = localTransform; Appearance = appearance; }
    }

    public readonly struct VegetationPageRequest
    {
        public readonly Vector2Int Cell;
        public readonly bool Trees;
        public readonly int MaximumDiskBytes;
        public readonly int MaximumDecompressedBytes;
        public VegetationPageRequest(Vector2Int cell, bool trees, int maximumDiskBytes, int maximumDecompressedBytes)
        {
            if (maximumDiskBytes < 1 || maximumDecompressedBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumDiskBytes));
            Cell = cell; Trees = trees; MaximumDiskBytes = maximumDiskBytes; MaximumDecompressedBytes = maximumDecompressedBytes;
        }
    }

    public readonly struct VegetationPageLoadResult
    {
        public readonly VegetationPageRecord[] Records;
        public readonly int DiskBytes;
        public readonly int DecompressedBytes;
        public VegetationPageLoadResult(VegetationPageRecord[] records, int diskBytes, int decompressedBytes)
        { Records = records ?? Array.Empty<VegetationPageRecord>(); DiskBytes = diskBytes; DecompressedBytes = decompressedBytes; }
    }

    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "IVegetationPageSource")]
    public interface IVegetationPageSource
    {
        Task<VegetationPageRecord[]> LoadAsync(Vector2Int cell, bool trees, CancellationToken cancellation);
    }

    public interface IBudgetedVegetationPageSource : IVegetationPageSource
    {
        Task<VegetationPageLoadResult> LoadAsync(VegetationPageRequest request, CancellationToken cancellation);
    }

    /// <summary>Versioned placement pages. Version 3 uses bounded Deflate decompression.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "FileVegetationPageSource")]
    public sealed class FileVegetationPageSource : IBudgetedVegetationPageSource
    {
        private const int Magic = 0x4C565031;
        private const int RecordBytes = 116;
        private const int MaximumIdBytes = 512;
        private static readonly UTF8Encoding Encoding = new(false, true);
        private readonly string _directory;
        private readonly long _revision;
        private readonly int _maximumRecords;

        public FileVegetationPageSource(string directory, long revision, int maximumRecords = 262144)
        {
            if (string.IsNullOrWhiteSpace(directory) || maximumRecords < 1 || maximumRecords > (int.MaxValue - 32) / (RecordBytes + MaximumIdBytes + 2))
                throw new ArgumentException("A page directory and positive record limit are required.");
            _directory = Path.GetFullPath(directory); _revision = revision; _maximumRecords = maximumRecords;
        }

        public async Task<VegetationPageRecord[]> LoadAsync(Vector2Int cell, bool trees, CancellationToken cancellation)
        {
            int maximum = 32 + _maximumRecords * (RecordBytes + MaximumIdBytes + 2);
            VegetationPageLoadResult result = await LoadAsync(new VegetationPageRequest(cell, trees, maximum, maximum), cancellation).ConfigureAwait(false);
            return result.Records;
        }

        public Task<VegetationPageLoadResult> LoadAsync(VegetationPageRequest request, CancellationToken cancellation)
        {
            string path = GetPath(_directory, request.Cell, request.Trees);
            return Task.Run(async () =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                long formatMaximum = 32L + (long)_maximumRecords * (RecordBytes + MaximumIdBytes + 2);
                if (stream.Length < 20 || stream.Length > Math.Min(formatMaximum, request.MaximumDiskBytes))
                    throw new InvalidDataException("Vegetation page length exceeds its I/O budget or declared limit.");
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellation).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    offset += count;
                }
                using var header = new BinaryReader(new MemoryStream(bytes, false));
                int magic = header.ReadInt32();
                int version = header.ReadInt32();
                if (magic != Magic || version < 1 || version > 3 || header.ReadInt64() != _revision)
                    throw new InvalidDataException("Vegetation page format or source revision does not match.");
                int records = header.ReadInt32();
                if (records < 0 || records > _maximumRecords) throw new InvalidDataException("Vegetation page record count is invalid.");
                byte[] payload;
                int decompressedBytes;
                if (version == 3)
                {
                    int rawLength = header.ReadInt32();
                    int compressedLength = header.ReadInt32();
                    if (rawLength < 0 || rawLength > request.MaximumDecompressedBytes || compressedLength < 0 ||
                        compressedLength != header.BaseStream.Length - header.BaseStream.Position)
                        throw new InvalidDataException("Vegetation page compression lengths exceed their budget.");
                    payload = new byte[rawLength];
                    using var compressed = new DeflateStream(header.BaseStream, CompressionMode.Decompress, true);
                    int read = 0;
                    while (read < payload.Length)
                    {
                        int count = compressed.Read(payload, read, payload.Length - read);
                        if (count == 0) throw new EndOfStreamException();
                        read += count;
                    }
                    if (compressed.ReadByte() != -1) throw new InvalidDataException("Vegetation page expands beyond its declared length.");
                    decompressedBytes = rawLength;
                }
                else
                {
                    int payloadLength = bytes.Length - 20;
                    if (payloadLength > request.MaximumDecompressedBytes) throw new InvalidDataException("Vegetation page exceeds its decompression budget.");
                    payload = new byte[payloadLength];
                    Buffer.BlockCopy(bytes, 20, payload, 0, payloadLength);
                    decompressedBytes = payloadLength;
                }
                return new VegetationPageLoadResult(Decode(payload, version, records, cancellation), bytes.Length, decompressedBytes);
            }, cancellation);
        }

        public static void Write(string directory, Vector2Int cell, bool trees, long revision, VegetationPageRecord[] records)
            => Write(directory, cell, trees, revision, records, true);

        public static void Write(string directory, Vector2Int cell, bool trees, long revision, VegetationPageRecord[] records, bool compressed)
        {
            ValidateRecords(records); Directory.CreateDirectory(directory); byte[] payload = Encode(records);
            using var writer = new BinaryWriter(File.Create(GetPath(directory, cell, trees)));
            writer.Write(Magic); writer.Write(compressed ? 3 : 2); writer.Write(revision); writer.Write(records.Length);
            if (compressed)
            {
                using var output = new MemoryStream();
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) deflate.Write(payload, 0, payload.Length);
                byte[] encoded = output.ToArray();
                writer.Write(payload.Length); writer.Write(encoded.Length); writer.Write(encoded);
            }
            else writer.Write(payload);
        }

        private static byte[] Encode(VegetationPageRecord[] records)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding, true);
            foreach (VegetationPageRecord record in records)
            {
                writer.Write(record.Prototype);
                for (int i = 0; i < 16; i++) writer.Write(record.LocalTransform[i]);
                WriteVector(writer, record.Appearance.Tint); WriteVector(writer, record.Appearance.LightmapColor); WriteVector(writer, record.Appearance.Custom);
                byte[] identity = record.Id == null ? Array.Empty<byte>() : Encoding.GetBytes(record.Id);
                writer.Write((ushort)identity.Length); writer.Write(identity);
            }
            return stream.ToArray();
        }

        private static VegetationPageRecord[] Decode(byte[] payload, int version, int records, CancellationToken cancellation)
        {
            int minimum = records * RecordBytes;
            if (payload.Length < minimum || (version == 1 && payload.Length != minimum)) throw new InvalidDataException("Vegetation page record payload is invalid.");
            var result = new VegetationPageRecord[records]; var identities = new HashSet<string>(StringComparer.Ordinal);
            using var reader = new BinaryReader(new MemoryStream(payload, false), Encoding);
            for (int i = 0; i < records; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                int prototype = reader.ReadInt32(); Matrix4x4 matrix = default;
                for (int j = 0; j < 16; j++) matrix[j] = reader.ReadSingle();
                Color tint = ReadVector(reader); Color lightmap = ReadVector(reader); Vector4 custom = ReadVector(reader); string id = null;
                if (version >= 2)
                {
                    int length = reader.ReadUInt16();
                    if (length > MaximumIdBytes || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Vegetation identity length is invalid.");
                    if (length > 0)
                    {
                        id = Encoding.GetString(reader.ReadBytes(length));
                        if (string.IsNullOrWhiteSpace(id) || !identities.Add(id)) throw new InvalidDataException("Vegetation identities must be nonempty and unique within a page.");
                    }
                }
                result[i] = new VegetationPageRecord(prototype, matrix, new InstanceAppearance(tint, lightmap, custom), id);
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Vegetation page contains trailing data.");
            return result;
        }

        private static void ValidateRecords(VegetationPageRecord[] records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (VegetationPageRecord record in records)
                if (record.Id != null && (string.IsNullOrWhiteSpace(record.Id) || Encoding.GetByteCount(record.Id) > MaximumIdBytes || !identities.Add(record.Id)))
                    throw new ArgumentException("Vegetation IDs must be unique, nonempty and at most 512 UTF-8 bytes.");
        }

        private static string GetPath(string directory, Vector2Int cell, bool trees)
        {
            if (cell.x < 0 || cell.y < 0) throw new ArgumentOutOfRangeException(nameof(cell));
            return Path.Combine(directory, (trees ? "trees-" : "details-") + cell.x + "-" + cell.y + ".lvpage");
        }
        private static Vector4 ReadVector(BinaryReader reader) => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static void WriteVector(BinaryWriter writer, Vector4 value) { for (int i = 0; i < 4; i++) writer.Write(value[i]); }
    }
}
