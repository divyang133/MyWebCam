# Local Webcam

Turn an Android phone into a Windows webcam over your own LAN — no cloud,
no account, no data leaving your network. Similar in spirit to Iriun
Webcam, built from scratch in C#/.NET.

The phone captures and hardware-encodes H.264, streams it to a Windows
desktop app over the local network, and the desktop app decodes it and
feeds a real, OS-level virtual camera device that any app (OBS, Teams,
Zoom, browsers) can select like a normal webcam.

## Status

Phases 0–4 (Android capture, LAN discovery/pairing, video transport,
decoding, and the Windows virtual camera) are implemented and verified on
real hardware. Phase 5 (reliability: heartbeat, automatic reconnection,
adaptive bitrate, foreground-service notification) is implemented and
build-verified. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the
detailed, per-phase status and what's still outstanding, and
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) for known issues.

iOS is not implemented — it needs macOS/Xcode and a physical iPhone, which
this project hasn't had access to.

## Requirements

- **Windows 11, version 22H2 or later**, on the desktop machine — required
  by `MFCreateVirtualCamera` (see
  [docs/WINDOWS_VIRTUAL_CAMERA.md](docs/WINDOWS_VIRTUAL_CAMERA.md) for why
  this is a hard platform floor, not an arbitrary choice).
- An Android phone (tested on Android 12 / API 31) on the **same LAN** as
  the desktop, with Wi-Fi (not just mobile data) and not in airplane mode.
- .NET 10 SDK, with the Android and Windows (WinUI/Windows App SDK)
  workloads installed, to build from source.

## Building

```powershell
dotnet build LocalWebcam.slnx
```

This builds every project in the solution, including both apps and all
platform-specific libraries. To build and run just one side:

```powershell
# Desktop (Windows)
dotnet build src\LocalWebcam.Desktop\LocalWebcam.Desktop.csproj
src\LocalWebcam.Desktop\bin\Debug\net10.0-windows10.0.22621.0\win-x64\LocalWebcam.Desktop.exe

# Mobile (Android) — with a device connected via adb
dotnet build src\LocalWebcam.Mobile\LocalWebcam.Mobile.csproj -f net10.0-android -t:Install
```

Run the tests with:

```powershell
dotnet test LocalWebcam.slnx
```

## One-time setup: registering the virtual camera

The desktop app's virtual camera device is backed by a COM server that
Windows must register once, with administrator rights, before it will show
up in OBS/Teams/Settings → Cameras:

```powershell
.\scripts\Register-VirtualCamera.ps1
```

This finds the built `LocalWebcam.VirtualCamera.Windows.comhost.dll`
automatically and elevates itself (a UAC prompt) to run `regsvr32` on it —
see the script's header comment and
[docs/WINDOWS_VIRTUAL_CAMERA.md](docs/WINDOWS_VIRTUAL_CAMERA.md) for why
this step exists and can't be avoided. `scripts\Unregister-VirtualCamera.ps1`
undoes it.

## Using it

1. Run `LocalWebcam.Desktop` on the Windows machine.
2. Open the Local Webcam app on the phone — it starts advertising itself
   automatically and appears in the desktop app's device list.
3. Click the device in the desktop app to connect. On first connection,
   compare the pairing code shown on both devices and tap Allow on the
   phone; later reconnections skip this automatically.
4. Once connected, the phone's camera feed appears in the desktop app's
   preview and is fed into the "Local Webcam" virtual camera device —
   select it as the camera in OBS, Teams, Zoom, or any other app.

## Project layout

- `src/LocalWebcam.Shared`, `LocalWebcam.Video`, `LocalWebcam.Network`,
  `LocalWebcam.Protocol`, `LocalWebcam.Security`, `LocalWebcam.Diagnostics`
  — platform-agnostic libraries shared by both apps.
- `src/LocalWebcam.Android`, `LocalWebcam.Windows`,
  `LocalWebcam.VirtualCamera.Windows` — platform-specific implementations
  (Camera2/MediaCodec, Media Foundation, the virtual camera's COM media
  source).
- `src/LocalWebcam.Mobile` — the .NET MAUI Android app.
- `src/LocalWebcam.Desktop` — the WinUI 3 Windows app.
- `tests/` — unit and integration tests for the shared libraries.
- `docs/` — architecture, protocol, and phase-by-phase status documents;
  start with [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- `scripts/` — the virtual camera's one-time registration scripts.
