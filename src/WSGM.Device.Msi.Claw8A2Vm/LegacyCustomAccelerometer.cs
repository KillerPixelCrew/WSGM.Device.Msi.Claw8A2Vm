using System;
using System.Numerics;
using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>Owns the A2VM accelerometer exposed only through the legacy Sensor API.</summary>
/// <remarks>
/// Intel's sensor stack classifies the LSM6DSO physical accelerometer as
/// <c>SENSOR_TYPE_CUSTOM</c>. WinRT therefore never projects it through
/// <c>Windows.Devices.Sensors.Accelerometer</c>, even though sensorsapi exposes a ready sensor and
/// live values. This is the direct package-local COM edge for that device fact; no host service or
/// copied Windows API Code Pack assembly is involved.
/// </remarks>
internal sealed class LegacyCustomAccelerometer : IDisposable
{
    internal const string ExpectedFriendlyName = "Physical Accelerometer";

    internal static readonly Guid CustomSensorType =
        new("E83AF229-8640-4D18-A213-E22675EBB2C3");

    private static readonly Guid CustomDataFormat =
        new("B14C764F-07CF-41E8-9D82-EBE3D0776A6F");

    private static readonly Guid CommonProperties =
        new("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920");

    private static readonly PropertyKey DevicePathProperty = new(CommonProperties, 15);
    private static readonly PropertyKey AccelerationX = new(CustomDataFormat, 7);
    private static readonly PropertyKey AccelerationY = new(CustomDataFormat, 8);
    private static readonly PropertyKey AccelerationZ = new(CustomDataFormat, 9);

    private readonly object _gate = new();
    private ISensor? _sensor;

    private LegacyCustomAccelerometer(ISensor sensor, string devicePath)
    {
        _sensor = sensor;
        DevicePath = devicePath;
    }

    /// <summary>The Sensor API path that proved this is the Intel ISS collection.</summary>
    public string DevicePath { get; }

    /// <summary>Finds and validates the exact physical accelerometer without configuring it.</summary>
    /// <returns>An owned sensor handle, or null when the reviewed collection is unavailable.</returns>
    public static LegacyCustomAccelerometer? TryOpen()
    {
        object? managerObject = null;
        ISensorCollection? collection = null;
        try
        {
            managerObject = new SensorManagerClass();
            ISensorManager manager = (ISensorManager)managerObject;
            Guid customType = CustomSensorType;
            int result = manager.GetSensorsByType(ref customType, out collection);
            if (result < 0 || collection is null)
            {
                PluginTrace.Info(
                    "motion",
                    $"Legacy Sensor API returned no custom-sensor collection (0x{result:X8}).");
                return null;
            }

            result = collection.GetCount(out uint count);
            if (result < 0)
            {
                PluginTrace.Info(
                    "motion",
                    $"Legacy Sensor API could not count custom sensors (0x{result:X8}).");
                return null;
            }

            for (uint index = 0; index < count; index++)
            {
                ISensor? sensor = null;
                try
                {
                    if (collection.GetAt(index, out sensor) < 0 || sensor is null)
                    {
                        continue;
                    }

                    if (!IsExpectedSensor(sensor, out string? devicePath))
                    {
                        continue;
                    }

                    LegacyCustomAccelerometer candidate = new(sensor, devicePath!);
                    sensor = null; // Ownership moved to candidate.
                    if (!candidate.TryRead(out _, out string? error))
                    {
                        PluginTrace.Warn(
                            "motion",
                            $"The physical accelerometer was present but its custom fields could not be read: {error}");
                        candidate.Dispose();
                        return null;
                    }

                    PluginTrace.Info(
                        "motion",
                        $"Legacy physical accelerometer active: {candidate.DevicePath}.");
                    return candidate;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    PluginTrace.Failure(
                        "motion",
                        $"Legacy custom sensor {index} could not be inspected",
                        ex);
                }
                finally
                {
                    Release(sensor);
                }
            }

            PluginTrace.Info(
                "motion",
                $"Legacy Sensor API exposed {count} custom sensors, but not the reviewed Intel ISS physical accelerometer.");
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            PluginTrace.Failure("motion", "Legacy Sensor API discovery failed", ex);
            return null;
        }
        finally
        {
            Release(collection);
            Release(managerObject);
        }
    }

    /// <summary>Reads the three custom values in hardware g units.</summary>
    /// <param name="acceleration">The physical sensor-space vector when successful.</param>
    /// <param name="error">The decisive COM or value failure when unsuccessful.</param>
    /// <returns>True only when all three current values are finite.</returns>
    public bool TryRead(out Vector3 acceleration, out string? error)
    {
        lock (_gate)
        {
            acceleration = default;
            error = null;
            if (_sensor is not { } sensor)
            {
                error = "the sensor handle is closed";
                return false;
            }

            ISensorDataReport? report = null;
            try
            {
                int result = sensor.GetData(out report);
                if (result < 0 || report is null)
                {
                    error = $"GetData returned 0x{result:X8}";
                    return false;
                }

                if (!TryReadValue(report, AccelerationX, out float x, out error)
                    || !TryReadValue(report, AccelerationY, out float y, out error)
                    || !TryReadValue(report, AccelerationZ, out float z, out error))
                {
                    return false;
                }

                acceleration = new Vector3(x, y, z);
                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
            finally
            {
                Release(report);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            Release(_sensor);
            _sensor = null;
        }
    }

    internal static bool MatchesExpectedIdentity(string? friendlyName, Guid type, string? devicePath) =>
        string.Equals(friendlyName, ExpectedFriendlyName, StringComparison.OrdinalIgnoreCase)
        && type == CustomSensorType
        && devicePath?.Contains("VID_8087&PID_0AC2", StringComparison.OrdinalIgnoreCase) is true;

    private static bool IsExpectedSensor(ISensor sensor, out string? devicePath)
    {
        devicePath = null;
        if (sensor.GetFriendlyName(out string? friendlyName) < 0
            || sensor.GetType(out Guid type) < 0
            || sensor.GetState(out int state) < 0
            || state != 0)
        {
            return false;
        }

        devicePath = ReadStringProperty(sensor, DevicePathProperty);
        return MatchesExpectedIdentity(friendlyName, type, devicePath)
            && Supports(sensor, AccelerationX)
            && Supports(sensor, AccelerationY)
            && Supports(sensor, AccelerationZ);
    }

    private static bool Supports(ISensor sensor, PropertyKey key) =>
        sensor.SupportsDataField(ref key, out short supported) >= 0 && supported != 0;

    private static string? ReadStringProperty(ISensor sensor, PropertyKey key)
    {
        PropVariant value = default;
        int result = sensor.GetProperty(ref key, out value);
        if (result < 0)
        {
            return null;
        }

        try
        {
            return value.VariantType == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static bool TryReadValue(
        ISensorDataReport report,
        PropertyKey key,
        out float value,
        out string? error)
    {
        value = 0;
        error = null;
        PropVariant variant = default;
        int result = report.GetSensorValue(ref key, out variant);
        if (result < 0)
        {
            error = $"custom field {key.PropertyId} returned 0x{result:X8}";
            return false;
        }

        try
        {
            double? numeric = variant.VariantType switch
            {
                3 => variant.Int32,
                19 => variant.UInt32,
                4 => variant.Single,
                5 => variant.Double,
                _ => null,
            };
            if (numeric is not { } present || !double.IsFinite(present))
            {
                error = $"custom field {key.PropertyId} had non-numeric variant type {variant.VariantType}";
                return false;
            }

            value = (float)present;
            if (!float.IsFinite(value))
            {
                error = $"custom field {key.PropertyId} exceeded the finite single-precision range";
                return false;
            }

            return true;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public int Int32;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public float Single;
        [FieldOffset(8)] public double Double;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport]
    [Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorManager
    {
        [PreserveSig] int GetSensorsByCategory([In] ref Guid category, out ISensorCollection? sensors);
        [PreserveSig] int GetSensorsByType([In] ref Guid type, out ISensorCollection? sensors);
        [PreserveSig] int GetSensorByID([In] ref Guid id, out ISensor? sensor);
        [PreserveSig] int SetEventSink(nint events);
        [PreserveSig] int RequestPermissions(nint window, ISensorCollection sensors, [MarshalAs(UnmanagedType.Bool)] bool modal);
    }

    [ComImport]
    [Guid("23571E11-E545-4DD8-A337-B89BF44B10DF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorCollection
    {
        [PreserveSig] int GetAt(uint index, out ISensor? sensor);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Add(ISensor sensor);
        [PreserveSig] int Remove(ISensor sensor);
        [PreserveSig] int RemoveByID([In] ref Guid id);
        [PreserveSig] int Clear();
    }

    [ComImport]
    [Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensor
    {
        [PreserveSig] int GetID(out Guid id);
        [PreserveSig] int GetCategory(out Guid category);
        [PreserveSig] int GetType(out Guid type);
        [PreserveSig] int GetFriendlyName([MarshalAs(UnmanagedType.BStr)] out string? name);
        [PreserveSig] int GetProperty([In] ref PropertyKey key, out PropVariant value);
        [PreserveSig] int GetProperties(nint keys, out nint values);
        [PreserveSig] int GetSupportedDataFields(out nint keys);
        [PreserveSig] int SetProperties(nint properties, out nint results);
        [PreserveSig] int SupportsDataField([In] ref PropertyKey key, out short supported);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetData(out ISensorDataReport? report);
        [PreserveSig] int SupportsEvent([In] ref Guid eventGuid, out short supported);
        [PreserveSig] int GetEventInterest(out nint values, out uint count);
        [PreserveSig] int SetEventInterest(nint values, uint count);
        [PreserveSig] int SetEventSink(nint events);
    }

    [ComImport]
    [Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorDataReport
    {
        [PreserveSig] int GetTimestamp(out SystemTime time);
        [PreserveSig] int GetSensorValue([In] ref PropertyKey key, out PropVariant value);
        [PreserveSig] int GetSensorValues(nint keys, out nint values);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [ComImport]
    [Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    private class SensorManagerClass;
}
