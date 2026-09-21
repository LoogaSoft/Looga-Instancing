using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Applies game-owned edits after a page load. Keep source revision and record order stable across evictions.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "LoogaSoft.Terrain.Instances", "LoogaSoft.Terrain.Instances", "EditedVegetationPageSource")]
    public sealed class EditedVegetationPageSource : IVegetationPageSource
    {
        private readonly IVegetationPageSource _source;
        private readonly InstanceEditJournal _journal;
        private readonly string _sourceId;

        /// <summary>Wrap an existing source. The caller invalidates affected resident pages after edits.</summary>
        public EditedVegetationPageSource(IVegetationPageSource source, string sourceId, InstanceEditJournal journal)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                throw new ArgumentException("A stable source and revision key is required.");
            }
            _sourceId = sourceId;
        }

        /// <summary>Return the stable record key for a cell and authored page index.</summary>
        public static string RecordId(Vector2Int cell, bool trees, int index)
        {
            if (cell.x < 0 || cell.y < 0 || index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return (trees ? "tree:" : "detail:") + cell.x + ":" + cell.y + ":" + index;
        }

        /// <summary>Load and apply edits on the caller's context. Do not call this wrapper from worker threads.</summary>
        public async Task<VegetationPageRecord[]> LoadAsync(Vector2Int cell, bool trees, CancellationToken cancellation)
        {
            var records = await _source.LoadAsync(cell, trees, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var result = new List<VegetationPageRecord>(records.Length);
            for (int i = 0; i < records.Length; i++)
            {
                var record = records[i];
                var authored = new InstanceContainer.Placement { Id = record.Id ?? RecordId(cell, trees, i), Scale = Vector3.one };
                if (!_journal.Resolve(_sourceId, authored, out var resolved))
                {
                    continue;
                }
                bool changed = _journal.TryGet(_sourceId, authored.Id, out _);
                result.Add(changed ? new VegetationPageRecord(record.Prototype,
                    Matrix4x4.TRS(resolved.Position, Quaternion.Euler(resolved.EulerAngles), resolved.Scale), record.Appearance, record.Id) : record);
            }
            return result.ToArray();
        }
    }
}
