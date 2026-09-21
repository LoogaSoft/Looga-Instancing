using System;

namespace LoogaSoft.Instancing
{
    /// <summary>Snapshot of a source adapter's rendering handoff.</summary>
    public readonly struct InstanceSourceAdapterStatus
    {
        /// <summary>Logical source populations currently requested by the adapter.</summary>
        public int RequestedSources { get; }
        /// <summary>Requested populations currently rendered by Looga.</summary>
        public int LoogaOwnedSources { get; }
        /// <summary>Requested populations deliberately left on their native renderer.</summary>
        public int NativeFallbackSources { get; }
        /// <summary>True while a source is updating and ownership cannot be transferred safely.</summary>
        public bool IsWaiting { get; }
        /// <summary>Current compatibility or synchronization diagnostic.</summary>
        public string Diagnostic { get; }

        /// <summary>True when every requested source is visible through Looga or its native fallback.</summary>
        public bool HasCompleteCoverage => RequestedSources == LoogaOwnedSources + NativeFallbackSources;

        public InstanceSourceAdapterStatus(int requestedSources, int loogaOwnedSources, int nativeFallbackSources,
            bool isWaiting, string diagnostic)
        {
            if (requestedSources < 0 || loogaOwnedSources < 0 || nativeFallbackSources < 0 ||
                loogaOwnedSources + nativeFallbackSources > requestedSources)
            {
                throw new ArgumentOutOfRangeException(nameof(requestedSources));
            }
            RequestedSources = requestedSources;
            LoogaOwnedSources = loogaOwnedSources;
            NativeFallbackSources = nativeFallbackSources;
            IsWaiting = isWaiting;
            Diagnostic = diagnostic;
        }

        /// <summary>Combine independent adapters without losing mixed native/Looga ownership.</summary>
        public static InstanceSourceAdapterStatus Combine(InstanceSourceAdapterStatus left, InstanceSourceAdapterStatus right)
        {
            string diagnostic = string.IsNullOrEmpty(left.Diagnostic) ? right.Diagnostic :
                string.IsNullOrEmpty(right.Diagnostic) ? left.Diagnostic : left.Diagnostic + "\n" + right.Diagnostic;
            return new InstanceSourceAdapterStatus(left.RequestedSources + right.RequestedSources,
                left.LoogaOwnedSources + right.LoogaOwnedSources,
                left.NativeFallbackSources + right.NativeFallbackSources,
                left.IsWaiting || right.IsWaiting, diagnostic);
        }
    }

    /// <summary>Read-only ownership contract shared by TerrainData, scene, painted and runtime source adapters.</summary>
    public interface IInstanceSourceAdapter
    {
        InstanceSourceAdapterStatus SourceStatus { get; }
    }
}
