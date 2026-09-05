# MSI Claw 8 AI+ A2VM plugin provenance

Source revision: `HW-2026-09-03`

The 2026-09-05 keyboard comparison against HandheldCompanion revision
`5c94abca83f8711ff5620906871b31a41c76bf05`, `Helpers/FirmwareWorkarounds.cs`, found that
synthetic Win releases also need the extended-key flag. The plugin now supplies it and follows
HC's Win+G key-down interception, including normal keyboard Win+G with modifiers, as requested
by the maintainer after continued desktop failures. The existing measured G/Tab orphan-up path
remains. Sequence tests cover repeats, release order and failure without input injection. These
software corrections are not a new attended suppression pass.

Power-preset data was checked on 2026-09-05 against HandheldCompanion commit
`5c94abca83f8711ff5620906871b31a41c76bf05`: `Devices/MSI/ClawA2VM.cs` supplies the
8/8/9, 17/17/18 and 30/30/31 W overrides; `ClawA1M.cs` and `Properties/Resources.resx`
supply the names and Windows modes. WSGM's sustained/slow pair maps these to PL1/PL2
8/9, 17/18 and 30/31 W. AC firmware targets follow HC's Eco/Green/Sport choices, with Comfort
on battery. Full Power is a WSGM addition using 37/37 W, Best Performance, and Sport on AC or
Comfort on battery. These are independently implemented through the SDK. HC's CPU boost and Intel Endurance settings are
outside this shortcut. This is source evidence and fake-transport validation, not a new attended
hardware measurement. Existing power transport limits and rollback behavior are unchanged.

The package is first-party code licensed under the MIT License. It is the reference implementation
of the WSGM Device SDK — the plugin other device plugins are expected to be read against and copied
from — which is why it is permissive rather than carrying WSGM's own GPL-3.0-or-later. A plugin
links only `WSGM.Device.Sdk`, never WSGM, so nothing here obliges a derived plugin to any licence.

Required third-party notices ship in `THIRD_PARTY_NOTICES.md`.

## Hardware knowledge in this package

Every register, report layout and WMI method here was established by observation on a physical
MSI Claw 8 AI+ A2VM, not from vendor documentation. Two consequences:

- The revision above identifies the hardware generation the behaviour was confirmed against. A
  different Claw model is a different device and is not claimed to be supported by detection.
- `ArcSyncTransport` binds Intel's Graphics Control Library dynamically, through `ControlLib.dll`
  as it already ships in `System32` with the Intel driver. No Intel code is redistributed here; the
  blittable structures mirror the published `igcl_api.h` layouts so the driver's own size checks
  pass, and a layout regression test pins them.
- Intel's IO/sensor driver exposes the STMicroelectronics LSM6DSO `Physical Accelerometer` and
  `Physical Gyrometer` through the legacy Sensor API as custom sensor type
  `e83af229-8640-4d18-a213-e22675ebb2c3` on the `VID_8087&PID_0AC2` HID collection. Their live
  values are `VT_R4` fields 7, 8, and 9 under property-set
  `b14c764f-07cf-41e8-9d82-ebe3d0776a6f`, in g and degrees/second respectively. Field 34 is the
  gyrometer's opaque `VT_UI4` hardware-report counter: it advances for stationary samples too.
  The gyrometer advertises a 10 ms minimum report interval (100 Hz); the accelerometer advertises
  2 ms, but its synchronous `GetData` can still wait about 200 ms for a changed report at rest.
  Combined reads therefore acquire acceleration first and the gyrometer last so a delayed
  accelerometer cannot make the published gyro report and timestamp stale. The application-axis
  transform for both die-aligned sensors is
  `(raw X, raw Z, -raw Y)`; the Steam Deck encoder reverses that once when filling the controller's
  raw IMU slots. WinRT does not project the accelerometer and its gyrometer event path suppresses
  unchanged reports.
