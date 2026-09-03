using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class MotionFreshnessReportingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CrossingTheFreshnessCapIsNotReported()
    {
        // Measured Intel transport jitter clusters just past the cap. Reporting each crossing put
        // two alternating lines into the log about 1.3 times a second.
        (MotionService motion, TestPluginHostAdapter host) = await StartAsync();

        motion.Current(Start + MotionService.MaximumMotionAge + TimeSpan.FromMilliseconds(9));

        Assert.Empty(host.Changes);
        Assert.Empty(host.Traces);
    }

    [Fact]
    public async Task APauseThatOutlastsJitterIsReportedOnce()
    {
        (MotionService motion, TestPluginHostAdapter host) = await StartAsync();

        DateTimeOffset past = Start + MotionService.StaleReportDelay + TimeSpan.FromMilliseconds(1);
        for (int frame = 0; frame < 200; frame++)
        {
            motion.Current(past + TimeSpan.FromMilliseconds(frame * 8));
        }

        (DeviceTraceLevel Level, string Scope, string Key, string Message) line =
            Assert.Single(host.Changes);
        Assert.Equal("motion", line.Scope);
        Assert.Equal("freshness", line.Key);
        Assert.Contains("holding rest", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResumeIsReportedOnlyWhereThePauseWas()
    {
        (MotionService motion, TestPluginHostAdapter host, Func<MotionSample, ValueTask> publish) =
            await StartCapturingAsync();

        // A crossing too brief to mention stays unmentioned at both ends.
        motion.Current(Start + MotionService.MaximumMotionAge + TimeSpan.FromMilliseconds(9));
        await publish(Sample(Start + TimeSpan.FromSeconds(1)));
        Assert.Empty(host.Changes);

        // A reported pause owes a resume.
        motion.Current(Start + TimeSpan.FromSeconds(1) + MotionService.StaleReportDelay
            + TimeSpan.FromMilliseconds(1));
        await publish(Sample(Start + TimeSpan.FromSeconds(3)));

        Assert.Equal(2, host.Changes.Count);
        Assert.Contains("holding rest", host.Changes[0].Message, StringComparison.Ordinal);
        Assert.Contains("resumed", host.Changes[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshReadingRearmsTheReport()
    {
        (MotionService motion, TestPluginHostAdapter host, Func<MotionSample, ValueTask> publish) =
            await StartCapturingAsync();

        for (int pause = 1; pause <= 3; pause++)
        {
            DateTimeOffset reading = Start + TimeSpan.FromSeconds(pause * 10);
            await publish(Sample(reading));
            motion.Current(reading + MotionService.StaleReportDelay + TimeSpan.FromMilliseconds(1));
        }

        // Three pauses, each reported once, each ended by the reading that opened the next.
        Assert.Equal(5, host.Changes.Count);
    }

    private static MotionSample Sample(DateTimeOffset stamp) => new()
    {
        HasGyro = true,
        HasAccelerometer = true,
        AccelZ = 1f,
        SensorTimestamp = stamp,
    };

    private static async Task<(MotionService Motion, TestPluginHostAdapter Host)> StartAsync()
    {
        (MotionService motion, TestPluginHostAdapter host, Func<MotionSample, ValueTask> publish) =
            await StartCapturingAsync();
        await publish(Sample(Start));
        return (motion, host);
    }

    private static async Task<(
        MotionService Motion,
        TestPluginHostAdapter Host,
        Func<MotionSample, ValueTask> Publish)> StartCapturingAsync()
    {
        TestPluginHostAdapter host = new(1);
        PluginTrace.Install(host);
        CapturingMotionSource source = new();
        MotionService motion = new(source);
        await motion.AcquireAsync(
            new ClawCycleContext(1, DateTimeOffset.MaxValue),
            CancellationToken.None);
        return (motion, host, source.Publish!);
    }

    private sealed class CapturingMotionSource : IClawMotionSource
    {
        public Func<MotionSample, ValueTask>? Publish { get; private set; }

        public ValueTask<bool> StartAsync(
            Func<MotionSample, ValueTask> publish,
            CancellationToken cancellationToken)
        {
            Publish = publish;
            return ValueTask.FromResult(true);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
