using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Devices.Sensors;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class WindowsClawMotionSource : IClawMotionSource
{
    private readonly object _gate = new();
    private Gyrometer? _gyrometer;
    private LegacyCustomAccelerometer? _accelerometer;
    private Channel<MotionSample>? _samples;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;
    private Vector3 _latestRawAcceleration;
    private long _latestAccelerationTick;
    private int _accelerometerReadFailed;

    /// <summary>How long one measured value may bridge a transient COM read failure.</summary>
    internal static readonly TimeSpan MaximumAccelerometerAge = TimeSpan.FromMilliseconds(250);

    public ValueTask<bool> StartAsync(
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_gyrometer is not null)
            {
                return ValueTask.FromResult(true);
            }

            Gyrometer? gyrometer = Gyrometer.GetDefault();
            if (gyrometer is null
                || !gyrometer.DeviceId.Contains("VID_8087&PID_0AC2", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(false);
            }

            Vector3 acceleration = default;
            string? error = null;
            LegacyCustomAccelerometer? accelerometer = LegacyCustomAccelerometer.TryOpen();
            if (accelerometer is null
                || !accelerometer.TryRead(out acceleration, out error))
            {
                PluginTrace.Warn(
                    "motion",
                    $"The Intel ISS gyrometer is present, but its physical accelerometer is unavailable: {error ?? "not found"}.");
                accelerometer?.Dispose();
                return ValueTask.FromResult(false);
            }

            try
            {
                uint requested = Math.Max(gyrometer.MinimumReportInterval, 10u);
                gyrometer.ReportInterval = requested;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                accelerometer.Dispose();
                PluginTrace.Failure("motion", "The Intel ISS gyrometer could not be configured", ex);
                return ValueTask.FromResult(false);
            }

            _accelerometer = accelerometer;
            _latestRawAcceleration = acceleration;
            _latestAccelerationTick = Stopwatch.GetTimestamp();
            _accelerometerReadFailed = 0;
            _samples = Channel.CreateBounded<MotionSample>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            _pumpCancellation = new CancellationTokenSource();
            _gyrometer = gyrometer;
            _gyrometer.ReadingChanged += OnReadingChanged;
            _pump = PumpAsync(_samples.Reader, publish, _pumpCancellation.Token);
            return ValueTask.FromResult(true);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task? pump;
        LegacyCustomAccelerometer? accelerometer;
        lock (_gate)
        {
            if (_gyrometer is null)
            {
                return;
            }

            _gyrometer.ReadingChanged -= OnReadingChanged;
            _gyrometer.ReportInterval = 0;
            _samples?.Writer.TryComplete();
            _pumpCancellation?.Cancel();
            pump = _pump;
            accelerometer = _accelerometer;
            _accelerometer = null;
        }

        accelerometer?.Dispose();

        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_pumpCancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failed publication or disappearing sensor is a resource-health event; teardown
                // still must detach the WinRT subscription and dispose the bounded pump.
            }
        }

        lock (_gate)
        {
            _pumpCancellation?.Dispose();
            _gyrometer = null;
            _latestRawAcceleration = default;
            _latestAccelerationTick = 0;
            _accelerometerReadFailed = 0;
            _samples = null;
            _pumpCancellation = null;
            _pump = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void OnReadingChanged(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        GyrometerReading reading = args.Reading;
        LegacyCustomAccelerometer? accelerometer;
        lock (_gate)
        {
            accelerometer = _accelerometer;
        }

        Vector3 acceleration = default;
        string? error = null;
        bool read = accelerometer?.TryRead(out acceleration, out error) is true;
        if (read)
        {
            if (Interlocked.Exchange(ref _accelerometerReadFailed, 0) != 0)
            {
                PluginTrace.Info("motion", "Physical accelerometer readings resumed.");
            }
        }
        else if (accelerometer is not null
            && Interlocked.Exchange(ref _accelerometerReadFailed, 1) == 0)
        {
            PluginTrace.Warn(
                "motion",
                $"Physical accelerometer read failed; holding its last hardware sample for at most {MaximumAccelerometerAge.TotalMilliseconds:F0} ms: {error}");
        }

        Vector3? currentAcceleration;
        ChannelWriter<MotionSample>? writer;
        lock (_gate)
        {
            if (!ReferenceEquals(accelerometer, _accelerometer))
            {
                return;
            }

            if (read)
            {
                _latestRawAcceleration = acceleration;
                _latestAccelerationTick = Stopwatch.GetTimestamp();
            }

            currentAcceleration = _latestAccelerationTick != 0
                && Stopwatch.GetElapsedTime(_latestAccelerationTick) <= MaximumAccelerometerAge
                    ? _latestRawAcceleration
                    : null;
            writer = _samples?.Writer;
        }

        writer?.TryWrite(CreateSample(
            (float)reading.AngularVelocityX,
            (float)reading.AngularVelocityY,
            (float)reading.AngularVelocityZ,
            reading.Timestamp,
            currentAcceleration));
    }

    /// <summary>Builds one canonical motion sample from the two Windows sensor projections.</summary>
    internal static MotionSample CreateSample(
        float gyroX,
        float gyroY,
        float gyroZ,
        DateTimeOffset timestamp,
        Vector3? rawAcceleration)
    {
        Vector3 acceleration = rawAcceleration is { } raw
            ? new Vector3(raw.X, raw.Z, -raw.Y)
            : default;
        return new MotionSample
        {
            GyroX = gyroX,
            GyroY = gyroY,
            GyroZ = -gyroZ,
            HasGyro = true,
            AccelX = acceleration.X,
            AccelY = acceleration.Y,
            AccelZ = acceleration.Z,
            HasAccelerometer = rawAcceleration.HasValue,
            SensorTimestamp = timestamp,
        };
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
