using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>Writes bounded physical-gyroscope diagnostics without blocking the sensor poller.</summary>
internal sealed class GyroCsvLog : IAsyncDisposable
{
    internal const long DefaultMaximumBytes = 16 * 1024 * 1024;
    internal const string FileName = "gyro.csv";
    internal const string PreviousFileName = "gyro.previous.csv";
    internal const string Header =
        "session,fresh_index,poll_index,received_utc,sensor_utc,receive_delta_ms,sensor_delta_ms,"
        + "sensor_age_ms,read_duration_ms,counter,counter_delta,duplicate_polls,read_failures,"
        + "queue_drops,raw_gyro_x,raw_gyro_y,raw_gyro_z,bias_x,bias_y,bias_z,corrected_gyro_x,"
        + "corrected_gyro_y,corrected_gyro_z,accel_x,accel_y,accel_z";

    private const int QueueCapacity = 4096;
    private const int FlushRowCount = 100;
    private const int StreamBufferBytes = 64 * 1024;

    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maximumBytes;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Channel<GyroCsvRow> _rows;
    private readonly Task _writer;
    private long _droppedRows;
    private int _completed;
    private int _faulted;

    private GyroCsvLog(string path, long maximumBytes)
    {
        _path = path;
        _previousPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            PreviousFileName);
        _maximumBytes = maximumBytes;
        _rows = Channel.CreateBounded<GyroCsvRow>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _writer = Task.Run(WriteAsync, CancellationToken.None);
    }

    /// <summary>Starts the production log beside WSGM's ordinary per-user log.</summary>
    /// <returns>The logger, or null when diagnostics cannot be opened.</returns>
    public static GyroCsvLog? TryCreateDefault()
    {
        try
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
            {
                PluginTrace.Warn("motion", "Gyro CSV logging is unavailable: local application data has no path.");
                return null;
            }

            string directory = System.IO.Path.Combine(localData, "WSGM");
            Directory.CreateDirectory(directory);
            GyroCsvLog log = Create(System.IO.Path.Combine(directory, FileName), DefaultMaximumBytes);
            PluginTrace.Info(
                "motion",
                $"Gyro CSV logging active at {log.FilePath} ({DefaultMaximumBytes / (1024 * 1024)} MiB plus one rotation).");
            return log;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure("motion", "Gyro CSV logging could not start", ex);
            return null;
        }
    }

    /// <summary>Creates a logger at an injected path for bounded verification.</summary>
    internal static GyroCsvLog Create(string path, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes <= Header.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        string fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        return new GyroCsvLog(fullPath, maximumBytes);
    }

    /// <summary>The live CSV path.</summary>
    public string FilePath => _path;

    /// <summary>Queues one fresh hardware report, dropping diagnostics rather than delaying motion.</summary>
    public void Write(GyroCsvRow row)
    {
        if (Volatile.Read(ref _completed) != 0 || Volatile.Read(ref _faulted) != 0)
        {
            return;
        }

        if (!_rows.Writer.TryWrite(row))
        {
            Interlocked.Increment(ref _droppedRows);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _rows.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteAsync()
    {
        FileStream? stream = null;
        StreamWriter? writer = null;
        try
        {
            (stream, writer) = await OpenWriterAsync().ConfigureAwait(false);
            int rowsSinceFlush = 0;
            await foreach (GyroCsvRow row in _rows.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                long droppedRows = Interlocked.Exchange(ref _droppedRows, 0);
                await writer.WriteLineAsync(row.ToCsv(_session, droppedRows)).ConfigureAwait(false);
                rowsSinceFlush++;
                if (rowsSinceFlush < FlushRowCount)
                {
                    continue;
                }

                await writer.FlushAsync().ConfigureAwait(false);
                rowsSinceFlush = 0;
                if (stream.Position < _maximumBytes)
                {
                    continue;
                }

                await writer.DisposeAsync().ConfigureAwait(false);
                writer = null;
                stream = null;
                Rotate();
                (stream, writer) = await OpenWriterAsync().ConfigureAwait(false);
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Interlocked.Exchange(ref _faulted, 1);
            PluginTrace.Failure("motion", "Gyro CSV logging stopped", ex);
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                stream?.Dispose();
            }
        }
    }

    private async Task<(FileStream Stream, StreamWriter Writer)> OpenWriterAsync()
    {
        if (File.Exists(_path) && new FileInfo(_path).Length >= _maximumBytes)
        {
            Rotate();
        }

        FileStream stream = new(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), StreamBufferBytes);
            if (stream.Length == 0)
            {
                await writer.WriteLineAsync(Header).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            return (stream, writer);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Rotate() => File.Move(_path, _previousPath, overwrite: true);
}

/// <summary>One fresh physical report and the delivery evidence needed to diagnose cadence gaps.</summary>
internal readonly record struct GyroCsvRow(
    ulong FreshIndex,
    ulong PollIndex,
    DateTimeOffset ReceivedTimestamp,
    DateTimeOffset SensorTimestamp,
    double? ReceiveDeltaMilliseconds,
    double? SensorDeltaMilliseconds,
    double SensorAgeMilliseconds,
    double ReadDurationMilliseconds,
    uint HardwareCounter,
    uint? HardwareCounterDelta,
    int DuplicatePolls,
    int ReadFailures,
    Vector3 RawAngularVelocity,
    Vector3? Bias,
    Vector3 CorrectedAngularVelocity,
    Vector3 Acceleration)
{
    /// <summary>Formats one culture-invariant row whose fields never need CSV quoting.</summary>
    public string ToCsv(string session, long droppedRows)
    {
        StringBuilder text = new(384);
        text.Append(session).Append(',')
            .Append(FreshIndex).Append(',')
            .Append(PollIndex).Append(',')
            .Append(ReceivedTimestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
            .Append(SensorTimestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',');
        Append(text, ReceiveDeltaMilliseconds);
        Append(text, SensorDeltaMilliseconds);
        Append(text, SensorAgeMilliseconds);
        Append(text, ReadDurationMilliseconds);
        text.Append(HardwareCounter).Append(',');
        if (HardwareCounterDelta is { } counterDelta)
        {
            text.Append(counterDelta);
        }

        text.Append(',').Append(DuplicatePolls)
            .Append(',').Append(ReadFailures)
            .Append(',').Append(droppedRows).Append(',');
        Append(text, RawAngularVelocity);
        if (Bias is { } bias)
        {
            Append(text, bias);
        }
        else
        {
            text.Append(",,,");
        }

        Append(text, CorrectedAngularVelocity);
        Append(text, Acceleration, trailingComma: false);
        return text.ToString();
    }

    private static void Append(StringBuilder text, double? value)
    {
        if (value is { } present)
        {
            text.Append(present.ToString("F3", CultureInfo.InvariantCulture));
        }

        text.Append(',');
    }

    private static void Append(StringBuilder text, Vector3 value, bool trailingComma = true)
    {
        text.Append(value.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Z.ToString("R", CultureInfo.InvariantCulture));
        if (trailingComma)
        {
            text.Append(',');
        }
    }
}
