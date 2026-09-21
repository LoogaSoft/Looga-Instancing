using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Impostor projection stored by a derived Looga asset.</summary>
    public enum InstanceImpostorMode
    {
        CrossCard,
        Octahedral
    }

    /// <summary>Derived impostor data. The source prefab remains unchanged.</summary>
    [CreateAssetMenu(menuName = "Looga/Instancing/Impostor Asset", fileName = "Instance Impostor")]
    public sealed class InstanceImpostorAsset : ScriptableObject
    {
        [SerializeField] private GameObject _sourcePrefab;
        [SerializeField] private GameObject _compiledPrefab;
        [SerializeField] private InstanceImpostorMode _mode;
        [SerializeField] private Texture2D _albedoOpacity;
        [SerializeField] private Texture2D _objectNormals;
        [SerializeField] private Texture2D _materialMask;
        [SerializeField] private Mesh _mesh;
        [SerializeField] private Material _material;
        [SerializeField] private Vector2Int _viewGrid = Vector2Int.one;
        [SerializeField] private Bounds _sourceBounds;

        public GameObject SourcePrefab => _sourcePrefab;
        public GameObject CompiledPrefab => _compiledPrefab;
        public InstanceImpostorMode Mode => _mode;
        public Texture2D AlbedoOpacity => _albedoOpacity;
        public Texture2D ObjectNormals => _objectNormals;
        public Texture2D MaterialMask => _materialMask;
        public Mesh Mesh => _mesh;
        public Material Material => _material;
        public Vector2Int ViewGrid => _viewGrid;
        public Bounds SourceBounds => _sourceBounds;

        /// <summary>Compile the derived prefab through the normal BRG compatibility path.</summary>
        public InstancePrototype CreatePrototype(InstanceMaterialProfile profile = null)
        {
            if (!_compiledPrefab) throw new InvalidOperationException("Bake the impostor before compilation.");
            return InstancePrototype.FromPrefab(_compiledPrefab, profile);
        }

        /// <summary>Check required surface data and derived rendering assets.</summary>
        public bool TryValidate(out string reason)
        {
            if (!_sourcePrefab || !_compiledPrefab || !_mesh || !_material)
            {
                reason = "The impostor needs source, prefab, mesh, and material assets.";
                return false;
            }
            if (!_albedoOpacity || !_objectNormals || !_materialMask)
            {
                reason = "The impostor needs albedo, normal, and material atlases.";
                return false;
            }
            if (_viewGrid.x < 1 || _viewGrid.y < 1 || _sourceBounds.size.sqrMagnitude <= 0)
            {
                reason = "The impostor view grid and source bounds are invalid.";
                return false;
            }
            reason = null;
            return true;
        }

#if UNITY_EDITOR
        internal void Configure(GameObject source, GameObject compiled, InstanceImpostorMode mode,
            Texture2D albedo, Texture2D normals, Texture2D mask, Mesh mesh, Material material,
            Vector2Int viewGrid, Bounds sourceBounds)
        {
            _sourcePrefab = source;
            _compiledPrefab = compiled;
            _mode = mode;
            _albedoOpacity = albedo;
            _objectNormals = normals;
            _materialMask = mask;
            _mesh = mesh;
            _material = material;
            _viewGrid = viewGrid;
            _sourceBounds = sourceBounds;
        }
#endif
    }
}
