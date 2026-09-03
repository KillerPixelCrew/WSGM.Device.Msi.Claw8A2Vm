using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class WindowsClawMotionSource : IClawMotionSource
{
    private readonly object _gate = new();
    private LegacyPhysicalMotionSensors? _sensors;
    private Channel<MotionSample>? _samples;
    private CancellationTokenSource? _cancellation;
    private Task? _producer;
    private Task? _pump;

    /// <summary>
    /// Poll faster than the physical sensor's 10 ms minimum report interval so scheduler jitter
    /// cannot routinely skip a hardware report. The counter prevents duplicate publication.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    public ValueTask<bool> StartAsync(
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_sensors is not null)
            {
                return ValueTask.FromResult(true);
            }

            LegacyPhysicalMotionSensors? sensors = LegacyPhysicalMotionSensors.TryOpen();
            if (sensors is null)
            {
                return ValueTask.FromResult(false);
            }

            Channel<MotionSample> samples = Channel.CreateBounded<MotionSample>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            CancellationTokenSource sourceCancellation = new();
            _sensors = sensors;
            _samples = samples;
            _cancellation = sourceCancellation;
            _producer = Task.Run(
                () => ProduceAsync(sensors, samples.Writer, sourceCancellation.Token),
                CancellationToken.None);
            _pump = PumpAsync(samples.Reader, publish, sourceCancellation.Token);
            return ValueTask.FromResult(true);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        LegacyPhysicalMotionSensors? sensors;
        CancellationTokenSource? sourceCancellation;
        Task? producer;
        Task? pump;
        lock (_gate)
        {
            if (_sensors is null)
            {
                return;
            }

            sensors = _sensors;
            sourceCancellation = _cancellation;
            producer = _producer;
            pump = _pump;
            _sensors = null;
            sourceCancellation?.Cancel();
        }

        try
        {
            if (producer is not null)
            {
                await producer.ConfigureAwait(false);
            }

            if (pump is not null)
            {
                await pump.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sourceCancellation?.IsCancellationRequested == true)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure("motion", "Physical IMU teardown observed a failed worker", ex);
        }
        finally
        {
            sensors.Dispose();
            sourceCancellation?.Dispose();
        }

        lock (_gate)
        {
            _samples = null;
            _cancellation = null;
            _producer = null;
            _pump = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync() =>
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>Builds one canonical sample from physical LSM6DSO sensor-space vectors.</summary>
    /// <remarks>
    /// Both physical collections share the same die axes. Steam Deck packets carry those raw axes,
    /// while Steam and SDL expose them to applications as X, Z, -Y. This is the only conversion in
    /// the plugin; the Neptune encoder applies the inverse when it writes the raw packet slots.
    /// </remarks>
    internal static MotionSample CreateSample(
        Vector3 rawAngularVelocity,
        DateTimeOffset timestamp,
        Vector3? rawAcceleration)
    {
        Vector3 gyro = ToApplicationBasis(rawAngularVelocity);
        Vector3 acceleration = rawAcceleration is { } raw
            ? ToApplicationBasis(raw)
            : default;
        return new MotionSample
        {
            GyroX = gyro.X,
            GyroY = gyro.Y,
            GyroZ = gyro.Z,
            HasGyro = true,
            AccelX = acceleration.X,
            AccelY = acceleration.Y,
            AccelZ = acceleration.Z,
            HasAccelerometer = rawAcceleration.HasValue,
            SensorTimestamp = timestamp,
        };
    }

    private static Vector3 ToApplicationBasis(Vector3 raw) =>
        new(raw.X, raw.Z, -raw.Y);

    private static async Task ProduceAsync(
        LegacyPhysicalMotionSensors sensors,
        ChannelWriter<MotionSample> writer,
        CancellationToken cancellationToken)
    {
        GyroCsvLog? csv = GyroCsvLog.TryCreateDefault();
        uint? lastCounter = null;
        DateTimeOffset? previousSensorTimestamp = null;
        long? previousReceivedTick = null;
        ulong pollIndex = 0;
        ulong freshIndex = 0;
        int duplicatePolls = 0;
        int readFailures = 0;
        bool readFailed = false;
        try
        {
            using PeriodicTimer timer = new(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                pollIndex++;
                long readStarted = Stopwatch.GetTimestamp();
                if (!sensors.TryRead(out PhysicalMotionReading reading, out string? error))
                {
                    readFailures++;
                    if (!readFailed)
                    {
                        readFailed = true;
                        PluginTrace.Warn("motion", $"Physical IMU read failed: {error}");
                    }

                    continue;
                }

                if (readFailed)
                {
                    readFailed = false;
                    PluginTrace.Info("motion", "Physical IMU readings resumed.");
                }

                if (lastCounter == reading.HardwareCounter)
                {
                    duplicatePolls++;
                    continue;
                }

                long receivedTick = Stopwatch.GetTimestamp();
                DateTimeOffset receivedTimestamp = DateTimeOffset.UtcNow;
                uint? counterDelta = lastCounter is { } priorCounter
                    ? unchecked(reading.HardwareCounter - priorCounter)
                    : null;
                double? receiveDelta = previousReceivedTick is { } priorReceivedTick
                    ? Stopwatch.GetElapsedTime(priorReceivedTick, receivedTick).TotalMilliseconds
                    : null;
                double? sensorDelta = previousSensorTimestamp is { } priorSensorTimestamp
                    ? (reading.Timestamp - priorSensorTimestamp).TotalMilliseconds
                    : null;
                freshIndex++;
                csv?.Write(new GyroCsvRow(
                    freshIndex,
                    pollIndex,
                    receivedTimestamp,
                    reading.Timestamp,
                    receiveDelta,
                    sensorDelta,
                    (receivedTimestamp - reading.Timestamp).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(readStarted, receivedTick).TotalMilliseconds,
                    reading.HardwareCounter,
                    counterDelta,
                    duplicatePolls,
                    readFailures,
                    reading.AngularVelocity,
                    reading.AngularVelocity,
                    reading.Acceleration));
                // Steam's Deck target expects the physical angular rate and owns its controller
                // calibration. A device-layer bias/deadband turns sensor noise into discontinuous
                // pulses and fights that target calibration.
                writer.TryWrite(CreateSample(
                    reading.AngularVelocity,
                    reading.Timestamp,
                    reading.Acceleration));
                lastCounter = reading.HardwareCounter;
                previousReceivedTick = receivedTick;
                previousSensorTimestamp = reading.Timestamp;
                duplicatePolls = 0;
                readFailures = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
            if (csv is not null)
            {
                await csv.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task PumpAsync(
        ChannelReader<MotionSample> reader,
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        await foreach (MotionSample sample in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await publish(sample).ConfigureAwait(false);
        }
    }
}
