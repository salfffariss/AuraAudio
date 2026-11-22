using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Display-synced, interruptible spring animation engine modeled after Apple's Fluid Interfaces
/// (damping ratio + response seconds, velocity preservation on re-target, CompositionTarget.Rendering).
/// </summary>
public static class SpringAnimator
{
    private sealed class SpringState
    {
        public required UIElement Element;
        public required string Channel;
        public double Current;
        public double Target;
        public double Velocity;
        public double DampingRatio;
        public double Response;
        public double Tolerance;
        public required Action<double> Apply;
        public Action? OnComplete;
    }

    internal interface IFrameTicker
    {
        /// <summary>
        /// Advances one deduplicated compositor frame. Returns true while active, false to auto-unhook.
        /// </summary>
        bool StepFrame(double dtSeconds);
    }

    private static readonly Dictionary<(UIElement Element, string Channel), SpringState> ActiveSprings = new();
    private static readonly HashSet<UIElement> UnloadSubscribed = new();
    private static readonly HashSet<UIElement> TransientCachedElements = new();
    private static readonly List<KeyValuePair<(UIElement Element, string Channel), SpringState>> SnapshotBuffer = new(64);
    private static readonly List<((UIElement Element, string Channel) Key, SpringState State)> CompletedBuffer = new(32);
    private static readonly List<(UIElement Element, string Channel)> UnloadRemoveBuffer = new(16);
    private static readonly List<IFrameTicker> ActiveTickers = new(8);
    private static readonly List<IFrameTicker> TickerSnapshotBuffer = new(8);
    private static readonly List<IFrameTicker> CompletedTickersBuffer = new(8);
    private static readonly Stopwatch Clock = new();
    private static long _lastTimestampTicks;
    private static TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private static long _duplicateRenderingCallbacksSkipped;
    private static bool _isRenderingHooked;
    private static bool _useExternalClockForTest;

    /// <summary>
    /// Deterministic test-only frame clock switch. When true, springs do not hook
    /// CompositionTarget.Rendering and advance exclusively via <see cref="StepExternalClockForTest"/>.
    /// </summary>
    internal static bool UseExternalClockForTest
    {
        get => _useExternalClockForTest;
        set
        {
            if (_useExternalClockForTest == value) return;
            _useExternalClockForTest = value;
            if (value && _isRenderingHooked)
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
                _isRenderingHooked = false;
            }
            else if (!value)
            {
                UpdateRenderingHookState();
            }
        }
    }

    internal static bool HasActiveSprings => ActiveSprings.Count > 0 || ActiveTickers.Count > 0;
    internal static bool IsRenderingHookedForTest => _isRenderingHooked;
    internal static long DuplicateRenderingCallbacksSkippedForTest => _duplicateRenderingCallbacksSkipped;

    internal static void RegisterFrameTicker(IFrameTicker ticker)
    {
        if (!ActiveTickers.Contains(ticker))
        {
            ActiveTickers.Add(ticker);
            UpdateRenderingHookState();
        }
    }

    internal static void UnregisterFrameTicker(IFrameTicker ticker)
    {
        if (ActiveTickers.Remove(ticker))
        {
            UpdateRenderingHookState();
        }
    }

    internal static void ResetRenderingDedupForTest()
    {
        _lastRenderingTime = TimeSpan.MinValue;
        _duplicateRenderingCallbacksSkipped = 0;
    }

    internal static bool TryConsumeRenderingTime(TimeSpan renderingTime)
    {
        if (renderingTime == _lastRenderingTime)
        {
            _duplicateRenderingCallbacksSkipped++;
            return false;
        }
        _lastRenderingTime = renderingTime;
        return true;
    }

    internal static void StepExternalClockForTest(double dtSeconds)
    {
        if ((ActiveSprings.Count == 0 && ActiveTickers.Count == 0) || dtSeconds <= 0.0) return;
        StepSpringsCore(Math.Min(dtSeconds, 0.10));
    }

    private static void EnableTransientCache(UIElement el)
    {
        if (el.CacheMode == null)
        {
            el.CacheMode = new BitmapCache
            {
                EnableClearType = true,
                SnapsToDevicePixels = true
            };
            TransientCachedElements.Add(el);
        }
    }

    private static void ReleaseTransientCacheIfIdle(UIElement el)
    {
        if (!TransientCachedElements.Contains(el)) return;
        foreach (var key in ActiveSprings.Keys)
        {
            if (ReferenceEquals(key.Element, el))
            {
                return;
            }
        }
        el.CacheMode = null;
        TransientCachedElements.Remove(el);
    }

    private static bool? _forceReducedMotionOverride;

    /// <summary>
    /// Raised whenever the in-app Force Reduced Motion override changes so live controls
    /// (e.g., BouncyBarControl equalizers) pause or resume immediately.
    /// </summary>
    public static event Action? ReducedMotionChanged;

    /// <summary>
    /// Optional in-app Accessibility override for Reduced Motion (Settings -> Accessibility).
    /// Null follows the Windows system preference (SystemParameters.ClientAreaAnimation).
    /// </summary>
    public static bool? ForceReducedMotionOverride
    {
        get => _forceReducedMotionOverride;
        set
        {
            if (_forceReducedMotionOverride != value)
            {
                _forceReducedMotionOverride = value;
                ReducedMotionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// True when Windows system preferences or in-app Accessibility settings disable animations.
    /// </summary>
    public static bool IsReducedMotion => _forceReducedMotionOverride ?? !SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Ensures the element has a mutable TransformGroup containing a ScaleTransform and TranslateTransform.
    /// </summary>
    public static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransformGroup(UIElement el)
    {
        if (el.RenderTransformOrigin.X == 0 && el.RenderTransformOrigin.Y == 0)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (el.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            ScaleTransform? existingScale = null;
            TranslateTransform? existingTranslate = null;
            foreach (Transform child in group.Children)
            {
                if (child is ScaleTransform st && !st.IsFrozen) existingScale ??= st;
                else if (child is TranslateTransform tt && !tt.IsFrozen) existingTranslate ??= tt;
            }

            if (existingScale == null)
            {
                existingScale = new ScaleTransform(1.0, 1.0);
                group.Children.Add(existingScale);
            }
            if (existingTranslate == null)
            {
                existingTranslate = new TranslateTransform(0.0, 0.0);
                group.Children.Add(existingTranslate);
            }

            return (existingScale, existingTranslate);
        }

        double initScaleX = 1.0, initScaleY = 1.0, initX = 0.0, initY = 0.0;
        if (el.RenderTransform is ScaleTransform singleScale)
        {
            initScaleX = singleScale.ScaleX;
            initScaleY = singleScale.ScaleY;
        }
        else if (el.RenderTransform is TranslateTransform singleTranslate)
        {
            initX = singleTranslate.X;
            initY = singleTranslate.Y;
        }

        var scale = new ScaleTransform(initScaleX, initScaleY);
        var translate = new TranslateTransform(initX, initY);
        var newGroup = new TransformGroup();
        newGroup.Children.Add(scale);
        newGroup.Children.Add(translate);
        el.RenderTransform = newGroup;

        return (scale, translate);
    }

    public static void SpringScale(
        UIElement el,
        double targetScale,
        double dampingRatio = 1.0,
        double response = 0.26,
        double initialVelocity = 0)
    {
        var (scale, _) = EnsureTransformGroup(el);

        if (IsReducedMotion)
        {
            Cancel(el, "scale");
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;
            return;
        }

        StartOrRetarget(
            el,
            "scale",
            currentValue: scale.ScaleX,
            targetValue: targetScale,
            dampingRatio: dampingRatio,
            response: response,
            initialVelocity: initialVelocity,
            tolerance: 0.0005,
            apply: v =>
            {
                scale.ScaleX = v;
                scale.ScaleY = v;
            },
            onComplete: null);
    }

    public static void SpringTranslateY(
        UIElement el,
        double targetY,
        double dampingRatio = 1.0,
        double response = 0.34,
        double initialVelocity = 0,
        Action? onComplete = null)
    {
        var (_, translate) = EnsureTransformGroup(el);

        if (IsReducedMotion)
        {
            Cancel(el, "translateY");
            translate.Y = targetY;
            onComplete?.Invoke();
            return;
        }

        StartOrRetarget(
            el,
            "translateY",
            currentValue: translate.Y,
            targetValue: targetY,
            dampingRatio: dampingRatio,
            response: response,
            initialVelocity: initialVelocity,
            tolerance: 0.05,
            apply: v => translate.Y = v,
            onComplete: onComplete);
    }

    public static void SpringTranslateX(
        UIElement el,
        double targetX,
        double dampingRatio = 1.0,
        double response = 0.34,
        double initialVelocity = 0,
        Action? onComplete = null)
    {
        var (_, translate) = EnsureTransformGroup(el);

        if (IsReducedMotion)
        {
            Cancel(el, "translateX");
            translate.X = targetX;
            onComplete?.Invoke();
            return;
        }

        StartOrRetarget(
            el,
            "translateX",
            currentValue: translate.X,
            targetValue: targetX,
            dampingRatio: dampingRatio,
            response: response,
            initialVelocity: initialVelocity,
            tolerance: 0.05,
            apply: v => translate.X = v,
            onComplete: onComplete);
    }

    public static void SpringWidth(
        FrameworkElement el,
        double targetWidth,
        double dampingRatio = 1.0,
        double response = 0.22,
        Action? onComplete = null)
    {
        double clampedTarget = Math.Max(0.0, targetWidth);
        if (IsReducedMotion)
        {
            Cancel(el, "width");
            el.Width = clampedTarget;
            onComplete?.Invoke();
            return;
        }

        double current = double.IsNaN(el.Width) ? el.ActualWidth : el.Width;
        StartOrRetarget(
            el,
            "width",
            currentValue: current,
            targetValue: clampedTarget,
            dampingRatio: dampingRatio,
            response: response,
            initialVelocity: 0,
            tolerance: 1.0,
            apply: v => el.Width = Math.Max(0.0, v),
            onComplete: onComplete);
    }

    public static void SpringHeight(
        FrameworkElement el,
        double targetHeight,
        double dampingRatio = 1.0,
        double response = 0.20,
        Action? onComplete = null)
    {
        double clampedTarget = Math.Max(0.0, targetHeight);
        if (IsReducedMotion)
        {
            Cancel(el, "height");
            el.Height = clampedTarget;
            onComplete?.Invoke();
            return;
        }

        double current = double.IsNaN(el.Height) ? el.ActualHeight : el.Height;
        StartOrRetarget(
            el,
            "height",
            currentValue: current,
            targetValue: clampedTarget,
            dampingRatio: dampingRatio,
            response: response,
            initialVelocity: 0,
            tolerance: 0.15,
            apply: v => el.Height = Math.Max(0.0, v),
            onComplete: onComplete);
    }

    /// <summary>
    /// Spatial-origin expansion animation (apple-design §7 Spatial Consistency &amp; §14 Reduced Motion).
    /// Expands a view or sheet from its trigger origin; under Reduced Motion replaces vestibular
    /// scale/slide motion with a calm opacity cross-fade.
    /// </summary>
    public static void AnimateSpatialExpand(
        UIElement el,
        Point origin,
        double fromScale = 0.95,
        double fromY = -14.0,
        double dampingRatio = 1.0,
        double response = 0.28)
    {
        el.RenderTransformOrigin = origin;
        var (scale, translate) = EnsureTransformGroup(el);

        if (IsReducedMotion)
        {
            Cancel(el, "scale");
            Cancel(el, "translateY");
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            translate.Y = 0.0;
            el.Opacity = 0.0;
            SpringOpacity(el, 1.0, response: 0.14);
            return;
        }

        EnableTransientCache(el);
        scale.ScaleX = fromScale;
        scale.ScaleY = fromScale;
        translate.Y = fromY;
        el.Opacity = 0.12;

        SpringScale(el, 1.0, dampingRatio: dampingRatio, response: response);
        SpringTranslateY(el, 0.0, dampingRatio: dampingRatio, response: response);
        SpringOpacity(el, 1.0, response: Math.Max(0.14, response * 0.65));
    }

    /// <summary>
    /// Pure GPU-composited slide + opacity transition (TranslateTransform.X + Opacity with transient BitmapCache)
    /// that performs zero WPF Measure/Arrange layout passes per frame.
    /// </summary>
    public static void SpringSlidePane(
        UIElement el,
        double fromX,
        double toX,
        double targetOpacity,
        double dampingRatio = 1.0,
        double response = 0.22,
        Action? onComplete = null)
    {
        var (_, translate) = EnsureTransformGroup(el);

        if (IsReducedMotion)
        {
            Cancel(el, "translateX");
            translate.X = toX;
            SpringOpacity(el, targetOpacity, response: 0.14, onComplete: onComplete);
            return;
        }

        EnableTransientCache(el);
        translate.X = fromX;
        SpringTranslateX(el, toX, dampingRatio: dampingRatio, response: response, onComplete: onComplete);
        SpringOpacity(el, targetOpacity, response: Math.Max(0.14, response * 0.80));
    }

    /// <summary>
    /// Symmetric spatial-origin collapse animation (apple-design §7: exit along the entry path).
    /// Under Reduced Motion performs a calm opacity cross-fade with zero transform motion.
    /// </summary>
    public static void AnimateSpatialCollapse(
        UIElement el,
        Point origin,
        double toScale = 0.95,
        double toY = 18.0,
        double dampingRatio = 1.0,
        double response = 0.22,
        Action? onComplete = null)
    {
        el.RenderTransformOrigin = origin;
        var (scale, translate) = EnsureTransformGroup(el);

        if (IsReducedMotion)
        {
            Cancel(el, "scale");
            Cancel(el, "translateY");
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            translate.Y = 0.0;
            SpringOpacity(el, 0.0, response: 0.14, onComplete: onComplete);
            return;
        }

        EnableTransientCache(el);
        SpringScale(el, toScale, dampingRatio: dampingRatio, response: response);
        SpringTranslateY(el, toY, dampingRatio: dampingRatio, response: response);
        SpringOpacity(el, 0.0, response: Math.Max(0.14, response * 0.72), onComplete: () =>
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            translate.Y = 0.0;
            ReleaseTransientCacheIfIdle(el);
            onComplete?.Invoke();
        });
    }

    /// <summary>
    /// Subtle continuous hover lift for cards and interactive pills (apple-design §1 Response & §12 Depth).
    /// </summary>
    public static void AnimateHoverLift(
        UIElement el,
        bool isHovered,
        double hoverScale = 1.014,
        double hoverY = -1.5,
        double response = 0.20)
    {
        SpringScale(el, isHovered ? hoverScale : 1.0, dampingRatio: 1.0, response: response);
        SpringTranslateY(el, isHovered ? hoverY : 0.0, dampingRatio: 1.0, response: response);
    }

    /// <summary>
    /// Drives an arbitrary scalar progress or dimension channel on an owner element using the
    /// shared zero-allocation critically-damped spring integrator. Under Reduced Motion snaps
    /// immediately to <paramref name="targetValue"/> and invokes <paramref name="onComplete"/>.
    /// </summary>
    public static void SpringValue(
        UIElement owner,
        string channel,
        double currentValue,
        double targetValue,
        double dampingRatio,
        double response,
        double tolerance,
        Action<double> apply,
        Action? onComplete = null,
        double initialVelocity = 0.0,
        bool resetProgressOnRetarget = false)
    {
        if (IsReducedMotion)
        {
            Cancel(owner, channel);
            apply(targetValue);
            onComplete?.Invoke();
            return;
        }

        if (resetProgressOnRetarget)
        {
            ActiveSprings.Remove((owner, channel));
        }

        StartOrRetarget(
            owner,
            channel,
            currentValue: currentValue,
            targetValue: targetValue,
            dampingRatio: dampingRatio,
            response: response,
            initialVelocity: initialVelocity,
            tolerance: tolerance,
            apply: apply,
            onComplete: onComplete);
    }

    public static void SpringOpacity(
        UIElement el,
        double targetOpacity,
        double response = 0.24,
        Action? onComplete = null)
    {
        double clampedTarget = Math.Clamp(targetOpacity, 0.0, 1.0);
        double effectiveResponse = IsReducedMotion ? Math.Min(response, 0.16) : response;

        StartOrRetarget(
            el,
            "opacity",
            currentValue: el.Opacity,
            targetValue: clampedTarget,
            dampingRatio: 1.0,
            response: effectiveResponse,
            initialVelocity: 0,
            tolerance: 0.002,
            apply: v => el.Opacity = Math.Clamp(v, 0.0, 1.0),
            onComplete: onComplete);
    }

    /// <summary>
    /// Exponential momentum projection formula from Apple Designing Fluid Interfaces.
    /// </summary>
    public static double ProjectMomentum(double velocityPxPerSec, double decelerationRate = 0.998)
    {
        if (decelerationRate >= 1.0 || decelerationRate <= 0.0) return 0.0;
        return (velocityPxPerSec / 1000.0) * decelerationRate / (1.0 - decelerationRate);
    }

    /// <summary>
    /// Progressive rubber-band resistance past scroll/drag boundaries.
    /// </summary>
    public static double Rubberband(double overshoot, double dimension, double constant = 0.55)
    {
        if (dimension <= 0.0) return 0.0;
        return (overshoot * dimension * constant) / (dimension + constant * Math.Abs(overshoot));
    }

    public static void Cancel(UIElement el, string channel)
    {
        ActiveSprings.Remove((el, channel));
        ReleaseTransientCacheIfIdle(el);
        UpdateRenderingHookState();
    }

    private static void StartOrRetarget(
        UIElement el,
        string channel,
        double currentValue,
        double targetValue,
        double dampingRatio,
        double response,
        double initialVelocity,
        double tolerance,
        Action<double> apply,
        Action? onComplete)
    {
        var key = (el, channel);
        EnsureUnloadCleanup(el);

        if (ActiveSprings.TryGetValue(key, out var existing))
        {
            // Preserve live presentation value and blend velocity on mid-flight interrupt.
            existing.Target = targetValue;
            existing.DampingRatio = Math.Clamp(dampingRatio, 0.1, 2.0);
            existing.Response = Math.Max(0.08, response);
            existing.Tolerance = tolerance;
            existing.Apply = apply;
            existing.OnComplete = onComplete;
            if (Math.Abs(initialVelocity) > 1e-6)
            {
                existing.Velocity = initialVelocity;
            }
        }
        else
        {
            if (Math.Abs(currentValue - targetValue) <= tolerance && Math.Abs(initialVelocity) <= tolerance)
            {
                apply(targetValue);
                onComplete?.Invoke();
                return;
            }

            ActiveSprings[key] = new SpringState
            {
                Element = el,
                Channel = channel,
                Current = currentValue,
                Target = targetValue,
                Velocity = initialVelocity,
                DampingRatio = Math.Clamp(dampingRatio, 0.1, 2.0),
                Response = Math.Max(0.08, response),
                Tolerance = tolerance,
                Apply = apply,
                OnComplete = onComplete
            };
        }

        UpdateRenderingHookState();
    }

    private static void EnsureUnloadCleanup(UIElement el)
    {
        if (el is FrameworkElement fe && UnloadSubscribed.Add(el))
        {
            fe.Unloaded += OnElementUnloaded;
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement el) return;

        UnloadRemoveBuffer.Clear();
        foreach (var key in ActiveSprings.Keys)
        {
            if (ReferenceEquals(key.Element, el))
            {
                UnloadRemoveBuffer.Add(key);
            }
        }

        for (int i = 0; i < UnloadRemoveBuffer.Count; i++)
        {
            ActiveSprings.Remove(UnloadRemoveBuffer[i]);
        }
        UnloadRemoveBuffer.Clear();

        if (sender is FrameworkElement fe)
        {
            fe.Unloaded -= OnElementUnloaded;
        }
        UnloadSubscribed.Remove(el);
        if (TransientCachedElements.Remove(el))
        {
            el.CacheMode = null;
        }
        UpdateRenderingHookState();
    }

    private static void UpdateRenderingHookState()
    {
        if (_useExternalClockForTest)
        {
            if (_isRenderingHooked)
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
                _isRenderingHooked = false;
            }
            return;
        }

        bool hasWork = ActiveSprings.Count > 0 || ActiveTickers.Count > 0;
        if (hasWork && !_isRenderingHooked)
        {
            if (!Clock.IsRunning) Clock.Start();
            _lastTimestampTicks = Clock.ElapsedTicks;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            _isRenderingHooked = true;
        }
        else if (!hasWork && _isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _isRenderingHooked = false;
        }
    }

    private static void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (ActiveSprings.Count == 0 && ActiveTickers.Count == 0)
        {
            UpdateRenderingHookState();
            return;
        }

        // Deduplicate same-frame MediaContext callbacks caused by layout passes
        if (e is RenderingEventArgs renderingArgs && !TryConsumeRenderingTime(renderingArgs.RenderingTime))
        {
            return;
        }

        long nowTicks = Clock.ElapsedTicks;
        double frameDt = (double)(nowTicks - _lastTimestampTicks) / Stopwatch.Frequency;
        _lastTimestampTicks = nowTicks;

        if (frameDt <= 0.0) return;
        StepSpringsCore(Math.Min(frameDt, 0.10));
    }

    private static void StepSpringsCore(double dt)
    {
        if (ActiveSprings.Count > 0)
        {
            const double maxSubStep = 1.0 / 180.0;
            int steps = Math.Max(1, (int)Math.Ceiling(dt / maxSubStep));
            double subDt = dt / steps;

            CompletedBuffer.Clear();
            SnapshotBuffer.Clear();
            foreach (var kvp in ActiveSprings)
            {
                SnapshotBuffer.Add(kvp);
            }

            for (int sIdx = 0; sIdx < SnapshotBuffer.Count; sIdx++)
            {
                var entry = SnapshotBuffer[sIdx];
                var state = entry.Value;
                double omega = 2.0 * Math.PI / Math.Max(0.08, state.Response);
                double stiffness = omega * omega;
                double damping = 2.0 * state.DampingRatio * omega;

                for (int i = 0; i < steps; i++)
                {
                    double displacement = state.Current - state.Target;
                    double accel = -stiffness * displacement - damping * state.Velocity;
                    state.Velocity += accel * subDt;
                    state.Current += state.Velocity * subDt;
                }

                if (Math.Abs(state.Current - state.Target) <= state.Tolerance &&
                    Math.Abs(state.Velocity) <= state.Tolerance * 10.0)
                {
                    state.Current = state.Target;
                    state.Velocity = 0.0;
                    state.Apply(state.Target);
                    CompletedBuffer.Add((entry.Key, state));
                }
                else
                {
                    state.Apply(state.Current);
                }
            }

            for (int cIdx = 0; cIdx < CompletedBuffer.Count; cIdx++)
            {
                var item = CompletedBuffer[cIdx];
                if (ActiveSprings.TryGetValue(item.Key, out var currentState) && ReferenceEquals(currentState, item.State))
                {
                    ActiveSprings.Remove(item.Key);
                    ReleaseTransientCacheIfIdle(item.Key.Element);
                    item.State.OnComplete?.Invoke();
                }
            }

            SnapshotBuffer.Clear();
            CompletedBuffer.Clear();
        }

        if (ActiveTickers.Count > 0)
        {
            TickerSnapshotBuffer.Clear();
            CompletedTickersBuffer.Clear();
            for (int i = 0; i < ActiveTickers.Count; i++)
            {
                TickerSnapshotBuffer.Add(ActiveTickers[i]);
            }

            for (int i = 0; i < TickerSnapshotBuffer.Count; i++)
            {
                var ticker = TickerSnapshotBuffer[i];
                if (!ticker.StepFrame(dt))
                {
                    CompletedTickersBuffer.Add(ticker);
                }
            }

            for (int i = 0; i < CompletedTickersBuffer.Count; i++)
            {
                ActiveTickers.Remove(CompletedTickersBuffer[i]);
            }

            TickerSnapshotBuffer.Clear();
            CompletedTickersBuffer.Clear();
        }

        UpdateRenderingHookState();
    }
}
