using System;
using System.Windows.Controls;
using AuraAudio.UI.Helpers;
using Xunit;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class SpringAnimatorDedupTests
{
    private sealed class CountingTicker : SpringAnimator.IFrameTicker
    {
        private readonly int _maxFrames;
        public int CallCount { get; private set; }
        public double TotalDt { get; private set; }

        public CountingTicker(int maxFrames)
        {
            _maxFrames = maxFrames;
        }

        public bool StepFrame(double dtSeconds)
        {
            CallCount++;
            TotalDt += dtSeconds;
            return CallCount < _maxFrames;
        }
    }

    [Fact]
    public void TryConsumeRenderingTime_RejectsDuplicateTimestampsAndAcceptsDistinctFrames()
    {
        SpringAnimator.ResetRenderingDedupForTest();

        var t1 = TimeSpan.FromMilliseconds(16.66);
        var t2 = TimeSpan.FromMilliseconds(33.33);

        Assert.True(SpringAnimator.TryConsumeRenderingTime(t1));
        Assert.False(SpringAnimator.TryConsumeRenderingTime(t1));
        Assert.False(SpringAnimator.TryConsumeRenderingTime(t1));
        Assert.Equal(2, SpringAnimator.DuplicateRenderingCallbacksSkippedForTest);

        Assert.True(SpringAnimator.TryConsumeRenderingTime(t2));
        Assert.False(SpringAnimator.TryConsumeRenderingTime(t2));
        Assert.Equal(3, SpringAnimator.DuplicateRenderingCallbacksSkippedForTest);
    }

    [Fact]
    public void FrameTicker_RunsZeroAllocSteadyStateAndAutoUnhooks()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool prevExt = SpringAnimator.UseExternalClockForTest;
            SpringAnimator.UseExternalClockForTest = true;
            var ticker = new CountingTicker(maxFrames: 120);

            try
            {
                SpringAnimator.RegisterFrameTicker(ticker);
                Assert.True(SpringAnimator.HasActiveSprings);

                // Warm up JIT and internal buffers
                for (int i = 0; i < 10; i++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }

                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }
                long allocAfter = GC.GetAllocatedBytesForCurrentThread();

                Assert.Equal(0, allocAfter - allocBefore);
                Assert.Equal(110, ticker.CallCount);

                // Advance remaining frames until ticker auto-unhooks
                for (int i = 0; i < 15; i++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }

                Assert.Equal(120, ticker.CallCount);
                Assert.False(SpringAnimator.HasActiveSprings);
            }
            finally
            {
                SpringAnimator.UnregisterFrameTicker(ticker);
                SpringAnimator.UseExternalClockForTest = prevExt;
            }
        });
    }

    [Fact]
    public void SpringValue_SteadyStateStepsZeroAlloc()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool prevExt = SpringAnimator.UseExternalClockForTest;
            bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.UseExternalClockForTest = true;
            SpringAnimator.ForceReducedMotionOverride = false;
            var el = new Border();
            double lastVal = 0.0;

            try
            {
                SpringAnimator.SpringValue(
                    el,
                    "zeroAllocCheck",
                    currentValue: 0.0,
                    targetValue: 100.0,
                    dampingRatio: 1.0,
                    response: 0.42,
                    tolerance: 0.01,
                    apply: v => lastVal = v);

                // Warm up
                for (int i = 0; i < 5; i++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 60; i++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }
                long after = GC.GetAllocatedBytesForCurrentThread();

                Assert.Equal(0, after - before);
                Assert.True(lastVal > 50.0);
            }
            finally
            {
                SpringAnimator.Cancel(el, "zeroAllocCheck");
                SpringAnimator.UseExternalClockForTest = prevExt;
                SpringAnimator.ForceReducedMotionOverride = prevRm;
            }
        });
    }
}
