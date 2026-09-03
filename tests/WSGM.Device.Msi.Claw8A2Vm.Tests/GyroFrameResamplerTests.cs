using System.Numerics;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class GyroFrameResamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AConstantRateResamplesToItself()
    {
        GyroFrameResampler resampler = new();
        resampler.OnReading(new Vector3(90f, 0f, 0f), T0);
        resampler.FrameAverage(T0);

        Vector3 average = resampler.FrameAverage(T0.AddMilliseconds(8));

        Assert.Equal(90f, average.X, 3);
        Assert.Equal(0f, average.Y, 3);
    }

    [Fact]
    public void FramesPreserveTheIntegratedAngleAcrossTheCadenceBeat()
    {
        GyroFrameResampler resampler = new();
        // 100 Hz sensor updates against 8 ms frames for one second. The sum of per-frame average
        // times frame length must equal the zero-order-held sensor integral exactly — that is the
        // property that removes the 40 ms beat without changing the total rotation.
        resampler.OnReading(new Vector3(0f, 0f, 0f), T0);
        resampler.FrameAverage(T0);
        double expected = 0;
        double integrated = 0;
        var previousRate = 0f;
        DateTimeOffset lastFrame = T0;
        for (var ms = 1; ms <= 1000; ms++)
        {
            DateTimeOffset now = T0.AddMilliseconds(ms);
            if (ms % 10 == 0)
            {
                float rate = 60f + (40f * MathF.Sin(ms / 90f));
                expected += previousRate * 0.010;
                previousRate = rate;
                resampler.OnReading(new Vector3(rate, 0f, 0f), now);
            }

            if (ms % 8 == 0)
            {
                Vector3 average = resampler.FrameAverage(now);
                integrated += average.X * (now - lastFrame).TotalSeconds;
                lastFrame = now;
            }
        }

        // Compare over the span both clocks fully covered.
        Assert.Equal(expected, integrated, 0.5);
    }

    [Fact]
    public void AQuietSensorDecaysTheAverageToZero()
    {
        GyroFrameResampler resampler = new();
        resampler.OnReading(new Vector3(120f, 0f, 0f), T0);
        resampler.FrameAverage(T0);

        // The held rate stops counting at the quiet cap, so a frame long after it sees only the
        // capped slice, and the next frame sees nothing at all.
        Vector3 tail = resampler.FrameAverage(T0.AddSeconds(1));
        Vector3 rest = resampler.FrameAverage(T0.AddSeconds(1.008));

        Assert.True(tail.X < 120f * 0.3f, $"tail={tail.X}");
        Assert.Equal(0f, rest.X, 4);
    }

    [Fact]
    public void ANonAdvancingFrameClockHoldsTheReading()
    {
        GyroFrameResampler resampler = new();
        resampler.OnReading(new Vector3(45f, 0f, 0f), T0);
        resampler.FrameAverage(T0.AddMilliseconds(8));

        Vector3 repeat = resampler.FrameAverage(T0.AddMilliseconds(8));

        Assert.Equal(45f, repeat.X, 3);
    }

    [Fact]
    public void ResetDropsMotionFromThePreviousDeviceCycle()
    {
        GyroFrameResampler resampler = new();
        resampler.OnReading(new Vector3(45f, 0f, 0f), T0);
        resampler.FrameAverage(T0.AddMilliseconds(8));

        resampler.Reset();

        Assert.Equal(Vector3.Zero, resampler.FrameAverage(T0.AddSeconds(1)));
    }
}
