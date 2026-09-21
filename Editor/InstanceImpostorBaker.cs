using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Editor
{
    /// <summary>Settings for one non-destructive impostor bake.</summary>
    [Serializable]
    public struct InstanceImpostorBakeSettings
    {
        public InstanceImpostorMode Mode;
        [Range(32, 512)] public int TileResolution;
        [Range(2, 16)] public int OctahedralGrid;
        [Range(0.001f, 0.25f)] public float TransitionHeight;

        public static InstanceImpostorBakeSettings Default => new()
        {
            Mode = InstanceImpostorMode.Octahedral,
            TileResolution = 128,
            OctahedralGrid = 8,
            TransitionHeight = 0.01f
        };

        internal void Validate()
        {
            if (TileResolution < 32 || TileResolution > 512 || (TileResolution & (TileResolution - 1)) != 0)
            {
                throw new ArgumentException("Tile resolution must be a power of two from 32 through 512.");
            }
            if (OctahedralGrid < 2 || OctahedralGrid > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(OctahedralGrid));
            }
            if (!float.IsFinite(TransitionHeight) || TransitionHeight <= 0 || TransitionHeight > 0.25f)
            {
                throw new ArgumentOutOfRangeException(nameof(TransitionHeight));
            }
        }
    }

    /// <summary>Bakes relightable cross-card and octahedral impostors from ordinary prefabs.</summary>
    public static class InstanceImpostorBaker
    {
        private const int CaptureLayer = 31;

        /// <summary>Create derived textures, mesh, material, prefab, and metadata beside the requested asset path.</summary>
        public static InstanceImpostorAsset Bake(GameObject sourcePrefab, InstanceImpostorBakeSettings settings, string assetPath)
        {
            if (!sourcePrefab) throw new ArgumentNullException(nameof(sourcePrefab));
            settings.Validate();
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Use an Assets path with an .asset extension.", nameof(assetPath));
            }
            if (sourcePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
            {
                throw new NotSupportedException("Bake static MeshRenderers. Skinned meshes need an animation-specific impostor path.");
            }

            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            string directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory) || !AssetDatabase.IsValidFolder(directory))
            {
                throw new DirectoryNotFoundException("Create the output folder before the bake.");
            }
            string stem = Path.GetFileNameWithoutExtension(assetPath);
            Bounds bounds = CalculateBounds(sourcePrefab);
            Vector2Int grid = settings.Mode == InstanceImpostorMode.CrossCard ? new Vector2Int(2, 2) :
                new Vector2Int(settings.OctahedralGrid, settings.OctahedralGrid);
            Texture2D albedo = CaptureAtlas(sourcePrefab, bounds, grid, settings.TileResolution, 0, false);
            Texture2D normals = CaptureAtlas(sourcePrefab, bounds, grid, settings.TileResolution, 1, true);
            Texture2D mask = CaptureAtlas(sourcePrefab, bounds, grid, settings.TileResolution, 2, true);
            albedo.name = stem + " AlbedoOpacity";
            normals.name = stem + " ObjectNormals";
            mask.name = stem + " MaterialMask";
            Mesh mesh = settings.Mode == InstanceImpostorMode.CrossCard ? CreateCrossCardMesh(bounds, 3, 4) : CreateBillboardMesh(bounds);
            mesh.name = stem + " Mesh";
            Shader shader = Shader.Find("Looga/Instancing/Relightable Impostor");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("The Looga impostor shader is unavailable.");
            var material = new Material(shader) { name = stem + " Material", enableInstancing = true };
            material.SetTexture("_BaseMap", albedo);
            material.SetTexture("_NormalAtlas", normals);
            material.SetTexture("_MaterialAtlas", mask);
            material.SetVector("_ViewGrid", new Vector4(grid.x, grid.y, 0, 0));
            material.SetFloat("_ImpostorMode", settings.Mode == InstanceImpostorMode.Octahedral ? 1 : 0);
            material.SetVector("_BoundsMin", bounds.min);
            material.SetVector("_BoundsSize", bounds.size);

            string albedoPath = directory + "/" + stem + "-Albedo.asset";
            string normalPath = directory + "/" + stem + "-Normals.asset";
            string maskPath = directory + "/" + stem + "-Material.asset";
            string meshPath = directory + "/" + stem + "-Mesh.asset";
            string materialPath = directory + "/" + stem + "-Material.mat";
            string prefabPath = directory + "/" + stem + "-Compiled.prefab";
            AssetDatabase.CreateAsset(albedo, albedoPath);
            AssetDatabase.CreateAsset(normals, normalPath);
            AssetDatabase.CreateAsset(mask, maskPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.CreateAsset(material, materialPath);

            GameObject compiled = BuildCompiledPrefab(sourcePrefab, mesh, material, settings.TransitionHeight);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(compiled, prefabPath);
            Object.DestroyImmediate(compiled);
            var asset = ScriptableObject.CreateInstance<InstanceImpostorAsset>();
            asset.Configure(sourcePrefab, prefab, settings.Mode, albedo, normals, mask, mesh, material, grid, bounds);
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        /// <summary>Create crossed vertical cards with one captured view assigned to each card.</summary>
        public static Mesh CreateCrossCardMesh(Bounds bounds, int cardCount = 3, int viewCount = 4)
        {
            if (cardCount < 2 || cardCount > 8 || viewCount < 1) throw new ArgumentOutOfRangeException();
            var vertices = new List<Vector3>(cardCount * 4);
            var normals = new List<Vector3>(cardCount * 4);
            var uv = new List<Vector2>(cardCount * 4);
            var frames = new List<Vector2>(cardCount * 4);
            var triangles = new List<int>(cardCount * 6);
            float halfWidth = Mathf.Max(bounds.extents.x, bounds.extents.z);
            for (int card = 0; card < cardCount; card++)
            {
                float angle = card * Mathf.PI / cardCount;
                Vector3 axis = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * halfWidth;
                int start = vertices.Count;
                vertices.Add(bounds.center - axis + Vector3.down * bounds.extents.y);
                vertices.Add(bounds.center + axis + Vector3.down * bounds.extents.y);
                vertices.Add(bounds.center + axis + Vector3.up * bounds.extents.y);
                vertices.Add(bounds.center - axis + Vector3.up * bounds.extents.y);
                Vector3 normal = Vector3.Cross(Vector3.up, axis).normalized;
                for (int i = 0; i < 4; i++)
                {
                    normals.Add(normal);
                    frames.Add(new Vector2(Mathf.Round(card * (float)viewCount / cardCount) % viewCount, 0));
                }
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
                triangles.Add(start); triangles.Add(start + 3); triangles.Add(start + 2);
            }
            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, frames);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = bounds;
            return mesh;
        }

        /// <summary>Create one vertical card. The shader selects and faces octahedral views.</summary>
        public static Mesh CreateBillboardMesh(Bounds bounds)
        {
            float halfWidth = Mathf.Max(bounds.extents.x, bounds.extents.z);
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                bounds.center + new Vector3(-halfWidth, -bounds.extents.y, 0),
                bounds.center + new Vector3(halfWidth, -bounds.extents.y, 0),
                bounds.center + new Vector3(halfWidth, bounds.extents.y, 0),
                bounds.center + new Vector3(-halfWidth, bounds.extents.y, 0)
            };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.uv2 = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.bounds = bounds;
            return mesh;
        }

        /// <summary>Decode the center of one octahedral atlas cell.</summary>
        public static Vector3 DecodeOctahedralView(int x, int y, int grid)
        {
            float px = ((x + 0.5f) / grid) * 2 - 1;
            float pz = ((y + 0.5f) / grid) * 2 - 1;
            var direction = new Vector3(px, 1 - Mathf.Abs(px) - Mathf.Abs(pz), pz);
            if (direction.y < 0)
            {
                float oldX = direction.x;
                direction.x = (1 - Mathf.Abs(direction.z)) * Mathf.Sign(oldX);
                direction.z = (1 - Mathf.Abs(oldX)) * Mathf.Sign(direction.z);
            }
            return direction.normalized;
        }

        private static Texture2D CaptureAtlas(GameObject source, Bounds bounds, Vector2Int grid, int tileSize,
            int captureMode, bool linear)
        {
            Shader captureShader = Shader.Find("Hidden/Looga/Instancing/Impostor Capture");
            if (!captureShader) throw new InvalidOperationException("The Looga impostor capture shader is unavailable.");
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            GameObject cameraObject = null;
            var render = new RenderTexture(tileSize, tileSize, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                hideFlags = HideFlags.HideAndDontSave
            };
            var atlas = new Texture2D(tileSize * grid.x, tileSize * grid.y, TextureFormat.RGBA32, true, linear);
            try
            {
                clone = Object.Instantiate(source);
                clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                clone.transform.localScale = Vector3.one;
                SceneManager.MoveGameObjectToScene(clone, scene);
                SetLayer(clone.transform, CaptureLayer);
                cameraObject = new GameObject("Looga impostor capture camera") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(bounds.extents.y, Mathf.Max(bounds.extents.x, bounds.extents.z)) * 1.1f;
                camera.aspect = 1;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = Mathf.Max(10, bounds.size.magnitude * 4);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << CaptureLayer;
                camera.targetTexture = render;
                Shader.SetGlobalInt("_LoogaImpostorCaptureMode", captureMode);
                for (int y = 0; y < grid.y; y++)
                {
                    for (int x = 0; x < grid.x; x++)
                    {
                        Vector3 direction = grid == new Vector2Int(2, 2) ?
                            Quaternion.Euler(0, (y * grid.x + x) * 90, 0) * Vector3.forward :
                            DecodeOctahedralView(x, y, grid.x);
                        float distance = bounds.size.magnitude * 1.5f + 1;
                        camera.transform.position = bounds.center + direction * distance;
                        Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.98f ? Vector3.forward : Vector3.up;
                        camera.transform.rotation = Quaternion.LookRotation(-direction, up);
                        camera.RenderWithShader(captureShader, string.Empty);
                        RenderTexture previous = RenderTexture.active;
                        RenderTexture.active = render;
                        atlas.ReadPixels(new Rect(0, 0, tileSize, tileSize), x * tileSize, y * tileSize, false);
                        RenderTexture.active = previous;
                    }
                }
                atlas.Apply(true, false);
                return atlas;
            }
            catch
            {
                Object.DestroyImmediate(atlas);
                throw;
            }
            finally
            {
                Shader.SetGlobalInt("_LoogaImpostorCaptureMode", 0);
                Object.DestroyImmediate(render);
                if (clone) Object.DestroyImmediate(clone);
                if (cameraObject) Object.DestroyImmediate(cameraObject);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static GameObject BuildCompiledPrefab(GameObject source, Mesh mesh, Material material, float transition)
        {
            GameObject root = Object.Instantiate(source);
            root.name = source.name + " Impostor";
            var sourceRenderers = root.GetComponentsInChildren<Renderer>(true);
            var child = new GameObject("Looga Impostor");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            var group = root.GetComponent<LODGroup>();
            if (!group) group = root.AddComponent<LODGroup>();
            LOD[] old = group.GetLODs();
            var assigned = new HashSet<Renderer>();
            foreach (var lod in old)
            {
                foreach (var item in lod.renderers)
                {
                    if (item) assigned.Add(item);
                }
            }
            var missing = new List<Renderer>();
            foreach (var item in sourceRenderers)
            {
                if (item && item.enabled && !assigned.Contains(item)) missing.Add(item);
            }
            if (old.Length == 0 || assigned.Count == 0)
            {
                old = new[] { new LOD(Mathf.Max(transition * 4, 0.1f), sourceRenderers) };
            }
            else if (missing.Count > 0)
            {
                var first = new List<Renderer>(old[0].renderers);
                first.AddRange(missing);
                old[0].renderers = first.ToArray();
            }
            float last = Mathf.Min(transition, old[^1].screenRelativeTransitionHeight * 0.5f);
            last = Mathf.Max(0.0001f, last);
            var lods = new LOD[old.Length + 1];
            Array.Copy(old, lods, old.Length);
            lods[^1] = new LOD(last, new Renderer[] { renderer }) { fadeTransitionWidth = 0.5f };
            group.SetLODs(lods);
            group.fadeMode = LODFadeMode.CrossFade;
            group.animateCrossFading = true;
            group.RecalculateBounds();
            return root;
        }

        private static Bounds CalculateBounds(GameObject source)
        {
            Renderer[] renderers = source.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new ArgumentException("The source prefab has no renderers.");
            Matrix4x4 root = source.transform.worldToLocalMatrix;
            bool initialized = false;
            Bounds result = default;
            foreach (var renderer in renderers)
            {
                Bounds local = renderer.localBounds;
                Matrix4x4 matrix = root * renderer.localToWorldMatrix;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z)));
                    if (!initialized)
                    {
                        result = new Bounds(point, Vector3.zero);
                        initialized = true;
                    }
                    else result.Encapsulate(point);
                }
            }
            return result;
        }

        private static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            foreach (Transform child in root) SetLayer(child, layer);
        }
    }

    /// <summary>Editor window for general Looga Instancing impostor bakes.</summary>
    public sealed class InstanceImpostorBakerWindow : EditorWindow
    {
        [SerializeField] private GameObject _source;
        [SerializeField] private InstanceImpostorBakeSettings _settings = InstanceImpostorBakeSettings.Default;

        [MenuItem("Tools/Looga/Instancing/Bake Impostor")]
        private static void Open() => GetWindow<InstanceImpostorBakerWindow>("Looga Impostor Baker");

        private void OnGUI()
        {
            _source = (GameObject)EditorGUILayout.ObjectField("Source Prefab", _source, typeof(GameObject), false);
            _settings.Mode = (InstanceImpostorMode)EditorGUILayout.EnumPopup("Mode", _settings.Mode);
            _settings.TileResolution = EditorGUILayout.IntPopup("Tile Resolution", _settings.TileResolution,
                new[] { "32", "64", "128", "256", "512" }, new[] { 32, 64, 128, 256, 512 });
            if (_settings.Mode == InstanceImpostorMode.Octahedral)
            {
                _settings.OctahedralGrid = EditorGUILayout.IntSlider("View Grid", _settings.OctahedralGrid, 2, 16);
            }
            _settings.TransitionHeight = EditorGUILayout.Slider("Transition Height", _settings.TransitionHeight, 0.001f, 0.25f);
            using (new EditorGUI.DisabledScope(!_source))
            {
                if (GUILayout.Button("Bake")) Bake();
            }
        }

        private void Bake()
        {
            string path = EditorUtility.SaveFilePanelInProject("Bake Looga Impostor", _source.name + " Impostor",
                "asset", "Choose an output asset.");
            if (string.IsNullOrEmpty(path)) return;
            InstanceImpostorAsset asset = InstanceImpostorBaker.Bake(_source, _settings, path);
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
