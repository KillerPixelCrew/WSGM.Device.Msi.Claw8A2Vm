using System.Numerics;
using WSGM.Device.Tests;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class GyroCsvLogTests
{
    [Fact]
    public async Task WritesInvariantDiagnosticRowAndHeader()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Root, GyroCsvLog.FileName);
        GyroCsvLog log = GyroCsvLog.Create(path, 1024 * 1024);

        log.Write(new GyroCsvRow(
            FreshIndex: 3,
            PollIndex: 17,
            ReceivedTimestamp: new DateTimeOffset(2026, 9, 3, 15, 0, 0, 123, TimeSpan.Zero),
            SensorTimestamp: new DateTimeOffset(2026, 9, 3, 15, 0, 0, 111, TimeSpan.Zero),
            ReceiveDeltaMilliseconds: 10.125,
            SensorDeltaMilliseconds: 10,
            SensorAgeMilliseconds: 12,
            ReadDurationMilliseconds: 0.375,
            HardwareCounter: 240,
            HardwareCounterDelta: 20,
            DuplicatePolls: 4,
            ReadFailures: 1,
            RawAngularVelocity: new Vector3(0.75f, -0.25f, 1.5f),
            Bias: new Vector3(0.5f, -0.125f, 0.25f),
            CorrectedAngularVelocity: new Vector3(0.25f, -0.125f, 1.25f),
            Acceleration: new Vector3(0.01f, -0.02f, 0.99f)));
        await log.DisposeAsync();

        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(GyroCsvLog.Header, lines[0]);
        Assert.Contains(",3,17,2026-09-03T15:00:00.1230000+00:00,", lines[1], StringComparison.Ordinal);
        Assert.Contains(",10.125,10.000,12.000,0.375,240,20,4,1,0,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",0.01,-0.02,0.99", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("10,125", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatesAnExistingFullLogBeforeWriting()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Root, GyroCsvLog.FileName);
        await File.WriteAllTextAsync(path, new string('x', 1024));
        GyroCsvLog log = GyroCsvLog.Create(path, 512);

        log.Write(new GyroCsvRow(
            1,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            0,
            0,
            1,
            null,
            0,
            0,
            Vector3.Zero,
            null,
            Vector3.Zero,
            Vector3.UnitZ));
        await log.DisposeAsync();

        Assert.Equal(new string('x', 1024), await File.ReadAllTextAsync(
            Path.Combine(temporary.Root, GyroCsvLog.PreviousFileName)));
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(GyroCsvLog.Header, lines[0]);
        Assert.Equal(2, lines.Length);
    }
}
