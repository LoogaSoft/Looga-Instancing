using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing.SpeedTreeInterop
{
    /// <summary>Unity 6 GPU-driven assembly bridge. No reflection or vendor runtime is required.</summary>
    public sealed class WindReader : IDisposable
    {
        /// <summary>Current engine wind property count.</summary>
        public static int PropertyCount => (int)SpeedTreeWindParamIndex.MaxWindParamsCount;

#if UNITY_6000_4_OR_NEWER
        private NativeArray<EntityId> _ids;
#else
        private NativeArray<int> _ids;
#endif
        /// <summary>Keep one native renderer identifier for the lifetime of its proxy.</summary>
        public WindReader(Renderer renderer)
        {
            if (!renderer) throw new ArgumentNullException(nameof(renderer));
#if UNITY_6000_4_OR_NEWER
            _ids = new NativeArray<EntityId>(1, Allocator.Persistent);
            _ids[0] = renderer.GetEntityId();
#else
            _ids = new NativeArray<int>(1, Allocator.Persistent);
            _ids[0] = renderer.GetInstanceID();
#endif
        }

        /// <summary>Release the persistent renderer identifier.</summary>
        public void Dispose()
        {
            if (_ids.IsCreated)
            {
                _ids.Dispose();
            }
        }

        /// <summary>Read current and previous SpeedTree state into two consecutive property blocks.</summary>
        public unsafe void Read(NativeArray<Vector4> output)
        {
            int count = PropertyCount;
            if (!_ids.IsCreated || output.Length != count * 2)
            {
                throw new ArgumentException("A live reader and two complete property blocks are required.");
            }
            SpeedTreeWindParamsBufferIterator iterator = default;
            iterator.uintStride = count * 4;
            iterator.elementsCount = 1;
            for (int property = 0; property < count; property++)
            {
                iterator.uintParamOffsets[property] = property * 4;
            }
            iterator.bufferPtr = (IntPtr)output.GetUnsafePtr();
            SpeedTreeWindManager.UpdateWindAndWriteBufferWindParams(_ids, iterator, false);
            iterator.bufferPtr = (IntPtr)((Vector4*)output.GetUnsafePtr() + count);
            SpeedTreeWindManager.UpdateWindAndWriteBufferWindParams(_ids, iterator, true);
        }
    }
}
