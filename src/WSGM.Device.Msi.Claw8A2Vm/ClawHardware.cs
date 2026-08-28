using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Input;

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
    public const byte ChargeLimitAddress = 0xD7;
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

internal sealed record FanTable(byte[] DutyBuffer, byte[] TemperatureBuffer)
{
    public FanTable Copy() => new([.. DutyBuffer], [.. TemperatureBuffer]);
}

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

internal sealed record ControllerTopology(
    ClawControllerMode Mode,
    string ProductId,
    string PhysicalLocation,
    IReadOnlyList<PhysicalDeviceIdentity> PhysicalDevices);

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

    ValueTask<ClawControllerMode> ReadModeAsync(CancellationToken cancellationToken);

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
        long deviceGeneration,
        Func<CanonicalControllerSample, ValueTask> publish,
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
    IFirmwareChordSuppressor ChordSuppressor);
