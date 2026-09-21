using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Instancing
{
    /// <summary>Coordinates Looga streaming work with Unity APV spatial and disk-streaming budgets.</summary>
    [DisallowMultipleComponent]
    public sealed class AdaptiveProbeVolumeStreaming : MonoBehaviour
    {
        [SerializeField, Min(1)] private int _cellsLoadedPerFrame = 4;
        [SerializeField, Min(0)] private int _cellsBlendedPerFrame = 2;

        public void Configure(int cellsLoadedPerFrame, int cellsBlendedPerFrame)
        {
            if (cellsLoadedPerFrame < 1 || cellsBlendedPerFrame < 0)
                throw new System.ArgumentOutOfRangeException(nameof(cellsLoadedPerFrame));
            _cellsLoadedPerFrame = cellsLoadedPerFrame;
            _cellsBlendedPerFrame = cellsBlendedPerFrame;
            Apply();
        }

        private void OnEnable() => Apply();
        private void OnValidate()
        {
            _cellsLoadedPerFrame = Mathf.Max(1, _cellsLoadedPerFrame);
            _cellsBlendedPerFrame = Mathf.Max(0, _cellsBlendedPerFrame);
            if (isActiveAndEnabled) Apply();
        }

        public bool Apply()
        {
            ProbeReferenceVolume volume = ProbeReferenceVolume.instance;
            if (volume == null) return false;
            volume.SetNumberOfCellsLoadedPerFrame(_cellsLoadedPerFrame);
            volume.numberOfCellsBlendedPerFrame = _cellsBlendedPerFrame;
            return true;
        }
    }
}
