# MSI Claw 8 AI+ A2VM plugin provenance

Source revision: `HW-2026-08-27`

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
