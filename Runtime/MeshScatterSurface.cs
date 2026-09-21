using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Read-only mesh snapshot for deterministic surface sampling and editor picking.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "MeshScatterSurface")]
    public sealed class MeshScatterSurface
    {
        /// <summary>One generated world-space placement.</summary>
        public struct Sample
        {
            /// <summary>Stable triangle/candidate identity within the supplied source prefix.</summary>
            public string Id;
            /// <summary>Selected weighted target index.</summary>
            public int Target;
            /// <summary>World position including the normal offset.</summary>
            public Vector3 Position;
            /// <summary>World orientation.</summary>
            public Quaternion Rotation;
            /// <summary>Uniform world scale.</summary>
            public float Scale;
        }

        private readonly Vector3[] _vertices;
        private readonly Vector3[] _normals;
        private readonly int[] _indices;
        private readonly Matrix4x4 _localToWorld;
        private readonly Matrix4x4 _normalMatrix;
        private readonly Bounds[] _bounds;
        private readonly float[] _areas;

        /// <summary>Number of source triangles across all submeshes.</summary>
        public int TriangleCount => _areas.Length;

        /// <summary>Capture readable triangle geometry. The mesh and its importer remain unchanged.</summary>
        public MeshScatterSurface(Mesh mesh, Matrix4x4 localToWorld)
        {
            if (mesh == null || !mesh.isReadable)
            {
                throw new ArgumentException("Assign a readable mesh. Enable Read/Write explicitly in its importer if needed.");
            }
            for (int i = 0; i < 16; i++)
            {
                if (!float.IsFinite(localToWorld[i]))
                {
                    throw new ArgumentException("Surface transform must be finite.");
                }
            }
            if (!float.IsFinite(localToWorld.determinant) || Mathf.Abs(localToWorld.determinant) < 0.000001f)
            {
                throw new ArgumentException("Surface transform must be invertible.");
            }
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles)
                {
                    throw new NotSupportedException("Mesh scattering supports triangle submeshes.");
                }
            }
            _vertices = mesh.vertices;
            _normals = mesh.normals;
            _indices = mesh.triangles;
            _localToWorld = localToWorld;
            _normalMatrix = localToWorld.inverse.transpose;
            _bounds = new Bounds[_indices.Length / 3];
            _areas = new float[_bounds.Length];
            for (int i = 0; i < TriangleCount; i++)
            {
                GetTriangle(i, out var a, out var b, out var c);
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b); bounds.Encapsulate(c);
                _bounds[i] = bounds;
                _areas[i] = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                if (!float.IsFinite(_areas[i]))
                {
                    throw new ArgumentException("Mesh geometry must contain finite coordinates.");
                }
            }
        }

        /// <summary>Return the world-space triangle bound for local regeneration.</summary>
        public Bounds GetBounds(int triangle) => _bounds[triangle];

        /// <summary>Check the complete candidate budget before any placement target is modified.</summary>
        public void ValidateBudget(MeshScatterSettings settings)
        {
            settings.Validate();
            double candidates = 0;
            foreach (float area in _areas)
            {
                candidates += Math.Ceiling((double)area * settings.Density);
                if (candidates > settings.CandidateLimit)
                {
                    throw new InvalidOperationException("Scatter candidate limit exceeded. Reduce density, split the surface or raise the explicit limit.");
                }
            }
        }

        /// <summary>Generate one triangle. The optional mask receives positions in source-mesh local space.</summary>
        public void GenerateTriangle(int triangle, string prefix, MeshScatterSettings settings, float[] weights,
            Func<Vector3, float> mask, List<Sample> output)
        {
            if (settings == null || weights == null || output == null || string.IsNullOrEmpty(prefix))
            {
                throw new ArgumentException("Supply settings, weights, output and a source prefix.");
            }
            settings.Validate();
            double totalWeight = 0;
            foreach (float weight in weights)
            {
                if (!float.IsFinite(weight) || weight < 0)
                {
                    throw new ArgumentException("Prototype weights must be finite and nonnegative.");
                }
                totalWeight += weight;
            }
            if (totalWeight <= 0 || totalWeight > float.MaxValue)
            {
                throw new ArgumentException("Prototype weights must have a positive finite sum.");
            }
            GetTriangle(triangle, out var a, out var b, out var c);
            float count = _areas[triangle] * settings.Density;
            if (!float.IsFinite(count) || count > settings.CandidateLimit)
            {
                throw new InvalidOperationException("Triangle candidate limit exceeded.");
            }
            int candidates = Mathf.CeilToInt(count);
            for (int i = 0; i < candidates; i++)
            {
                uint seed = Hash(unchecked((uint)settings.Seed) ^ Hash((uint)triangle + 1) ^ Hash((uint)i + 0x9e3779b9u));
                if (i >= Mathf.FloorToInt(count) && Random(seed, 0) >= count - Mathf.Floor(count))
                {
                    continue;
                }
                float u = Mathf.Sqrt(Random(seed, 1));
                float v = Random(seed, 2);
                Vector3 bary = new(1 - u, u * (1 - v), u * v);
                Vector3 local = _vertices[_indices[triangle * 3]] * bary.x +
                    _vertices[_indices[triangle * 3 + 1]] * bary.y + _vertices[_indices[triangle * 3 + 2]] * bary.z;
                Vector3 position = a * bary.x + b * bary.y + c * bary.z;
                Vector3 normal = Normal(triangle, bary);
                float maskValue = mask?.Invoke(local) ?? 1;
                if (!float.IsFinite(maskValue))
                {
                    throw new ArgumentException("Mask samples must be finite.");
                }
                float acceptance = Filter(position, normal, settings) * Mathf.Clamp01(maskValue);
                if (Random(seed, 3) >= acceptance)
                {
                    continue;
                }
                int target = ChooseTarget(weights, Random(seed, 4));
                if (target < 0)
                {
                    continue;
                }
                Vector3 up = Vector3.Slerp(Vector3.up, normal, settings.NormalAlignment);
                output.Add(new Sample
                {
                    Id = prefix + triangle + ":" + i,
                    Target = target,
                    Position = position + normal * Mathf.Lerp(settings.NormalOffset.x, settings.NormalOffset.y, Random(seed, 5)),
                    Rotation = Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0, Mathf.Lerp(settings.Yaw.x, settings.Yaw.y, Random(seed, 6)), 0),
                    Scale = Mathf.Lerp(settings.Scale.x, settings.Scale.y, Random(seed, 7))
                });
            }
        }

        /// <summary>Find the nearest mesh hit without adding a collider or changing physics state.</summary>
        public bool Raycast(Ray ray, out Vector3 point, out Vector3 normal)
        {
            float closest = float.PositiveInfinity;
            point = normal = default;
            for (int i = 0; i < TriangleCount; i++)
            {
                if (!_bounds[i].IntersectRay(ray))
                {
                    continue;
                }
                GetTriangle(i, out var a, out var b, out var c);
                Vector3 edge1 = b - a, edge2 = c - a;
                Vector3 p = Vector3.Cross(ray.direction, edge2);
                float determinant = Vector3.Dot(edge1, p);
                if (Mathf.Abs(determinant) < 0.0000001f)
                {
                    continue;
                }
                float inverse = 1 / determinant;
                Vector3 t = ray.origin - a;
                float u = Vector3.Dot(t, p) * inverse;
                Vector3 q = Vector3.Cross(t, edge1);
                float v = Vector3.Dot(ray.direction, q) * inverse;
                float distance = Vector3.Dot(edge2, q) * inverse;
                if (u < 0 || v < 0 || u + v > 1 || distance < 0 || distance >= closest)
                {
                    continue;
                }
                closest = distance;
                point = ray.GetPoint(distance);
                normal = Normal(i, new Vector3(1 - u - v, u, v));
            }
            return float.IsFinite(closest);
        }

        private void GetTriangle(int triangle, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            a = _localToWorld.MultiplyPoint3x4(_vertices[_indices[triangle * 3]]);
            b = _localToWorld.MultiplyPoint3x4(_vertices[_indices[triangle * 3 + 1]]);
            c = _localToWorld.MultiplyPoint3x4(_vertices[_indices[triangle * 3 + 2]]);
        }

        private Vector3 Normal(int triangle, Vector3 bary)
        {
            int a = _indices[triangle * 3], b = _indices[triangle * 3 + 1], c = _indices[triangle * 3 + 2];
            Vector3 normal = _normals.Length == _vertices.Length ? _normals[a] * bary.x + _normals[b] * bary.y + _normals[c] * bary.z : Vector3.zero;
            if (normal.sqrMagnitude < 0.000001f)
            {
                normal = Vector3.Cross(_vertices[b] - _vertices[a], _vertices[c] - _vertices[a]);
            }
            return _normalMatrix.MultiplyVector(normal).normalized;
        }

        private static float Filter(Vector3 position, Vector3 normal, MeshScatterSettings settings)
        {
            float value = RangeWeight(position.y, settings.Height, settings.HeightFalloff) *
                RangeWeight(Vector3.Angle(Vector3.up, normal), settings.Slope, settings.SlopeFalloff);
            if (settings.AspectRange < 180 && normal.x * normal.x + normal.z * normal.z > 0.000001f)
            {
                float heading = Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg;
                float angle = Mathf.Abs(Mathf.DeltaAngle(heading, settings.Aspect));
                value *= RangeWeight(angle, new Vector2(-180, settings.AspectRange), settings.AspectFalloff);
            }
            return value;
        }

        private static float RangeWeight(float value, Vector2 range, float falloff)
        {
            if (value < range.x || value > range.y) return 0;
            if (falloff <= 0) return 1;
            return Mathf.SmoothStep(0, 1, Mathf.Min(value - range.x, range.y - value) / falloff);
        }

        private static int ChooseTarget(float[] weights, float random)
        {
            float total = 0;
            foreach (float weight in weights)
            {
                total += weight;
            }
            float threshold = random * total;
            for (int i = 0; i < weights.Length; i++)
            {
                threshold -= weights[i];
                if (weights[i] > 0 && threshold < 0) return i;
            }
            return -1;
        }

        private static float Random(uint seed, uint stream) => (Hash(seed ^ Hash(stream + 17)) >> 8) * (1f / 16777216f);

        private static uint Hash(uint value)
        {
            unchecked
            {
                value ^= value >> 16; value *= 0x7feb352d;
                value ^= value >> 15; value *= 0x846ca68b;
                return value ^ (value >> 16);
            }
        }
    }
}
