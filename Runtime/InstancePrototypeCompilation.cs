using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Severity of one prefab compiler diagnostic.</summary>
    public enum InstancePrototypeDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>One non-destructive prefab compiler finding.</summary>
    public readonly struct InstancePrototypeDiagnostic
    {
        /// <summary>Stable machine-readable finding code.</summary>
        public string Code { get; }
        /// <summary>Finding severity.</summary>
        public InstancePrototypeDiagnosticSeverity Severity { get; }
        /// <summary>Hierarchy path relative to the source root.</summary>
        public string HierarchyPath { get; }
        /// <summary>Human-readable finding.</summary>
        public string Message { get; }

        internal InstancePrototypeDiagnostic(string code, InstancePrototypeDiagnosticSeverity severity,
            string hierarchyPath, string message)
        {
            Code = code;
            Severity = severity;
            HierarchyPath = hierarchyPath ?? string.Empty;
            Message = message ?? string.Empty;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string location = string.IsNullOrEmpty(HierarchyPath) ? string.Empty : " [" + HierarchyPath + "]";
            return Severity + " " + Code + location + ": " + Message;
        }
    }

    /// <summary>Extracted prefab data used to review a compiled prototype.</summary>
    public readonly struct InstancePrototypeDescriptor
    {
        /// <summary>Number of compatible mesh renderers.</summary>
        public int RendererCount { get; }
        /// <summary>Number of unique source meshes.</summary>
        public int MeshCount { get; }
        /// <summary>Number of unique source materials.</summary>
        public int MaterialCount { get; }
        /// <summary>Number of mesh/submesh draw parts across all LODs.</summary>
        public int PartCount { get; }
        /// <summary>Number of source LOD levels.</summary>
        public int LodCount { get; }
        /// <summary>Number of renderers that cast shadows.</summary>
        public int ShadowCasterCount { get; }
        /// <summary>Number of renderers that receive shadows.</summary>
        public int ShadowReceiverCount { get; }
        /// <summary>Combined local-space renderer bounds.</summary>
        public Bounds LocalBounds { get; }
        /// <summary>True when the source keeps native collider components.</summary>
        public bool HasColliders { get; }
        /// <summary>True when the source profile or material declares wind displacement.</summary>
        public bool HasWind { get; }
        /// <summary>True when the source uses Unity's SpeedTree LOD fade mode.</summary>
        public bool UsesSpeedTreeLod { get; }

        internal InstancePrototypeDescriptor(int rendererCount, int meshCount, int materialCount, int partCount,
            int lodCount, int shadowCasterCount, int shadowReceiverCount, Bounds localBounds, bool hasColliders,
            bool hasWind, bool usesSpeedTreeLod)
        {
            RendererCount = rendererCount;
            MeshCount = meshCount;
            MaterialCount = materialCount;
            PartCount = partCount;
            LodCount = lodCount;
            ShadowCasterCount = shadowCasterCount;
            ShadowReceiverCount = shadowReceiverCount;
            LocalBounds = localBounds;
            HasColliders = hasColliders;
            HasWind = hasWind;
            UsesSpeedTreeLod = usesSpeedTreeLod;
        }
    }

    /// <summary>Result of compiling an ordinary prefab hierarchy into Looga draw data.</summary>
    public sealed class InstancePrototypeCompilation
    {
        private readonly InstancePrototypeDiagnostic[] _diagnostics;

        /// <summary>Compiled prototype, or null when errors require native fallback.</summary>
        public InstancePrototype Prototype { get; }
        /// <summary>Extracted source summary.</summary>
        public InstancePrototypeDescriptor Descriptor { get; }
        /// <summary>Revision of all renderer data that affects the derived prototype.</summary>
        public Hash128 SourceRevision { get; }
        /// <summary>True when unchanged derived data was reused.</summary>
        public bool ReusedDerivedData { get; }
        /// <summary>Compiler diagnostics. Unsupported renderers are left to their native path.</summary>
        public IReadOnlyList<InstancePrototypeDiagnostic> Diagnostics => _diagnostics;
        /// <summary>True when a prototype is available.</summary>
        public bool Succeeded => Prototype != null;
        /// <summary>All diagnostics formatted for an Inspector or log.</summary>
        public string Summary
        {
            get
            {
                if (_diagnostics.Length == 0)
                {
                    return Succeeded ? "Prefab is compatible." : "Prefab compilation failed.";
                }
                var text = new StringBuilder();
                for (int i = 0; i < _diagnostics.Length; i++)
                {
                    if (i > 0) text.AppendLine();
                    text.Append(_diagnostics[i]);
                }
                return text.ToString();
            }
        }

        internal InstancePrototypeCompilation(InstancePrototype prototype, InstancePrototypeDescriptor descriptor,
            Hash128 sourceRevision, bool reusedDerivedData, List<InstancePrototypeDiagnostic> diagnostics)
        {
            Prototype = prototype;
            Descriptor = descriptor;
            SourceRevision = sourceRevision;
            ReusedDerivedData = reusedDerivedData;
            _diagnostics = diagnostics.ToArray();
        }
    }
}
