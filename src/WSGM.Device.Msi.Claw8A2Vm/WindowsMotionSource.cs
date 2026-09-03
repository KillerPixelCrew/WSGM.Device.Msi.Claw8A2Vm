using System;
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
        StationaryGyroBiasCalibrator bias = new();
        uint? lastCounter = null;
        bool readFailed = false;
        bool calibrationReported = false;
        try
        {
            using PeriodicTimer timer = new(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!sensors.TryRead(out PhysicalMotionReading reading, out string? error))
                {
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
                    continue;
                }

                lastCounter = reading.HardwareCounter;
                Vector3 corrected = bias.Correct(reading.AngularVelocity, reading.Acceleration);
                if (!calibrationReported && bias.Bias is { } calibrated)
                {
                    calibrationReported = true;
                    PluginTrace.Info(
                        "motion",
                        $"Physical gyroscope stationary bias calibrated to "
                        + $"({calibrated.X:F3}, {calibrated.Y:F3}, {calibrated.Z:F3}) degrees/second.");
                }

                writer.TryWrite(CreateSample(corrected, reading.Timestamp, reading.Acceleration));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
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

/// <summary>Removes the stationary zero-rate offset without learning normal aiming motion.</summary>
internal sealed class StationaryGyroBiasCalibrator
{
    internal const int RequiredSampleCount = 32;
    internal const float MaximumCandidateMagnitude = 1.5f;
    internal const float MaximumCandidateAxisSpan = 0.35f;
    internal const float MinimumGravityMagnitude = 0.85f;
    internal const float MaximumGravityMagnitude = 1.15f;
    internal const float RestDeadband = 0.15f;

    private int _candidateCount;
    private Vector3 _candidateSum;
    private Vector3 _candidateMinimum;
    private Vector3 _candidateMaximum;

    /// <summary>The zero-rate offset fixed for the current device cycle.</summary>
    public Vector3? Bias { get; private set; }

    /// <summary>Returns bias-corrected physical angular velocity in degrees per second.</summary>
    public Vector3 Correct(Vector3 angularVelocity, Vector3 acceleration)
    {
        if (Bias is { } bias)
        {
            return ApplyRestDeadband(angularVelocity - bias);
        }

        float gravity = acceleration.Length();
        if (!float.IsFinite(gravity)
            || gravity < MinimumGravityMagnitude
            || gravity > MaximumGravityMagnitude
            || angularVelocity.LengthSquared() > MaximumCandidateMagnitude * MaximumCandidateMagnitude)
        {
            ResetCandidate();
            return angularVelocity;
        }

        AddCandidate(angularVelocity);
        Vector3 span = _candidateMaximum - _candidateMinimum;
        if (span.X > MaximumCandidateAxisSpan
            || span.Y > MaximumCandidateAxisSpan
            || span.Z > MaximumCandidateAxisSpan)
        {
            ResetCandidate();
            AddCandidate(angularVelocity);
        }

        if (_candidateCount < RequiredSampleCount)
        {
            // A low, stable startup value is indistinguishable from zero-rate bias. Holding rest
            // during this short window prevents the offset itself from moving Steam's cursor.
            return Vector3.Zero;
        }

        Bias = _candidateSum / _candidateCount;
        return ApplyRestDeadband(angularVelocity - Bias.Value);
    }

    private static Vector3 ApplyRestDeadband(Vector3 corrected) =>
        corrected.LengthSquared() <= RestDeadband * RestDeadband
            ? Vector3.Zero
            : corrected;

    private void AddCandidate(Vector3 value)
    {
        if (_candidateCount == 0)
        {
            _candidateMinimum = value;
            _candidateMaximum = value;
        }
        else
        {
            _candidateMinimum = Vector3.Min(_candidateMinimum, value);
            _candidateMaximum = Vector3.Max(_candidateMaximum, value);
        }

        _candidateCount++;
        _candidateSum += value;
    }

    private void ResetCandidate()
    {
        _candidateCount = 0;
        _candidateSum = default;
        _candidateMinimum = default;
        _candidateMaximum = default;
    }
}
