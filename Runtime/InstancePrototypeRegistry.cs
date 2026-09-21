using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Deterministic identity for one share-compatible draw prototype.</summary>
    public readonly struct InstancePrototypeId : IEquatable<InstancePrototypeId>
    {
        private readonly Hash128 _value;
        public Hash128 Value => _value;
        public bool IsValid => _value.isValid;
        internal InstancePrototypeId(Hash128 value) => _value = value;
        public bool Equals(InstancePrototypeId other) => _value.Equals(other._value);
        public override bool Equals(object obj) => obj is InstancePrototypeId other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value.ToString();
        public static bool operator ==(InstancePrototypeId left, InstancePrototypeId right) => left.Equals(right);
        public static bool operator !=(InstancePrototypeId left, InstancePrototypeId right) => !left.Equals(right);
    }

    /// <summary>Current CPU-side prototype sharing diagnostics.</summary>
    public readonly struct InstancePrototypeRegistryDiagnostics
    {
        public int ActivePrototypes { get; }
        public int References { get; }
        public long Hits { get; }
        public long Misses { get; }
        public long HashCollisions { get; }
        internal InstancePrototypeRegistryDiagnostics(int active, int references, long hits, long misses, long collisions)
        {
            ActivePrototypes = active;
            References = references;
            Hits = hits;
            Misses = misses;
            HashCollisions = collisions;
        }
    }

    /// <summary>A reference to one canonical immutable prototype. Dispose it with the owning renderer.</summary>
    public sealed class InstancePrototypeRegistration : IDisposable
    {
        private InstancePrototypeRegistry.Entry _entry;
        public InstancePrototypeId Id => _entry == null ? default : _entry.Id;
        public InstancePrototype Prototype => _entry?.Prototype;
        internal InstancePrototypeRegistration(InstancePrototypeRegistry.Entry entry) => _entry = entry;
        public void Dispose()
        {
            InstancePrototypeRegistry.Entry entry = _entry;
            _entry = null;
            if (entry != null) InstancePrototypeRegistry.Release(entry);
        }
    }

    /// <summary>World-wide canonical prototype metadata shared by every Looga renderer.</summary>
    public static class InstancePrototypeRegistry
    {
        internal sealed class Entry
        {
            internal InstancePrototypeId Id;
            internal InstancePrototype Prototype;
            internal int References;
        }

        private static readonly Dictionary<InstancePrototypeId, Entry> Entries = new();
        private static int _thread;
        private static long _hits;
        private static long _misses;
        private static long _collisions;

        /// <summary>Compute the deterministic structural identity without retaining the prototype.</summary>
        public static InstancePrototypeId GetId(InstancePrototype prototype)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            return new InstancePrototypeId(Hash128.Compute(Signature(prototype, false)));
        }

        /// <summary>Acquire a canonical share-compatible prototype for a renderer lifetime.</summary>
        public static InstancePrototypeRegistration Acquire(InstancePrototype prototype)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            CheckThread();
            InstancePrototypeId id = GetId(prototype);
            if (Entries.TryGetValue(id, out Entry entry))
            {
                if (ReferenceEquals(entry.Prototype, prototype) || entry.Prototype.CanShareWith(prototype))
                {
                    entry.References++;
                    _hits++;
                    return new InstancePrototypeRegistration(entry);
                }
                _collisions++;
                id = ResolveCollision(prototype, id);
                if (Entries.TryGetValue(id, out entry) &&
                    (ReferenceEquals(entry.Prototype, prototype) || entry.Prototype.CanShareWith(prototype)))
                {
                    entry.References++;
                    _hits++;
                    return new InstancePrototypeRegistration(entry);
                }
            }
            // Keep the first live source while registrations exist: material profiles can create wind data from it.
            entry = new Entry { Id = id, Prototype = prototype, References = 1 };
            Entries.Add(id, entry);
            _misses++;
            return new InstancePrototypeRegistration(entry);
        }

        public static InstancePrototypeRegistryDiagnostics GetDiagnostics()
        {
            int references = 0;
            foreach (Entry entry in Entries.Values) references += entry.References;
            return new InstancePrototypeRegistryDiagnostics(Entries.Count, references, _hits, _misses, _collisions);
        }

        internal static void Release(Entry entry)
        {
            CheckThread();
            if (--entry.References > 0) return;
            Entries.Remove(entry.Id);
        }

        private static InstancePrototypeId ResolveCollision(InstancePrototype prototype, InstancePrototypeId baseId)
        {
            string exact = Signature(prototype, true);
            for (int salt = 0; ; salt++)
            {
                var candidate = new InstancePrototypeId(Hash128.Compute(baseId + ":" + exact + ":" + salt));
                if (!Entries.TryGetValue(candidate, out Entry existing) || ReferenceEquals(existing.Prototype, prototype) ||
                    existing.Prototype.CanShareWith(prototype)) return candidate;
            }
        }

        private static string Signature(InstancePrototype prototype, bool exactRuntimeIdentity)
        {
            var text = new StringBuilder(1024);
            Append(text, prototype.HasColliders);
            Append(text, prototype.LodBias);
            Append(text, prototype.LegacyProbes);
            Append(text, prototype.NativeLodDistance);
            Append(text, prototype.Bounds.center);
            Append(text, prototype.Bounds.size);
            Append(text, prototype.LodCenter);
            Append(text, prototype.LodSize);
            Append(text, prototype.CrossFade);
            Append(text, prototype.AnimatedCrossFade);
            Append(text, prototype.PercentageLods);
            ObjectKey(text, prototype.Profile, exactRuntimeIdentity);
            if (prototype.Profile)
            {
                Append(text, prototype.Profile.GetType().AssemblyQualifiedName);
                Append(text, JsonUtility.ToJson(prototype.Profile));
                ObjectKey(text, prototype.Source, exactRuntimeIdentity);
            }
            Append(text, prototype.Thresholds.Length);
            for (int i = 0; i < prototype.Thresholds.Length; i++)
            {
                Append(text, prototype.Thresholds[i]);
                Append(text, prototype.FadeWidths[i]);
            }
            Append(text, prototype.Parts.Length);
            foreach (InstancePrototype.Part part in prototype.Parts)
            {
                ObjectKey(text, part.Mesh, exactRuntimeIdentity);
                if (part.Mesh)
                {
                    Append(text, part.Mesh.vertexCount);
                    Append(text, part.Mesh.subMeshCount);
                    Append(text, part.Mesh.bounds.center);
                    Append(text, part.Mesh.bounds.size);
                }
                ObjectKey(text, part.Material, exactRuntimeIdentity);
                Append(text, part.Material ? part.Material.ComputeCRC() : 0);
                Append(text, part.Material && part.Material.shader ? part.Material.shader.name : string.Empty);
                Append(text, part.Local);
                Append(text, part.Submesh);
                Append(text, part.Lod);
                Append(text, (int)part.Shadows);
                Append(text, part.ReceiveShadows);
                Append(text, part.HasMotion);
                Append(text, unchecked((int)part.RenderingLayers));
                Append(text, part.MaterialColor);
                Append(text, part.MeshLevel);
                Append(text, part.MeshLevels);
                Append(text, unchecked((int)part.IndexStart));
                Append(text, unchecked((int)part.IndexCount));
                Append(text, part.MeshSelection);
                Append(text, part.LightmapIndex);
                Append(text, part.LightmapST);
            }
            return text.ToString();
        }

        private static void ObjectKey(StringBuilder text, UnityEngine.Object value, bool exact)
        {
            if (!value)
            {
                Append(text, "<null>");
                return;
            }
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId))
            {
                Append(text, guid);
                Append(text, localId.ToString(CultureInfo.InvariantCulture));
                return;
            }
#endif
            Append(text, value.GetType().AssemblyQualifiedName);
            Append(text, value.name);
            if (exact) Append(text, value.GetInstanceID());
        }

        private static void Append(StringBuilder text, bool value) => Append(text, value ? 1 : 0);
        private static void Append(StringBuilder text, int value) => text.Append(value).Append('|');
        private static void Append(StringBuilder text, ushort value) => Append(text, (int)value);
        private static void Append(StringBuilder text, long value) => text.Append(value).Append('|');
        private static void Append(StringBuilder text, float value) => Append(text, BitConverter.SingleToInt32Bits(value));
        private static void Append(StringBuilder text, string value) => text.Append(value ?? string.Empty).Append('|');
        private static void Append(StringBuilder text, Vector3 value) { Append(text, value.x); Append(text, value.y); Append(text, value.z); }
        private static void Append(StringBuilder text, Vector4 value) { Append(text, value.x); Append(text, value.y); Append(text, value.z); Append(text, value.w); }
        private static void Append(StringBuilder text, Color value) { Append(text, value.r); Append(text, value.g); Append(text, value.b); Append(text, value.a); }
        private static void Append(StringBuilder text, Matrix4x4 value) { for (int i = 0; i < 16; i++) Append(text, value[i]); }

        private static void CheckThread()
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (_thread == 0) _thread = thread;
            else if (_thread != thread) throw new InvalidOperationException("Prototype registry mutations must run on the Unity main thread.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            Entries.Clear();
            _thread = 0;
            _hits = _misses = _collisions = 0;
        }
    }
}
