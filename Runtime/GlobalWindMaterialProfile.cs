using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Opt-in profile for a qualified DOTS shader whose wind comes from material or global properties.</summary>
    [CreateAssetMenu(menuName = "Looga/Instancing/Global Wind Material Profile")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "GlobalWindMaterialProfile")]
    public sealed class GlobalWindMaterialProfile : InstanceMaterialProfile
    {
        [SerializeField] private Shader _shader;
        [SerializeField, Min(0)] private float _boundsPadding = 2;
        public override float BoundsPadding => _boundsPadding;
        public override bool Supports(Material material) => material && material.shader == _shader;

        /// <summary>Assign a shader after validating its DOTS, depth, LOD and shadow passes.</summary>
        public void Configure(Shader shader, float boundsPadding)
        {
            if (!shader || !float.IsFinite(boundsPadding) || boundsPadding < 0)
            {
                throw new ArgumentException("A shader and finite nonnegative displacement are required.");
            }
            _shader = shader;
            _boundsPadding = boundsPadding;
        }
    }
}
