using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Instancing.Universal
{
    /// <summary>Captures opaque depth once per camera for next-frame instance culling.</summary>
    public sealed class InstanceOcclusionRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private ComputeShader _depthReduction;
        [SerializeField, Range(128, 2048)] private int _maximumDimension = 512;
        [SerializeField, Min(0)] private float _depthBias = 0.0001f;
        [SerializeField, Min(0)] private float _maximumCameraMovement = 2f;
        [SerializeField, Range(0, 45)] private float _maximumCameraRotation = 5f;
        [SerializeField, Min(0)] private float _maximumProjectionChange = 0.25f;
        [SerializeField] private bool _adaptive = true;
        [SerializeField, Range(0, 1)] private float _enableRejection = 0.2f;
        [SerializeField, Range(0, 1)] private float _disableRejection = 0.15f;
        [SerializeField, Range(15, 600)] private int _sampleInterval = 120;
        [SerializeField, Min(64)] private int _minimumCandidates = 8192;

        private DepthPass _pass;
        private IDisposable _lease;
        private IDisposable _sharedLease;

        /// <summary>Enable GPU depth readback only during validation.</summary>
        public bool CaptureDiagnostics;
        /// <summary>Number of renderer owners that can use the last completed capture.</summary>
        public int RefinedViews => _pass?.RefinedViews ?? 0;
        /// <summary>Minimum and maximum raw depth from the last diagnostic readback.</summary>
        public Vector2 DepthRange => _pass?.DepthRange ?? Vector2.zero;
        /// <summary>Optional diagnostic snapshot. Subscribers own any retained copy.</summary>
        public event Action<Matrix4x4, int, int, float[]> DepthCaptured;

        /// <summary>Configure per-camera adaptive depth capture.</summary>
        public void ConfigureAdaptive(bool enabled, float enableRejection = 0.2f, float disableRejection = 0.15f,
            int sampleInterval = 120, int minimumCandidates = 8192)
        {
            _adaptive = enabled;
            _enableRejection = Mathf.Clamp01(enableRejection);
            _disableRejection = Mathf.Min(_enableRejection, Mathf.Clamp01(disableRejection));
            _sampleInterval = Mathf.Clamp(sampleInterval, 15, 600);
            _minimumCandidates = Mathf.Max(64, minimumCandidates);
        }

        public override void Create()
        {
            _sharedLease?.Dispose();
            _sharedLease = SharedResources.Acquire();
            _lease?.Dispose();
            _lease = InstanceOcclusion.Acquire();
            _depthReduction ??= Resources.Load<ComputeShader>("LoogaInstanceDepth");
            _pass = new DepthPass(this)
            {
                Shader = _depthReduction,
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            InstanceCullScheduler.Begin(cameraData.camera, this);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            // URP calls this after context.Cull and before it draws shadow casters.
            InstanceCullScheduler.Flush(data.cameraData.camera, this);
            var camera = data.cameraData;
            Camera source = camera.camera;
            Camera scope = source;
            bool stack = camera.renderType == CameraRenderType.Overlay
                && camera.baseCamera != null;
            if (stack && InstanceOcclusion.CanShareStack(source, camera.baseCamera))
            {
                scope = camera.baseCamera;
            }
            var kind = InstanceOcclusion.Classify(source, stack && scope != source);
            InstanceOcclusion.RegisterCamera(source, scope, kind);
            SharedResources.Cleanup(Time.frameCount);
            if (InstanceOcclusion.ConsumerCount == 0 || !_depthReduction || !SystemInfo.supportsComputeShaders ||
                !InstanceOcclusion.IsEligible(source) || scope != source ||
                !InstanceOcclusion.ShouldCapture(scope))
            {
                return;
            }
            _pass.Dimension = Mathf.ClosestPowerOfTwo(Mathf.Clamp(_maximumDimension, 128, 2048));
            _pass.Bias = Mathf.Max(0, _depthBias);
            _pass.MaximumMovement = Mathf.Max(0, _maximumCameraMovement);
            _pass.MaximumRotation = Mathf.Max(0, _maximumCameraRotation);
            _pass.MaximumProjectionChange = Mathf.Max(0, _maximumProjectionChange);
            _pass.Adaptive = _adaptive;
            _pass.EnableRejection = Mathf.Clamp01(_enableRejection);
            _pass.DisableRejection = Mathf.Min(_pass.EnableRejection, Mathf.Clamp01(_disableRejection));
            _pass.SampleInterval = Mathf.Max(15, _sampleInterval);
            _pass.MinimumCandidates = Mathf.Max(64, _minimumCandidates);
            _pass.Diagnostics = CaptureDiagnostics;
            _pass.Captured = DepthCaptured;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            InstanceCullScheduler.CancelFeature(this);
            _sharedLease?.Dispose();
            _sharedLease = null;
            _lease?.Dispose();
            _lease = null;
            _pass = null;
        }

        private sealed class CameraHistory
        {
            internal Camera Camera;
            internal RTHandle Pyramid;
            internal int LastFrame;
        }

        private sealed class DepthPass : ScriptableRenderPass
        {
            private readonly InstanceOcclusionRendererFeature _owner;

            internal ComputeShader Shader;
            internal int Dimension;
            internal float Bias;
            internal float MaximumMovement;
            internal float MaximumRotation;
            internal float MaximumProjectionChange;
            internal bool Adaptive;
            internal float EnableRejection;
            internal float DisableRejection;
            internal int SampleInterval;
            internal int MinimumCandidates;
            internal bool Diagnostics;
            internal int RefinedViews;
            internal Vector2 DepthRange;
            internal Action<Matrix4x4, int, int, float[]> Captured;

            internal DepthPass(InstanceOcclusionRendererFeature owner)
            {
                _owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                UniversalCameraData camera = frame.Get<UniversalCameraData>();
                UniversalResourceData resources = frame.Get<UniversalResourceData>();
                Camera source = camera.camera;
                Camera scope = source;
                bool stack = camera.renderType == CameraRenderType.Overlay
                    && camera.baseCamera != null;
                if (stack && InstanceOcclusion.CanShareStack(source, camera.baseCamera))
                {
                    scope = camera.baseCamera;
                }
                var kind = InstanceOcclusion.Classify(source, stack && scope != source);
                InstanceOcclusion.RegisterCamera(source, scope, kind);
                if (!resources.activeDepthTexture.IsValid() || scope != source ||
                    !InstanceOcclusion.TryBeginCapture(source, scope, kind))
                {
                    return;
                }
                int width = Mathf.Min(Dimension, Mathf.NextPowerOfTwo(camera.cameraTargetDescriptor.width));
                int height = Mathf.Min(Dimension, Mathf.NextPowerOfTwo(camera.cameraTargetDescriptor.height));
                CameraHistory history = SharedResources.GetHistory(scope,
                    camera.cameraTargetDescriptor, width, height);
                TextureHandle pyramid = graph.ImportTexture(history.Pyramid);
                using var builder = graph.AddUnsafePass<Data>("Looga Instances.Temporal depth capture", out Data data);
                data.Owner = this;
                data.Source = resources.activeDepthTexture;
                data.Pyramid = pyramid;
                data.Persistent = history.Pyramid;
                data.Camera = source;
                data.Scope = scope;
                data.Shader = Shader;
                data.View = camera.GetViewMatrix();
                data.Projection = camera.GetProjectionMatrix();
                data.ViewProjection = GL.GetGPUProjectionMatrix(data.Projection, true) * data.View;
                data.SourceWidth = camera.cameraTargetDescriptor.width;
                data.SourceHeight = camera.cameraTargetDescriptor.height;
                data.Width = width;
                data.Height = height;
                data.Levels = 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(width, height), 2));
                data.Bias = Bias;
                data.MaximumMovement = MaximumMovement;
                data.MaximumRotation = MaximumRotation;
                data.MaximumProjectionChange = MaximumProjectionChange;
                data.Adaptive = Adaptive;
                data.EnableRejection = EnableRejection;
                data.DisableRejection = DisableRejection;
                data.SampleInterval = SampleInterval;
                data.MinimumCandidates = MinimumCandidates;
                data.Diagnostics = Diagnostics;
                data.Captured = Captured;
                builder.UseTexture(data.Source, AccessFlags.Read);
                builder.UseTexture(data.Pyramid, AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (Data pass, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    int kernel = pass.Shader.FindKernel("Reduce");
                    int previousWidth = pass.SourceWidth;
                    int previousHeight = pass.SourceHeight;
                    cmd.SetComputeIntParam(pass.Shader, "_Reversed", SystemInfo.usesReversedZBuffer ? 1 : 0);
                    for (int level = 0; level < pass.Levels; level++)
                    {
                        int nextWidth = Mathf.Max(1, pass.Width >> level);
                        int nextHeight = Mathf.Max(1, pass.Height >> level);
                        cmd.SetComputeTextureParam(pass.Shader, kernel, "_Source",
                            level == 0 ? pass.Source : pass.Pyramid);
                        cmd.SetComputeIntParam(pass.Shader, "_SourceMip", Mathf.Max(0, level - 1));
                        cmd.SetComputeVectorParam(pass.Shader, "_ReductionSize",
                            new Vector4(previousWidth, previousHeight, nextWidth, nextHeight));
                        cmd.SetComputeTextureParam(pass.Shader, kernel, "_Destination", pass.Pyramid, level);
                        cmd.DispatchCompute(pass.Shader, kernel, (nextWidth + 7) / 8, (nextHeight + 7) / 8, 1);
                        previousWidth = nextWidth;
                        previousHeight = nextHeight;
                    }
                    InstanceOcclusion.Publish(pass.Camera, pass.Scope, pass.Persistent.rt,
                        pass.ViewProjection, pass.View, pass.Projection, pass.Width,
                        pass.Height, pass.Levels, pass.Bias, pass.MaximumMovement,
                        pass.MaximumRotation, pass.MaximumProjectionChange,
                        pass.Adaptive, pass.EnableRejection, pass.DisableRejection,
                        pass.SampleInterval, pass.MinimumCandidates);
                    pass.Owner.RefinedViews = InstanceOcclusion.RendererCount;
                    if (pass.Diagnostics)
                    {
                        DepthPass owner = pass.Owner;
                        Matrix4x4 projection = pass.ViewProjection;
                        int width = pass.Width;
                        int height = pass.Height;
                        cmd.RequestAsyncReadback(pass.Persistent.rt, 0, request =>
                        {
                            if (request.hasError)
                            {
                                return;
                            }
                            var values = request.GetData<float>();
                            float minimum = 1;
                            float maximum = 0;
                            foreach (float value in values)
                            {
                                minimum = Mathf.Min(minimum, value);
                                maximum = Mathf.Max(maximum, value);
                            }
                            owner.DepthRange = new Vector2(minimum, maximum);
                            owner.Captured?.Invoke(projection, width, height, values.ToArray());
                        });
                    }
                });
            }

            private sealed class Data
            {
                internal DepthPass Owner;
                internal TextureHandle Source;
                internal TextureHandle Pyramid;
                internal RTHandle Persistent;
                internal Camera Camera;
                internal Camera Scope;
                internal ComputeShader Shader;
                internal Matrix4x4 ViewProjection;
                internal Matrix4x4 View;
                internal Matrix4x4 Projection;
                internal int SourceWidth;
                internal int SourceHeight;
                internal int Width;
                internal int Height;
                internal int Levels;
                internal float Bias;
                internal float MaximumMovement;
                internal float MaximumRotation;
                internal float MaximumProjectionChange;
                internal bool Adaptive;
                internal float EnableRejection;
                internal float DisableRejection;
                internal int SampleInterval;
                internal int MinimumCandidates;
                internal bool Diagnostics;
                internal Action<Matrix4x4, int, int, float[]> Captured;
            }
        }

        private static class SharedResources
        {
            private static readonly Dictionary<int, CameraHistory> Histories = new();
            private static readonly List<int> Stale = new();
            private static int _leases;
            private static int _nextCleanupFrame;

            internal static IDisposable Acquire()
            {
                _leases++;
                return new SharedLease();
            }

            internal static CameraHistory GetHistory(Camera camera,
                RenderTextureDescriptor cameraDescriptor, int width, int height)
            {
                int cameraId = camera.GetInstanceID();
                if (!Histories.TryGetValue(cameraId, out CameraHistory history))
                {
                    history = new CameraHistory { Camera = camera };
                    Histories.Add(cameraId, history);
                }
                history.LastFrame = Time.frameCount;
                var descriptor = cameraDescriptor;
                descriptor.width = width;
                descriptor.height = height;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                descriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
                descriptor.enableRandomWrite = true;
                descriptor.useMipMap = true;
                descriptor.autoGenerateMips = false;
                descriptor.sRGB = false;
                if (RenderingUtils.ReAllocateHandleIfNeeded(ref history.Pyramid,
                    descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "Looga Shared Visibility " + cameraId))
                {
                    InstanceOcclusion.InvalidateHistory(camera);
                }
                return history;
            }

            internal static void Cleanup(int frame)
            {
                if (frame < _nextCleanupFrame)
                {
                    return;
                }
                _nextCleanupFrame = frame + 120;
                Stale.Clear();
                foreach (KeyValuePair<int, CameraHistory> pair in Histories)
                {
                    if (!pair.Value.Camera || frame - pair.Value.LastFrame > 600)
                    {
                        Stale.Add(pair.Key);
                    }
                }
                foreach (int cameraId in Stale)
                {
                    CameraHistory history = Histories[cameraId];
                    InstanceOcclusion.Invalidate(cameraId);
                    history.Pyramid?.Release();
                    Histories.Remove(cameraId);
                }
                Stale.Clear();
            }

            private static void Release()
            {
                _leases = Mathf.Max(0, _leases - 1);
                if (_leases != 0)
                {
                    return;
                }
                foreach (CameraHistory history in Histories.Values)
                {
                    InstanceOcclusion.Invalidate(history.Camera);
                    history.Pyramid?.Release();
                }
                Histories.Clear();
            }

            private sealed class SharedLease : IDisposable
            {
                private bool _disposed;

                public void Dispose()
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                    Release();
                }
            }
        }
    }
}
