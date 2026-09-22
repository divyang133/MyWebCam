# Windows Virtual Camera

## Requirement being solved

Section 16 of the spec is explicit: the virtual camera must be a real OS-level
device visible to other applications (OBS, Teams, Zoom, browsers) via Windows
Settings → Cameras, not a fake device only visible inside this app.

## Chosen approach: Media Foundation `IMFVirtualCamera` (Frame Server)

Windows 11 version 22H2 introduced `MFCreateVirtualCamera`, which lets a
**user-mode** application register a virtual camera backed by the Windows
Frame Server, without shipping and signing a kernel-mode driver. This is a
genuine platform capability, not a workaround:

1. **Why it's required**: there is no managed .NET API for registering a
   system-visible camera device; this is inherently an OS/driver-model
   concern. Confirmed via research before implementing (not assumed from
   memory): the media source backing an `IMFVirtualCamera` device is loaded
   **natively, in-process, inside the Windows Frame Server service itself**
   (a system service, not this app's process) — there is no way to keep the
   actual frame-serving code inside `LocalWebcam.Desktop`'s own process.
2. **What API it exposes**: `mfvirtualcamera.h` / `IMFVirtualCamera` COM
   interfaces (`MFCreateVirtualCamera`, `Start`, `Stop`, `Remove`), used from
   `LocalWebcam.Desktop` via `Vortice.MediaFoundation` (already a dependency
   for Phase 3 decoding) to register/control the device. Separately, a
   Media Foundation custom media source (`IMFMediaSource`/`IMFMediaStream`)
   is what the Frame Server actually loads and calls into when a consumer
   (OBS, Teams, ...) opens the camera.
3. **How C# communicates with it**: two different mechanisms for two
   different pieces —
   - `LocalWebcam.Desktop` calls `MFCreateVirtualCamera` etc. directly via
     Vortice.MediaFoundation, in-process, same as any other MF call.
   - The media source itself is a **separate COM server** —
     `LocalWebcam.VirtualCamera.Windows`, a class library with
     `<EnableComHosting>true</EnableComHosting>` (the standard, NuGet-free
     .NET 5+ mechanism for exposing a managed class as a classic COM server
     loadable by an arbitrary native process) — built against
     [DirectN](https://github.com/smourier/DirectN)/`DirectNCore`, an
     actively maintained library that (unlike Vortice, which only
     *consumes* MF COM objects) supports *exposing* a C# class as a COM
     server implementing MF interfaces. Modeled on
     [smourier/VCamNetSample](https://github.com/smourier/VCamNetSample),
     the closest working reference for this exact scenario. Live decoded
     frames reach this out-of-process media source over a dedicated IPC
     channel (named pipe) from `LocalWebcam.Desktop` — there is no shared
     memory space to pass a frame pointer through.
4. **How it is built**: `net10.0-windows10.0.22621.0` specifically (not the
   rest of the solution's `10.0.19041.0` floor) — `MFCreateVirtualCamera`/
   `IMFVirtualCamera` are only declared from the Windows 11 22H2 SDK
   contract onward. This is the actual reason the whole product has a
   Windows 11 22H2+ floor, not an arbitrary choice.
5. **How it is packaged**: the `.comhost.dll` build output ships alongside
   `LocalWebcam.Desktop`; no separate driver package, kernel component, or
   signing is required (this is the point of Frame Server virtual cameras
   vs. the legacy DirectShow-filter/kernel-driver approach).
6. **How it is installed**: the COM server **must** be registered via
   `regsvr32 LocalWebcam.VirtualCamera.Windows.comhost.dll`, which **requires
   administrator elevation** — this is a real, unavoidable one-time setup
   step (confirmed via research, not something later phases can remove),
   because the Frame Server service loads it from `HKLM`, not per-user
   `HKCU` registration. This will be a documented manual step for MVP;
   wrapping it in a proper installer (with an elevation prompt) is Phase 6
   polish, not Phase 4 scope.

## Platform limitation (must not be hidden)

`MFCreateVirtualCamera` requires **Windows 11 22H2 or later**. There is no
fallback to a signed kernel driver in this project's MVP scope — on
unsupported Windows versions, the app must show a clear, actionable error
(section 27) rather than silently failing or pretending to work.

Registering the COM server also requires **administrator rights** (see point
6 above) — this is disclosed to the user before any elevated action is
taken, not performed silently.

## Frame format

NV12 is the target format handed to the virtual camera's media source,
matching the Media Foundation decoder output and avoiding an extra color
conversion. BGRA is the fallback if a downstream consumer requires it and
Media Foundation's video processor (`MFVideoProcessor`/`IMFTransform`) is used
for the conversion rather than a hand-rolled one.

## Status

Implemented and verified as far as possible without administrator access to
this machine:

- `LocalWebcam.VirtualCamera.Windows` (`Activator`, `MediaSource`,
  `MediaStream`, `SharedFrameSource`) implements the out-of-process COM media
  source per the design above, modeled on smourier/VCamNetSample. Builds
  clean (0 errors) targeting `net10.0-windows10.0.22621.0`.
- `LocalWebcam.Windows.Video.WindowsVirtualCamera` implements `IVirtualCamera`:
  calls `MFCreateVirtualCamera`/`Start`/`Stop`/`Remove` via
  Vortice.MediaFoundation, and is the named-pipe **server** side of
  `VirtualCameraFrameChannel` (`LocalWebcam.Shared.VirtualCamera`) — it holds
  only the single latest decoded frame and feeds it to whichever media-source
  instance is currently connected, matching the project's drop-stale-data
  policy. `LocalWebcam.Desktop` wires this in: registers the virtual camera
  device on launch and pushes every decoded frame to it
  (`DesktopConnectionService`).
- **Verified on hardware**: launching `LocalWebcam.Desktop` calls
  `MFCreateVirtualCamera` successfully (the device object is created — no
  exception) and logs `Virtual camera 'Local Webcam' registered
  (sourceId={f813c6d7-55ee-4237-b958-ce4205212874})`. Calling `Start()` on it
  then fails with `HRESULT 0x80040154` (`REGDB_E_CLASSNOTREG`, "Class not
  registered") — exactly the expected failure mode when the Activator's CLSID
  has not yet been registered via `regsvr32`, confirming the wiring between
  `WindowsVirtualCamera` and the COM server is correct and this is the *only*
  remaining gap.
- **Blocked step**: registering
  `LocalWebcam.VirtualCamera.Windows.comhost.dll` via an elevated
  `regsvr32`. This requires HKLM write access (see above); the coding
  environment's sandbox refused the elevated `regsvr32` invocation
  ("Unauthorized Persistence") even though this was pre-authorized for this
  session — it is a hard tool-level denial, not something to work around.
  **A human with admin rights must run this one step once**, via
  `scripts\Register-VirtualCamera.ps1` (finds the most recently built
  `.comhost.dll` automatically, self-elevates with a UAC prompt, and calls
  `regsvr32` — see that script's header comment for details;
  `scripts\Unregister-VirtualCamera.ps1` undoes it). After that, no further
  elevation is needed for normal use — only for re-registering after the
  DLL changes location (e.g. moving from a Debug to a Release build).
- **Acceptance test passed on real hardware (2026-09-18)**: after running
  `scripts\Register-VirtualCamera.ps1`, `MFCreateVirtualCamera` → `Start()`
  succeeded cleanly, `DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)`
  listed "Local Webcam (Windows Virtual Camera)" as a real capture device,
  and — the real test — opening it in the **Windows Camera app** (an
  independent, standard system app, not code from this project) showed the
  phone's actual live video feed, matching the same scene visible in
  `LocalWebcam.Desktop`'s own preview.
- **Real bug found and fixed during that test**: Windows creates a
  **separate media-source activation per consumer**, not one shared
  instance — `LocalWebcam.Desktop`'s own preview and the Camera app each
  got their own `SharedFrameSource` needing its own named-pipe connection
  (three separate activations were observed even before any external app
  opened the device, likely from OS-level enumeration/thumbnail probing).
  `WindowsVirtualCamera`'s pipe server originally served **one client at a
  time** (`maxNumberOfServerInstances: 1`), so every activation after the
  first sat forever on `SharedFrameSource`'s gray "no frame yet" placeholder
  — a real, reproducible bug, not a hypothetical. Fixed by serving all
  connected clients concurrently (`NamedPipeServerStream.MaxAllowedServerInstances`,
  one independent `WriteLoopAsync` per client) and replacing the
  single-waiter `SemaphoreSlim` frame signal (which only wakes *one* waiter
  per release) with a broadcast pattern — a swapped `TaskCompletionSource`
  per frame, so every connected client's write loop wakes on every frame.
