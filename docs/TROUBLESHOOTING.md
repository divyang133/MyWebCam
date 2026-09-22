# Troubleshooting

This document grows alongside the implementation phases. Placeholder sections
below will be filled in as each phase produces real, observed failure modes
(not speculative ones) — see spec section 27 (never fail silently, always give
actionable errors) and section 24 (diagnostic log export).

## Windows

- *Virtual camera not visible in Settings → Cameras, or OBS does not list
  "Local Webcam"* — most likely the one-time COM registration step hasn't
  been run yet. Run `scripts\Register-VirtualCamera.ps1` (elevates itself),
  then restart `LocalWebcam.Desktop`. Confirmed on real hardware: without
  registration, `LocalWebcam.Desktop`'s log
  (`%LOCALAPPDATA%\LocalWebcam\desktop.log`, or the app's "Open Log Folder"
  button) shows `MFCreateVirtualCamera` succeeding but `Start()` failing
  with `HRESULT 0x80040154` (`REGDB_E_CLASSNOTREG`) — that exact error is
  the signature of this specific problem.
- *"Virtual camera installation required" / feature unavailable* — expected
  on Windows versions older than 11 22H2, see
  [WINDOWS_VIRTUAL_CAMERA.md](WINDOWS_VIRTUAL_CAMERA.md); there is no
  fallback in this project's scope.

## Android

- *Connecting over USB, but Desktop never finds the phone* — USB tethering
  must be turned on manually on the phone first; a plain USB cable alone
  only gives Windows MTP/ADB access, not a network link. Confirmed on real
  hardware: Windows shows the phone's "Remote NDIS based Internet Sharing
  Device" (its USB-tethering network adapter) only once tethering is
  toggled on - before that, `Get-NetAdapter` shows no adapter for the phone
  at all, so LocalWebcam has nothing to discover it through. This app can't
  turn tethering on itself:
  `ConnectivityManager.startTethering()` requires the signature-level
  `TETHER_PRIVILEGED` permission, which only system/priv-app packages can
  hold - not achievable for a normally-installed app. The Mobile app's "Open
  Tethering Settings" button deep-links to the phone's tethering settings
  screen to save hunting through menus, but still needs a manual toggle.
  **Not yet verified on real hardware** (written without an Android
  toolchain available this session) - confirm the deep-link actually
  resolves on your device before relying on it; if it does nothing, go to
  Settings → Network & internet → Hotspot & tethering → USB tethering by
  hand instead.
- *Camera permission denied* — the app requests it on first launch; if
  denied, grant Camera manually in Android Settings → Apps → Local Webcam →
  Permissions, per the in-app error message.
- *Hardware encoder unavailable on this device* — verified working on a
  OnePlus 7 Pro (Android 12, API 31); other devices not yet tested.
- *Phone unreachable from adb (airplane mode / screen won't stay unlocked)*
  — encountered during Phase 5 testing: the test device went into airplane
  mode with the screen re-locking within ~1-2s of any wake attempt, and
  `adb shell settings put` failed with
  `SecurityException: ... requires android.permission.WRITE_SECURE_SETTINGS`
  (this device doesn't grant the shell user that permission). There is no
  reliable way to force airplane mode off or keep the screen awake from adb
  alone on such a device — someone needs to unlock the phone and disable
  airplane mode by hand before LAN discovery/pairing/streaming can work.

## Networking

- *Phone not discovered on Windows* — check both devices are on the same
  subnet/VLAN, that mDNS multicast isn't blocked by router/AP-isolation
  settings, and (see above) that the phone isn't in airplane mode.
- *Pairing rejected or times out* — the phone's user must tap Allow within
  the pairing prompt's lifetime; a Reject or timeout surfaces via
  `PairResultMessage.Reason` on both sides.
- *Stream freezes after Wi-Fi drop* — `LocalWebcam.Desktop` should show
  "Reconnecting" and recover automatically once Wi-Fi returns (Phase 5's
  heartbeat/reconnect logic — see docs/ARCHITECTURE.md); if it doesn't,
  check the desktop log for heartbeat-timeout messages and whether the
  phone's `PhoneConnectionService` is still advertising.
- *Reconnect attempts hang forever - TCP connects (both sides log it) but
  nothing happens after that, no error* — check for an active VPN on the
  desktop machine first (`Get-NetAdapter` in PowerShell). A VPN connecting
  mid-session was observed to reproduce exactly this symptom by disrupting
  routing to the phone's LAN address; disconnecting it fixed it
  immediately. This is separate from (and looks identical to) a real bug
  that was also found and fixed the same way it presents — see
  `IControlConnection.StartReceiving()` in docs/ARCHITECTURE.md.

## Status

Phases 0-4 fully verified on real hardware (see docs/ARCHITECTURE.md for
exact status per phase). Phase 5 (reliability) and the virtual camera's OBS
acceptance test are implemented and build-verified but not yet
hardware-verified, both blocked by the same session's test-phone
unavailability and the sandboxed environment's inability to run an elevated
`regsvr32` — see docs/ARCHITECTURE.md and docs/WINDOWS_VIRTUAL_CAMERA.md.
