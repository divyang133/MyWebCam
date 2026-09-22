# Architecture

Local Webcam turns an Android phone into a Windows webcam over the LAN only,
with no cloud dependency. This document is the Phase 0 deliverable: technology
choices, the pipeline shape, and known risks, before any implementation.

## Pipeline

```
Android phone                              Windows PC
──────────────                             ──────────
Camera2 / CameraX
      │
      ▼
MediaCodec (H.264 hardware encoder)
      │
      ▼
LocalWebcam.Protocol (framing)  ──UDP/RTP──▶  LocalWebcam.Network (receive)
                                                     │
                                                     ▼
                                     LocalWebcam.Windows (Media Foundation
                                     H.264 hardware/software decoder)
                                                     │
                                                     ▼
                                     LocalWebcam.VirtualCamera.Windows
                                     (IMFVirtualCamera / Frame Server)
                                                     │
                                                     ▼
                                     OBS / Teams / Zoom / Discord / browsers
```

A parallel **control channel** (TCP, JSON-ish structured messages — see
[PROTOCOL.md](PROTOCOL.md)) carries discovery, pairing, negotiation,
heartbeats, and adaptive-quality commands. Video never rides the control
channel and control messages never ride the video channel.

## Layering

```
UI (WinUI 3 / MAUI, MVVM)
      │
Application Services (ICameraService, IConnectionManager, ...)
      │
Domain (LocalWebcam.Shared models, LocalWebcam.Protocol messages)
      │
Infrastructure (LocalWebcam.Network, LocalWebcam.Video, LocalWebcam.Security,
                platform implementations: Android / Windows / VirtualCamera.Windows)
```

UI code never touches sockets, encoders, or COM directly — it depends only on
interfaces defined in `LocalWebcam.Video`, `LocalWebcam.Network`, and
`LocalWebcam.Security`, injected via DI. Platform-specific implementations
(`LocalWebcam.Android`, `LocalWebcam.Windows`, `LocalWebcam.VirtualCamera.Windows`)
are the only assemblies allowed to call native/platform APIs.

Core abstractions (see section 2/25 of the spec):

```
IDeviceDiscovery      – find phones on the LAN (mDNS + UDP fallback)
IDevicePairing        – pairing-code exchange, trust storage
ICameraController     – phone-side camera control (switch, zoom, torch, ...)
IVideoEncoder          – phone-side H.264 hardware encoding
IVideoTransport        – framed send/receive over UDP
IVideoDecoder          – PC-side H.264 hardware/software decoding
IVirtualCamera         – PC-side OS virtual camera exposure
```

## Technology choices

| Concern | Choice | Why |
|---|---|---|
| Mobile UI | .NET MAUI | C# end-to-end, Android first, iOS reuses shared layers later (Phase 7) |
| Mobile camera/encode | Android Camera2/CameraX + MediaCodec via C# Android bindings | Hardware H.264 encode; pure-managed encoding isn't fast enough for 1080p30 |
| Desktop UI | WinUI 3 (Windows App SDK) | Modern native Windows UI, no Electron, first-class C#/XAML/MVVM |
| Desktop decode | Media Foundation via COM interop | Hardware-accelerated H.264 decode on Windows |
| Virtual camera | Media Foundation `IMFVirtualCamera` (Windows 11 22H2+ Frame Server) | User-mode registration, no signed kernel driver required — see [WINDOWS_VIRTUAL_CAMERA.md](WINDOWS_VIRTUAL_CAMERA.md) |
| Video transport | Custom framing over UDP | Lowest latency on LAN with control over buffering/drop behavior — see [VIDEO_TRANSPORT.md](VIDEO_TRANSPORT.md) |
| Control transport | TCP | Reliability matters more than latency for pairing/config/heartbeats |
| Discovery | mDNS/DNS-SD + UDP broadcast fallback | No manual IP entry; works even without a DNS-capable router |
| Frame format | NV12 | Native `MediaCodec` surface output and Media Foundation's preferred decode output; avoids YUV→RGB→YUV round-trips |
| Pairing | Numeric code + ECDH-derived shared secret | LAN-appropriate mutual authentication without a CA/cloud identity provider |
| Secure storage | Windows DPAPI / Android Keystore | Never store trust secrets in plaintext |

## Repository layout

See section 30 of the project spec; implemented as:

```
/src
    LocalWebcam.Shared                  – common models, config, logging, errors
    LocalWebcam.Protocol                – message types, binary framing, serialization
    LocalWebcam.Network                 – discovery, pairing, connection management, transport
    LocalWebcam.Video                   – IVideoEncoder/IVideoDecoder/IVirtualCamera abstractions
    LocalWebcam.Security                – crypto, trust store abstractions
    LocalWebcam.Diagnostics             – metrics/diagnostics models
    LocalWebcam.Mobile                  – MAUI app (UI/ViewModels), Android-first
    LocalWebcam.Android                 – Android platform implementations (camera, encoder, keystore)
    LocalWebcam.Desktop                 – WinUI 3 app (UI/ViewModels)
    LocalWebcam.Windows                 – Windows platform implementations (decoder, DPAPI)
    LocalWebcam.VirtualCamera.Windows   – isolated IMFVirtualCamera COM interop
/tests
    LocalWebcam.Protocol.Tests
    LocalWebcam.Network.Tests
    LocalWebcam.Video.Tests
/docs
```

## Pairing & trust (implemented in Phase 2)

`PairingSession` (`LocalWebcam.Security`) wraps one ECDH P-256 key exchange:
each side generates an ephemeral key pair, exchanges public keys over the
(still-untrusted) control channel, and derives a shared secret via
`ECDiffieHellman.DeriveKeyMaterial`. Two things are then derived from that
secret with `HKDF.DeriveKey` (RFC 5869, domain-separated by an `info` label
so the two derivations can't be confused with each other):

- A 6-digit **verification code**, identical on both sides for a genuine
  exchange — shown on both screens (spec section 7) for the user to visually
  compare. An active MITM ends up with a *different* shared secret with each
  side, so the two codes mismatch; this is what makes the comparison
  meaningful rather than theatrical. Unit-tested including a simulated MITM
  (`PairingSessionTests.MitmAttempt_ProducesMismatchedVerificationCodes`).
- A 32-byte **trust key**, stored per `DeviceId` for future reconnection
  without repeating the human-visible step. The phone issues an HMAC
  challenge over the stored trust key on reconnect
  (`TrustChallenge`/`TrustResponse`); verified end-to-end on real hardware —
  see the Status section below.

Trust storage (`ITrustedDeviceStore`) never touches plaintext:

- **Windows** (`WindowsTrustedDeviceStore`): a single JSON document encrypted
  with DPAPI (`ProtectedData`, `CurrentUser` scope) under
  `%LOCALAPPDATA%\LocalWebcam`. Verified with a real DPAPI round-trip
  (encrypt, restart with a fresh instance, decrypt, confirm the on-disk bytes
  aren't plaintext).
- **Android** (`AndroidTrustedDeviceStore`): AES-256-GCM using a
  non-exportable key generated directly in the Android Keystore
  (`KeyGenerator`/`Cipher` APIs). Deliberately **not**
  `androidx.security.crypto`'s `EncryptedSharedPreferences`/`MasterKey` —
  the .NET binding flags that wrapper obsolete on current Android, so this
  goes straight to the (non-deprecated) Keystore primitives instead of
  building on a wrapper the platform itself is moving away from.

## Risks

1. **`IMFVirtualCamera` COM interop** is the highest-risk, least-forgiving
   component: no managed wrapper exists in the BCL, and behavior varies across
   Windows 11 builds. Must be prototyped in isolation before the rest of the
   pipeline depends on it.
2. **Android hardware encoder access from C#** — CameraX's ergonomic APIs are
   Kotlin/Java-first; the C# bindings layer for `MediaCodec` + `Surface` input
   needs early validation on real hardware across a couple of Android
   versions/vendors.
3. **WinUI 3 / Windows App SDK version churn** — Windows App SDK ships
   frequently; pin an exact stable version (currently 2.5.1) and revisit
   deliberately rather than floating on `*`.
4. **Windows version floor** — `MFCreateVirtualCamera` requires Windows 11
   22H2+. Windows 10 users cannot get a real OS-level virtual camera without a
   signed kernel driver, which is out of scope for the MVP. This must be
   surfaced clearly to users, not silently degraded.

## Windows H.264 decoding (Phase 3)

`WindowsVideoDecoder` (`LocalWebcam.Windows.Video`) wraps the Media
Foundation H.264 decoder MFT, found via `MFTEnumEx` against
`TransformCategoryGuids.VideoDecoder` (rather than a hardcoded CLSID, so a
hardware-accelerated decoder is picked up automatically when present) using
[Vortice.MediaFoundation](https://www.nuget.org/packages/Vortice.MediaFoundation)
— an actively maintained managed binding over Media Foundation's COM
interfaces; no official managed MF wrapper ships in the BCL, and hand-rolling
`ComImport` declarations for the dozens of MF interfaces involved
(`IMFTransform`, `IMFMediaType`, `IMFSample`, `IMFMediaBuffer`, ...) would
have been its own large, error-prone project. Output stays NV12 (spec
section 17); converting to BGRA32 for on-screen preview
(`Nv12ToBgraConverter`) is a display-only step, not part of the core
pipeline that will eventually feed the virtual camera (Phase 4).

For rendering, decoded frames go through `SoftwareBitmap` +
`SoftwareBitmapSource` rather than `WriteableBitmap` +
`IBufferByteAccess`/unsafe pointer writes — the latter is a commonly-cited
pattern online but **crashed the app outright** (`0xc000027b` in
`CoreMessagingXP.dll`) during on-device testing; `SoftwareBitmap` is the
safer, fully-managed, Microsoft-documented path for writing raw pixel
buffers into a WinUI Image control and involves no unsafe code.

## Status

**Phases 0–3 complete and verified end-to-end on real hardware** (a physical
OnePlus 7 Pro over real Wi-Fi, this Windows PC as the desktop side — not just
loopback/synthetic tests):

- Phase 0: solution scaffolding.
- Phase 1: Android camera capture + hardware H.264 encoding — see `docs/ANDROID.md`.
- Phase 2: LAN discovery (mDNS, found the phone by its real advertised
  `_localwebcam._tcp` service), full ECDH pairing (phone showed a pairing
  prompt with a code identical to the one the desktop displayed), trust
  storage, and UDP video streaming (desktop received frames at the phone's
  real encoder output — thousands of frames, 0% packet loss, live FPS
  matching the phone's encoder). Disconnecting and reconnecting correctly
  used the stored trust key to skip the pairing prompt entirely on the
  second connection.
- Phase 3: the received H.264 stream is decoded via Media Foundation and
  rendered live in the WinUI desktop app — an actual picture from the
  phone's camera, not just a frame counter. Verified with the phone's front
  camera (the rear camera on the test device was physically covered).
  Includes a real "Switch Camera" control end-to-end (desktop button →
  `SwitchCamera` control message → phone's `ICameraService.SwitchCameraAsync`).

No crashes on either side across the whole session (aside from the
WriteableBitmap approach above, caught and fixed before considering Phase 3
done).

The orchestration layer for Phases 2-3 lives outside the shared libraries,
in each app's own `Services/` folder — `PhoneConnectionService`
(`LocalWebcam.Mobile`) and `DesktopConnectionService`
(`LocalWebcam.Desktop`) — since the responder and initiator state machines
differ enough that a shared abstraction wasn't worth it, and both need
access to app-layer concerns (DI-registered `ICameraService`,
`ITrustedDeviceStore`, discovery/control/video-transport types) that don't
belong in the platform-neutral libraries.

**Known limitation, not yet fixed (expected — it's Phase 5 scope)**: a lost
UDP packet drops its whole frame (by design, see `docs/VIDEO_TRANSPORT.md`),
but with no loss recovery yet, that gap in the H.264 bitstream corrupts
decode until the next keyframe (visible as speckle/block artifacts in dark
regions during testing at ~4% packet loss). Adaptive bitrate and
keyframe-on-loss requests are spec section 12 / Phase 5 territory.

**Phase 4 (Windows virtual camera): COMPLETE, acceptance test passed on
real hardware (2026-09-18).** After the user ran
`scripts\Register-VirtualCamera.ps1` (the one elevated step this
environment's sandbox can't perform itself), `MFCreateVirtualCamera` →
`Start()` succeeded, "Local Webcam (Windows Virtual Camera)" appeared as a
real capture device, and — the actual acceptance test — the **Windows
Camera app** (an independent system app) showed the phone's live video
feed through it, matching `LocalWebcam.Desktop`'s own preview exactly.

**Real bug found and fixed during that test**: Windows creates a separate
media-source activation per consumer (not one shared instance) — three
activations were observed even before any external app opened the device,
and `LocalWebcam.Desktop`'s own preview vs. the Camera app each got their
own. `WindowsVirtualCamera`'s named-pipe server originally served only one
client at a time, so every activation after the first sat forever on the
placeholder gray frame. Fixed by serving all connected clients
concurrently and broadcasting each new frame to every one of them (a
single-waiter `SemaphoreSlim` can't do that — only one waiter wakes per
release — replaced with a swapped-`TaskCompletionSource` broadcast
pattern). See `docs/WINDOWS_VIRTUAL_CAMERA.md` for details.

**Phase 5 (reliability): COMPLETE, verified on real hardware (2026-09-18).**
`DesktopConnectionService` sends a `Heartbeat` every 5s once streaming and
treats 16s without a `HeartbeatAck` as a dropped connection; an unexpected
drop after a session reached `Paired` triggers automatic reconnection with
exponential backoff (capped at 30s, reset after each successful
reconnect); observed UDP packet loss above 5% sends a `QualityHint` asking
the phone to step its encoder bitrate down (with hysteresis at 1% to
recover) — **observed live during hardware testing**: packet loss crossed
5.5% and the log shows the desktop correctly requesting the lower bitrate.
`PhoneConnectionService`/`AndroidVideoEncoder` apply this live via
`MediaCodec`'s `PARAMETER_KEY_VIDEO_BITRATE` (no stop/reconfigure/start
needed — resolution stays fixed, matching the virtual camera's fixed NV12
media type). The phone's foreground-service notification
(`StreamingForegroundService`/`ForegroundServiceController`) is implemented
per docs/ANDROID.md. Builds clean, all 51 unit/integration tests pass.

**Two more real bugs found and fixed during hardware testing, both
verified fixed on real hardware afterward**:

- The on-screen preview logged frequent `TaskCanceledException` under
  load (packet loss + multiple virtual camera consumers): the UI thread
  could dispatch a new `SoftwareBitmapSource.SetBitmapAsync` call before
  the previous one finished, and that API isn't safe to call concurrently
  with itself. Fixed in `MainViewModel.OnFrameDecoded` by dropping a
  decoded frame instead of racing a new preview update against one still
  in flight — streaming and the virtual camera feed were never affected,
  only the preview. Confirmed clean (no exceptions) across a full
  subsequent reconnect cycle.
- `TcpControlConnection.ConnectAsync`/`FromAcceptedClient` started the
  receive loop *before* returning the connection to the caller, leaving a
  window where a message arriving before the caller subscribed to
  `MessageReceived` would fire with no subscribers and be silently
  dropped. On real hardware, under heavy concurrent load, this
  reproducibly hung a reconnect attempt indefinitely (TCP handshake
  completes on both sides, then nothing — no Hello/HelloAck, no error).
  Fixed by adding `IControlConnection.StartReceiving()`, called only after
  both sides subscribe to `MessageReceived`/`Disconnected`. (A separate,
  unrelated WireGuard VPN connecting mid-session independently caused the
  *same symptom* for a while during this investigation — worth remembering
  that "TCP connects, then silence" can be either cause; check
  `Get-NetAdapter` for an unexpected VPN adapter before assuming it's this
  code.)

After both fixes, the full reconnection cycle was cleanly verified twice
end to end on real hardware: heartbeat-timeout detection → backoff retry →
successful reconnect → trust-based re-authentication (no pairing prompt) →
streaming resumed, with 0% packet loss and a clean preview immediately
after.

**Not yet done**: Phase 6 polish beyond what's already done (log export,
README, troubleshooting docs — see below); iOS (Phase 7, blocked in this
environment — no macOS/Xcode, no physical iPhone).
