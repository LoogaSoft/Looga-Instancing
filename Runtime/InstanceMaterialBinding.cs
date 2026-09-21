using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Owns material resources until the renderer unregisters the corresponding BRG draw.</summary>
    public abstract class InstanceMaterialBinding : IDisposable
    {
        /// <summary>Material used by the draw. The binding must keep it alive until disposal.</summary>
        public abstract Material Material { get; }
        /// <summary>Release the material and any associated resource leases.</summary>
        public abstract void Dispose();
    }
}
