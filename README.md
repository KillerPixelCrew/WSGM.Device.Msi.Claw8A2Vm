# WSGM Device Plugin — MSI Claw 8 AI+ A2VM

The device plugin that teaches [WSGM](https://github.com/KillerPixelCrew/WSGM) an MSI Claw 8 AI+
A2VM: power and charge limits, fan behaviour, lighting, the controller and its motion sensors, the
OEM buttons, variable refresh, and the physical glyphs shown in Steam.

It is also **the reference implementation of the
[WSGM Device SDK](https://github.com/KillerPixelCrew/WSGM.Device.Sdk)** — the plugin to read, and
copy from, when writing one for another handheld. That is why it is MIT: a reference nobody may copy
is not a reference.

## What a plugin actually does

WSGM owns the session and the UI; the plugin owns the hardware. It publishes *semantic capabilities*
— "a TDP limit between these bounds", "a fan curve", "a lighting zone" — and WSGM renders them and
routes user intent back as commands. WSGM never touches the device.

This one is a worked example of the parts that are easy to get wrong:

| File | What it demonstrates |
| --- | --- |
| `Claw8A2VmPlugin.cs` | the lifecycle: detect, start, command, settings, stop |
| `ClawCapabilities.cs` | publishing capabilities and reporting refusals honestly |
| `MsiWmiPlatform.cs` | the vendor WMI surface behind power and fans |
| `WindowsHidTransports.cs` | HID transports for OEM controls and lighting |
| `WindowsMotionSource.cs` | physical legacy-Sensor-API IMU polling, freshness, and raw target delivery |
| `LegacyPhysicalMotionSensors.cs` | exact Intel ISS/LSM6DSO COM identity, fields, interval ownership, and cleanup |
| `GyroCsvLog.cs` | bounded, non-blocking raw/published gyroscope cadence diagnostics |
| `ArcSyncTransport.cs` | variable refresh through Intel's Graphics Control Library |
| `ClawRecoveryJournal.cs` | leaving the device safe when a cycle ends badly |

While motion is active, the production source writes `%LOCALAPPDATA%\WSGM\gyro.csv`. It retains one
`gyro.previous.csv` rotation; each file is capped at 16 MiB. The ordinary WSGM log receives only the
start path or a writer failure, never the per-report data.

## Hardware knowledge is observed, not documented

Every register, report layout and WMI method here was established on a physical device. `PROVENANCE.md`
records the hardware revision it was confirmed against. A different Claw model is a different device,
and detection does not claim it. Both detection and startup require the exact manufacturer,
`MS-1T52` baseboard and `1T52.1` SKU. Startup repeats that check from SMBIOS and returns before it
queries MSI's EC-backed WMI provider, controller inventory, HID endpoints or power state on any
other machine.

That is the honest constraint of this whole category: nothing here can be derived from a datasheet,
so nothing here should be trusted on a machine it was not confirmed on.

## Building

```powershell
git clone --recursive https://github.com/KillerPixelCrew/WSGM.Device.Msi.Claw8A2Vm
dotnet build WSGM.Device.Msi.Claw8A2Vm.slnx
dotnet test  WSGM.Device.Msi.Claw8A2Vm.slnx
```

`--recursive` matters: the SDK and Device Lab are commit-pinned submodules. Without them, the build
or packaging step fails on an empty project path.

The tests are unattended and hardware-free. They drive the plugin through `PluginTestKit` against
fake transports, which is the only kind of test that belongs in CI — the real behaviour is only ever
proven on the device.

## Packaging

```powershell
./eng/pack.ps1
```

This publishes framework-dependent (WSGM loads the plugin into its own process, which already has
the runtime), validates the assembled package with Device Lab built from its pinned submodule
commit, then packs the `.wsgmpkg`. The order is deliberate: validating after packing would prove
nothing about what was packed, and the Git link records exactly which validator source performed
the validation.

## Installing

WSGM ships this package as its built-in device component, so a normal WSGM install already has it.
To install a build of your own, see
[the authoring guide](https://github.com/KillerPixelCrew/WSGM/blob/2.0/docs/device-plugin-authoring.md) —
in short, expand the `.wsgmpkg` into a fresh directory and hand it to
`WSGM.exe --install-device-plugin`, which validates it again before replacing the protected slot.

## Licence

MIT. See `LICENSE`. A plugin links only the MIT SDK, never WSGM, so nothing here obliges a derived
plugin to any particular licence. Third-party notices are in
`src/WSGM.Device.Msi.Claw8A2Vm/THIRD_PARTY_NOTICES.md` — the glyph artwork is MIT from
`handheld-controller-glyphs`, and no Intel code is redistributed.
