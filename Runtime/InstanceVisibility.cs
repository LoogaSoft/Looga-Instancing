using System;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Identifies systems that consume shared visibility.</summary>
    [Flags]
    public enum InstanceVisibilityConsumerKind
    {
        Instances = 1 << 0,
        Terrain = 1 << 1,
        Impostors = 1 << 2,
        Hlod = 1 << 3,
        External = 1 << 4
    }

    /// <summary>Identifies the camera scope that owns shared visibility.</summary>
    public enum InstanceVisibilityCameraKind
    {
        Game,
        Scene,
        Stack,
        Scope,
        Reflection,
        Preview
    }

    /// <summary>Prior-frame depth and ownership for one camera scope.</summary>
    public readonly struct InstanceVisibilityContext
    {
        public readonly Camera Camera;
        public readonly Camera ScopeCamera;
        public readonly InstanceVisibilityCameraKind CameraKind;
        public readonly Texture DepthPyramid;
        public readonly Matrix4x4 ViewProjection;
        public readonly int Width;
        public readonly int Height;
        public readonly int Levels;
        public readonly float Bias;
        public readonly bool Measure;
        public readonly int SampleId;

        internal InstanceVisibilityContext(Camera camera, Camera scope,
            InstanceVisibilityCameraKind kind, InstanceOcclusion.Snapshot snapshot)
        {
            Camera = camera;
            ScopeCamera = scope;
            CameraKind = kind;
            DepthPyramid = snapshot.Texture;
            ViewProjection = snapshot.ViewProjection;
            Width = snapshot.Width;
            Height = snapshot.Height;
            Levels = snapshot.Levels;
            Bias = snapshot.Bias;
            Measure = snapshot.Measure;
            SampleId = snapshot.SampleId;
        }
    }

    /// <summary>Shared visibility counters for diagnostics and qualification.</summary>
    public readonly struct InstanceVisibilityDiagnostics
    {
        public readonly int ActiveContexts;
        public readonly int RegisteredCameras;
        public readonly int Consumers;
        public readonly InstanceVisibilityConsumerKind ConsumerKinds;
        public readonly long CaptureClaims;
        public readonly long CapturesCompleted;
        public readonly long DuplicateCapturesSkipped;

        internal InstanceVisibilityDiagnostics(int activeContexts,
            int registeredCameras, int consumers,
            InstanceVisibilityConsumerKind consumerKinds, long captureClaims,
            long capturesCompleted, long duplicateCapturesSkipped)
        {
            ActiveContexts = activeContexts;
            RegisteredCameras = registeredCameras;
            Consumers = consumers;
            ConsumerKinds = consumerKinds;
            CaptureClaims = captureClaims;
            CapturesCompleted = capturesCompleted;
            DuplicateCapturesSkipped = duplicateCapturesSkipped;
        }
    }

    /// <summary>Shares one visibility context between Looga renderers.</summary>
    public static class InstanceVisibility
    {
        /// <summary>Registers a terrain, impostor, HLOD, or external consumer.</summary>
        public static IDisposable RegisterConsumer(InstanceVisibilityConsumerKind kind)
        {
            return InstanceOcclusion.RegisterConsumer(kind);
        }

        /// <summary>Gets valid prior-frame visibility for the requested camera.</summary>
        public static bool TryGet(Camera camera, out InstanceVisibilityContext context)
        {
            context = default;
            if (!InstanceOcclusion.TryGet(camera,
                out InstanceOcclusion.Snapshot snapshot, out Camera scope,
                out InstanceVisibilityCameraKind kind))
            {
                return false;
            }

            context = new InstanceVisibilityContext(camera, scope, kind, snapshot);
            return true;
        }

        /// <summary>Reports one adaptive visibility sample.</summary>
        public static void Report(Camera camera, int sampleId,
            uint candidates, uint visible)
        {
            InstanceOcclusion.Report(camera, sampleId, candidates, visible);
        }

        /// <summary>Gets shared visibility diagnostics.</summary>
        public static InstanceVisibilityDiagnostics GetDiagnostics()
        {
            return InstanceOcclusion.GetDiagnostics();
        }
    }
}
