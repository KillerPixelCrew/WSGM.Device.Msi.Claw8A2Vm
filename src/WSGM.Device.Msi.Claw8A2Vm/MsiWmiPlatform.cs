using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Identity;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class MsiWmiPlatform : IMsiWmiTransport
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _serializer = new(1, 1);
    private bool _disposed;

    public ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            static () =>
            {
                using ManagementClass definition = new("root\\WMI", "MSI_ACPI", null);
                if (!definition.Methods.Cast<MethodData>().Any(method => method.Name == "Get_WMI"))
                {
                    return false;
                }

                using ManagementObject? instance = FindActiveInstance();
                return instance is not null;
            },
            cancellationToken);

    public ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        if (!methodName.StartsWith("Get_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only MSI_ACPI Get_* methods may use the getter path.", nameof(methodName));
        }

        return RunSerializedAsync(
            () => InvokeCore(methodName, CreatePackage(selector)),
            cancellationToken);
    }

    public ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(package);
        if (!methodName.StartsWith("Set_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only MSI_ACPI Set_* methods may use the setter path.", nameof(methodName));
        }

        if (package.Length != ClawHardwareFacts.WmiPackageLength)
        {
            throw new ArgumentException("MSI_ACPI writes require exactly 32 bytes.", nameof(package));
        }

        return RunSerializedAsync(
            () =>
            {
                _ = InvokeCore(methodName, [.. package]);
                return true;
            },
            cancellationToken).AsVoid();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async ValueTask<T> RunSerializedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        await _serializer.WaitAsync(deadline.Token).ConfigureAwait(false);
        Task<T>? operationTask = null;
        try
        {
            operationTask = Task.Run(operation, CancellationToken.None);
            return await operationTask
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (operationTask is null || operationTask.IsCompleted)
            {
                _serializer.Release();
            }
            else
            {
                _ = ReleaseSerializerWhenCompleteAsync(operationTask);
            }
        }
    }

    private async Task ReleaseSerializerWhenCompleteAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The caller already received the bounded timeout. Observing the late transport failure
            // here prevents an unobserved exception while retaining serialization until WMI exits.
        }
        finally
        {
            _serializer.Release();
        }
    }

    private static byte[] InvokeCore(string methodName, byte[] request)
    {
        using ManagementObject instance = FindActiveInstance()
            ?? throw new FileNotFoundException("The reviewed MSI_ACPI instance was not present.");
        using ManagementBaseObject input = instance.GetMethodParameters(methodName);
        using ManagementClass packageClass = new("root\\WMI", "Package_32", null);
        using ManagementObject package = packageClass.CreateInstance();
        package["Bytes"] = request;
        input["Data"] = package;

        using ManagementBaseObject output = instance.InvokeMethod(methodName, input, null)
            ?? throw new IOException($"{methodName} returned no response.");
        if (output["Data"] is not ManagementBaseObject returned)
        {
            throw new InvalidDataException($"{methodName} returned no Package_32 response.");
        }

        using (returned)
        {
            if (returned["Bytes"] is not byte[] response
                || response.Length != ClawHardwareFacts.WmiPackageLength)
            {
                throw new InvalidDataException($"{methodName} returned an invalid Package_32 response.");
            }

            if (response[0] != 0x01)
            {
                throw new InvalidDataException(
                    $"{methodName} returned status 0x{response[0]:X2} instead of success.");
            }

            return response;
        }
    }

    private static ManagementObject? FindActiveInstance()
    {
        using ManagementObjectSearcher searcher = new(
            "root\\WMI",
            "SELECT * FROM MSI_ACPI WHERE Active = TRUE");
        ManagementObject? found = null;
        foreach (ManagementBaseObject candidate in searcher.Get())
        {
            if (found is not null)
            {
                candidate.Dispose();
                found.Dispose();
                throw new InvalidDataException("The reviewed definition requires exactly one active MSI_ACPI instance.");
            }

            found = (ManagementObject)candidate;
        }

        return found;
    }

    private static byte[] CreatePackage(byte selector)
    {
        byte[] package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = selector;
        return package;
    }
}

internal sealed class WindowsClawIdentityReader(IMsiWmiTransport wmi) : IClawIdentityReader
{
    private readonly IMsiWmiTransport _wmi = wmi ?? throw new ArgumentNullException(nameof(wmi));

    public async ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        DeviceIdentitySnapshot snapshot = await Task.Run(ReadMachineIdentity, CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        bool providerAvailable;
        string? ecFirmware = null;
        bool wmiFirmwareVerified = false;
        try
        {
            providerAvailable = await _wmi.IsProviderAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (providerAvailable)
            {
                byte[] wmiVersion = await _wmi.InvokeGetterAsync("Get_WMI", 0, cancellationToken)
                    .ConfigureAwait(false);
                byte[] ec = await _wmi.InvokeGetterAsync("Get_EC", 0, cancellationToken)
                    .ConfigureAwait(false);
                ecFirmware = DecodeEcFirmware(ec);
                wmiFirmwareVerified = wmiVersion[2] == 8
                    && wmiVersion[3] == 0
                    && string.Equals(ecFirmware, ClawHardwareFacts.EcFirmware, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is ManagementException or IOException or UnauthorizedAccessException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            providerAvailable = false;
        }

        snapshot = snapshot with
        {
            EcFirmwareVersion = ecFirmware,
            WmiProviderSignatures = providerAvailable
                ? ["root\\WMI:MSI_ACPI", "root\\WMI:MSI_ACPI.Get_WMI:8.0"]
                : [],
        };

        bool exactMachineMatch = IsExactMachine(snapshot);
        bool mcuFirmwareVerified = snapshot.UsbEndpoints.Any(endpoint =>
            string.Equals(endpoint.VendorId, ClawHardwareFacts.UsbVendorId, StringComparison.OrdinalIgnoreCase)
            && IsControllerProduct(endpoint.ProductId)
            && string.Equals(endpoint.DeviceRelease, ClawHardwareFacts.McuFirmware, StringComparison.OrdinalIgnoreCase));

        return new ClawIdentityState
        {
            Snapshot = snapshot,
            ExactMachineMatch = exactMachineMatch,
            WmiFirmwareVerified = exactMachineMatch && wmiFirmwareVerified,
            McuFirmwareVerified = exactMachineMatch && mcuFirmwareVerified,
            OnAcPower = ReadOnAcPower(),
        };
    }

    internal static bool IsExactMachine(DeviceIdentitySnapshot identity) =>
        string.Equals(identity.SystemManufacturer, ClawHardwareFacts.Manufacturer, StringComparison.OrdinalIgnoreCase)
        && string.Equals(identity.BaseboardProduct, ClawHardwareFacts.BoardProduct, StringComparison.OrdinalIgnoreCase)
        && string.Equals(identity.SystemSku, ClawHardwareFacts.SystemSku, StringComparison.OrdinalIgnoreCase);

    private static DeviceIdentitySnapshot ReadMachineIdentity()
    {
        using ManagementObject system = QuerySingle(
            "root\\CIMV2",
            "SELECT Manufacturer, Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem");
        using ManagementObject board = QuerySingle(
            "root\\CIMV2",
            "SELECT Product, Version FROM Win32_BaseBoard");
        using ManagementObject bios = QuerySingle(
            "root\\CIMV2",
            "SELECT SMBIOSBIOSVersion FROM Win32_BIOS");

        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = Normalize(system["Manufacturer"]),
            SystemProduct = Normalize(system["Model"]),
            SystemSku = Normalize(system["SystemSKUNumber"]),
            SystemFamily = Normalize(system["SystemFamily"]),
            BaseboardProduct = Normalize(board["Product"]),
            BaseboardVersion = Normalize(board["Version"]),
            BiosVersion = Normalize(bios["SMBIOSBIOSVersion"]),
            UsbEndpoints = ReadControllerEndpoints(),
        };
    }

    private static IReadOnlyList<UsbEndpointObservation> ReadControllerEndpoints()
    {
        var endpoints = new List<UsbEndpointObservation>();
        using ManagementObjectSearcher searcher = new(
            "root\\CIMV2",
            "SELECT DeviceID, HardwareID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_0DB0&PID_19%' ");
        foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
        {
            using (item)
            {
                string id = Convert.ToString(item["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty;
                string? vendorId = ExtractHex(id, "VID_");
                string? productId = ExtractHex(id, "PID_");
                string? release = (item["HardwareID"] as string[])
                    ?.Select(value => ExtractHex(value, "REV_"))
                    .FirstOrDefault(value => value is not null);
                if (vendorId is null || productId is null || !IsControllerProduct(productId))
                {
                    continue;
                }

                endpoints.Add(new UsbEndpointObservation
                {
                    VendorId = vendorId,
                    ProductId = productId,
                    DeviceRelease = release,
                });
            }
        }

        return endpoints;
    }

    private static ManagementObject QuerySingle(string scope, string query)
    {
        using ManagementObjectSearcher searcher = new(scope, query);
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault()
            ?? throw new FileNotFoundException($"Inventory query returned no rows: {query}");
    }

    private static bool ReadOnAcPower()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\WMI",
                "SELECT PowerOnline FROM BatteryStatus");
            ManagementObject? battery = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (battery is null)
            {
                return true;
            }

            using (battery)
            {
                return Convert.ToBoolean(battery["PowerOnline"], CultureInfo.InvariantCulture);
            }
        }
        catch (ManagementException)
        {
            // The descriptor permits both AC and DC. Failure to observe the source does not widen a
            // range or authorize a write that would otherwise be forbidden.
            return false;
        }
    }

    private static string? Normalize(object? value)
    {
        string? text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? ExtractHex(string value, string marker)
    {
        int start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || value.Length < start + marker.Length + 4)
        {
            return null;
        }

        string result = value.Substring(start + marker.Length, 4);
        return result.All(Uri.IsHexDigit) ? result.ToUpperInvariant() : null;
    }

    private static bool IsControllerProduct(string value) => value is
        ClawHardwareFacts.XInputProductId or ClawHardwareFacts.DirectInputProductId;

    private static string? DecodeEcFirmware(byte[] response)
    {
        int marker = Array.IndexOf(response, (byte)0x81, 1);
        if (marker < 0 || marker + 1 >= response.Length)
        {
            return null;
        }

        int end = Array.IndexOf(response, (byte)0, marker + 1);
        if (end < 0)
        {
            end = response.Length;
        }

        string value = Encoding.ASCII.GetString(response, marker + 1, end - marker - 1).Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

internal sealed class MsiOemEventSource : IMsiOemEventSource
{
    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private Func<byte, DateTimeOffset, ValueTask>? _callback;

    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return ValueTask.FromResult(true);
            }

            _callback = callback;
            _watcher = new ManagementEventWatcher("root\\WMI", "SELECT * FROM MSI_Event");
            _watcher.EventArrived += OnEventArrived;
            try
            {
                _watcher.Start();
                return ValueTask.FromResult(true);
            }
            catch
            {
                _watcher.EventArrived -= OnEventArrived;
                _watcher.Dispose();
                _watcher = null;
                _callback = null;
                return ValueTask.FromResult(false);
            }
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_watcher is null)
            {
                return ValueTask.CompletedTask;
            }

            _watcher.EventArrived -= OnEventArrived;
            try
            {
                _watcher.Stop();
            }
            catch (ManagementException)
            {
                // The subscription can disappear with the provider during teardown. Disposal is the
                // terminal operation and no hardware state depends on the watcher.
            }

            _watcher.Dispose();
            _watcher = null;
            _callback = null;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void OnEventArrived(object sender, EventArrivedEventArgs args)
    {
        Func<byte, DateTimeOffset, ValueTask>? callback = _callback;
        if (callback is null)
        {
            return;
        }

        object? raw = args.NewEvent.Properties["MSIEvt"]?.Value;
        if (raw is null)
        {
            return;
        }

        byte code = unchecked((byte)(Convert.ToUInt32(raw, CultureInfo.InvariantCulture) & 0xFF));
        Task publication = callback(code, DateTimeOffset.UtcNow).AsTask();
        if (!publication.IsCompletedSuccessfully)
        {
            _ = ObservePublicationAsync(publication);
        }
    }

    private static async Task ObservePublicationAsync(Task publication)
    {
        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // DeviceHost owns diagnostics for a rejected publication. An OEM event must not tear
            // down the WMI callback thread or leave an unobserved task exception behind.
        }
    }
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> task) =>
        _ = await task.ConfigureAwait(false);
}
