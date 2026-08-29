using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Devices.Sensors;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class WindowsClawMotionSource : IClawMotionSource
{
    private readonly object _gate = new();
    private Gyrometer? _gyrometer;
    private Channel<MotionSample>? _samples;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;

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

            uint requested = Math.Max(gyrometer.MinimumReportInterval, 10u);
            gyrometer.ReportInterval = requested;
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
        }

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
            _samples = null;
            _pumpCancellation = null;
            _pump = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void OnReadingChanged(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        GyrometerReading reading = args.Reading;
        _samples?.Writer.TryWrite(new MotionSample
        {
            GyroX = (float)reading.AngularVelocityX,
            GyroY = (float)reading.AngularVelocityY,
            GyroZ = (float)-reading.AngularVelocityZ,
            HasGyro = true,
            HasAccelerometer = false,
            SensorTimestamp = reading.Timestamp,
        });
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
