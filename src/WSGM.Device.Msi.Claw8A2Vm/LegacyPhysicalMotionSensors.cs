using System;
using System.Numerics;
using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>Owns the A2VM physical IMU exposed through the legacy Sensor API.</summary>
/// <remarks>
/// Intel's sensor stack classifies both LSM6DSO physical sensors as
/// <c>SENSOR_TYPE_CUSTOM</c>. WinRT therefore does not expose the accelerometer and suppresses
/// unchanged gyroscope readings. This package-local COM edge reads the two physical collections
/// directly and uses their hardware report counter to distinguish a new sample from a repeated
/// <c>GetData</c> result.
/// </remarks>
internal sealed class LegacyPhysicalMotionSensors : IDisposable
{
    internal const string ExpectedAccelerometerName = "Physical Accelerometer";
    internal const string ExpectedGyrometerName = "Physical Gyrometer";

    internal static readonly Guid CustomSensorType =
        new("E83AF229-8640-4D18-A213-E22675EBB2C3");

    private static readonly Guid CustomDataFormat =
        new("B14C764F-07CF-41E8-9D82-EBE3D0776A6F");

    private static readonly Guid CommonProperties =
        new("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920");

    private static readonly PropertyKey DevicePathProperty = new(CommonProperties, 15);
    private static readonly PropertyKey MinimumReportInterval = new(CommonProperties, 12);
    private static readonly PropertyKey CurrentReportInterval = new(CommonProperties, 13);
    private static readonly PropertyKey AxisX = new(CustomDataFormat, 7);
    private static readonly PropertyKey AxisY = new(CustomDataFormat, 8);
    private static readonly PropertyKey AxisZ = new(CustomDataFormat, 9);
    private static readonly PropertyKey HardwareReportCounter = new(CustomDataFormat, 34);

    private readonly object _gate = new();
    private ISensor? _accelerometer;
    private ISensor? _gyrometer;
    private IntervalState _accelerometerInterval;
    private IntervalState _gyrometerInterval;
    private uint? _lastCounter;

    private LegacyPhysicalMotionSensors(
        ISensor accelerometer,
        ISensor gyrometer,
        string accelerometerPath,
        string gyrometerPath)
    {
        _accelerometer = accelerometer;
        _gyrometer = gyrometer;
        AccelerometerPath = accelerometerPath;
        GyrometerPath = gyrometerPath;
    }

    /// <summary>The Sensor API path for the Intel ISS physical accelerometer.</summary>
    public string AccelerometerPath { get; }

    /// <summary>The Sensor API path for the Intel ISS physical gyrometer.</summary>
    public string GyrometerPath { get; }

    /// <summary>Finds, validates, and configures the exact physical LSM6DSO collections.</summary>
    /// <returns>An owned sensor pair, or null when either reviewed collection is unavailable.</returns>
    public static LegacyPhysicalMotionSensors? TryOpen()
    {
        object? managerObject = null;
        ISensorCollection? collection = null;
        ISensor? accelerometer = null;
        ISensor? gyrometer = null;
        string? accelerometerPath = null;
        string? gyrometerPath = null;
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

                    if (accelerometer is null
                        && IsExpectedSensor(sensor, ExpectedAccelerometerName, requireCounter: false, out string? path))
                    {
                        accelerometer = sensor;
                        accelerometerPath = path;
                        sensor = null;
                        continue;
                    }

                    if (gyrometer is null
                        && IsExpectedSensor(sensor, ExpectedGyrometerName, requireCounter: true, out path))
                    {
                        gyrometer = sensor;
                        gyrometerPath = path;
                        sensor = null;
                    }
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

            if (accelerometer is null || gyrometer is null)
            {
                PluginTrace.Info(
                    "motion",
                    $"Legacy Sensor API exposed {count} custom sensors, but the reviewed physical "
                    + $"IMU pair was incomplete (accelerometer={accelerometer is not null}, gyrometer={gyrometer is not null}).");
                return null;
            }

            LegacyPhysicalMotionSensors candidate = new(
                accelerometer,
                gyrometer,
                accelerometerPath!,
                gyrometerPath!);
            accelerometer = null;
            gyrometer = null;
            if (candidate.TryRead(out _, out string? error) == PhysicalMotionReadResult.Failed)
            {
                PluginTrace.Warn(
                    "motion",
                    $"The physical IMU was present but its custom fields could not be read: {error}");
                candidate.Dispose();
                return null;
            }

            candidate.ConfigureFastestIntervals();
            PluginTrace.Info(
                "motion",
                $"Legacy physical IMU active: gyro={candidate.GyrometerPath}; accel={candidate.AccelerometerPath}.");
            return candidate;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            PluginTrace.Failure("motion", "Legacy Sensor API discovery failed", ex);
            return null;
        }
        finally
        {
            Release(accelerometer);
            Release(gyrometer);
            Release(collection);
            Release(managerObject);
        }
    }

    /// <summary>Reads one physical gyroscope report and the latest physical acceleration.</summary>
    /// <param name="reading">The sensor-space values and timestamp; meaningful only when fresh.</param>
    /// <param name="error">The decisive COM or value failure when the read failed.</param>
    /// <returns>
    /// Whether this poll produced a new hardware report. Repeat reports are this transport's
    /// concern, not the caller's: the gyrometer's opaque report counter is the only way to tell
    /// them apart, and it is read here before the values it would otherwise qualify.
    /// </returns>
    public PhysicalMotionReadResult TryRead(out PhysicalMotionReading reading, out string? error)
    {
        lock (_gate)
        {
            reading = default;
            error = null;
            if (_gyrometer is not { } gyrometer || _accelerometer is not { } accelerometer)
            {
                error = "the physical IMU handles are closed";
                return PhysicalMotionReadResult.Failed;
            }

            // The Intel accelerometer's synchronous GetData can wait for its next changed report
            // while the device is still. Read it first so that the gyrometer report and timestamp
            // are acquired last, immediately before this combined sample is published.
            if (!TryReadVector(accelerometer, out Vector3 acceleration, out error))
            {
                return PhysicalMotionReadResult.Failed;
            }

            PhysicalMotionReadResult result = TryReadGyrometer(
                gyrometer,
                out Vector3 angularVelocity,
                out DateTimeOffset timestamp,
                out uint counter,
                out error);
            if (result != PhysicalMotionReadResult.Fresh)
            {
                return result;
            }

            _lastCounter = counter;
            reading = new PhysicalMotionReading(angularVelocity, acceleration, timestamp);
            return PhysicalMotionReadResult.Fresh;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            RestoreInterval(_gyrometer, ExpectedGyrometerName, _gyrometerInterval);
            RestoreInterval(_accelerometer, ExpectedAccelerometerName, _accelerometerInterval);
            Release(_gyrometer);
            Release(_accelerometer);
            _gyrometer = null;
            _accelerometer = null;
        }
    }

    internal static bool MatchesExpectedIdentity(
        string? friendlyName,
        Guid type,
        string? devicePath,
        string expectedFriendlyName) =>
        string.Equals(friendlyName, expectedFriendlyName, StringComparison.OrdinalIgnoreCase)
        && type == CustomSensorType
        && devicePath?.Contains("VID_8087&PID_0AC2", StringComparison.OrdinalIgnoreCase) is true;

    private void ConfigureFastestIntervals()
    {
        lock (_gate)
        {
            _gyrometerInterval = ConfigureFastestInterval(_gyrometer, ExpectedGyrometerName);
            _accelerometerInterval = ConfigureFastestInterval(_accelerometer, ExpectedAccelerometerName);
        }
    }

    private static IntervalState ConfigureFastestInterval(ISensor? sensor, string name)
    {
        if (sensor is null
            || !TryReadUnsignedProperty(sensor, CurrentReportInterval, out uint original)
            || !TryReadUnsignedProperty(sensor, MinimumReportInterval, out uint minimum))
        {
            PluginTrace.Warn("motion", $"{name} report interval properties could not be read.");
            return default;
        }

        uint requested = minimum == 0 ? 10u : minimum;
        if (original == requested)
        {
            PluginTrace.Info("motion", $"{name} report interval is {requested} ms.");
            return new IntervalState(original, requested, Changed: false);
        }

        if (!TrySetUnsignedProperty(sensor, CurrentReportInterval, requested, out string? error))
        {
            PluginTrace.Warn(
                "motion",
                $"{name} could not request its fastest {requested} ms report interval: {error}.");
            return new IntervalState(original, requested, Changed: false);
        }

        if (!TryReadUnsignedProperty(sensor, CurrentReportInterval, out uint effective)
            || effective != requested)
        {
            PluginTrace.Warn(
                "motion",
                $"{name} did not confirm its requested {requested} ms report interval.");
            // SetProperties succeeded, so retain ownership state even when readback did not. On
            // release, restoration occurs only if the current value still equals our request.
            return new IntervalState(original, requested, Changed: true);
        }

        PluginTrace.Info(
            "motion",
            $"{name} report interval changed from {original} ms to the driver minimum {effective} ms.");
        return new IntervalState(original, effective, Changed: true);
    }

    private static void RestoreInterval(ISensor? sensor, string name, IntervalState state)
    {
        if (sensor is null || !state.Changed)
        {
            return;
        }

        if (!TryReadUnsignedProperty(sensor, CurrentReportInterval, out uint current))
        {
            PluginTrace.Warn("motion", $"{name} report interval could not be read during release.");
            return;
        }

        // Do not overwrite a newer choice made by another client while this cycle was active.
        if (current != state.Applied)
        {
            PluginTrace.Info(
                "motion",
                $"{name} report interval is now {current} ms; leaving that external value unchanged.");
            return;
        }

        if (!TrySetUnsignedProperty(sensor, CurrentReportInterval, state.Original, out string? error))
        {
            PluginTrace.Warn(
                "motion",
                $"{name} report interval could not be restored to {state.Original} ms: {error}.");
        }
    }

    private static bool IsExpectedSensor(
        ISensor sensor,
        string expectedFriendlyName,
        bool requireCounter,
        out string? devicePath)
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
        return MatchesExpectedIdentity(friendlyName, type, devicePath, expectedFriendlyName)
            && Supports(sensor, AxisX)
            && Supports(sensor, AxisY)
            && Supports(sensor, AxisZ)
            && (!requireCounter || Supports(sensor, HardwareReportCounter));
    }

    private static bool Supports(ISensor sensor, PropertyKey key) =>
        sensor.SupportsDataField(ref key, out short supported) >= 0 && supported != 0;

    private PhysicalMotionReadResult TryReadGyrometer(
        ISensor sensor,
        out Vector3 angularVelocity,
        out DateTimeOffset timestamp,
        out uint counter,
        out string? error)
    {
        angularVelocity = default;
        timestamp = default;
        counter = 0;
        error = null;
        ISensorDataReport? report = null;
        try
        {
            int result = sensor.GetData(out report);
            if (result < 0 || report is null)
            {
                error = $"Physical Gyrometer GetData returned 0x{result:X8}";
                return PhysicalMotionReadResult.Failed;
            }

            // Poll faster than the 10 ms report interval and roughly four polls in five repeat the
            // previous report. Qualify the report before marshalling anything else out of it: the
            // three axis PROPVARIANTs and the SYSTEMTIME below are the bulk of this path's cost and
            // would only be discarded.
            if (!TryReadUnsignedValue(report, HardwareReportCounter, out counter, out error))
            {
                return PhysicalMotionReadResult.Failed;
            }

            if (_lastCounter == counter)
            {
                return PhysicalMotionReadResult.Duplicate;
            }

            if (!TryReadVector(report, out angularVelocity, out error))
            {
                return PhysicalMotionReadResult.Failed;
            }

            result = report.GetTimestamp(out SystemTime systemTime);
            if (result < 0 || !systemTime.TryToUtc(out timestamp))
            {
                error = result < 0
                    ? $"Physical Gyrometer timestamp returned 0x{result:X8}"
                    : "Physical Gyrometer returned an invalid SYSTEMTIME";
                return PhysicalMotionReadResult.Failed;
            }

            return PhysicalMotionReadResult.Fresh;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return PhysicalMotionReadResult.Failed;
        }
        finally
        {
            Release(report);
        }
    }

    private static bool TryReadVector(ISensor sensor, out Vector3 value, out string? error)
    {
        value = default;
        error = null;
        ISensorDataReport? report = null;
        try
        {
            int result = sensor.GetData(out report);
            if (result < 0 || report is null)
            {
                error = $"Physical Accelerometer GetData returned 0x{result:X8}";
                return false;
            }

            return TryReadVector(report, out value, out error);
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

    private static bool TryReadVector(
        ISensorDataReport report,
        out Vector3 value,
        out string? error)
    {
        value = default;
        if (!TryReadNumericValue(report, AxisX, out float x, out error)
            || !TryReadNumericValue(report, AxisY, out float y, out error)
            || !TryReadNumericValue(report, AxisZ, out float z, out error))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

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

    private static bool TryReadUnsignedProperty(ISensor sensor, PropertyKey key, out uint result)
    {
        result = 0;
        PropVariant value = default;
        int hresult = sensor.GetProperty(ref key, out value);
        if (hresult < 0)
        {
            return false;
        }

        try
        {
            if (value.VariantType != 19)
            {
                return false;
            }

            result = value.UInt32;
            return true;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static bool TrySetUnsignedProperty(
        ISensor sensor,
        PropertyKey key,
        uint value,
        out string? error)
    {
        error = null;
        IPortableDeviceValues? properties = null;
        IPortableDeviceValues? results = null;
        try
        {
            properties = (IPortableDeviceValues)new PortableDeviceValuesClass();
            int result = properties.SetUnsignedIntegerValue(ref key, value);
            if (result < 0)
            {
                error = $"building the property set returned 0x{result:X8}";
                return false;
            }

            result = sensor.SetProperties(properties, out results);
            if (result < 0)
            {
                error = $"SetProperties returned 0x{result:X8}";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            Release(results);
            Release(properties);
        }
    }

    private static bool TryReadNumericValue(
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

    private static bool TryReadUnsignedValue(
        ISensorDataReport report,
        PropertyKey key,
        out uint value,
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
            if (variant.VariantType != 19)
            {
                error = $"custom field {key.PropertyId} had variant type {variant.VariantType}, not VT_UI4";
                return false;
            }

            value = variant.UInt32;
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

    private readonly record struct IntervalState(uint Original, uint Applied, bool Changed);

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

        public readonly bool TryToUtc(out DateTimeOffset timestamp)
        {
            try
            {
                timestamp = new DateTimeOffset(
                    Year,
                    Month,
                    Day,
                    Hour,
                    Minute,
                    Second,
                    Milliseconds,
                    TimeSpan.Zero);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                timestamp = default;
                return false;
            }
        }
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
        [PreserveSig] int SetProperties(IPortableDeviceValues properties, out IPortableDeviceValues? results);
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

    [ComImport]
    [Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetValue([In] ref PropertyKey key, [In] ref PropVariant value);
        [PreserveSig] int GetValue([In] ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetStringValue([In] ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue([In] ref PropertyKey key, out nint value);
        [PreserveSig] int SetUnsignedIntegerValue([In] ref PropertyKey key, uint value);
    }

    [ComImport]
    [Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    private class SensorManagerClass;

    [ComImport]
    [Guid("0C15D503-D017-47CE-9016-7B3F978721CC")]
    private class PortableDeviceValuesClass;
}

/// <summary>One physical IMU report before device-to-application axis conversion.</summary>
internal readonly record struct PhysicalMotionReading(
    Vector3 AngularVelocity,
    Vector3 Acceleration,
    DateTimeOffset Timestamp);

/// <summary>What one poll of the physical IMU produced.</summary>
internal enum PhysicalMotionReadResult
{
    /// <summary>A hardware report the caller has not seen before.</summary>
    Fresh,

    /// <summary>The sensor repeated its previous report; nothing to publish.</summary>
    Duplicate,

    /// <summary>The read failed; the reported error names the decisive COM or value fault.</summary>
    Failed,
}
