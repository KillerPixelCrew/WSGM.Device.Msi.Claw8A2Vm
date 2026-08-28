# WSGM.Device.Msi.Claw8A2Vm

The first-party MSI Claw 8 AI+ A2VM plugin, board `MS-1T52`. Read
`_plan\claw-8-a2vm-plugin.md` before changing anything here — findings marked `[HW 2026-08-27]` were
captured on the reference unit and supersede any claim inherited from Handheld Companion, HHD, the
Linux `hid-msi` series, or ClawTweaks.

The rules in `plugins\AGENTS.md` apply in full. What follows is specific to this device.

## This package owns the keyboard suppressor

`FirmwareChordSuppressor` — the `WH_KEYBOARD_LL` state machine that neutralizes the firmware's
`Win+G` and `Win+Tab` chords — **lives here, not in `src\WSGM\Input\`.** It is exact-device policy
for one board's firmware behavior, not general WSGM input handling, and `src\WSGM\Input\AGENTS.md`
carries the matching prohibition.

Its discriminator is the **orphan key-up**: the firmware delivers `G`/`Tab` as a key-UP with no
preceding key-DOWN, which a physical keyboard cannot produce. That makes the rule sound rather than
heuristic, so a real keyboard's `Win+G` passes through untouched and nothing is blocked globally.
Consequences that are easy to undo by accident:

- Suppress only an orphan key-up, only for `G` and `Tab`, only while a Windows key is held, and only
  with no other modifier down — `Ctrl+Win+G` must survive.
- Volume keys arrive from the **same** `ACPI\MSNB1001` device as the chords, and they are ordinary
  well-formed presses. A shape-based rule passes them automatically; never add a device-based rule,
  and never disable `i8042prt` or the ACPI keyboard device.
- If a future BIOS emits a well-formed chord, the signature stops matching and the suppressor **fails
  open**. It must never fall back to blocking `Win+G` globally.
- The hook is suppression-only. `KBDLLHOOKSTRUCT` cannot identify the source keyboard, so the logical
  OEM control is published from the MSI WMI event (`0x29` OEM1, `0x58` OEM2 short, `0x2A` OEM2 long),
  never from the hook.
- The callback does the state transition, at most one `SendInput` batch, and a bounded-queue write.
  No pipe, WMI, HID, UI, allocation-heavy work, or synchronous logging, ever.

## Device facts that are easy to get wrong

- **Board and system product are different SMBIOS fields.** `MS-1T52` is `Win32_BaseBoard.Product`;
  `Win32_ComputerSystem.Model` is the marketing string. A matcher reading "SMBIOS product" as Type 1
  never matches this unit. `MS-1T42` (7-inch A2VM) and `MS-1T41` (A1M) need separate descriptors, and
  the A1M's larger power limits must never leak into this one.
- **EC firmware comes from `Get_EC`, not SMBIOS** — `Win32_BIOS.EmbeddedControllerMajor/MinorVersion`
  both return `255`.
- **Container ID is unusable** (null GUID on every relevant device) and the USB serial exists only in
  XInput mode. Key hotplug and mode-switch continuation on `DEVPKEY_Device_LocationPaths`, or parent
  hub plus address — verified byte-identical across a full switch-and-restore cycle.
- **HC has M1 and M2 inverted.** Measured here, DirectInput index 15 is the RIGHT paddle (M2) and 16
  is the LEFT (M1). Copying HC's indices mirrors every user's rear-paddle assignment.
- **RGB writes are persistent across reboot with no `SyncToROM`, and no volatile path exists.**
  Coalescing is mandatory; never write per lighting frame, and never write profile memory on every
  launch.
- MSI WMI requires elevation; `MSI_Event` does not. The OEM event path and the suppressor therefore
  work regardless of the host's privilege.
