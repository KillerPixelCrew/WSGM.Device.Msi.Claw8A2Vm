# MSI Claw 8 AI+ A2VM plugin contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.Device.Msi.Claw8A2Vm/**`.

This repository is the MIT-licensed reference plugin for the MSI Claw 8 AI+ A2VM only. Read the root
`README.md`, `PROVENANCE.md`, the relevant tests, and the current implementation before changing
behavior. `PROVENANCE.md` records the revision plus current IGCL and motion evidence; implementation
comments and tests capture other measured facts and safety boundaries. Do not generalize measured
behavior to another Claw model or firmware without new attended evidence.

## Build, pins, and packaging

From the repository root:

```powershell
dotnet build WSGM.Device.Msi.Claw8A2Vm.slnx --configuration Release
dotnet test WSGM.Device.Msi.Claw8A2Vm.slnx --configuration Release --no-build
```

The plugin targets `net10.0-windows10.0.19041.0` and x64. `plugin.wsgm.json` is authoritative for
package ID, plugin version, API version, entry assembly, and entry type; a release tag must match
it. When package verification is requested, run `./eng/pack.ps1`; it performs the
framework-dependent publish, glyph staging, offline Device Lab validation, and deterministic package
creation. Do not tag, publish, or release unless explicitly asked.

`external/WSGM.Device.Sdk` and `external/WSGM.DeviceLab` are pinned source dependencies. Inspect
`git submodule status --recursive` first, and initialize or synchronize only to intended recorded
commits when no local submodule work would be overwritten. Advance them deliberately and
recursively; the direct SDK pin must equal Device Lab's nested SDK pin. Never copy their source into
this project or edit files inside a submodule as if they belonged here.

## Exact device boundary

Match the machine only by the measured SMBIOS identity in `ClawHardwareFacts`: manufacturer
`MICRO-STAR INTERNATIONAL CO., LTD.`, board `MS-1T52`, and SKU `1T52.1`. Package ID
`wsgm.device.msi.claw-8-a2vm` and definition ID `ms-1t52` identify software records, not the
machine. Treat EC firmware prefix `1T52EMS1.109`, MCU revision `0229`, and MSI USB VID `0DB0` with
the supported PIDs as separate service/capability gates.

Detection must remain side-effect free. `StartAsync` and every mutation must revalidate live
identity, firmware, service availability, generation, deadline, range, and current state before
access. Install `PluginTrace` before the first hardware read. Report truthful outcomes; never
convert an uncertain write or restore into success.

## Lifecycle and service behavior

- Serialize commands with observation so reads cannot race writes. Preserve whole-set publication
  and generation semantics.
- Keep periodic observation inside the host freshness window. A service read failure may degrade
  that service but must not kill the observation loop or unrelated services.
- Retract physical/OEM/descriptors if startup cannot establish the supported device. Treat OEM,
  power/charge/fans/telemetry, lighting, motion, controller, chord suppression, and optional display
  as independently degradable.
- Trace transitions, decisions, and keyed state changes only. Do not log every HID or motion report;
  cancellation is diagnostic, not an error.
- Stop and disposal must be bounded, idempotent, and honest about incomplete cleanup.

## Mutation invariants

All hardware protocols are bounded and allowlisted at their call sites. WMI calls are serialized,
the transport admits only `Get_*`/`Set_*` names, and callers must remain limited to the measured
methods with exact 32-byte payloads and the established timeout. Preserve unknown bytes and flags in
stateful read-modify-write formats such as fan and lighting payloads; power and charge deliberately
use zero-filled command envelopes.

- Power: keep PL1/PL2 within 8-37 W and PL1 <= PL2. Use ordered writes, exact readback, and rollback
  of the original pair.
- Scenarios: presets map Super Battery/Balanced/Extreme Performance to Eco/Green/Sport on AC
  and Comfort on battery, following HC's local ClawA1M handler inherited by ClawA2VM. Journal the
  exact original scenario byte with the watt pair. Select or restore the scenario before the pair,
  since firmware can reset power limits. Publish the resulting pair before reporting scenario
  success; inactive SHIFT must not be reported as an active preset. This mapping is source evidence,
  not an attended verification of its firmware effects.
- Charge: 60-100 percent is a persistent user setting. Verify it and roll back failed/cancelled
  changes; do not restore a successful choice on normal stop.
- Fans: one six-point semantic curve applies atomically to both channels under one snapshot. Verify
  both readbacks and restore both originals on failure.
- Lighting: treat the 32-byte MCU profile as persistent state. Preserve unknown bytes, replicate the
  three logical zones as measured, keep the write-rate limit, verify the full profile, and exactly
  roll back failure or cancellation. Do not revert a successful user choice on normal stop.
- Controller mode: stop source/output first; journal the original mode; switch, wait for
  re-enumeration, and identify the same physical device through `DEVPKEY_Device_LocationPaths`.
  Restore and verify the original mode during cleanup. Never report an unverified device as
  restored.
- Power, fans, and controller mode are temporary. Capture the first original value in
  `temporary-state.v1.json` before mutation, publish the bounded journal atomically, restore only on
  the same supported firmware, retain failed entries for retry, and block unsafe mismatches.
- Optional VRR/display support remains capability-probed and cycle-scoped. Load the user's Intel
  control library dynamically; do not ship Intel binaries. Preserve tested IGCL ABI sizes, capture
  the original profile on acquire, and restore that exact profile during make-safe.

## Input and motion invariants

- Preserve the measured DirectInput report layout: byte 7 bit 4 is left/M1 and bit 3 is right/M2.
  Assert the two bits separately so a swapped mapping cannot pass. Preserve OEM key codes and the
  120 ms latch used for reports without release events.
- Chord suppression belongs in this plugin. Suppress only the measured non-injected orphan `G`/`Tab`
  key-up while Win is down and Ctrl/Alt/Shift are not. Fail open otherwise and synthesize a Win
  release only when required. The hook callback must remain bounded, allocation-light, and free of
  I/O and logging.
- Keep the x64 `INPUT` ABI at 40 bytes with its 32-byte union, including for keyboard-only
  injection. A smaller record makes `SendInput` reject the synthetic Win release and the hook
  pass the firmware chord through. Keep the layout and shortcut-preservation regression tests.
- Synthetic left/right Win events must carry `KEYEVENTF_EXTENDEDKEY`; dummy-key events must not.
  Source comparison and layout tests do not establish that desktop Game Bar suppression works.
- Bind only the measured legacy Sensor API accelerometer/gyrometer identities and fields. Read
  accelerometer before gyrometer, reject duplicate counters, and keep the bounded drop-oldest
  channel.
- Apply the axis transform `(raw X, raw Z, -raw Y)` exactly once.
- Preserve the measurement-derived stationary gyro bias behavior: approximately 200-report windows,
  subtraction without deadband, rest gates, and agreement across three separated windows before
  distant-bias reacquisition. Preserve resampling and reset semantics; do not clamp away a valid
  distant correction.

## Glyphs, tests, and evidence

The glyph profile is a byte-locked evidence manifest. When artwork changes, update the corresponding
SHA-256, byte length, source revision, and notice; do not silently normalize or replace authored
bytes. Keep all manifest assets packaged.

CI is software-only. Any claim about WMI, HID, Sensor API, controller re-enumeration, fan/lighting
payloads, power behavior, or display behavior requires an explicit attended Device Lab run on the
reference unit and a provenance update. Add focused regression tests for each changed identity gate,
protocol byte, timeout, rollback, journal, mapping, transform, calibration, ABI, and package
invariant. Preserve repository `.editorconfig` conventions.
