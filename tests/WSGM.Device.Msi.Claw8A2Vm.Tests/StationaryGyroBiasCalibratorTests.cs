using System.Numerics;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class StationaryGyroBiasCalibratorTests
{
    /// <summary>The zero-rate offset measured on the reference unit over eight stationary minutes.</summary>
    private static readonly Vector3 MeasuredOffset = new(0.752f, -0.373f, -0.144f);

    /// <summary>The acceleration that unit reports lying flat and still.</summary>
    private static readonly Vector3 Rest = new(-0.004f, -0.005f, 1.012f);

    [Fact]
    public void PassesRatesThroughUnchangedBeforeTheFirstRestWindowCompletes()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        Vector3 corrected = Vector3.Zero;
        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount - 1; index++)
        {
            corrected = calibrator.Correct(MeasuredOffset, Rest);
        }

        Assert.Null(calibrator.Bias);
        Assert.Equal(MeasuredOffset, corrected);
    }

    [Fact]
    public void MeasuresTheZeroRateOffsetFromARestWindowAndSubtractsIt()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        FeedRestWindow(calibrator);

        Vector3 bias = Assert.NotNull(calibrator.Bias);
        Assert.Equal(MeasuredOffset.X, bias.X, 3);
        Assert.Equal(MeasuredOffset.Y, bias.Y, 3);
        Assert.Equal(MeasuredOffset.Z, bias.Z, 3);

        Vector3 corrected = calibrator.Correct(MeasuredOffset, Rest);
        Assert.Equal(0f, corrected.X, 3);
        Assert.Equal(0f, corrected.Y, 3);
        Assert.Equal(0f, corrected.Z, 3);
    }

    [Fact]
    public void PreservesSmallResidualRatesInsteadOfApplyingADeadband()
    {
        StationaryGyroBiasCalibrator calibrator = new();
        FeedRestWindow(calibrator);

        Vector3 corrected = calibrator.Correct(MeasuredOffset + new Vector3(0.02f, 0f, 0f), Rest);

        Assert.Equal(0.02f, corrected.X, 3);
    }

    [Fact]
    public void NoiseWithinTheRestWindowAveragesOutOfTheMeasuredOffset()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        // Alternating deviations far larger than the recorded per-axis noise, so an estimator that
        // latched a single quiet report rather than the window mean would fail here.
        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount; index++)
        {
            float swing = index % 2 == 0 ? 0.4f : -0.4f;
            calibrator.Correct(MeasuredOffset + new Vector3(swing, swing, swing), Rest);
        }

        Vector3 bias = calibrator.Bias!.Value;
        Assert.Equal(MeasuredOffset.X, bias.X, 3);
        Assert.Equal(MeasuredOffset.Y, bias.Y, 3);
        Assert.Equal(MeasuredOffset.Z, bias.Z, 3);
    }

    [Fact]
    public void ChangingAngularRateRestartsTheRestWindow()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount * 4; index++)
        {
            float ramp = index % 8 * 1.5f;
            calibrator.Correct(MeasuredOffset + new Vector3(ramp, 0f, 0f), Rest);
        }

        Assert.Null(calibrator.Bias);
    }

    [Fact]
    public void TiltingBreaksTheRestWindowEvenWhileGravityMagnitudeStaysCorrect()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        // A slow pitch keeps |acceleration| at 1 g throughout, so only the acceleration span gate
        // separates it from rest. Without it the tilt rate itself would be learned as an offset.
        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount * 4; index++)
        {
            float angle = index * 0.01f;
            Vector3 acceleration = new(MathF.Sin(angle), 0f, MathF.Cos(angle));
            calibrator.Correct(new Vector3(0.6f, -0.3f, -0.1f), acceleration);
        }

        Assert.Null(calibrator.Bias);
    }

    [Fact]
    public void AccelerationThatIsNotGravityBreaksTheRestWindow()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount * 4; index++)
        {
            calibrator.Correct(MeasuredOffset, new Vector3(0f, 0f, 1.6f));
        }

        Assert.Null(calibrator.Bias);
    }

    [Fact]
    public void RejectsARestWindowWhoseRateIsAnImplausibleHardwareOffset()
    {
        StationaryGyroBiasCalibrator calibrator = new();

        // A steady yaw is the one rotation the acceleration gates cannot see. The magnitude limit
        // is what stops it from being adopted as this part's zero-rate offset.
        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount * 2; index++)
        {
            calibrator.Correct(new Vector3(0f, 30f, 0f), Rest);
        }

        Assert.Null(calibrator.Bias);
    }

    [Fact]
    public void OneDistantWindowDoesNotDisturbAMeasuredOffset()
    {
        StationaryGyroBiasCalibrator calibrator = new();
        FeedRestWindow(calibrator);
        Vector3 latched = calibrator.Bias!.Value;

        FeedRestWindow(calibrator, MeasuredOffset + new Vector3(0f, 2f, 0f));

        Assert.Equal(latched, calibrator.Bias!.Value);
    }

    [Fact]
    public void AStartupYawIsMeasuredAsOffsetButDoesNotSurviveTheDeviceSettling()
    {
        // A device powered on aboard a vehicle already turning at a steady 3 degrees/second: the
        // turn holds gravity at 1 g and the rate steady, so no acceleration gate can tell it from
        // rest and the turn is measured as the offset. This is the documented limit.
        StationaryGyroBiasCalibrator calibrator = new();
        Vector3 turning = MeasuredOffset + new Vector3(0f, -3f, 0f);
        FeedRestWindow(calibrator, turning);
        Assert.Equal(-3f + MeasuredOffset.Y, calibrator.Bias!.Value.Y, 3);

        // What must not happen is that the mistake outlives the turn. Once the vehicle stops
        // turning, agreeing rest windows re-acquire the real offset.
        for (int window = 0; window < StationaryGyroBiasCalibrator.ReacquireWindowCount; window++)
        {
            FeedRestWindow(calibrator);
        }

        Vector3 bias = calibrator.Bias!.Value;
        Assert.Equal(MeasuredOffset.X, bias.X, 3);
        Assert.Equal(MeasuredOffset.Y, bias.Y, 3);
        Assert.Equal(MeasuredOffset.Z, bias.Z, 3);
    }

    [Fact]
    public void DisagreeingDistantWindowsDoNotAccumulateTowardReacquisition()
    {
        StationaryGyroBiasCalibrator calibrator = new();
        FeedRestWindow(calibrator);
        Vector3 latched = calibrator.Bias!.Value;

        // Far from the measured offset, and far from each other: motion, not a moved offset.
        for (int window = 0; window < StationaryGyroBiasCalibrator.ReacquireWindowCount * 2; window++)
        {
            FeedRestWindow(calibrator, MeasuredOffset + new Vector3(0f, window % 2 == 0 ? 2f : -2f, 0f));
        }

        Assert.Equal(latched, calibrator.Bias!.Value);
    }

    [Fact]
    public void TracksSlowOffsetDriftByPartiallyApplyingALaterWindow()
    {
        StationaryGyroBiasCalibrator calibrator = new();
        FeedRestWindow(calibrator);

        Vector3 warmed = MeasuredOffset + new Vector3(0.2f, 0f, 0f);
        FeedRestWindow(calibrator, warmed);

        float expected = MeasuredOffset.X
            + (0.2f * StationaryGyroBiasCalibrator.RefinementWeight);
        Assert.Equal(expected, calibrator.Bias!.Value.X, 3);
    }

    private static void FeedRestWindow(StationaryGyroBiasCalibrator calibrator) =>
        FeedRestWindow(calibrator, MeasuredOffset);

    private static void FeedRestWindow(
        StationaryGyroBiasCalibrator calibrator,
        Vector3 angularVelocity)
    {
        for (int index = 0; index < StationaryGyroBiasCalibrator.WindowSampleCount; index++)
        {
            calibrator.Correct(angularVelocity, Rest);
        }
    }
}
