using System.Numerics;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class WindowsMotionSourceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PhysicalImuValuesUseTheSteamDeckApplicationAxisBasisOnce()
    {
        var sample = WindowsClawMotionSource.CreateSample(
            new Vector3(1f, 2f, 3f),
            Timestamp,
            new Vector3(0.25f, 0.75f, -0.5f));

        Assert.True(sample.HasAccelerometer);
        Assert.Equal(0.25f, sample.AccelX);
        Assert.Equal(-0.5f, sample.AccelY);
        Assert.Equal(-0.75f, sample.AccelZ);
        Assert.Equal(1f, sample.GyroX);
        Assert.Equal(3f, sample.GyroY);
        Assert.Equal(-2f, sample.GyroZ);
        Assert.Equal(Timestamp, sample.SensorTimestamp);
    }

    [Fact]
    public void MissingAccelerometerDataIsNotApproximated()
    {
        var sample = WindowsClawMotionSource.CreateSample(new Vector3(1f, 2f, 3f), Timestamp, null);

        Assert.False(sample.HasAccelerometer);
        Assert.Equal(0f, sample.AccelX);
        Assert.Equal(0f, sample.AccelY);
        Assert.Equal(0f, sample.AccelZ);
    }

    [Theory]
    [InlineData("Physical Accelerometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", true)]
    [InlineData("Physical Gyrometer", "Physical Gyrometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", true)]
    [InlineData("Calibrated Accelerometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", false)]
    [InlineData("Physical Gyrometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", false)]
    [InlineData("Physical Accelerometer", "Physical Accelerometer", "c2fb0f5f-e2d2-4c78-bcd0-352a9582819d", "HID#VID_8087&PID_0AC2", false)]
    [InlineData("Physical Accelerometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_1234&PID_5678", false)]
    public void OnlyTheReviewedCustomIntelCollectionMatches(
        string name,
        string expectedName,
        string type,
        string path,
        bool expected)
    {
        Assert.Equal(
            expected,
            LegacyPhysicalMotionSensors.MatchesExpectedIdentity(
                name,
                Guid.Parse(type),
                path,
                expectedName));
    }

    [Fact]
    public void StableRestCalibratesBiasAndKeepsSubCountNoiseAtZero()
    {
        var calibrator = new StationaryGyroBiasCalibrator();
        var stationaryBias = new Vector3(0.7f, -0.35f, -0.1f);

        for (int index = 0; index < StationaryGyroBiasCalibrator.RequiredSampleCount; index++)
        {
            Assert.Equal(Vector3.Zero, calibrator.Correct(stationaryBias, Vector3.UnitZ));
        }

        Assert.NotNull(calibrator.Bias);
        Assert.Equal(stationaryBias.X, calibrator.Bias.Value.X, 3);
        Assert.Equal(stationaryBias.Y, calibrator.Bias.Value.Y, 3);
        Assert.Equal(stationaryBias.Z, calibrator.Bias.Value.Z, 3);
        Assert.Equal(
            Vector3.Zero,
            calibrator.Correct(stationaryBias + new Vector3(0.07f, 0f, 0f), Vector3.UnitZ));
        Vector3 movement = calibrator.Correct(
            stationaryBias + new Vector3(0.25f, 0f, 0f),
            Vector3.UnitZ);
        Assert.Equal(0.25f, movement.X, 3);
        Assert.Equal(0f, movement.Y, 3);
        Assert.Equal(0f, movement.Z, 3);
    }

    [Fact]
    public void AimingMotionIsNotLearnedAsStationaryBias()
    {
        var calibrator = new StationaryGyroBiasCalibrator();
        var movement = new Vector3(2f, 0f, 0f);

        for (int index = 0; index < StationaryGyroBiasCalibrator.RequiredSampleCount * 2; index++)
        {
            Assert.Equal(movement, calibrator.Correct(movement, Vector3.UnitZ));
        }

        Assert.Null(calibrator.Bias);
    }
}
