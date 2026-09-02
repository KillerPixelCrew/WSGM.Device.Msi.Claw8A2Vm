using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class SyntheticGravityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstGyroSampleGainsAUnitUpAccelerometer()
    {
        SyntheticGravity gravity = new();

        MotionSample result = gravity.WithSyntheticAccelerometer(Gyro(0f, 0f, 0f, T0));

        Assert.True(result.HasAccelerometer);
        Assert.True(result.HasGyro);
        Assert.Equal(0f, result.AccelX);
        Assert.Equal(1f, result.AccelY);
        Assert.Equal(0f, result.AccelZ);
    }

    [Fact]
    public void ARealAccelerometerPassesThroughUntouched()
    {
        SyntheticGravity gravity = new();
        MotionSample real = Gyro(1f, 2f, 3f, T0) with
        {
            HasAccelerometer = true,
            AccelX = 0.5f,
            AccelY = 0.5f,
            AccelZ = 0.5f,
        };

        Assert.Same(real, gravity.WithSyntheticAccelerometer(real));
    }

    [Fact]
    public void AGyrolessSamplePassesThroughUntouched()
    {
        SyntheticGravity gravity = new();
        MotionSample sample = new() { HasGyro = false, SensorTimestamp = T0 };

        Assert.Same(sample, gravity.WithSyntheticAccelerometer(sample));
    }

    [Fact]
    public void ANinetyDegreeRollTransportsUpIntoTheBodyFrame()
    {
        SyntheticGravity gravity = new();
        // 90 deg/s about +X for one second, 10 ms steps. World up expressed in the rotated body
        // frame lands on (0, cos90, -sin90) = (0, 0, -1).
        MotionSample last = Feed(gravity, x: 90f, steps: 100, stepMs: 10);

        Assert.Equal(0f, last.AccelX, 2);
        Assert.Equal(0f, last.AccelY, 2);
        Assert.Equal(-1f, last.AccelZ, 2);
    }

    [Fact]
    public void ARepeatedSensorTimestampHoldsTheVector()
    {
        SyntheticGravity gravity = new();
        gravity.WithSyntheticAccelerometer(Gyro(0f, 0f, 0f, T0));
        MotionSample moved = gravity.WithSyntheticAccelerometer(
            Gyro(500f, 0f, 0f, T0.AddMilliseconds(10)));
        MotionSample repeat = gravity.WithSyntheticAccelerometer(
            Gyro(500f, 0f, 0f, T0.AddMilliseconds(10)));

        Assert.Equal(moved.AccelY, repeat.AccelY);
        Assert.Equal(moved.AccelZ, repeat.AccelZ);
    }

    [Fact]
    public void ATimestampGapResetsInsteadOfInventingRotation()
    {
        SyntheticGravity gravity = new();
        Feed(gravity, x: 90f, steps: 50, stepMs: 10);

        // Half a second of missing history: the end rate says nothing about the gap, so the
        // vector must return to the initial reference rather than integrate across it.
        MotionSample afterGap = gravity.WithSyntheticAccelerometer(
            Gyro(90f, 0f, 0f, T0.AddSeconds(1)));

        Assert.Equal(0f, afterGap.AccelX, 3);
        Assert.Equal(1f, afterGap.AccelY, 3);
        Assert.Equal(0f, afterGap.AccelZ, 3);
    }

    [Fact]
    public void AStillDeviceNullsTheGyroBias()
    {
        SyntheticGravity gravity = new();
        // A constant 1 deg/s reading is indistinguishable from bias while the spread stays inside
        // the stillness band. After the two-second window the bias is nulled and the vector stops
        // tipping; without the correction three further seconds would add another three degrees.
        MotionSample last = Feed(gravity, x: 1f, steps: 500, stepMs: 10);

        double tilt = Math.Acos(Math.Clamp(last.AccelY, -1f, 1f)) * 180.0 / Math.PI;
        Assert.True(tilt < 2.5, $"Reference vector tipped {tilt:F2} degrees despite stillness.");
    }

    [Fact]
    public void TheVectorStaysUnitLengthThroughLongIntegration()
    {
        SyntheticGravity gravity = new();
        MotionSample last = Gyro(0f, 0f, 0f, T0);
        for (var i = 0; i < 2000; i++)
        {
            last = gravity.WithSyntheticAccelerometer(Gyro(
                x: 200f * MathF.Sin(i / 17f),
                y: 150f * MathF.Cos(i / 11f),
                z: 90f * MathF.Sin(i / 7f),
                T0.AddMilliseconds(10 * (i + 1))));
        }

        double length = Math.Sqrt(
            (last.AccelX * (double)last.AccelX)
            + (last.AccelY * (double)last.AccelY)
            + (last.AccelZ * (double)last.AccelZ));
        Assert.Equal(1.0, length, 3);
    }

    private static MotionSample Feed(SyntheticGravity gravity, float x, int steps, int stepMs)
    {
        MotionSample last = gravity.WithSyntheticAccelerometer(Gyro(x, 0f, 0f, T0));
        for (var i = 1; i <= steps; i++)
        {
            last = gravity.WithSyntheticAccelerometer(
                Gyro(x, 0f, 0f, T0.AddMilliseconds(stepMs * i)));
        }

        return last;
    }

    private static MotionSample Gyro(float x, float y, float z, DateTimeOffset stamp) => new()
    {
        GyroX = x,
        GyroY = y,
        GyroZ = z,
        HasGyro = true,
        HasAccelerometer = false,
        SensorTimestamp = stamp,
    };
}
