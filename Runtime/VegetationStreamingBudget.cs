using System;

namespace LoogaSoft.Instancing
{
    /// <summary>Independent source work, upload and GPU residency limits.</summary>
    [Serializable]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "VegetationStreamingBudget")]
    public struct VegetationStreamingBudget
    {
        /// <summary>Maximum placement records prepared during one update.</summary>
        public int PlacementRecords;
        /// <summary>Maximum source bytes uploaded during one update.</summary>
        public int UploadBytes;
        /// <summary>Cooperative CPU time limit between placement records and native calls.</summary>
        public float CpuMilliseconds;
        /// <summary>Maximum allocated instance GPU storage. View buffers count toward the next update's check.</summary>
        public long ResidentGpuBytes;
        /// <summary>Maximum compressed page bytes accepted during one update.</summary>
        public int DiskReadBytes;
        /// <summary>Maximum decompressed page bytes accepted during one update.</summary>
        public int DecompressionBytes;
        /// <summary>Maximum page reads in flight.</summary>
        public int ConcurrentReads;
        /// <summary>Maximum cells loaded or evicted during one update.</summary>
        public int ResidencyChanges;
        /// <summary>Seconds of observer motion used for predictive prefetch.</summary>
        public float PrefetchSeconds;
        /// <summary>Conservative initial limits. Measure these limits in the target world.</summary>
        public static VegetationStreamingBudget Default => new VegetationStreamingBudget
        {
            PlacementRecords = 2048, UploadBytes = 262144, CpuMilliseconds = 3, ResidentGpuBytes = 268435456,
            DiskReadBytes = 2097152, DecompressionBytes = 8388608, ConcurrentReads = 4,
            ResidencyChanges = 16, PrefetchSeconds = 1.5f
        };
        /// <summary>Fill fields added after the initial streaming contract with conservative defaults.</summary>
        public VegetationStreamingBudget Normalized()
        {
            VegetationStreamingBudget value = this;
            VegetationStreamingBudget defaults = Default;
            if (value.DiskReadBytes == 0) value.DiskReadBytes = defaults.DiskReadBytes;
            if (value.DecompressionBytes == 0) value.DecompressionBytes = defaults.DecompressionBytes;
            if (value.ConcurrentReads == 0) value.ConcurrentReads = defaults.ConcurrentReads;
            if (value.ResidencyChanges == 0) value.ResidencyChanges = defaults.ResidencyChanges;
            if (value.PrefetchSeconds == 0) value.PrefetchSeconds = defaults.PrefetchSeconds;
            return value;
        }
        internal void Validate()
        {
            if (PlacementRecords < 1 || UploadBytes < 4096 || !float.IsFinite(CpuMilliseconds) || CpuMilliseconds <= 0 || ResidentGpuBytes < 4096)
            {
                throw new ArgumentException("Streaming limits require positive work/time values and at least 4096 bytes.");
            }
            if (DiskReadBytes < 4096 || DecompressionBytes < 4096 || ConcurrentReads < 1 || ResidencyChanges < 1 ||
                !float.IsFinite(PrefetchSeconds) || PrefetchSeconds < 0)
                throw new ArgumentException("I/O, decompression, residency, and predictive prefetch limits must be positive.");
        }
    }
}
