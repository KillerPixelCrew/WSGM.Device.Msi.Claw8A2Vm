using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>A2VM power limits from HC's ClawA2VM overrides and ClawA1M profile names/modes.</summary>
internal static class ClawPowerPresets
{
    // HC's inherited ClawA1M profile handler selects Eco/Green/Sport on AC and Comfort on DC.
    // TDP, firmware scenario and Windows mode are included. HC's CPU boost and Intel Endurance settings are
    // separate controls, not part of these shortcuts.
    internal static IReadOnlyList<DevicePowerPreset> All { get; } =
    [
        new("super-battery", "Super Battery", 8, 9, DevicePowerMode.BetterBattery)
        { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
        new("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)
        { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
        new("extreme-performance", "Extreme Performance", 30, 31, DevicePowerMode.BestPerformance)
        { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" },
        new("full-power", "Full Power", 37, 37, DevicePowerMode.BestPerformance)
        { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" },
    ];
}
