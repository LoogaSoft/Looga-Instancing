using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Baked solid boxes for conservative camera occlusion. Changed source geometry remains visible until rebaked.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class BakedInstanceOcclusion : MonoBehaviour
    {
        /// <summary>Maximum number of boxes used by one camera. Additional boxes cannot remove visibility.</summary>
        public const int MaximumBoxes = 16;
        [Serializable]
        private struct Entry
        {
            public MeshRenderer Source;
            public Material Material;
            public Matrix4x4 Transform;
            public Matrix4x4 Inverse;
            public uint Geometry;
        }
        [SerializeField] private Entry[] _entries = Array.Empty<Entry>();
        [SerializeField] private int _revision;
        private static readonly List<BakedInstanceOcclusion> Sources = new();
        private static readonly List<Vector3> Vertices = new(24);
        private static readonly List<int> Indices = new(36);
        private static readonly List<Material> Materials = new(1);
        private static Mesh _cube;
        private static uint _geometry;

        /// <summary>Number of captured source boxes.</summary>
        public int BoxCount => _entries.Length;
        /// <summary>Serialized bake version. Each successful bake advances this value.</summary>
        public int Revision => _revision;

        private void OnEnable()
        {
            if (!Sources.Contains(this))
            {
                Sources.Add(this);
            }
        }
        private void OnDisable() => Sources.Remove(this);

        /// <summary>Capture selected Unity cube renderers. Unsupported or transparent sources leave the previous bake unchanged.</summary>
        public void Bake(MeshRenderer[] sources)
        {
            if (sources == null || sources.Length > MaximumBoxes)
            {
                throw new ArgumentException("Select at most sixteen solid Unity cube renderers.", nameof(sources));
            }
            var next = new Entry[sources.Length];
            uint geometry = ReadGeometry();
            if (geometry == 0)
            {
                throw new ArgumentException("The built-in cube geometry is unavailable or has been modified.");
            }
            for (int i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                if (!Supported(source))
                {
                    throw new ArgumentException("Baked occlusion requires an enabled Unity cube with one opaque URP Lit or Unlit material.");
                }
                Matrix4x4 matrix = source.localToWorldMatrix;
                if (!float.IsFinite(matrix.determinant) || Mathf.Abs(matrix.determinant) < 0.000001f)
                {
                    throw new ArgumentException("Occluder transforms must be finite and invertible.");
                }
                next[i] = new Entry { Source = source, Material = source.sharedMaterial,
                    Transform = matrix, Inverse = matrix.inverse, Geometry = geometry };
            }
            _entries = next;
            _revision++;
        }

        /// <summary>Discard this bake immediately. Source renderers remain unchanged.</summary>
        public void Invalidate()
        {
            _entries = Array.Empty<Entry>();
            _revision++;
        }

        /// <summary>Write valid baked boxes for a layer mask. The return value is the number of matrices written.</summary>
        public static int CopyActiveBoxes(Matrix4x4[] output, uint mask)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int count = 0;
            if (Sources.Count == 0 || output.Length == 0) return 0;
            _geometry = ReadGeometry();
            if (_geometry == 0) return 0;
            foreach (var owner in Sources)
            {
                if (!owner || !owner.isActiveAndEnabled) continue;
                foreach (var entry in owner._entries)
                {
                    var source = entry.Source;
                    if (!Supported(source) || entry.Geometry != _geometry || entry.Material != source.sharedMaterial ||
                        entry.Transform != source.localToWorldMatrix || (mask & (1u << source.gameObject.layer)) == 0) continue;
                    output[count++] = entry.Inverse;
                    if (count == Mathf.Min(output.Length, MaximumBoxes)) return count;
                }
            }
            return count;
        }

        private static bool Supported(MeshRenderer source)
        {
            if (!source || !source.enabled || !source.gameObject.activeInHierarchy || source.forceRenderingOff ||
                source.shadowCastingMode == ShadowCastingMode.ShadowsOnly || source.HasPropertyBlock()) return false;
            _cube ??= Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var filter = source.GetComponent<MeshFilter>();
            if (!filter || filter.sharedMesh != _cube || source.GetComponentInParent<LODGroup>()) return false;
            source.GetSharedMaterials(Materials);
            var material = source.sharedMaterial;
            if (!material || Materials.Count != 1 || material.renderQueue >= 2500 ||
                material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                (material.HasProperty("_SrcBlend") && material.GetFloat("_SrcBlend") != 1) ||
                (material.HasProperty("_DstBlend") && material.GetFloat("_DstBlend") != 0) ||
                (material.HasProperty("_ZWrite") && material.GetFloat("_ZWrite") != 1) ||
                (material.HasProperty("_Cull") && material.GetFloat("_Cull") != 2) ||
                (material.HasProperty("_Surface") && material.GetFloat("_Surface") != 0) ||
                (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") != 0)) return false;
            string shader = material.shader.name;
            return shader == "Universal Render Pipeline/Lit" || shader == "Universal Render Pipeline/Unlit";
        }

        private static int Corner(Vector3 point)
        {
            if (Mathf.Abs(point.x) != 0.5f || Mathf.Abs(point.y) != 0.5f || Mathf.Abs(point.z) != 0.5f) return 0;
            return 1 << ((point.x > 0 ? 1 : 0) | (point.y > 0 ? 2 : 0) | (point.z > 0 ? 4 : 0));
        }

        private static uint ReadGeometry()
        {
            _cube ??= Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (!_cube || !_cube.isReadable) return 0;
            _cube.GetVertices(Vertices);
            _cube.GetIndices(Indices, 0);
            if (Indices.Count != 36) return 0;
            Span<int> first = stackalloc int[6];
            Span<int> second = stackalloc int[6];
            first.Clear();
            second.Clear();
            for (int i = 0; i < Indices.Count; i += 3)
            {
                Vector3 a = Vertices[Indices[i]];
                Vector3 b = Vertices[Indices[i + 1]];
                Vector3 c = Vertices[Indices[i + 2]];
                int face = -1;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (a[axis] == b[axis] && a[axis] == c[axis])
                    {
                        face = axis * 2 + (a[axis] > 0 ? 1 : 0);
                    }
                }
                if (face < 0) return 0;
                int ca = Corner(a);
                int cb = Corner(b);
                int cc = Corner(c);
                if (ca == 0 || cb == 0 || cc == 0 || ca == cb || ca == cc || cb == cc ||
                    Vector3.Cross(b - a, c - a)[face / 2] * a[face / 2] <= 0) return 0;
                int mask = ca | cb | cc;
                if (first[face] == 0)
                {
                    first[face] = mask;
                }
                else if (second[face] == 0)
                {
                    second[face] = mask;
                }
                else return 0;
            }
            for (int face = 0; face < 6; face++)
            {
                int shared = first[face] & second[face];
                int start = -1;
                int end = -1;
                for (int corner = 0; corner < 8; corner++)
                {
                    if ((shared & (1 << corner)) == 0) continue;
                    if (start < 0)
                    {
                        start = corner;
                    }
                    else if (end < 0)
                    {
                        end = corner;
                    }
                    else return 0;
                }
                int diagonal = start ^ end;
                if (start < 0 || end < 0 || (diagonal != 3 && diagonal != 5 && diagonal != 6)) return 0;
            }
            uint hash = 2166136261;
            foreach (var vertex in Vertices)
            {
                hash = (hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(vertex.x))) * 16777619;
                hash = (hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(vertex.y))) * 16777619;
                hash = (hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(vertex.z))) * 16777619;
            }
            foreach (int index in Indices)
            {
                hash = (hash ^ (uint)index) * 16777619;
            }
            return hash;
        }
    }
}
