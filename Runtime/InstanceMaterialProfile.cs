using System;
using Unity.Collections;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Explicit shader compatibility and wind-data contract. Source materials remain unchanged.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceMaterialProfile")]
    public abstract class InstanceMaterialProfile : ScriptableObject
    {
        /// <summary>True only for materials supported by this profile.</summary>
        public abstract bool Supports(Material material);
        /// <summary>Maximum mesh-local vertex displacement used to expand culling bounds.</summary>
        public abstract float BoundsPadding { get; }
        /// <summary>True when the shader implements SpeedTree percentage LOD deformation.</summary>
        public virtual bool SpeedTreeLod => false;
        /// <summary>Create one owned wind source per registered prototype, or null for global shader wind.</summary>
        public virtual InstanceWindSource CreateWind(GameObject source) => null;
        /// <summary>Create an owned material binding, or null to use the original material.</summary>
        public virtual InstanceMaterialBinding CreateMaterial(Material source) => null;
    }

    /// <summary>Owned prototype wind data. Each property receives one shared float4 in the BRG batch.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceWindSource")]
    public abstract class InstanceWindSource : IDisposable
    {
        /// <summary>Shader property identifiers in the same order as the update destination.</summary>
        public abstract int[] PropertyIds { get; }
        /// <summary>Fill every property before camera rendering. Current and historical wind use separate properties.</summary>
        public abstract void Update(NativeArray<Vector4> destination);
        /// <summary>Release native arrays and hidden wind proxies.</summary>
        public abstract void Dispose();
    }

}
