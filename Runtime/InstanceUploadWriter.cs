using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace LoogaSoft.Instancing
{
    /// <summary>Zero-copy writer for persistent GPU tables. Failed mappings use the existing buffered path.</summary>
    internal static class InstanceUploadWriter
    {
        internal static bool TryWrite<T>(GraphicsBuffer buffer, T[] source, int sourceIndex, int destinationIndex, int count)
            where T : struct
        {
            if (buffer == null || count <= 0) return false;
            NativeArray<T> mapped = default;
            try
            {
                mapped = buffer.LockBufferForWrite<T>(destinationIndex, count);
                NativeArray<T>.Copy(source, sourceIndex, mapped, 0, count);
                buffer.UnlockBufferAfterWrite<T>(count);
                mapped = default;
                return true;
            }
            catch (Exception)
            {
                if (mapped.IsCreated)
                {
                    try { buffer.UnlockBufferAfterWrite<T>(0); }
                    catch (Exception) { }
                }
                return false;
            }
        }
    }

    internal readonly struct InstanceUploadSegment
    {
        internal readonly int Source;
        internal readonly int Destination;
        internal readonly int Count;
        internal InstanceUploadSegment(int source, int destination, int count)
        { Source = source; Destination = destination; Count = count; }
    }

    /// <summary>Three persistently allocated staging tables with fence-safe GPU patch dispatches.</summary>
    internal sealed class InstanceUploadRing : IDisposable
    {
        private sealed class Slot
        {
            internal GraphicsBuffer Buffer;
            internal int Capacity;
            internal GraphicsFence Fence;
            internal bool HasFence;
        }

        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot() };
        private readonly ComputeShader _shader;
        private readonly int _kernel;
        private int _cursor;

        internal InstanceUploadRing(ComputeShader shader, int kernel)
        { _shader = shader; _kernel = kernel; }

        internal bool TryPatch(GraphicsBuffer destination, float[] source,
            List<InstanceUploadSegment> segments, out long bytes)
        {
            bytes = 0;
            int words = 0;
            foreach (InstanceUploadSegment segment in segments) words += segment.Count;
            if (words == 0) return true;
            Slot slot = null;
            for (int attempt = 0; attempt < _slots.Length; attempt++)
            {
                Slot candidate = _slots[(_cursor + attempt) % _slots.Length];
                if (!candidate.HasFence || candidate.Fence.passed) { slot = candidate; _cursor = (_cursor + attempt + 1) % _slots.Length; break; }
            }
            if (slot == null) return false;
            int capacity = Mathf.NextPowerOfTwo(words);
            if (slot.Buffer == null || slot.Capacity < capacity)
            {
                slot.Buffer?.Dispose();
                slot.Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite,
                    capacity, sizeof(float));
                slot.Capacity = capacity;
                slot.HasFence = false;
            }
            NativeArray<float> mapped = default;
            try
            {
                mapped = slot.Buffer.LockBufferForWrite<float>(0, words);
                int target = 0;
                foreach (InstanceUploadSegment segment in segments)
                {
                    NativeArray<float>.Copy(source, segment.Source, mapped, target, segment.Count);
                    target += segment.Count;
                }
                slot.Buffer.UnlockBufferAfterWrite<float>(words);
                mapped = default;
            }
            catch (Exception)
            {
                if (mapped.IsCreated)
                {
                    try { slot.Buffer.UnlockBufferAfterWrite<float>(0); }
                    catch (Exception) { }
                }
                return false;
            }
            CommandBuffer command = CommandBufferPool.Get("Looga Instances.UploadRing");
            try
            {
                int uploadOffset = 0;
                foreach (InstanceUploadSegment segment in segments)
                {
                    command.SetComputeBufferParam(_shader, _kernel, "_PatchSource", slot.Buffer);
                    command.SetComputeBufferParam(_shader, _kernel, "_PatchDestination", destination);
                    command.SetComputeIntParam(_shader, "_PatchSourceOffset", uploadOffset);
                    command.SetComputeIntParam(_shader, "_PatchDestinationOffset", segment.Destination);
                    command.SetComputeIntParam(_shader, "_PatchWordCount", segment.Count);
                    command.DispatchCompute(_shader, _kernel, (segment.Count + 63) / 64, 1, 1);
                    uploadOffset += segment.Count;
                }
                slot.Fence = command.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,
                    SynchronisationStageFlags.AllGPUOperations);
                slot.HasFence = true;
                Graphics.ExecuteCommandBuffer(command);
            }
            finally { CommandBufferPool.Release(command); }
            bytes = words * sizeof(float);
            return true;
        }

        public void Dispose()
        {
            foreach (Slot slot in _slots) { slot.Buffer?.Dispose(); slot.Buffer = null; slot.HasFence = false; }
        }
    }
}
