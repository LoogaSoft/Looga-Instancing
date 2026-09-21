using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Instancing
{
    /// <summary>Describes one derived quality state. The state does not change source assets or render scale.</summary>
    public readonly struct AdaptiveQualityState
    {
        /// <summary>Normalized quality. One keeps authored quality and zero uses the configured floor.</summary>
        public float Scale { get; }
        /// <summary>Measured frame cost in milliseconds.</summary>
        public float FrameMilliseconds { get; }
        /// <summary>Requested frame budget in milliseconds.</summary>
        public float TargetMilliseconds { get; }

        public AdaptiveQualityState(float scale, float frameMilliseconds, float targetMilliseconds)
        {
            Scale = Mathf.Clamp01(scale);
            FrameMilliseconds = Mathf.Max(0, frameMilliseconds);
            TargetMilliseconds = Mathf.Max(0.1f, targetMilliseconds);
        }
    }

    /// <summary>Receives adaptive changes to derived rendering data.</summary>
    public interface IAdaptiveQualityTarget
    {
        void ApplyAdaptiveQuality(AdaptiveQualityState state);
    }

    /// <summary>Creates runtime quality from an unchanged authored policy.</summary>
    public static class InstanceQualityScaler
    {
        public static InstanceQualitySettings Scale(InstanceQualitySettings source, float scale)
        {
            scale = Mathf.Clamp01(scale);
            InstanceQualitySettings result = source;
            if (source.Decorative)
            {
                result.Density = Mathf.Lerp(source.Density * 0.5f, source.Density, scale);
                result.MinimumPixels = Mathf.Max(source.MinimumPixels, (1 - scale) * 2);
            }
            result.MinimumShadowLod = Mathf.Clamp(source.MinimumShadowLod + Mathf.RoundToInt((1 - scale) * 2), 0, 7);
            if (source.ShadowDistance > 0)
            {
                result.ShadowDistance = source.ShadowDistance * Mathf.Lerp(0.55f, 1, scale);
            }
            return result;
        }
    }

    /// <summary>Adjusts derived Looga quality without dynamic resolution, upscaling, or source edits.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class AdaptiveQualityController : MonoBehaviour
    {
        [SerializeField, Min(1)] private float _targetFramesPerSecond = 90;
        [SerializeField, Range(8, 240)] private int _sampleFrames = 30;
        [SerializeField, Range(1, 600)] private int _cooldownFrames = 120;
        [SerializeField, Range(0.25f, 1)] private float _minimumScale = 0.55f;
        [SerializeField, Range(0.01f, 0.5f)] private float _decreaseStep = 0.1f;
        [SerializeField, Range(0.01f, 0.5f)] private float _increaseStep = 0.05f;
        [SerializeField, Range(1, 2)] private float _decreaseThreshold = 1.05f;
        [SerializeField, Range(0.25f, 1)] private float _increaseThreshold = 0.85f;
        [SerializeField] private bool _findTargetsOnEnable = true;
        private readonly List<IAdaptiveQualityTarget> _targets = new();
        private readonly FrameTiming[] _timings = new FrameTiming[1];
        private float _sum;
        private int _samples;
        private int _lastChangeFrame = -10000;

        /// <summary>Current normalized derived quality.</summary>
        public float Scale { get; private set; } = 1;
        /// <summary>Last completed sample-window cost in milliseconds.</summary>
        public float LastFrameMilliseconds { get; private set; }
        /// <summary>Number of active targets found by the last refresh.</summary>
        public int TargetCount => _targets.Count;

        private void OnEnable()
        {
            if (_findTargetsOnEnable)
            {
                RefreshTargets();
            }
            Apply();
        }

        private void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
        }

        private void LateUpdate()
        {
            float frame = Time.unscaledDeltaTime * 1000;
            if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
            {
                FrameTiming timing = _timings[0];
                frame = (float)Math.Max(timing.gpuFrameTime, Math.Max(timing.cpuMainThreadFrameTime,
                    timing.cpuRenderThreadFrameTime));
            }
            if (!float.IsFinite(frame) || frame <= 0)
            {
                return;
            }
            _sum += frame;
            _samples++;
            if (_samples < _sampleFrames)
            {
                return;
            }
            LastFrameMilliseconds = _sum / _samples;
            _sum = 0;
            _samples = 0;
            if (Time.frameCount - _lastChangeFrame < _cooldownFrames)
            {
                return;
            }
            float target = 1000 / Mathf.Max(1, _targetFramesPerSecond);
            float next = CalculateNextScale(Scale, LastFrameMilliseconds, target, _minimumScale,
                _decreaseStep, _increaseStep, _decreaseThreshold, _increaseThreshold);
            if (Mathf.Approximately(next, Scale))
            {
                return;
            }
            Scale = next;
            _lastChangeFrame = Time.frameCount;
            Apply();
        }

        /// <summary>Find active Looga quality targets in loaded scenes.</summary>
        public void RefreshTargets()
        {
            _targets.Clear();
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour != this && behaviour is IAdaptiveQualityTarget target)
                {
                    _targets.Add(target);
                }
            }
        }

        /// <summary>Set derived quality directly. This method does not change source assets.</summary>
        public void SetScale(float scale)
        {
            Scale = Mathf.Clamp(scale, _minimumScale, 1);
            _lastChangeFrame = Time.frameCount;
            Apply();
        }

        /// <summary>Calculate one hysteretic quality step.</summary>
        public static float CalculateNextScale(float current, float frameMilliseconds, float targetMilliseconds,
            float minimum, float decreaseStep, float increaseStep, float decreaseThreshold, float increaseThreshold)
        {
            current = Mathf.Clamp01(current);
            minimum = Mathf.Clamp01(minimum);
            if (frameMilliseconds > targetMilliseconds * decreaseThreshold)
            {
                return Mathf.Max(minimum, current - Mathf.Max(0, decreaseStep));
            }
            if (frameMilliseconds < targetMilliseconds * increaseThreshold)
            {
                return Mathf.Min(1, current + Mathf.Max(0, increaseStep));
            }
            return current;
        }

        private void Apply()
        {
            float target = 1000 / Mathf.Max(1, _targetFramesPerSecond);
            var state = new AdaptiveQualityState(Scale, LastFrameMilliseconds, target);
            for (int index = _targets.Count - 1; index >= 0; index--)
            {
                if (_targets[index] is not UnityEngine.Object item || !item)
                {
                    _targets.RemoveAt(index);
                    continue;
                }
                _targets[index].ApplyAdaptiveQuality(state);
            }
        }
    }
}
