using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Instancing.Editor
{
    [Serializable]
    public struct ForestHlodBakeSettings
    {
        [Min(16)] public float CellSize;
        [Min(1)] public float TransitionDistance;
        [Min(0)] public float MinimumSpacing;
        [Min(1)] public int MaximumCardsPerCell;
        public bool FixedLighting;

        public static ForestHlodBakeSettings Default => new ForestHlodBakeSettings
        {
            CellSize = 512, TransitionDistance = 450, MinimumSpacing = 3,
            MaximumCardsPerCell = 512, FixedLighting = true
        };
    }

    /// <summary>Bakes derived forest cells from ordinary placements and a cross-card impostor.</summary>
    public static class ForestHlodBaker
    {
        public static ForestHlodAsset Bake(InstanceContainer source, InstanceImpostorAsset impostor,
            ForestHlodBakeSettings settings, string assetPath)
        {
            if (!source || !impostor) throw new ArgumentNullException(source ? nameof(impostor) : nameof(source));
            if (impostor.Mode != InstanceImpostorMode.CrossCard)
                throw new ArgumentException("Forest cells require a cross-card impostor.");
            if (!impostor.TryValidate(out string reason))
                throw new ArgumentException(reason ?? "The impostor asset is invalid.");
            if (!float.IsFinite(settings.CellSize) || settings.CellSize < 16 ||
                !float.IsFinite(settings.TransitionDistance) || settings.TransitionDistance < 1 ||
                !float.IsFinite(settings.MinimumSpacing) || settings.MinimumSpacing < 0 || settings.MaximumCardsPerCell < 1)
                throw new ArgumentException("Forest HLOD bake settings are invalid.");
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use an Assets-relative .asset path.");

            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("The asset path needs a folder.");
            EnsureFolder(folder);
            string stem = Path.GetFileNameWithoutExtension(assetPath);
            var groups = source.CopyResolvedPlacements().GroupBy(p => new Vector2Int(
                Mathf.FloorToInt((source.transform.TransformPoint(p.Position).x) / settings.CellSize),
                Mathf.FloorToInt((source.transform.TransformPoint(p.Position).z) / settings.CellSize)))
                .OrderBy(group => group.Key.x).ThenBy(group => group.Key.y);
            var cells = new List<ForestHlodAsset.Cell>();
            Material material = new Material(impostor.Material) { name = stem + " HLOD Material" };
            material.SetFloat("_LoogaFixedLighting", settings.FixedLighting ? 1 : 0);
            Vector4 lighting = CaptureLightingSignature();
            material.SetVector("_LoogaFixedLightDirection", lighting);
            if (RenderSettings.sun) material.SetColor("_LoogaFixedLightColor", RenderSettings.sun.color);
            material.SetColor("_LoogaFixedAmbient", RenderSettings.ambientLight);
            AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + stem + "-Material.mat"));

            foreach (var group in groups)
            {
                InstanceContainer.Placement[] all = group.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
                InstanceContainer.Placement[] retained = Reduce(all, source.transform, settings.MinimumSpacing, settings.MaximumCardsPerCell);
                if (retained.Length == 0) continue;
                var combine = new CombineInstance[retained.Length];
                for (int i = 0; i < retained.Length; i++)
                {
                    var placement = retained[i];
                    combine[i] = new CombineInstance
                    {
                        mesh = impostor.Mesh,
                        transform = source.transform.localToWorldMatrix * Matrix4x4.TRS(placement.Position,
                            Quaternion.Euler(placement.EulerAngles), placement.Scale)
                    };
                }
                var mesh = new Mesh { name = stem + " Cell " + group.Key.x + " " + group.Key.y, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(combine, true, true, false);
                mesh.RecalculateBounds();
                string cellStem = stem + "-" + group.Key.x + "-" + group.Key.y;
                AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + cellStem + ".asset"));
                var temporary = new GameObject(cellStem);
                temporary.AddComponent<MeshFilter>().sharedMesh = mesh;
                temporary.AddComponent<MeshRenderer>().sharedMaterial = material;
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(temporary,
                    AssetDatabase.GenerateUniqueAssetPath(folder + "/" + cellStem + ".prefab"));
                UnityEngine.Object.DestroyImmediate(temporary);
                cells.Add(new ForestHlodAsset.Cell
                {
                    Id = group.Key.x + ":" + group.Key.y,
                    Bounds = mesh.bounds,
                    PlacementIds = all.Select(p => p.Id).ToArray(),
                    Prefab = prefab,
                    SourceCards = all.Length,
                    RetainedCards = retained.Length
                });
            }
            var asset = ScriptableObject.CreateInstance<ForestHlodAsset>();
            asset.SetData(source.SourceId, settings.CellSize, settings.TransitionDistance, settings.FixedLighting,
                lighting, cells.ToArray());
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        public static InstanceContainer.Placement[] Reduce(IReadOnlyList<InstanceContainer.Placement> placements,
            Transform source, float minimumSpacing, int maximumCards)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (maximumCards < 1 || minimumSpacing < 0) throw new ArgumentOutOfRangeException(nameof(maximumCards));
            var retained = new List<InstanceContainer.Placement>(Mathf.Min(placements.Count, maximumCards));
            float square = minimumSpacing * minimumSpacing;
            foreach (InstanceContainer.Placement placement in placements.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                Vector3 point = source ? source.TransformPoint(placement.Position) : placement.Position;
                bool overlaps = false;
                foreach (InstanceContainer.Placement existing in retained)
                {
                    Vector3 other = source ? source.TransformPoint(existing.Position) : existing.Position;
                    Vector2 delta = new Vector2(point.x - other.x, point.z - other.z);
                    if (delta.sqrMagnitude < square) { overlaps = true; break; }
                }
                if (!overlaps) retained.Add(placement);
                if (retained.Count == maximumCards) break;
            }
            return retained.ToArray();
        }

        private static Vector4 CaptureLightingSignature()
        {
            Light sun = RenderSettings.sun;
            if (!sun) return Vector4.zero;
            Vector3 direction = -sun.transform.forward;
            return new Vector4(direction.x, direction.y, direction.z, sun.intensity * sun.color.maxColorComponent);
        }

        private static void EnsureFolder(string folder)
        {
            string current = "Assets";
            foreach (string part in folder.Substring("Assets".Length).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }
    }
}
