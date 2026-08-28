# MSI Claw 8 AI+ A2VM plugin provenance

This package is a first-party WSGM implementation for the exact `MS-1T52` device definition. Its
write eligibility is limited to EC firmware `1T52EMS1.109`, MSI WMI interface `8.0`, and controller
firmware/device release `0x0229`.

The identity values, MSI WMI package shape and addresses, controller-mode continuation key, raw HID
layout, rumble report, Intel ISS gyrometer binding, OEM event codes, firmware-chord signature, and
lighting profile layout were independently captured on the reference unit on 2026-08-27. The
hardware-verified record and rejected alternatives are retained in `_plan/claw-8-a2vm-plugin.md` in
the WSGM source tree.

Handheld Companion, HHD, the Linux `hid-msi` series, and ClawTweaks were used only as behavioral or
open-source references when choosing what to measure. No source code, binary, firmware, driver,
artwork, or other third-party expression from those projects is included in this package.

The package source is licensed with WSGM under `GPL-3.0-or-later`. Packaging copies the repository
root `LICENSE` byte-for-byte to the package path `LICENSE.txt`, which is the path declared by
`plugin.wsgm.json`.
