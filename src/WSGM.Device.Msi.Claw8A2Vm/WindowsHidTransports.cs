using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class WindowsClawMcuTransport : IClawMcuTransport
{
    private readonly SemaphoreSlim _serializer = new(1, 1);
    private bool _disposed;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using HidEndpoint? endpoint = HidEndpointEnumerator.FindMcu();
        return ValueTask.FromResult(endpoint is not null);
    }

    public async ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (length is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HidEndpoint endpoint = HidEndpointEnumerator.FindMcu()
                ?? throw new FileNotFoundException("The exact A2VM MCU HID collection was not present.");
            await using FileStream stream = endpoint.OpenReadWrite();
            byte[] request = CreateRequest(0x04);
            request[5] = 1;
            request[6] = checked((byte)(address >> 8));
            request[7] = checked((byte)(address & 0xFF));
            request[8] = length;
            await WriteReportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            byte[] response = await ReadMatchingAsync(
                stream,
                report => report[0] == 0x10
                    && report[4] == 0x05
                    && report[5] == 1
                    && report[6] == (byte)(address >> 8)
                    && report[7] == (byte)(address & 0xFF),
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
            if (response[8] != length || 9 + length > response.Length)
            {
                throw new InvalidDataException("ReadProfile returned an invalid payload length.");
            }

            return response.AsSpan(9, length).ToArray();
        }
        finally
        {
            _serializer.Release();
        }
    }

    public async ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (payload.Length is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HidEndpoint endpoint = HidEndpointEnumerator.FindMcu()
                ?? throw new FileNotFoundException("The exact A2VM MCU HID collection was not present.");
            await using FileStream stream = endpoint.OpenReadWrite();
            byte[] request = CreateRequest(0x21);
            request[5] = 1;
            request[6] = checked((byte)(address >> 8));
            request[7] = checked((byte)(address & 0xFF));
            request[8] = checked((byte)payload.Length);
            payload.CopyTo(request.AsMemory(9));
            await WriteReportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            _ = await ReadMatchingAsync(
                stream,
                report => report[0] == 0x10 && report[4] == 0x06,
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serializer.Release();
        }
    }

    public async ValueTask<ClawControllerMode> ReadModeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HidEndpoint endpoint = HidEndpointEnumerator.FindMcu()
                ?? throw new FileNotFoundException("The exact A2VM MCU HID collection was not present.");
            await using FileStream stream = endpoint.OpenReadWrite();
            await WriteReportAsync(stream, CreateRequest(0x26), cancellationToken).ConfigureAwait(false);
            byte[] response = await ReadMatchingAsync(
                stream,
                report => report[0] == 0x10 && report[4] == 0x27,
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
            return response[5] switch
            {
                1 => ClawControllerMode.XInput,
                2 => ClawControllerMode.DirectInput,
                _ => ClawControllerMode.Offline,
            };
        }
        finally
        {
            _serializer.Release();
        }
    }

    public async ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mode is not (ClawControllerMode.XInput or ClawControllerMode.DirectInput))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (deadline - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(2))
        {
            throw new OperationCanceledException("Insufficient lifecycle budget for controller re-enumeration.");
        }

        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (HidEndpoint endpoint = HidEndpointEnumerator.FindMcu()
                ?? throw new FileNotFoundException("The exact A2VM MCU HID collection was not present."))
            {
                if (!HidEndpointEnumerator.SamePhysicalLocation(
                        endpoint.PhysicalLocation,
                        physicalLocation))
                {
                    throw new InvalidOperationException("The MCU endpoint moved to another physical USB location.");
                }

                await using FileStream stream = endpoint.OpenReadWrite();
                byte[] request = CreateRequest(0x24);
                request[5] = (byte)mode;
                request[6] = 0;
                await WriteReportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }

            string productId = mode is ClawControllerMode.XInput
                ? ClawHardwareFacts.XInputProductId
                : ClawHardwareFacts.DirectInputProductId;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ControllerTopology? topology = HidEndpointEnumerator.DiscoverControllerTopology();
                if (topology is not null
                    && topology.Mode == mode
                    && string.Equals(topology.ProductId, productId, StringComparison.OrdinalIgnoreCase)
                    && HidEndpointEnumerator.SamePhysicalLocation(
                        topology.PhysicalLocation,
                        physicalLocation))
                {
                    return topology;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The controller did not re-enumerate in the requested mode at its physical location.");
        }
        finally
        {
            _serializer.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _serializer.Dispose();
        return ValueTask.CompletedTask;
    }

    private static byte[] CreateRequest(byte command)
    {
        byte[] request = new byte[ClawHardwareFacts.McuReportLength];
        request[0] = 0x0F;
        request[3] = 0x3C;
        request[4] = command;
        return request;
    }

    private static async ValueTask WriteReportAsync(
        FileStream stream,
        byte[] report,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(report, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadMatchingAsync(
        FileStream stream,
        Func<byte[], bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            byte[] report = new byte[ClawHardwareFacts.McuReportLength];
            int offset = 0;
            while (offset < report.Length)
            {
                int read = await stream.ReadAsync(report.AsMemory(offset), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The MCU HID collection closed while awaiting an acknowledgement.");
                }

                offset += read;
            }

            if (predicate(report))
            {
                return report;
            }
        }
    }
}

internal sealed class WindowsClawControllerSource : IClawControllerSource
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeSerializer = new(1, 1);
    private HidEndpoint? _endpoint;
    private FileStream? _stream;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private long _sequence;

    public ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(HidEndpointEnumerator.DiscoverControllerTopology());
    }

    public async ValueTask StartAsync(
        long cycleGeneration,
        Func<CanonicalControllerSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_readerTask is not null)
            {
                throw new InvalidOperationException("The DirectInput controller reader is already active.");
            }

            _endpoint = HidEndpointEnumerator.FindDirectInputGamepad()
                ?? throw new FileNotFoundException("The reviewed DirectInput gamepad collection was unavailable.");
            _stream = _endpoint.OpenReadWrite();
            _readerCancellation = new CancellationTokenSource();
            _sequence = 0;
            _readerTask = ReadLoopAsync(
                _stream,
                cycleGeneration,
                publish,
                firstSample,
                _readerCancellation.Token);
        }

        try
        {
            await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task? reader;
        lock (_gate)
        {
            reader = _readerTask;
            _readerCancellation?.Cancel();
        }

        if (reader is not null)
        {
            try
            {
                await reader.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_readerCancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Disconnection or publication rejection is a source-health failure, but teardown
                // must still close every handle so mode restoration can continue independently.
            }
        }

        bool ownsWriteGate = false;
        try
        {
            try
            {
                await _writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
                ownsWriteGate = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Closing the stream below aborts an in-flight output write. Handle cleanup cannot
                // be skipped merely because the release budget expired while waiting for it.
            }

            lock (_gate)
            {
                _stream?.Dispose();
                _endpoint?.Dispose();
                _readerCancellation?.Dispose();
                _stream = null;
                _endpoint = null;
                _readerCancellation = null;
                _readerTask = null;
            }
        }
        finally
        {
            if (ownsWriteGate)
            {
                _writeSerializer.Release();
            }
        }
    }

    public async ValueTask WriteRumbleAsync(
        byte weak,
        byte strong,
        CancellationToken cancellationToken)
    {
        await _writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream? stream;
            lock (_gate)
            {
                stream = _stream;
            }

            if (stream is null)
            {
                return;
            }

            byte[] report = ClawControllerCodec.EncodeRumble(weak, strong);
            await stream.WriteAsync(report, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeSerializer.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task ReadLoopAsync(
        FileStream stream,
        long cycleGeneration,
        Func<CanonicalControllerSample, ValueTask> publish,
        TaskCompletionSource firstSample,
        CancellationToken cancellationToken)
    {
        bool first = true;
        byte[] report = new byte[64];
        while (!cancellationToken.IsCancellationRequested)
        {
            int offset = 0;
            while (offset < report.Length)
            {
                int read = await stream.ReadAsync(report.AsMemory(offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The DirectInput gamepad collection disconnected.");
                }

                offset += read;
            }

            if (first && report.AsSpan(1, 9).IndexOfAnyExcept((byte)0xFF) < 0)
            {
                continue;
            }

            SampleQuality quality = first ? SampleQuality.Discontinuity : SampleQuality.Good;
            first = false;
            CanonicalControllerSample sample = ClawControllerCodec.Decode(
                report,
                Interlocked.Increment(ref _sequence),
                cycleGeneration,
                DateTimeOffset.UtcNow,
                quality);
            await publish(sample).ConfigureAwait(false);
            firstSample.TrySetResult();
        }
    }
}

internal sealed class HidEndpoint : IDisposable
{
    private bool _disposed;

    public required string DevicePath { get; init; }

    public required string InstancePath { get; init; }

    public required string ProductId { get; init; }

    public required ushort UsagePage { get; init; }

    public required ushort Usage { get; init; }

    public required ushort InputLength { get; init; }

    public required ushort OutputLength { get; init; }

    public required string PhysicalLocation { get; init; }

    public FileStream OpenReadWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SafeFileHandle handle = NativeHid.CreateFile(
            DevicePath,
            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            0,
            NativeHid.OPEN_EXISTING,
            NativeHid.FILE_FLAG_OVERLAPPED,
            0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException($"The HID collection could not be opened (Win32 {Marshal.GetLastWin32Error()}).");
        }

        return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
    }

    public void Dispose() => _disposed = true;
}

internal static class HidEndpointEnumerator
{
    private static readonly NativeHid.DevPropKey LocationPathsKey = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 37,
    };

    public static HidEndpoint? FindMcu() => Enumerate().FirstOrDefault(endpoint =>
        endpoint.ProductId switch
        {
            ClawHardwareFacts.XInputProductId => endpoint.UsagePage == 0xFFA0 && endpoint.Usage == 0x0001,
            ClawHardwareFacts.DirectInputProductId => endpoint.UsagePage == 0xFFF0 && endpoint.Usage == 0x0040,
            _ => false,
        }
        && endpoint.InputLength == 64
        && endpoint.OutputLength == 64);

    /// <summary>The DirectInput pad the MCU presents after switching to that mode.</summary>
    /// <returns>The endpoint, or null when it cannot be found.</returns>
    /// <remarks>
    /// Currently always null on the reference Claw, and the reason is <see cref="Enumerate"/> rather
    /// than this predicate — see the note in <see cref="DiscoverControllerTopology"/>. Windows does
    /// list the pad (HID\VID_0DB0&amp;PID_1902&amp;MI_00&amp;COL01, "HID-compliant game controller"),
    /// so this returning null means the enumeration did not surface a collection that exists.
    /// <para>
    /// Do not repoint this at the vendor pipe to make it non-null. That was tried and measured: the
    /// pipe delivered no input over eight seconds of real use and answers only commands.
    /// </para>
    /// </remarks>
    public static HidEndpoint? FindDirectInputGamepad() => Enumerate().FirstOrDefault(endpoint =>
        endpoint.ProductId == ClawHardwareFacts.DirectInputProductId
        && endpoint.UsagePage == 0x0001
        && endpoint.Usage == 0x0005
        && endpoint.InputLength == 64);

    public static ControllerTopology? DiscoverControllerTopology()
    {
        IReadOnlyList<HidEndpoint> endpoints = Enumerate();
        try
        {
            HidEndpoint? mcu = endpoints.FirstOrDefault(endpoint =>
                (endpoint.ProductId is ClawHardwareFacts.XInputProductId
                    or ClawHardwareFacts.DirectInputProductId)
                && endpoint.OutputLength == 64
                && endpoint.UsagePage >= 0xFF00);
            if (mcu is null || string.IsNullOrWhiteSpace(mcu.PhysicalLocation))
            {
                return null;
            }

            ClawControllerMode mode = mcu.ProductId == ClawHardwareFacts.XInputProductId
                ? ClawControllerMode.XInput
                : ClawControllerMode.DirectInput;
            // NOTE (device-observed, reference Claw MS-1T52 firmware 0229, 2026-08-29): in
            // DirectInput mode this finds nothing, and the cause is Enumerate() below rather than
            // this rule. The DirectInput pad genuinely exists — Windows lists
            // HID\VID_0DB0&PID_1902&MI_00&COL01 as a HID game controller — but it never comes back
            // from the HID interface enumeration, which returns only MI_01's keyboard, mouse and
            // consumer collections plus MI_00&COL02's vendor pipe.
            //
            // Two things were ruled out by measurement rather than argument. It is not a
            // re-enumeration race: the mode switch settles in ~1.6 s and the set is unchanged after
            // 12 s. And the vendor pipe is not a substitute source: reading it for 8 s while the
            // device was being used produced no input at all, only a command response, so it is a
            // command channel and hiding it would hide the wrong device.
            IReadOnlyList<PhysicalDeviceIdentity> physical = endpoints
                .Where(endpoint =>
                    endpoint.ProductId == mcu.ProductId
                    && SamePhysicalLocation(endpoint.PhysicalLocation, mcu.PhysicalLocation)
                    && (endpoint.InstancePath.Contains("&IG_", StringComparison.OrdinalIgnoreCase)
                        || (endpoint.UsagePage == 0x0001 && endpoint.Usage == 0x0005)))
                .Select(endpoint => new PhysicalDeviceIdentity
                {
                    InstancePath = endpoint.InstancePath,
                    LocationPath = endpoint.PhysicalLocation,
                    VendorId = ClawHardwareFacts.UsbVendorId,
                    ProductId = endpoint.ProductId,
                    RequiresHiding = true,
                })
                .ToArray();
            // Summarized here, where the endpoints are still alive, because they are disposed in the
            // finally below and a failed handoff otherwise has nothing to report but its own
            // absence.
            string observed = string.Join(", ", endpoints
                .Where(endpoint => SamePhysicalLocation(endpoint.PhysicalLocation, mcu.PhysicalLocation))
                .Select(endpoint => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{endpoint.ProductId}/{endpoint.UsagePage:X4}:{endpoint.Usage:X4} in{endpoint.InputLength} out{endpoint.OutputLength}"))
                .Take(16));
            return new ControllerTopology(
                mode,
                mcu.ProductId,
                mcu.PhysicalLocation,
                physical,
                observed);
        }
        finally
        {
            foreach (HidEndpoint endpoint in endpoints)
            {
                endpoint.Dispose();
            }
        }
    }

    public static bool SamePhysicalLocation(string left, string right) =>
        string.Equals(CompositeLocation(left), CompositeLocation(right), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<HidEndpoint> Enumerate()
    {
        NativeHid.HidD_GetHidGuid(out Guid hidGuid);
        nint set = NativeHid.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            0,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);
        if (set == NativeHid.InvalidHandleValue)
        {
            return [];
        }

        var endpoints = new List<HidEndpoint>();
        try
        {
            for (uint index = 0; ; index++)
            {
                NativeHid.DeviceInterfaceData interfaceData = new()
                {
                    Size = (uint)Marshal.SizeOf<NativeHid.DeviceInterfaceData>(),
                };
                if (!NativeHid.SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == NativeHid.ERROR_NO_MORE_ITEMS)
                    {
                        break;
                    }

                    continue;
                }

                _ = NativeHid.SetupDiGetDeviceInterfaceDetail(
                    set,
                    ref interfaceData,
                    0,
                    0,
                    out uint required,
                    0);
                if (required == 0 || required > 64 * 1024)
                {
                    continue;
                }

                nint detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    NativeHid.DeviceInfoData info = new()
                    {
                        Size = (uint)Marshal.SizeOf<NativeHid.DeviceInfoData>(),
                    };
                    if (!NativeHid.SetupDiGetDeviceInterfaceDetail(
                            set,
                            ref interfaceData,
                            detail,
                            required,
                            out _,
                            ref info))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path is null || !TryDescribe(path, set, info, out HidEndpoint? endpoint))
                    {
                        continue;
                    }

                    endpoints.Add(endpoint!);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            _ = NativeHid.SetupDiDestroyDeviceInfoList(set);
        }

        return endpoints;
    }

    private static bool TryDescribe(
        string path,
        nint set,
        NativeHid.DeviceInfoData info,
        out HidEndpoint? endpoint)
    {
        endpoint = null;
        using SafeFileHandle handle = NativeHid.CreateFile(
            path,
            0,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            0,
            NativeHid.OPEN_EXISTING,
            0,
            0);
        if (handle.IsInvalid)
        {
            return false;
        }

        NativeHid.HidAttributes attributes = new()
        {
            Size = Marshal.SizeOf<NativeHid.HidAttributes>(),
        };
        if (!NativeHid.HidD_GetAttributes(handle, ref attributes)
            || attributes.VendorId != 0x0DB0
            || attributes.ProductId is not (0x1901 or 0x1902))
        {
            return false;
        }

        if (!NativeHid.HidD_GetPreparsedData(handle, out nint preparsed))
        {
            return false;
        }

        NativeHid.HidCaps caps;
        try
        {
            if (NativeHid.HidP_GetCaps(preparsed, out caps) != NativeHid.HIDP_STATUS_SUCCESS)
            {
                return false;
            }
        }
        finally
        {
            _ = NativeHid.HidD_FreePreparsedData(preparsed);
        }

        string instance = ReadInstancePath(set, info);
        string location = ReadPhysicalLocation(info.DeviceInstance);
        endpoint = new HidEndpoint
        {
            DevicePath = path,
            InstancePath = instance,
            ProductId = attributes.ProductId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputLength = caps.InputReportByteLength,
            OutputLength = caps.OutputReportByteLength,
            PhysicalLocation = location,
        };
        return true;
    }

    private static string ReadInstancePath(nint set, NativeHid.DeviceInfoData info)
    {
        var buffer = new StringBuilder(1024);
        return NativeHid.SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string ReadPhysicalLocation(uint deviceInstance)
    {
        uint current = deviceInstance;
        NativeHid.DevPropKey locationPathsKey = LocationPathsKey;
        for (int depth = 0; depth < 6; depth++)
        {
            byte[] buffer = new byte[4096];
            uint length = checked((uint)buffer.Length);
            if (NativeHid.CM_Get_Device_Property(
                    current,
                    ref locationPathsKey,
                    out _,
                    buffer,
                    ref length,
                    0) == 0)
            {
                string value = Encoding.Unicode.GetString(buffer, 0, checked((int)length)).TrimEnd('\0');
                int terminator = value.IndexOf('\0');
                if (terminator >= 0)
                {
                    value = value[..terminator];
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return CompositeLocation(value);
                }
            }

            if (NativeHid.CM_Get_Parent(out current, current, 0) != 0)
            {
                break;
            }
        }

        return string.Empty;
    }

    private static string CompositeLocation(string location)
    {
        int interfaceComponent = location.IndexOf("#USBMI(", StringComparison.OrdinalIgnoreCase);
        return interfaceComponent < 0 ? location : location[..interfaceComponent];
    }
}

internal static class NativeHid
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(nint preparsedData, out HidCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        nint parent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        nint detailData,
        uint detailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        nint detailData,
        uint detailDataSize,
        out uint requiredSize,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(
        nint deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        StringBuilder instanceId,
        int instanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint parent, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Property(
        uint deviceInstance,
        ref DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[] buffer,
        ref uint bufferLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
