using System;
using System.Numerics;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>
/// Derives a specific-force reference vector from the gyroscope so this device's motion samples
/// carry an accelerometer field despite the hardware exposing none to Windows.
/// </summary>
/// <remarks>
/// The Claw has an accelerometer, but no path on Windows reaches it: the pad firmware (rev 0229)
/// ignores its motion-upload command in every mode and profile state, and the Intel Sensor Hub
/// exposes only the gyrometer (device-verified 2026-09-02). Steam Input, however, gates its Deck
/// gyro processing on a plausible gravity vector — an all-zero accelerometer reads as freefall and
/// disables gyro entirely, while a unit vector restores it (device-verified the same day).
/// <para>
/// This is short-term dead reckoning, not gravity estimation: the vector starts at app-space +Y
/// (the device-proven value) and is transported into the rotating body frame by the gyro via
/// Rodrigues' rotation, so short-term tilt stays consistent with the angular velocity Steam also
/// receives. Without an absolute reference the direction drifts; the guard that bounds it is
/// stillness-based gyro-bias nulling (the dominant drift source). A long timestamp gap is never
/// integrated across — the gap's end rate says nothing about the rotation inside it — but the
/// vector holds rather than resets, because on this sensor a quiet stretch means a still device
/// (the ISH suppresses unchanged readings), not lost history. There is deliberately no decay
/// toward the initial vector: rotating the reported gravity while the gyro reads zero is a
/// self-contradictory IMU stream.
/// </para>
/// <para>
/// The bias correction is internal only. The published gyro stays raw, because Steam runs its own
/// gyro calibration and a pre-corrected stream would fight it.
/// </para>
/// </remarks>
internal sealed class SyntheticGravity
{
    private static readonly Vector3 InitialUp = new(0f, 1f, 0f);

    /// <summary>Longest gap integrated as one step; anything above holds the vector instead.</summary>
    /// <remarks>
    /// Five 100 Hz sensor periods. The rate at the end of a longer stall is not evidence of the
    /// average rate during it — clamping instead of skipping can invent a large phantom rotation.
    /// </remarks>
    internal static readonly TimeSpan MaximumIntegrationStep = TimeSpan.FromMilliseconds(50);

    /// <summary>Per-axis spread within which a window still counts as the device lying still.</summary>
    private const float StillnessBandDegreesPerSecond = 1.5f;

    /// <summary>How long the device must stay inside the band before the bias is taken.</summary>
    private static readonly TimeSpan StillnessDuration = TimeSpan.FromSeconds(2);

    /// <summary>Largest believable per-axis bias; a misdetected slow rotation cannot exceed it.</summary>
    private const float BiasClampDegreesPerSecond = 3f;

    private const float DegreesToRadians = MathF.PI / 180f;

    private readonly object _gate = new();
    private Vector3 _up = InitialUp;
    private Vector3 _bias;
    private DateTimeOffset? _lastTimestamp;
    private DateTimeOffset? _windowStart;
    private Vector3 _windowMin;
    private Vector3 _windowMax;
    private Vector3 _windowSum;
    private int _windowCount;

    /// <summary>Returns the sample with the synthesized accelerometer attached.</summary>
    /// <param name="sample">The gyro reading as published, in the application-space basis.</param>
    /// <returns>The same sample carrying the current reference vector as a 1g accelerometer.</returns>
    /// <remarks>
    /// A sample that already carries an accelerometer, or carries no gyro, passes through
    /// untouched — real sensor data is never replaced with a derived value.
    /// </remarks>
    public MotionSample WithSyntheticAccelerometer(MotionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!sample.HasGyro || sample.HasAccelerometer)
        {
            return sample;
        }

        lock (_gate)
        {
            Vector3 gyro = new(sample.GyroX, sample.GyroY, sample.GyroZ);
            if (sample.SensorTimestamp is { } stamp)
            {
                UpdateStillnessUnderGate(gyro, stamp);
                if (_lastTimestamp is { } last)
                {
                    TimeSpan dt = stamp - last;
                    if (dt > TimeSpan.Zero && dt <= MaximumIntegrationStep)
                    {
                        Rotate(gyro - _bias, (float)dt.TotalSeconds);
                    }
                    else if (dt < TimeSpan.Zero)
                    {
                        // A backwards clock is a genuine discontinuity: the vector's history no
                        // longer describes anything, so reset and rebase.
                        _up = InitialUp;
                        _windowStart = null;
                    }
                    else if (dt > MaximumIntegrationStep)
                    {
                        // A long positive gap is the ISH gyrometer suppressing unchanged readings
                        // on a still device — routine, not a fault. The device did not rotate
                        // while the sensor was quiet, so the vector HOLDS; only the stillness
                        // window restarts (its mean would otherwise span the gap). Resetting here
                        // snapped gravity back to +Y after every pause, which itself jerked
                        // Steam's fusion (device-observed 2026-09-02).
                        _windowStart = null;
                    }
                    // dt == 0 is the same sensor reading riding a later publication: hold.
                }

                _lastTimestamp = stamp;
            }

            return sample with
            {
                AccelX = _up.X,
                AccelY = _up.Y,
                AccelZ = _up.Z,
                HasAccelerometer = true,
            };
        }
    }

    /// <summary>Forgets all state for a fresh sensor session.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _up = InitialUp;
            _bias = Vector3.Zero;
            _lastTimestamp = null;
            _windowStart = null;
        }
    }

    /// <summary>Transports the reference vector into the body frame rotated by one gyro step.</summary>
    /// <remarks>
    /// Exact Rodrigues rotation rather than an Euler step: a normalized Euler update advances by
    /// atan(|w|dt) instead of |w|dt and under-rotates fast flicks by whole degrees per second.
    /// For a world-fixed vector in body coordinates the transport is rotation by -angle about the
    /// angular-velocity axis, which the cross-product order below encodes.
    /// </remarks>
    private void Rotate(Vector3 gyroDegreesPerSecond, float dtSeconds)
    {
        Vector3 omega = gyroDegreesPerSecond * DegreesToRadians;
        float magnitude = omega.Length();
        float angle = magnitude * dtSeconds;
        if (angle <= 0f)
        {
            return;
        }

        Vector3 axis = omega / magnitude;
        (float sin, float cos) = MathF.SinCos(angle);
        Vector3 rotated = (_up * cos)
            + (Vector3.Cross(_up, axis) * sin)
            + (axis * Vector3.Dot(axis, _up) * (1f - cos));
        float length = rotated.Length();
        if (!float.IsFinite(length) || length < 0.5f)
        {
            _up = InitialUp;
            return;
        }

        _up = rotated / length;
    }

    /// <summary>Learns the gyro bias whenever the device demonstrably lies still.</summary>
    /// <remarks>
    /// Uncorrected bias of one or two degrees per second tips the reference vector over within a
    /// minute; nulling it while still is the one correction available without an accelerometer
    /// (the zero-angular-rate update of inertial navigation). The window restarts whenever any
    /// axis leaves the stillness band, so ordinary play never contributes to the estimate.
    /// </remarks>
    private void UpdateStillnessUnderGate(Vector3 gyro, DateTimeOffset stamp)
    {
        Vector3 min = Vector3.Min(_windowMin, gyro);
        Vector3 max = Vector3.Max(_windowMax, gyro);
        // Admission requires the whole window's spread, new sample included, to fit in the band.
        // Checking against the running extremes instead would let a slow ramp creep the window
        // open one in-band step at a time and turn a genuine rotation into a "bias".
        if (_windowStart is not { } start
            || stamp < start
            || max.X - min.X > StillnessBandDegreesPerSecond
            || max.Y - min.Y > StillnessBandDegreesPerSecond
            || max.Z - min.Z > StillnessBandDegreesPerSecond)
        {
            _windowStart = stamp;
            _windowMin = gyro;
            _windowMax = gyro;
            _windowSum = gyro;
            _windowCount = 1;
            return;
        }

        _windowMin = min;
        _windowMax = max;
        _windowSum += gyro;
        _windowCount++;
        if (stamp - _windowStart >= StillnessDuration)
        {
            Vector3 mean = _windowSum / _windowCount;
            _bias = Vector3.Clamp(
                mean,
                new Vector3(-BiasClampDegreesPerSecond),
                new Vector3(BiasClampDegreesPerSecond));
            _windowStart = stamp;
            _windowMin = gyro;
            _windowMax = gyro;
            _windowSum = gyro;
            _windowCount = 1;
        }
    }
}
