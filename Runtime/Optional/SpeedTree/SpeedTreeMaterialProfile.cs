using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using LoogaSoft.Instancing.SpeedTreeInterop;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing.SpeedTree
{
    /// <summary>Unity's URP SpeedTree 8/9 Shader Graph profile with an owned engine wind proxy.</summary>
    [CreateAssetMenu(menuName = "Looga/Instancing/SpeedTree Material Profile")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.SpeedTree", "LoogaSoft.Terrain.SpeedTree", "SpeedTreeMaterialProfile")]
    public sealed class SpeedTreeMaterialProfile : InstanceMaterialProfile
    {
        [SerializeField, Min(0)] private float _boundsPadding = 10;
        [Tooltip("Copies of the SpeedTree 8 or 9 graph that keep its wind, LOD and vertex inputs.")]
        [SerializeField] private Shader[] _compatibleShaders = Array.Empty<Shader>();
        public override float BoundsPadding => _boundsPadding;
        public override bool SpeedTreeLod => true;
        public override bool Supports(Material material)
        {
            return material && (material.shader.name == "Universal Render Pipeline/Nature/SpeedTree8_PBRLit" ||
                material.shader.name == "Universal Render Pipeline/Nature/SpeedTree9_URP" ||
                Array.IndexOf(_compatibleShaders, material.shader) >= 0);
        }
        private readonly Dictionary<GameObject, WindSource> _sources = new();

        public override InstanceWindSource CreateWind(GameObject source)
        {
            if (!_sources.TryGetValue(source, out WindSource wind))
            {
                wind = new WindSource(source);
                _sources.Add(source, wind);
            }
            wind.Users++;
            return new Lease(this, source, wind);
        }

        private sealed class Lease : InstanceWindSource
        {
            private SpeedTreeMaterialProfile _owner;
            private readonly GameObject _source;
            private readonly WindSource _wind;
            public override int[] PropertyIds => _wind.PropertyIds;
            internal Lease(SpeedTreeMaterialProfile owner, GameObject source, WindSource wind)
            {
                _owner = owner;
                _source = source;
                _wind = wind;
            }
            public override void Update(NativeArray<Vector4> destination) => _wind.Update(destination);
            public override void Dispose()
            {
                if (ReferenceEquals(_owner, null)) return;
                if (--_wind.Users == 0)
                {
                    _owner._sources.Remove(_source);
                    _wind.Dispose();
                }
                _owner = null;
            }
        }

        private sealed class WindSource : InstanceWindSource
        {
            private GameObject _proxy;
            private Renderer _renderer;
            private WindReader _reader;
            private NativeArray<Vector4> _cached;
            private int _frame = -1;
            internal int Users;
            private readonly int[] _properties;
            public override int[] PropertyIds => _properties;

            internal WindSource(GameObject source)
            {
                var group = source.GetComponent<LODGroup>();
                if (!group)
                {
                    throw new NotSupportedException("SpeedTree wind requires its imported root LODGroup.");
                }
                // Clone under an inactive parent so user scripts cannot run before removal.
                var staging = new GameObject("Looga wind staging") { hideFlags = HideFlags.HideAndDontSave };
                staging.SetActive(false);
                try
                {
                    _proxy = UnityEngine.Object.Instantiate(source, staging.transform, false);
                    _proxy.name = "Looga SpeedTree wind: " + source.name;
                    _proxy.hideFlags = HideFlags.HideAndDontSave;
                    foreach (Component component in _proxy.GetComponentsInChildren<Component>(true))
                    {
                        if (component is Transform || component is Renderer || component is MeshFilter || component is LODGroup || component is Tree) continue;
                        UnityEngine.Object.DestroyImmediate(component);
                    }
                    foreach (Renderer renderer in _proxy.GetComponentsInChildren<Renderer>(true))
                    {
                        renderer.forceRenderingOff = true;
                        renderer.enabled = true;
                        renderer.gameObject.SetActive(true);
                        if (!_renderer)
                        {
                            _renderer = renderer;
                        }
                    }
                    _proxy.transform.SetParent(null, false);
                    _proxy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    _proxy.transform.localScale = Vector3.one;
                    _proxy.SetActive(true);
                    _proxy.GetComponent<LODGroup>().enabled = true;
                    _renderer = _proxy.GetComponent<LODGroup>().GetLODs()[0].renderers[0];
                    _reader = new WindReader(_renderer);
                    int count = WindReader.PropertyCount;
                    _cached = new NativeArray<Vector4>(count * 2, Allocator.Persistent);
                    _properties = new int[count * 2];
                    for (int i = 0; i < count; i++)
                    {
                        _properties[i] = Shader.PropertyToID("DOTS_ST_WindParam" + i);
                        _properties[count + i] = Shader.PropertyToID("DOTS_ST_WindHistoryParam" + i);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
                finally
                {
                    CoreUtils.Destroy(staging);
                }
            }

            public override void Update(NativeArray<Vector4> destination)
            {
                if (_frame != Time.frameCount)
                {
                    _reader.Read(_cached);
                    _frame = Time.frameCount;
                }
                destination.CopyFrom(_cached);
            }
            public override void Dispose()
            {
                _reader?.Dispose();
                _reader = null;
                if (_cached.IsCreated)
                {
                    _cached.Dispose();
                }
                if (_proxy)
                {
                    _proxy.SetActive(false);
                }
                CoreUtils.Destroy(_proxy);
                _proxy = null;
            }
        }
    }
}
