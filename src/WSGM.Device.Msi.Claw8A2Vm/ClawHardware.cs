using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal static class ClawHardwareFacts
{
    public const string PackageId = "wsgm.device.msi.claw-8-a2vm";
    public const string DeviceDefinitionId = "ms-1t52";
    public const string Manufacturer = "MICRO-STAR INTERNATIONAL CO., LTD.";
    public const string BoardProduct = "MS-1T52";
    public const string SystemSku = "1T52.1";
    public const string EcFirmware = "1T52EMS1.109";
    public const string McuFirmware = "0229";
    public const string UsbVendorId = "0DB0";
    public const string XInputProductId = "1901";
    public const string DirectInputProductId = "1902";

    public const byte PowerSustainedAddress = 0x50;
    public const byte PowerBoostAddress = 0x51;
    public const byte ScenarioAddress = 0xD2;
    public const byte FanCustomAddress = 0xD4;
    public const byte FanFullSpeedAddress = 0x98;

    public const ushort LightingProfileAddress = 0x024A;
    public const int McuReportLength = 64;
    public const int WmiPackageLength = 32;
}

internal enum ClawControllerMode : byte
{
    Offline = 0,
    XInput = 1,
    DirectInput = 2,
}

internal sealed record ClawIdentityState
{
    public required DeviceIdentitySnapshot Snapshot { get; init; }

    public required bool ExactMachineMatch { get; init; }

    public required bool WmiFirmwareVerified { get; init; }

    public required bool McuFirmwareVerified { get; init; }

    public required bool OnAcPower { get; init; }
}

internal sealed record PowerPair(int SustainedWatts, int BoostWatts, byte Scenario);

internal sealed record FanTable(byte[] DutyBuffer, byte[] TemperatureBuffer);

internal sealed record FanSnapshot(
    FanTable Left,
    FanTable Right,
    byte CustomFlag,
    byte FullSpeedFlag);

internal sealed record FanTelemetry(int LeftRpm, int RightRpm, int TemperatureCelsius);

internal sealed record LightingState(
    int Brightness,
    int RightRingColor,
    int LeftRingColor,
    int ButtonsColor);

/// <summary>The controller interfaces observed at one moment, and what they looked like.</summary>
/// <param name="Mode">The mode the MCU reports.</param>
/// <param name="ProductId">The USB product id the MCU enumerated as.</param>
/// <param name="PhysicalLocation">Composite USB location shared by the interfaces.</param>
/// <param name="PhysicalDevices">Interfaces WSGM may hide, which is the set the handoff needs.</param>
/// <param name="ObservedEndpoints">
/// Every candidate endpoint seen at this location, as "productId/usagePage:usage in/out". Carried so
/// a failed handoff can say what it actually found rather than only that it found nothing — a plugin
/// has no logging channel of its own, so a reason string is the only way this reaches a log.
/// </param>
internal sealed record ControllerTopology(
    ClawControllerMode Mode,
    string ProductId,
    string PhysicalLocation,
    IReadOnlyList<PhysicalDeviceIdentity> PhysicalDevices,
    string ObservedEndpoints = "");

internal interface IClawIdentityReader
{
    ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken);
}

internal interface IMsiWmiTransport : IAsyncDisposable
{
    ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken);

    ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken);

    ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken);
}

internal interface IMsiOemEventSource : IAsyncDisposable
{
    ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal interface IClawMcuTransport : IAsyncDisposable
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken);

    ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);
}

internal interface IClawControllerSource : IAsyncDisposable
{
    ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(
        long cycleGeneration,
        Func<CanonicalControllerSample, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken);
}

internal interface IClawMotionSource : IAsyncDisposable
{
    ValueTask<bool> StartAsync(
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal interface IFirmwareChordSuppressor : IAsyncDisposable
{
    ValueTask<bool> StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal sealed record ClawHardwareServices(
    IClawIdentityReader Identity,
    IMsiWmiTransport Wmi,
    IMsiOemEventSource OemEvents,
    IClawMcuTransport Mcu,
    IClawControllerSource Controller,
    IClawMotionSource Motion,
    IFirmwareChordSuppressor ChordSuppressor,
    ClawOemButtonLatch OemButtons);

/// <summary>Applies the one minimum budget required before any Claw hardware write.</summary>
/// <remarks>
/// Two seconds covers the slowest journal flush plus one bounded firmware exchange. Keeping this
/// threshold here prevents lifecycle, command, lighting, and mode-switch paths from drifting apart.
/// </remarks>
internal static class ClawWriteBudget
{
    internal static readonly TimeSpan Minimum = TimeSpan.FromSeconds(2);

    internal static bool IsAvailable(DateTimeOffset deadline) =>
        deadline - DateTimeOffset.UtcNow >= Minimum;

    internal static void Require(DateTimeOffset deadline, string operation)
    {
        if (!IsAvailable(deadline))
        {
            throw new OperationCanceledException($"Insufficient budget for {operation}.");
        }
    }
}
