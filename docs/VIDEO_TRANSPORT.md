# Video Transport

## Decision

Video frames are sent over **UDP** using a custom, minimal binary framing
(sequence number, timestamp, frame flags, fragment index — see
[PROTOCOL.md](PROTOCOL.md)). The control channel (discovery, pairing,
negotiation, heartbeats) is a separate **TCP** connection.

## Alternatives considered

| Option | Verdict | Reason |
|---|---|---|
| **WebRTC** | Rejected for v1 | Correct latency characteristics, but requires either a native libwebrtc dependency with thin/incomplete C# bindings, or a large managed reimplementation. ICE/STUN/TURN/SDP negotiation solves NAT traversal and multi-network problems we don't have on a single LAN. Revisit only if browser-side *sending* (not just virtual-camera *receiving*) is ever required. |
| **QUIC** (`System.Net.Quic`) | Rejected for v1 | Built into .NET, but still requires an application framing layer on top, is less battle-tested for real-time media specifically (it's stream/reliability oriented), and platform support (msquic) on Android is not yet a smooth story. Revisit once QUIC-on-Android is solid. |
| **TCP** | Rejected for video | Reliable delivery causes head-of-line blocking: one lost packet stalls every frame behind it, which is exactly the unbounded-latency-growth behavior the spec forbids (section 9/11). Used for the control channel instead, where correctness matters more than latency. |
| **Raw UDP + custom framing** | **Chosen** | Smallest moving part that gives full control over sequencing, timestamps, and drop policy. No reliability layer to fight; loss is handled by design (drop stale frames, request a keyframe) rather than masked. |

## Requirements this must satisfy (spec section 9)

- Low latency, minimal buffering.
- Good behavior on Wi-Fi (i.e., tolerate packet loss without stalling).
- Sequence numbers and timestamps on every packet.
- Reconnection without restarting either app.
- **Frame dropping, not queuing**, when the receiver falls behind — old frames
  are discarded rather than displayed late.

## Design sketch

- Each encoded H.264 frame is split into one or more UDP datagrams sized under
  the path MTU (target ~1400 bytes payload to avoid IP fragmentation).
- A frame header carries: sequence number (per-packet), frame ID, fragment
  index/count, capture timestamp, and an `IsKeyFrame` flag.
- The receiver reassembles fragments belonging to one frame ID; if any
  fragment of a stale frame is still missing when a newer frame's first
  fragment arrives, the incomplete frame is dropped rather than waited on.
- A small jitter buffer (bounded, target ~2-3 frames) absorbs Wi-Fi jitter
  without becoming unbounded latency.
- Packet loss is measured on the receiver (gaps in sequence numbers) and fed
  into the adaptive-quality policy (section 12) over the control channel.
- Loss of a keyframe triggers a control-channel request for the encoder to
  emit a new one, rather than waiting for the next scheduled keyframe
  interval.

## Status

**Implemented and tested** (`LocalWebcam.Protocol.Video.VideoPacketCodec`
for the wire format; `LocalWebcam.Network.Video.UdpVideoSender`/
`UdpVideoReceiver` for the transport). Loopback tests
(`UdpVideoTransportTests`) cover: multi-fragment reassembly over a real UDP
socket, in-order multi-frame delivery, and — the test that actually matters
for the "don't buffer" requirement — sending a deliberately incomplete frame
(one fragment of two) followed by a newer complete frame, and confirming the
incomplete one is dropped and never delivered rather than held onto. Packet
loss is exposed as `PacketLossRatio` (min/max sequence number range vs.
packets actually received); not yet consumed by anything (that's the
adaptive-quality policy, spec section 12, deferred to Phase 5).

**Verified end-to-end on real hardware since**: `PhoneConnectionService`
wires `ICameraService.FrameEncoded` straight into a `UdpVideoSender`
targeting the desktop's negotiated video port (sent over the control channel
in `ConfigureStreamMessage.VideoPort`), and `DesktopConnectionService` feeds
`UdpVideoReceiver.FrameReceived` into `WindowsVideoDecoder` (Phase 3) for a
live preview. Over real Wi-Fi (not loopback): thousands of frames delivered,
live FPS on the receiving end matching the phone's encoder output (~30 FPS).
The synthetic-data loopback tests above still exist and still pass — they're
what to run for a quick regression check without needing a phone attached.

At ~4% observed packet loss, the "drop the whole frame" policy is visible as
decode corruption (speckle artifacts, self-healing at the next keyframe)
rather than a clean, error-concealed picture — expected given there's no
loss recovery yet (keyframe-on-loss requests, adaptive bitrate; spec section
12, Phase 5). See `docs/ARCHITECTURE.md`'s Phase 3 section.
