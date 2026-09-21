using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Compatibility facade for the shared visibility service.</summary>
    public static class InstanceOcclusion
    {
        private const int MinimumBeneficialCandidates = 8192;
        private const int StaleCameraFrames = 600;
        private static readonly List<InstanceRenderer> _renderers = new();
        private static readonly Dictionary<int, History> _histories = new();
        private static readonly Dictionary<int, CameraBinding> _cameras = new();
        private static readonly Dictionary<int, int> _captureFrames = new();
        private static readonly List<int> _stale = new();
        private static readonly int[] _consumerKinds = new int[5];
        private static int _leases;
        private static int _externalConsumers;
        private static int _nextCleanupFrame;
        private static long _captureClaims;
        private static long _capturesCompleted;
        private static long _duplicateCapturesSkipped;

        /// <summary>Number of registered renderer owners.</summary>
        public static int RendererCount => _renderers.Count;

        /// <summary>Number of systems that need shared visibility.</summary>
        public static int ConsumerCount => _renderers.Count + _externalConsumers;

        /// <summary>Retain shared visibility while a render feature is active.</summary>
        public static IDisposable Acquire()
        {
            _leases++;
            return new Lease();
        }

        /// <summary>Keep shared visibility active for an external renderer.</summary>
        public static IDisposable RegisterConsumer()
        {
            return RegisterConsumer(InstanceVisibilityConsumerKind.External);
        }

        internal static IDisposable RegisterConsumer(InstanceVisibilityConsumerKind kind)
        {
            _externalConsumers++;
            ChangeConsumerKinds(kind, 1);
            return new ConsumerLease(kind);
        }

        /// <summary>Gets valid depth from the immediately preceding frame.</summary>
        public static bool TryGet(Camera camera, out Snapshot snapshot)
        {
            return TryGet(camera, out snapshot, out _, out _);
        }

        internal static bool TryGet(Camera camera, out Snapshot snapshot, out Camera scope,
            out InstanceVisibilityCameraKind kind)
        {
            snapshot = default;
            scope = null;
            kind = default;
            if (_leases == 0 || !camera || camera.stereoEnabled)
            {
                return false;
            }

            Cleanup(Time.frameCount);
            Resolve(camera, out scope, out kind);
            if (!scope || !_histories.TryGetValue(scope.GetInstanceID(), out History history)
                || history.Texture == null)
            {
                return false;
            }

            EvaluatePending(history);
            int age = Time.frameCount - history.Frame;
            if (age != 1 && (Application.isPlaying || age != 0))
            {
                return false;
            }

            if (!Matches(history, scope))
            {
                return false;
            }

            bool measure = false;
            if (history.Adaptive)
            {
                if (Time.frameCount >= history.NextSampleFrame)
                {
                    history.LastSampleId = Time.frameCount;
                    history.NextSampleFrame = Time.frameCount + history.SampleInterval;
                }
                measure = history.LastSampleId == Time.frameCount;
                if (!history.Enabled && !measure)
                {
                    return false;
                }
            }

            snapshot = new Snapshot(history.Texture, history.ViewProjection, history.Width,
                history.Height, history.Levels, history.Bias, measure, history.LastSampleId);
            return true;
        }

        internal static bool ShouldCapture(Camera camera)
        {
            if (_leases == 0 || !camera)
            {
                return false;
            }

            Resolve(camera, out Camera scope, out _);
            if (!_histories.TryGetValue(scope.GetInstanceID(), out History history))
            {
                return true;
            }

            EvaluatePending(history);
            return !history.Adaptive || history.Enabled
                || Time.frameCount + 1 >= history.NextSampleFrame;
        }

        internal static bool TryBeginCapture(Camera camera, Camera scope,
            InstanceVisibilityCameraKind kind)
        {
            if (_leases == 0 || !camera || !scope)
            {
                return false;
            }

            RegisterCamera(camera, scope, kind);
            Cleanup(Time.frameCount);
            int scopeId = scope.GetInstanceID();
            if (_captureFrames.TryGetValue(scopeId, out int frame)
                && frame == Time.frameCount)
            {
                _duplicateCapturesSkipped++;
                return false;
            }

            if (!ShouldCapture(scope))
            {
                return false;
            }

            _captureFrames[scopeId] = Time.frameCount;
            _captureClaims++;
            return true;
        }

        internal static void RegisterCamera(Camera camera, Camera scope,
            InstanceVisibilityCameraKind kind)
        {
            if (!camera || !scope)
            {
                return;
            }

            int frame = Time.frameCount;
            _cameras[camera.GetInstanceID()] = new CameraBinding(camera, scope, kind, frame);
            if (camera == scope && kind == InstanceVisibilityCameraKind.Stack)
            {
                kind = Classify(scope, false);
            }
            if (!_cameras.TryGetValue(scope.GetInstanceID(), out CameraBinding existing)
                || !existing.Camera || existing.Camera != scope)
            {
                _cameras[scope.GetInstanceID()] = new CameraBinding(scope, scope, kind, frame);
            }
            else
            {
                existing.LastFrame = frame;
            }
        }

        internal static bool IsEligible(Camera camera)
        {
            if (!camera)
            {
                return false;
            }

            return IsEligible(camera.cameraType, camera.stereoEnabled);
        }

        internal static bool IsEligible(CameraType type, bool stereo)
        {
            if (stereo)
            {
                return false;
            }

            return type == CameraType.Game || type == CameraType.SceneView
                || type == CameraType.Reflection || type == CameraType.Preview;
        }

        internal static InstanceVisibilityCameraKind Classify(Camera camera, bool stack)
        {
            bool scope = camera.targetTexture
                || camera.rect != new Rect(0, 0, 1, 1);
            return Classify(camera.cameraType, scope, stack);
        }

        internal static InstanceVisibilityCameraKind Classify(CameraType type,
            bool scope, bool stack)
        {
            if (stack)
            {
                return InstanceVisibilityCameraKind.Stack;
            }
            if (type == CameraType.SceneView)
            {
                return InstanceVisibilityCameraKind.Scene;
            }
            if (type == CameraType.Reflection)
            {
                return InstanceVisibilityCameraKind.Reflection;
            }
            if (type == CameraType.Preview)
            {
                return InstanceVisibilityCameraKind.Preview;
            }
            if (scope)
            {
                return InstanceVisibilityCameraKind.Scope;
            }
            return InstanceVisibilityCameraKind.Game;
        }

        internal static bool CanShareStack(Camera camera, Camera baseCamera)
        {
            if (!camera || !baseCamera || camera.stereoEnabled != baseCamera.stereoEnabled
                || camera.orthographic != baseCamera.orthographic
                || camera.targetDisplay != baseCamera.targetDisplay
                || camera.rect != baseCamera.rect
                || Vector3.Distance(camera.transform.position,
                    baseCamera.transform.position) > 0.001f
                || Quaternion.Angle(camera.transform.rotation,
                    baseCamera.transform.rotation) > 0.01f)
            {
                return false;
            }

            return MatrixDifference(camera.projectionMatrix,
                baseCamera.projectionMatrix) <= 0.001f;
        }

        /// <summary>Reports one asynchronous temporal-occlusion sample.</summary>
        public static void Report(Camera camera, int sampleId, uint candidates, uint visible)
        {
            if (!camera || candidates == 0)
            {
                return;
            }

            Resolve(camera, out Camera scope, out _);
            if (!scope || !_histories.TryGetValue(scope.GetInstanceID(), out History history))
            {
                return;
            }

            if (history.PendingSampleId >= 0 && history.PendingSampleId != sampleId)
            {
                EvaluatePending(history, true);
            }
            if (history.PendingSampleId != sampleId)
            {
                history.PendingSampleId = sampleId;
                history.PendingCandidates = 0;
                history.PendingVisible = 0;
            }
            history.PendingCandidates += candidates;
            history.PendingVisible += Math.Min(candidates, visible);
            history.PendingReportFrame = Time.frameCount;
        }

        /// <summary>Gets the latest adaptive decision for one camera scope.</summary>
        public static bool TryGetAdaptiveStatus(Camera camera, out AdaptiveStatus status)
        {
            status = default;
            if (!camera)
            {
                return false;
            }

            Resolve(camera, out Camera scope, out _);
            if (!scope || !_histories.TryGetValue(scope.GetInstanceID(), out History history))
            {
                return false;
            }

            EvaluatePending(history);
            status = new AdaptiveStatus(history.Adaptive, history.Enabled,
                history.LastRejection, history.LastCandidates, history.LastVisible,
                history.NextSampleFrame);
            return true;
        }

        internal static void Publish(Camera camera, Camera scope, Texture texture,
            Matrix4x4 viewProjection, Matrix4x4 view, Matrix4x4 projection,
            int width, int height, int levels, float bias, float maximumMovement,
            float maximumRotation, float maximumProjectionChange, bool adaptive,
            float enableRejection, float disableRejection, int sampleInterval,
            int minimumCandidates)
        {
            if (_leases == 0 || !camera || !scope || texture == null)
            {
                return;
            }

            int scopeId = scope.GetInstanceID();
            if (!_histories.TryGetValue(scopeId, out History history))
            {
                history = new History
                {
                    Enabled = true,
                    LastSampleId = -1,
                    PendingSampleId = -1,
                    NextSampleFrame = Time.frameCount + 1
                };
                _histories.Add(scopeId, history);
            }

            history.Texture = texture;
            history.ViewProjection = viewProjection;
            history.View = view;
            history.ProjectionMatrix = projection;
            history.Width = width;
            history.Height = height;
            history.Levels = levels;
            history.Bias = bias;
            history.Frame = Time.frameCount;
            history.Position = scope.transform.position;
            history.Rotation = scope.transform.rotation;
            history.Orthographic = scope.orthographic;
            history.Projection = scope.orthographic
                ? scope.orthographicSize : scope.fieldOfView;
            history.Aspect = scope.aspect;
            history.MaximumMovement = maximumMovement;
            history.MaximumRotation = maximumRotation;
            history.MaximumProjectionChange = maximumProjectionChange;
            history.Adaptive = adaptive;
            history.EnableRejection = Mathf.Clamp01(enableRejection);
            history.DisableRejection = Mathf.Min(history.EnableRejection,
                Mathf.Clamp01(disableRejection));
            history.SampleInterval = Mathf.Max(1, sampleInterval);
            history.MinimumCandidates = Mathf.Max(MinimumBeneficialCandidates,
                minimumCandidates);
            _capturesCompleted++;
        }

        internal static InstanceVisibilityDiagnostics GetDiagnostics()
        {
            InstanceVisibilityConsumerKind kinds = 0;
            if (_renderers.Count > 0)
            {
                kinds |= InstanceVisibilityConsumerKind.Instances;
            }
            for (int index = 0; index < _consumerKinds.Length; index++)
            {
                if (_consumerKinds[index] > 0)
                {
                    kinds |= (InstanceVisibilityConsumerKind)(1 << index);
                }
            }

            return new InstanceVisibilityDiagnostics(_histories.Count, _cameras.Count,
                ConsumerCount, kinds, _captureClaims, _capturesCompleted,
                _duplicateCapturesSkipped);
        }

        private static bool Matches(History history, Camera camera)
        {
            if (history.Orthographic != camera.orthographic
                || Vector3.Distance(history.Position, camera.transform.position)
                    > history.MaximumMovement
                || Quaternion.Angle(history.Rotation, camera.transform.rotation)
                    > history.MaximumRotation
                || Mathf.Abs(history.Aspect - camera.aspect) > 0.001f)
            {
                return false;
            }

            float projection = camera.orthographic
                ? camera.orthographicSize : camera.fieldOfView;
            if (Mathf.Abs(history.Projection - projection)
                > history.MaximumProjectionChange)
            {
                return false;
            }

            float matrixLimit = Mathf.Max(0.001f,
                history.MaximumProjectionChange * 0.05f);
            return MatrixDifference(history.ProjectionMatrix,
                camera.projectionMatrix) <= matrixLimit;
        }

        private static float MatrixDifference(Matrix4x4 left, Matrix4x4 right)
        {
            float difference = 0;
            for (int index = 0; index < 16; index++)
            {
                difference = Mathf.Max(difference,
                    Mathf.Abs(left[index] - right[index]));
            }
            return difference;
        }

        private static void Resolve(Camera camera, out Camera scope,
            out InstanceVisibilityCameraKind kind)
        {
            int cameraId = camera.GetInstanceID();
            if (_cameras.TryGetValue(cameraId, out CameraBinding binding)
                && binding.Camera == camera && binding.Scope)
            {
                binding.LastFrame = Time.frameCount;
                scope = binding.Scope;
                kind = binding.Kind;
                return;
            }

            scope = camera;
            kind = Classify(camera, false);
        }

        private static void Cleanup(int frame)
        {
            if (frame < _nextCleanupFrame)
            {
                return;
            }

            _nextCleanupFrame = frame + 120;
            _stale.Clear();
            foreach (KeyValuePair<int, CameraBinding> pair in _cameras)
            {
                if (!pair.Value.Camera || !pair.Value.Scope
                    || frame - pair.Value.LastFrame > StaleCameraFrames)
                {
                    _stale.Add(pair.Key);
                }
            }
            foreach (int cameraId in _stale)
            {
                if (_cameras.TryGetValue(cameraId, out CameraBinding binding)
                    && binding.Camera == binding.Scope)
                {
                    _histories.Remove(cameraId);
                    _captureFrames.Remove(cameraId);
                }
                _cameras.Remove(cameraId);
            }
            _stale.Clear();
        }

        private static void EvaluatePending(History history, bool force = false)
        {
            if (history.PendingSampleId < 0
                || (!force && Time.frameCount - history.PendingReportFrame < 2))
            {
                return;
            }
            if (history.PendingCandidates > 0)
            {
                float rejection = 1f
                    - history.PendingVisible / (float)history.PendingCandidates;
                history.LastRejection = rejection;
                history.LastCandidates = history.PendingCandidates;
                history.LastVisible = history.PendingVisible;
                history.Enabled = history.PendingCandidates >= Math.Max(
                    MinimumBeneficialCandidates, history.MinimumCandidates)
                    && (history.Enabled
                        ? rejection >= history.DisableRejection
                        : rejection >= history.EnableRejection);
            }
            history.PendingSampleId = -1;
            history.PendingCandidates = 0;
            history.PendingVisible = 0;
        }

        internal static void Invalidate(Camera camera)
        {
            if (camera)
            {
                Invalidate(camera.GetInstanceID());
            }
        }

        internal static void InvalidateHistory(Camera camera)
        {
            if (!camera)
            {
                return;
            }
            Resolve(camera, out Camera scope, out _);
            if (scope)
            {
                _histories.Remove(scope.GetInstanceID());
            }
        }

        internal static void Invalidate(int cameraId)
        {
            if (_cameras.TryGetValue(cameraId, out CameraBinding binding)
                && binding.Scope)
            {
                int scopeId = binding.Scope.GetInstanceID();
                _histories.Remove(scopeId);
                _captureFrames.Remove(scopeId);
            }
            _histories.Remove(cameraId);
            _captureFrames.Remove(cameraId);
            _cameras.Remove(cameraId);
        }

        internal static void Add(InstanceRenderer renderer)
        {
            _renderers.Add(renderer);
        }

        internal static void Remove(InstanceRenderer renderer)
        {
            _renderers.Remove(renderer);
        }

        private static void ChangeConsumerKinds(
            InstanceVisibilityConsumerKind kinds, int change)
        {
            for (int index = 0; index < _consumerKinds.Length; index++)
            {
                int bit = 1 << index;
                if (((int)kinds & bit) != 0)
                {
                    _consumerKinds[index] = Mathf.Max(0,
                        _consumerKinds[index] + change);
                }
            }
        }

        public readonly struct Snapshot
        {
            public readonly Texture Texture;
            public readonly Matrix4x4 ViewProjection;
            public readonly int Width;
            public readonly int Height;
            public readonly int Levels;
            public readonly float Bias;
            public readonly bool Measure;
            public readonly int SampleId;

            internal Snapshot(Texture texture, Matrix4x4 viewProjection, int width,
                int height, int levels, float bias, bool measure, int sampleId)
            {
                Texture = texture;
                ViewProjection = viewProjection;
                Width = width;
                Height = height;
                Levels = levels;
                Bias = bias;
                Measure = measure;
                SampleId = sampleId;
            }
        }

        public readonly struct AdaptiveStatus
        {
            public readonly bool Adaptive;
            public readonly bool Enabled;
            public readonly float Rejection;
            public readonly long Candidates;
            public readonly long Visible;
            public readonly int NextSampleFrame;

            internal AdaptiveStatus(bool adaptive, bool enabled, float rejection,
                long candidates, long visible, int nextSampleFrame)
            {
                Adaptive = adaptive;
                Enabled = enabled;
                Rejection = rejection;
                Candidates = candidates;
                Visible = visible;
                NextSampleFrame = nextSampleFrame;
            }
        }

        private sealed class CameraBinding
        {
            internal readonly Camera Camera;
            internal readonly Camera Scope;
            internal readonly InstanceVisibilityCameraKind Kind;
            internal int LastFrame;

            internal CameraBinding(Camera camera, Camera scope,
                InstanceVisibilityCameraKind kind, int frame)
            {
                Camera = camera;
                Scope = scope;
                Kind = kind;
                LastFrame = frame;
            }
        }

        private sealed class History
        {
            internal Texture Texture;
            internal Matrix4x4 ViewProjection;
            internal Matrix4x4 View;
            internal Matrix4x4 ProjectionMatrix;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal int Width;
            internal int Height;
            internal int Levels;
            internal int Frame;
            internal float Bias;
            internal float Projection;
            internal float Aspect;
            internal float MaximumMovement;
            internal float MaximumRotation;
            internal float MaximumProjectionChange;
            internal bool Orthographic;
            internal bool Adaptive;
            internal bool Enabled;
            internal float EnableRejection;
            internal float DisableRejection;
            internal float LastRejection;
            internal long LastCandidates;
            internal long LastVisible;
            internal int SampleInterval;
            internal int MinimumCandidates;
            internal int NextSampleFrame;
            internal int LastSampleId;
            internal int PendingSampleId;
            internal int PendingReportFrame;
            internal long PendingCandidates;
            internal long PendingVisible;
        }

        private sealed class Lease : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _leases = Mathf.Max(0, _leases - 1);
                if (_leases == 0)
                {
                    _histories.Clear();
                    _cameras.Clear();
                    _captureFrames.Clear();
                }
            }
        }

        private sealed class ConsumerLease : IDisposable
        {
            private readonly InstanceVisibilityConsumerKind _kind;
            private bool _disposed;

            internal ConsumerLease(InstanceVisibilityConsumerKind kind)
            {
                _kind = kind;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _externalConsumers = Mathf.Max(0, _externalConsumers - 1);
                ChangeConsumerKinds(_kind, -1);
            }
        }
    }
}
