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
| `WindowsMotionSource.cs` | physical legacy-Sensor-API IMU polling, freshness, and zero-rate offset correction |
| `LegacyPhysicalMotionSensors.cs` | exact Intel ISS/LSM6DSO COM identity, fields, interval ownership, and cleanup |
| `ArcSyncTransport.cs` | variable refresh through Intel's Graphics Control Library |
| `ClawRecoveryJournal.cs` | leaving the device safe when a cycle ends badly |

Motion writes no per-report file. Nothing here may log at the 100 Hz sensor cadence: a CSV of every
report cost roughly 10 MB per five minutes of play, which is not a diagnostic anyone should leave on
a handheld's SSD. The ordinary WSGM log receives transitions only — the measured offset, a read
failure and its recovery — never per-report data. Investigations that genuinely need the raw stream
add the capture temporarily and remove it with the finding, as the offset below was.

### This part's gyroscope has a zero-rate offset, and only the plugin can remove it

Intel's ISS stack publishes the LSM6DSO's rates uncorrected. Two eight-minute stationary captures on
the reference unit, hours apart, both measured the same offset in sensor space:

| | X | Y | Z |
| --- | --- | --- | --- |
| offset (degrees/second) | +0.75 | −0.37 | −0.14 |
| noise, 1σ | 0.13 | 0.07 | 0.06 |

It is a hardware offset, not a mapping or gravity error: it is identical while the device lies flat
and while it is held tilted, and identical across sessions. Nothing downstream removes it. A Steam
Deck's own gyroscope arrives offset-free, so Steam integrates whatever a Deck target reports — here
that was a permanent 12-count pitch and −6-count yaw, drifting the view along one fixed diagonal
forever. `StationaryGyroBiasCalibrator` therefore measures the offset from ~2 s rest windows and
subtracts it. Every threshold in it comes from the table above and from the same captures: peak
stationary spans of 1.47 degrees/second and 0.023 g over 200 reports. Re-deriving them means
re-capturing, which is the deliberate cost of not shipping the capture. Correction is subtraction
only — a deadband would trade the drift for a dead zone around rest, which is a worse artifact than
the noise it hides.

One case is worth understanding before changing this. A steady yaw is the single motion no
acceleration gate can separate from rest — a constant-radius turn holds both the rate and the
acceleration vector still — so a device powered on aboard a moving vehicle measures that turn as its
offset. No software fixes that without an external heading reference. What the design does instead
is make it temporary: a run of later windows that agree with each other but not with the stored
value replaces it. Clamping refinement to a maximum step, which looks like the safe choice, is the
one thing that must not be done here — every honest window after the turn stops is further away than
such a clamp allows, so the wrong offset would outlive the entire device cycle.

### OEM keyboard side effects

The right OEM button also emits a malformed Windows-key chord: an orphan `G UP` for a short
press, or `Tab UP` for a long press. The plugin suppresses those measured sequences while its
OEM service is active, including on the Windows desktop. Normal keyboard Win+G and Win+Tab,
Ctrl/Alt/Shift-modified chords, injected input, volume keys and unknown sequences pass through.

The synthetic Win-key release uses the full 40-byte Windows x64 `INPUT` record. A keyboard-only
union incorrectly reduced it to 32 bytes, so Windows rejected the release and the hook passed
the firmware chord through. Layout and sequence tests cover the correction without installing a
hook or sending input to the live desktop. No new device observation is claimed by those tests.

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
