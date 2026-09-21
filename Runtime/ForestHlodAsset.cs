using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Derived forest-cell HLOD data. Source placements and prefabs remain authoritative.</summary>
    [CreateAssetMenu(menuName = "Looga/Instancing/Forest HLOD Asset", fileName = "ForestHlod")]
    public sealed class ForestHlodAsset : ScriptableObject
    {
        [Serializable]
        public struct Cell
        {
            public string Id;
            public Bounds Bounds;
            public string[] PlacementIds;
            public GameObject Prefab;
            public int SourceCards;
            public int RetainedCards;
        }

        [SerializeField] private string _sourceId;
        [SerializeField, Min(16)] private float _cellSize = 512;
        [SerializeField, Min(1)] private float _transitionDistance = 450;
        [SerializeField] private bool _fixedLighting;
        [SerializeField] private Vector4 _lightingSignature;
        [SerializeField] private Cell[] _cells = Array.Empty<Cell>();

        public string SourceId => _sourceId;
        public float CellSize => _cellSize;
        public float TransitionDistance => _transitionDistance;
        public bool FixedLighting => _fixedLighting;
        public Vector4 LightingSignature => _lightingSignature;
        public Cell[] Cells => _cells;
        public int SourceCardCount { get; private set; }
        public int RetainedCardCount { get; private set; }

        public bool TryValidate(out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(_sourceId) || !float.IsFinite(_cellSize) || _cellSize < 16 ||
                !float.IsFinite(_transitionDistance) || _transitionDistance < 1)
            {
                reason = "Forest HLOD source, cell size, or transition distance is invalid.";
                return false;
            }
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            SourceCardCount = RetainedCardCount = 0;
            foreach (Cell cell in _cells ?? Array.Empty<Cell>())
            {
                if (string.IsNullOrWhiteSpace(cell.Id) || !ids.Add(cell.Id) || !cell.Prefab ||
                    cell.PlacementIds == null || cell.SourceCards < cell.RetainedCards || cell.RetainedCards < 1)
                {
                    reason = "Forest HLOD cells require unique IDs, a prefab, source IDs, and valid card counts.";
                    return false;
                }
                SourceCardCount += cell.SourceCards;
                RetainedCardCount += cell.RetainedCards;
            }
            return true;
        }

#if UNITY_EDITOR
        internal void SetData(string sourceId, float cellSize, float transitionDistance, bool fixedLighting,
            Vector4 lightingSignature, Cell[] cells)
        {
            _sourceId = sourceId;
            _cellSize = cellSize;
            _transitionDistance = transitionDistance;
            _fixedLighting = fixedLighting;
            _lightingSignature = lightingSignature;
            _cells = cells ?? Array.Empty<Cell>();
            TryValidate(out _);
        }
#endif
    }
}
