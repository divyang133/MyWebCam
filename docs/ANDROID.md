# Android Camera & Encoding

## Requirement

Section 13 of the spec: rear/front camera, switching, orientation, zoom,
exposure/focus where supported, torch, hardware H.264 encoding, 720p/1080p at
30 FPS, foreground-service streaming with a persistent notification.

## Approach

- Camera access via **Camera2** (through .NET for Android bindings), chosen
  over CameraX for now because the encoder pipeline needs direct control over
  the capture `Surface` handed to `MediaCodec` — CameraX's higher-level
  session abstractions are convenient for preview-only apps but add an extra
  layer between capture and the encoder input surface that this project
  doesn't need. CameraX can be reconsidered if it turns out to simplify
  lifecycle handling meaningfully without giving up encoder-surface control.
- Hardware encoding via **`MediaCodec`** configured for H.264
  (`video/avc`), fed by a `Surface` input (camera writes directly to the
  encoder's input surface — no CPU-side YUV copy), per spec section 10's
  "don't unnecessarily convert YUV → RGB → YUV" requirement.
- Encoder output (Annex-B NAL units) is exposed as `EncodedVideoFrame` events
  from `IVideoEncoder`/`ICameraService` (`LocalWebcam.Video`). No networking
  exists yet in Phase 1: the frames are written to a local file instead (see
  Acceptance test below), and `LocalWebcam.Protocol`/`LocalWebcam.Network`
  pick them up starting Phase 2.
- Foreground service: a `Service` with `foregroundServiceType="camera"`
  (Android 14+ requires this), showing a persistent notification while
  streaming, per section 13 and Android's background-camera-access rules.
  **Deferred past Phase 1** — the app only streams in the foreground for now,
  so this isn't required yet; do not skip it silently once background
  streaming (Phase 5) is in scope.

## Permissions

- `CAMERA`, plus `android.hardware.camera` as a required `<uses-feature>`.
- `INTERNET` / `ACCESS_NETWORK_STATE` — required by Android for any socket
  I/O, including LAN-only traffic; declared now even though Phase 1 does no
  networking, since Phase 2 needs them and the manifest is otherwise final.
- `FOREGROUND_SERVICE` / `FOREGROUND_SERVICE_CAMERA` / `POST_NOTIFICATIONS` —
  declared for the Phase 5 foreground-service notification; unused until then.

## Implementation (Phase 1)

- `LocalWebcam.Video`: `ICameraController`, `IVideoEncoder`, `ICameraService`
  (+ `CameraService`, the platform-agnostic orchestrator) and supporting
  models (`CameraDescriptor`, `EncodedVideoFrame`, `StreamingState`).
- `LocalWebcam.Android`: `AndroidCameraController` (Camera2) and
  `AndroidVideoEncoder` (`MediaCodec`, Surface-input, drains encoded output on
  a dedicated background thread).
- `LocalWebcam.Mobile`: `Controls/CameraPreviewView` + its Android
  `TextureView`-backed handler for the live preview; `ViewModels/MainViewModel`
  (CommunityToolkit.Mvvm) driving Start/Stop/Switch; DI wiring in
  `MauiProgram.cs`.
- `Platforms/Android/FrameFileRecorder.cs` in `LocalWebcam.Mobile`: writes the
  raw encoded stream to the app's external files directory. This is Phase 1's
  acceptance-test harness only — it is removed once Phase 2 sends frames over
  the network instead of to disk.

### Known platform gotcha: `TextureView` cannot have a background

Android's `TextureView` throws `UnsupportedOperationException: TextureView
doesn't support displaying a background drawable` if anything sets a
background on it — including MAUI's default `BackgroundColor` mapping, which
crashed the app on first on-device run. Fixed by giving the parent `Grid` the
background instead and overriding `CameraPreviewViewHandler.Mapper` to no-op
`Background`/`BackgroundColor`, so a future XAML change can't reintroduce the
crash. This is a hard platform limitation, not a workaround to revisit later.

### Acceptance test (manual, on-device) — PASSED 2026-09-17

Run against a physical OnePlus 7 Pro (Android 12, API 31):

1. `dotnet build src/LocalWebcam.Mobile/LocalWebcam.Mobile.csproj -f net10.0-android -t:Install` then launch via `adb shell am start` (or deploy via Visual Studio / `-t:Run` from a machine set up for interactive `adb` device authorization).
   Note: a plain `adb install` of the built APK is **not** sufficient — .NET for
   Android's debug builds use Fast Deployment, where the managed assemblies are
   synced separately by the build tooling. Installing the raw APK produces a
   "No assemblies found... Assuming this is part of Fast Deployment" crash on
   launch. Always deploy via `-t:Install`/`-t:Run` (or Visual Studio) for debug
   builds.
2. Granted the camera permission ("While using the app") when prompted after tapping **Start Streaming**. ✅
3. Live preview rendered correctly; status showed `Streaming`, resolution
   `1280x720 @ 30 FPS`, encoder FPS steady at 29.8–30.3. ✅
4. **Switch Camera** flipped between the front and rear sensors (confirmed by
   the drastically different scene — one camera saw the ceiling, the other
   went solid black against the table the phone was resting on) without the
   status leaving `Streaming` or the app restarting. ✅
5. **Stop Streaming** returned status to `Idle` cleanly; no crashes in
   `logcat` (`AndroidRuntime`/`FATAL`/`SIGABRT`) across the whole session. ✅
6. Pulled the captured elementary stream from
   `/storage/emulated/0/Android/data/com.companyname.localwebcam.mobile/files/capture.h264`
   and validated its Annex-B NAL structure directly (no `ffmpeg` available in
   this environment): 1 SPS, 1 PPS, 18 IDR keyframes (matching the
   1-second keyframe interval) and 517 non-IDR slices over ~18 seconds — a
   structurally valid, correctly configured H.264 stream at the target
   frame rate. ✅

## Status

**Phase 1 complete and verified on real hardware.** `CameraService`
orchestration is covered by unit tests (`LocalWebcam.Video.Tests`); the full
camera → hardware encoder → preview pipeline, camera switching, and
start/stop lifecycle were exercised end-to-end on a physical device (see
Acceptance test above). Not yet covered: 1080p, torch/zoom/exposure
adjustment (all deferred per the notes above).

**Foreground-service notification (Phase 5): implemented, build-verified
only.** `StreamingForegroundService` (a plain `Service` with
`foregroundServiceType="camera"`, generated into the manifest from its
`[Service(...)]` attribute) is started/stopped by `ForegroundServiceController`
in lockstep with `ICameraService`'s own `Streaming`/non-`Streaming` state
(`MainViewModel.OnStateChanged`) — not tied to the remote connection's
state, since the thing that needs protecting from Android's background
kill/throttle policy is the camera itself, regardless of why it's running.
Builds clean; **not yet exercised on the device** (e.g. confirming the
notification appears, and that streaming survives the screen turning off) —
the test phone was unreachable (airplane mode, screen re-locking
near-instantly) for the rest of this session; see docs/ARCHITECTURE.md for
current overall phase status.
