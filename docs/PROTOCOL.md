# Network Protocol

Discovery happens before either of the two connection-oriented channels
below exist, and uses its own minimal wire format (see "Discovery" below).

## Discovery (UDP, pre-connection)

Two independent mechanisms, merged by `CompositeDeviceDiscovery`:

- **mDNS/DNS-SD** (primary): Android advertises `_localwebcam._tcp` via
  `NsdManager`; Windows browses for it via the `Zeroconf` library
  (`LocalWebcam.Windows.Discovery.MdnsDeviceDiscovery`). The phone's stable
  `DeviceId` travels in a TXT record (`id`).
- **UDP broadcast fallback** (spec section 6): for networks where multicast
  is filtered but ordinary broadcast/unicast UDP isn't. Implemented entirely
  in platform-neutral `LocalWebcam.Network` (`UdpBroadcastAdvertiser` /
  `UdpBroadcastDiscovery`), well-known port `57621`
  (`DiscoveryConstants.UdpBroadcastPort`):

  ```
  Discover (PC → broadcast):
  ┌──────────┬──────────┬──────────┐
  │ Magic    │ Type=1   │ Version  │
  │ "LWCD"   │ (1 byte) │ (1 byte) │
  └──────────┴──────────┴──────────┘

  Hello (phone → sender, unicast reply):
  ┌──────────┬──────────┬──────────┬─────────┬──────────┬─────────┬──────────┬──────────────┐
  │ Magic    │ Type=2   │ Version  │ IdLen   │ DeviceId │ NameLen │ Name     │ ControlPort  │
  │ "LWCD"   │ (1 byte) │ (1 byte) │ (1 byte)│ (UTF-8)  │ (1 byte)│ (UTF-8)  │ (u16, BE)    │
  └──────────┴──────────┴──────────┴─────────┴──────────┴─────────┴──────────┴──────────────┘
  ```

  This is deliberately separate from the control-channel framing below:
  discovery is connectionless and pre-authentication, so it doesn't carry a
  negotiated protocol version or anything security-sensitive — that's what
  pairing (next) is for. The advertiser binds the well-known port; each
  discoverer sends from an ephemeral port, so Hello replies naturally route
  back without a shared listening port on the browsing side.

## Control channel (TCP)

Used for discovery hand-off, pairing, authentication, camera configuration,
start/stop, heartbeats, adaptive-quality commands, and error reporting.
Structured, infrequent, correctness-over-latency — reliable delivery is
wanted here, unlike video.

### Message framing

```
[Header]                          [Payload]
┌──────────┬──────────┬──────────┬─────────────────┐
│ Version  │ MsgType  │ Length   │ Payload (JSON/   │
│ (1 byte) │ (1 byte) │ (u32 LE) │ binary, by type) │
└──────────┴──────────┴──────────┴─────────────────┘
```

Message types, as implemented in `LocalWebcam.Protocol.Control.ControlMessageType`:

- `Hello` / `HelloAck` — version + capability exchange; `HelloAck.IsTrusted`
  tells the initiator which authentication path comes next
- `PairRequest` / `PairChallenge` / `PairResult` — full ECDH pairing.
  `PairResult` is the single terminal message for the whole authentication
  phase (sent by the phone), whichever path was taken
- `TrustChallenge` / `TrustResponse` — reconnecting an already-trusted device
  via an HMAC challenge/response, skipping the human-visible pairing code
- `ConfigureStream` (resolution, FPS, bitrate, the desktop's UDP video port) / `ConfigureAck`
- `StartStream` / `StopStream`
- `SwitchCamera`
- `Heartbeat` / `HeartbeatAck` — defined, not yet implemented (Phase 5)
- `QualityHint` — defined, not yet implemented (Phase 5 adaptive bitrate)
- `Error` (code + human-readable message) — defined, not yet used
- `Disconnect` — defined, not yet used (the desktop currently just sends `StopStream` and closes the socket)

## Video channel (UDP)

Used only for encoded video frames once streaming has started. See
[VIDEO_TRANSPORT.md](VIDEO_TRANSPORT.md) for the framing layout and drop
policy.

```
[Header]
┌──────────┬──────────┬───────────┬────────────┬───────────┬────────────┐
│ Version  │ FrameId  │ FragIndex │ FragCount  │ Timestamp │ SeqNumber  │
│ (1 byte) │ (u32 LE) │ (u16 LE)  │ (u16 LE)   │ (u64 LE)  │ (u32 LE)   │
└──────────┴──────────┴───────────┴────────────┴───────────┴────────────┘
[Payload: H.264 NAL fragment bytes]
```

## Status

**Everything above is implemented, unit/integration-tested, and verified
end-to-end on real hardware** — a physical Android phone and this Windows
PC, over real Wi-Fi, not just loopback:

- Discovery found the phone via its real mDNS advertisement.
- Full ECDH pairing showed a real pairing prompt on the phone with a code
  matching the one displayed on the desktop.
- Trust storage worked: disconnecting and reconnecting used
  `TrustChallenge`/`TrustResponse` to skip the pairing prompt entirely on
  the second connection.
- The video channel carried the phone's real hardware-encoded H.264 output
  to the desktop's `UdpVideoReceiver` — thousands of frames, 0% packet loss.
- No crashes on either side across the session.

The orchestrating "connect to this device" flow lives in
`PhoneConnectionService` (`LocalWebcam.Mobile.Services`) and
`DesktopConnectionService` (`LocalWebcam.Desktop.Services`) — see
`docs/ARCHITECTURE.md` for why that's in the app projects rather than the
shared libraries.

**Not yet implemented**: `Heartbeat`/`HeartbeatAck`, `QualityHint`, `Error`,
and `Disconnect` are defined but unused — all Phase 5 (reliability/adaptive
quality) concerns. Decoding the received video to pixels is Phase 3.
