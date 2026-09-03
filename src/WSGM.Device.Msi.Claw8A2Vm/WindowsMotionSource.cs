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

    /// <summary>
    /// Report a refined zero-rate offset only once it has moved by more than the residual a single
    /// rest window can resolve, so a settling estimate does not fill the log with noise.
    /// </summary>
    private const float MinimumLoggedBiasChange = 0.05f;

    /// <summary>
    /// Roughly ten seconds of reports. Reaching this without a measured offset means the device
    /// never held still, which is the decisive fact behind an uncorrected drift complaint.
    /// </summary>
    private const ulong UncalibratedReportSampleCount = 1000;

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
        StationaryGyroBiasCalibrator calibrator = new();
        uint? lastCounter = null;
        ulong freshIndex = 0;
        bool readFailed = false;
        Vector3? reportedBias = null;
        bool uncalibratedReported = false;
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

                freshIndex++;
                // This IMU's zero-rate offset reaches the wire as a permanent rotation no target
                // removes: the Deck's own gyro is offset-free in hardware, so Steam integrates
                // whatever arrives. Correcting it here is the only place it can be corrected.
                Vector3 corrected = calibrator.Correct(
                    reading.AngularVelocity,
                    reading.Acceleration);
                if (calibrator.Bias is { } bias)
                {
                    if (reportedBias is not { } priorBias
                        || (bias - priorBias).Length() > MinimumLoggedBiasChange)
                    {
                        reportedBias = bias;
                        PluginTrace.Info(
                            "motion",
                            $"Physical gyroscope zero-rate offset measured at "
                            + $"({bias.X:F3}, {bias.Y:F3}, {bias.Z:F3}) degrees/second.");
                    }
                }
                else if (!uncalibratedReported && freshIndex >= UncalibratedReportSampleCount)
                {
                    uncalibratedReported = true;
                    PluginTrace.Info(
                        "motion",
                        $"Physical gyroscope is still uncorrected after {freshIndex} reports: no "
                        + $"{StationaryGyroBiasCalibrator.WindowSampleCount}-report rest window has "
                        + "occurred yet, so its zero-rate offset remains unmeasured.");
                }

                writer.TryWrite(CreateSample(corrected, reading.Timestamp, reading.Acceleration));
                lastCounter = reading.HardwareCounter;
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

/// <summary>Subtracts this IMU's measured zero-rate offset without absorbing aiming motion.</summary>
/// <remarks>
/// Correction is plain subtraction: no deadband and no zero-hold, so sensor noise stays continuous
/// and every rate a target integrates is the rate the die reported. The offset is measured from
/// rest windows recognized by three device-derived gates, whose thresholds are sized against the
/// stationary noise recorded in <c>gyro.csv</c> — see the plugin README. A pure yaw rotation is the
/// one motion no accelerometer gate can distinguish from rest, so a latched offset only ever moves
/// by a bounded fraction of a bounded correction.
/// </remarks>
internal sealed class StationaryGyroBiasCalibrator
{
    /// <summary>Reports per rest window: about two seconds at the gyrometer's 100 Hz cadence.</summary>
    internal const int WindowSampleCount = 200;

    /// <summary>
    /// Per-axis peak-to-peak angular rate a rest window may span. The noisiest axis spans up to
    /// 1.47 degrees/second across 200 stationary reports, so this admits every real rest window
    /// while a hand's changing rate breaks the window immediately.
    /// </summary>
    internal const float MaximumAxisSpan = 2f;

    /// <summary>
    /// Per-axis peak-to-peak acceleration a rest window may span, in g. Stationary reports span at
    /// most 0.023 g; 0.05 g still detects roughly 1.4 degrees/second of pitch or roll, which is
    /// what makes a slowly tilted device fail the gate instead of teaching a false offset.
    /// </summary>
    internal const float MaximumAccelerationSpan = 0.05f;

    /// <summary>The narrowest gravity magnitude, in g, that a rest window's acceleration may show.</summary>
    internal const float MinimumGravityMagnitude = 0.85f;

    /// <summary>The widest gravity magnitude, in g, that a rest window's acceleration may show.</summary>
    internal const float MaximumGravityMagnitude = 1.15f;

    /// <summary>
    /// The largest offset magnitude accepted as hardware, in degrees/second. This part's measured
    /// offset is under 1; anything far above it is a sustained rotation, not a zero-rate error.
    /// </summary>
    internal const float MaximumBiasMagnitude = 5f;

    /// <summary>How far, per axis, a later rest window may pull an already measured offset.</summary>
    internal const float MaximumRefinementDelta = 0.5f;

    /// <summary>The fraction of an accepted refinement applied, damping a contaminated window.</summary>
    internal const float RefinementWeight = 0.25f;

    private int _count;
    private Vector3 _angularSum;
    private Vector3 _angularMinimum;
    private Vector3 _angularMaximum;
    private Vector3 _accelerationMinimum;
    private Vector3 _accelerationMaximum;

    /// <summary>The zero-rate offset measured so far in this device cycle, in degrees/second.</summary>
    /// <remarks>Null until the first rest window completes; corrections pass through until then.</remarks>
    public Vector3? Bias { get; private set; }

    /// <summary>Observes one report and returns its corrected angular velocity.</summary>
    /// <param name="angularVelocity">Sensor-space angular velocity in degrees/second.</param>
    /// <param name="acceleration">The same report's acceleration in g, used only to detect rest.</param>
    /// <returns>
    /// The angular velocity less the measured offset, or unchanged while no offset is known. A
    /// caller receiving an uncorrected value is being told honestly that rest has not occurred.
    /// </returns>
    public Vector3 Correct(Vector3 angularVelocity, Vector3 acceleration)
    {
        Observe(angularVelocity, acceleration);
        return Bias is { } bias ? angularVelocity - bias : angularVelocity;
    }

    private void Observe(Vector3 angularVelocity, Vector3 acceleration)
    {
        float gravity = acceleration.Length();
        if (!float.IsFinite(gravity)
            || !float.IsFinite(angularVelocity.LengthSquared())
            || gravity < MinimumGravityMagnitude
            || gravity > MaximumGravityMagnitude)
        {
            ResetWindow();
            return;
        }

        Accumulate(angularVelocity, acceleration);
        if (Exceeds(_angularMaximum - _angularMinimum, MaximumAxisSpan)
            || Exceeds(_accelerationMaximum - _accelerationMinimum, MaximumAccelerationSpan))
        {
            // The device moved during this window. Restart from the current report rather than
            // discarding it, so a window can begin the moment motion stops.
            ResetWindow();
            Accumulate(angularVelocity, acceleration);
            return;
        }

        if (_count < WindowSampleCount)
        {
            return;
        }

        Vector3 candidate = _angularSum / _count;
        ResetWindow();
        if (candidate.Length() > MaximumBiasMagnitude)
        {
            return;
        }

        if (Bias is not { } bias)
        {
            Bias = candidate;
            return;
        }

        Vector3 delta = candidate - bias;
        if (Exceeds(Vector3.Abs(delta), MaximumRefinementDelta))
        {
            return;
        }

        Bias = bias + (delta * RefinementWeight);
    }

    private static bool Exceeds(Vector3 value, float limit) =>
        value.X > limit || value.Y > limit || value.Z > limit;

    private void Accumulate(Vector3 angularVelocity, Vector3 acceleration)
    {
        if (_count == 0)
        {
            _angularMinimum = angularVelocity;
            _angularMaximum = angularVelocity;
            _accelerationMinimum = acceleration;
            _accelerationMaximum = acceleration;
        }
        else
        {
            _angularMinimum = Vector3.Min(_angularMinimum, angularVelocity);
            _angularMaximum = Vector3.Max(_angularMaximum, angularVelocity);
            _accelerationMinimum = Vector3.Min(_accelerationMinimum, acceleration);
            _accelerationMaximum = Vector3.Max(_accelerationMaximum, acceleration);
        }

        _count++;
        _angularSum += angularVelocity;
    }

    private void ResetWindow()
    {
        _count = 0;
        _angularSum = default;
        _angularMinimum = default;
        _angularMaximum = default;
        _accelerationMinimum = default;
        _accelerationMaximum = default;
    }
}
