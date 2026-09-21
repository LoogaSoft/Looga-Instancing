using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Persistent gameplay edits keyed by source and placement IDs. The game owns storage and replication.</summary>
    [Serializable]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "InstanceEditJournal")]
    public sealed class InstanceEditJournal : ISerializationCallbackReceiver
    {
        /// <summary>One removal or complete local transform override.</summary>
        [Serializable]
        public struct Entry
        {
            public string Source;
            public string Id;
            public bool Removed;
            public InstanceContainer.Placement Placement;
        }

        [SerializeField] private List<Entry> _entries = new();
        private Dictionary<(string, string), Entry> _index;
        /// <summary>Raised after a validated edit. Listeners must not throw.</summary>
        public event Action<string> Changed;
        /// <summary>Number of persisted overrides.</summary>
        public int Count => _entries.Count;

        /// <summary>Remove an instance on every later load of this source.</summary>
        public void Remove(string source, string id) => Set(new Entry { Source = source, Id = id, Removed = true });

        /// <summary>Override a local transform on every later load of this source.</summary>
        public void Override(string source, InstanceContainer.Placement placement)
        {
            InstanceContainer.CopyValidated(new[] { placement });
            Set(new Entry { Source = source, Id = placement.Id, Placement = placement });
        }

        /// <summary>Apply proxy transform overrides and send one source change notification.</summary>
        public void OverrideMany(string source, InstanceContainer.Placement[] placements)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Source ID must be nonempty.", nameof(source));
            var copy = InstanceContainer.CopyValidated(placements);
            Index();
            foreach (var placement in copy)
            {
                var entry = new Entry { Source = source, Id = placement.Id, Placement = placement };
                _index[(source, placement.Id)] = entry;
                int index = _entries.FindIndex(item => item.Source == source && item.Id == placement.Id);
                if (index < 0) _entries.Add(entry);
                else _entries[index] = entry;
            }
            Changed?.Invoke(source);
        }

        /// <summary>Remove an override and restore authored source data.</summary>
        public bool Restore(string source, string id)
        {
            ValidateKey(source, id);
            Index();
            if (!_index.Remove((source, id))) return false;
            _entries.RemoveAll(entry => entry.Source == source && entry.Id == id);
            Changed?.Invoke(source);
            return true;
        }

        /// <summary>Resolve authored data without changing it. False means the instance is removed.</summary>
        public bool Resolve(string source, InstanceContainer.Placement authored, out InstanceContainer.Placement result)
        {
            Index();
            result = authored;
            if (!_index.TryGetValue((source, authored.Id), out var entry)) return true;
            if (entry.Removed) return false;
            result = entry.Placement;
            return true;
        }

        /// <summary>Look up one saved edit without allocating a snapshot.</summary>
        public bool TryGet(string source, string id, out Entry entry)
        {
            Index();
            return _index.TryGetValue((source, id), out entry);
        }

        /// <summary>Copy records for an external save system.</summary>
        public Entry[] Export() => _entries.ToArray();

        /// <summary>Replace saved edits after complete validation. A failed import leaves the journal unchanged.</summary>
        public void Import(Entry[] entries)
        {
            var next = new Dictionary<(string, string), Entry>();
            foreach (var entry in entries ?? Array.Empty<Entry>())
            {
                ValidateKey(entry.Source, entry.Id);
                if (!entry.Removed)
                {
                    InstanceContainer.CopyValidated(new[] { entry.Placement });
                    if (entry.Placement.Id != entry.Id)
                    {
                        throw new ArgumentException("An override must retain the placement ID.");
                    }
                }
                if (!next.TryAdd((entry.Source, entry.Id), entry))
                {
                    throw new ArgumentException("Duplicate source and placement ID in saved edits.");
                }
            }
            _entries = new List<Entry>(next.Values);
            _index = next;
            Changed?.Invoke(null);
        }

        private void Set(Entry entry)
        {
            ValidateKey(entry.Source, entry.Id);
            Index();
            _index[(entry.Source, entry.Id)] = entry;
            int index = _entries.FindIndex(item => item.Source == entry.Source && item.Id == entry.Id);
            if (index < 0)
            {
                _entries.Add(entry);
            }
            else
            {
                _entries[index] = entry;
            }
            Changed?.Invoke(entry.Source);
        }

        /// <summary>Keep serialized entries separate from the derived lookup.</summary>
        public void OnBeforeSerialize() { }

        /// <summary>Discard stale lookup data after Unity replaces the serialized entries.</summary>
        public void OnAfterDeserialize() => _index = null;

        private void Index()
        {
            if (_index != null) return;
            _index = new Dictionary<(string, string), Entry>();
            foreach (var entry in _entries)
            {
                _index.Add((entry.Source, entry.Id), entry);
            }
        }

        private static void ValidateKey(string source, string id)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Source and placement IDs must be nonempty.");
            }
        }
    }
}
