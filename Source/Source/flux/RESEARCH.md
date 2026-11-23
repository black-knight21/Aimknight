# Kaiden Flux 0.3 research and verification

## Sensitivity

cm/360 = 360 x 2.54 / (DPI x game sensitivity x effective driver multiplier x yaw). Inches = centimeters / 2.54. Reference degrees/count at sensitivity 1: Valorant 0.06996, Source/CS2/Apex 0.022, Overwatch 0.0066. Sources inspected: https://kovaaks.com/kovaaks/sens-converter and https://github.com/KovaaK/SensitivityMatcher/blob/master/ReleaseAssets/bin/SensitivityMatcher.au3 . These are community reference scales, not physical tests on the user's game build. Custom yaw is supported; no ADS formula is claimed verified. Native curves supply accelerated multipliers; one cm/360 cannot describe every speed.

## Requested community projects

Both repositories exist; their source and protocol documentation were inspected:

- https://github.com/akawazak/attack-shark-x11-community-driver : Electron/Vue Windows community implementation. Full LICENSE retrieved: MIT, copyright (c) 2026 HarukaYamamoto0.
- https://github.com/clevim/OpenSharkX11 : Rust/Tauri, Linux USB/BLE transports. Full LICENSE retrieved: MIT, copyright (c) 2026 Clevs. Reviewed docs/protocol/PROTOCOL_EN.md, src-tauri/src/driver/mod.rs and src-tauri/src/driver/attack_shark.rs.

Full consulted LICENSE texts are included in Research-Licenses/. No upstream implementation code was copied; the C# adapter is separate. Community behavior does not prove compatibility with every Windows firmware/transport.

Documented family: VID 1D57, PID FA55 wired / FA60 receiver, interface 2. The passive battery pattern starts 03 55 40 and contains percentage at byte 4. Only this inbound pattern with a 0..100 value is accepted. Reports 04-06 describe configuration, and 0B can unpair a receiver: Flux sends none. No active battery query, feature report, USB output report, firmware command or configuration write is implemented.

## Actual Windows observations, 2026-10-29

Read-only SetupAPI/HID enumeration found 14 matching collections: X11 Mouse (1D57:FA55, descriptor 0107) and 2.4G Wireless Device (1D57:FA60, descriptor 1111). Interface 2 exposes usage page 000A / usage 0000 with five-byte input reports. Generic keyboard/mouse/consumer collections are excluded from passive reading. An attached receiver does not prove the wireless mouse is online. Descriptor versions are not verified protocol revisions.

A bounded three-second passive input read timed out without a recognized battery packet. Battery/charging and active DPI/polling readback therefore remain unavailable/unverified on real hardware. No device configuration command was sent. The descriptor listing is included as WINDOWS-HID-OBSERVATIONS.txt without USB instance paths.

The adapter uses SetupDi enumeration, zero-access HID descriptor handles and a separate read-only overlapped status-input handle. Cancellation disposes the stream. Disconnect/errors clear old values; late results are rejected if the device disappeared. IMouseDevice isolates transport for mocked tests. Unsupported writes throw before device access.

Firmware compatibility, exact DPI/stage commands and readback, polling configuration/readback, charging, lighting, button maps and onboard settings remain unverified and unavailable. The signed acceleration driver is unchanged and separate. Future verified DPI writes must synchronize successful readback with normalization, calculator/context and proposal invalidation, without silently changing curves/game sensitivity. No unverified combined transaction exists here.
