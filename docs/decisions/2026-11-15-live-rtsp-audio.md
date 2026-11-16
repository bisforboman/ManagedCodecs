# Live camera viewing, step 5: audio over RTSP (2026-11-15)

A plan, decided on by the owner (Decided, below). Nothing here is built yet. Steps 2 to 4 (RTSP with H.264, then JPEG,
then RTP over UDP: docs/decisions/2026-11-10-live-rtsp-h264.md, docs/decisions/2026-11-12-live-rtsp-jpeg.md and
docs/decisions/2026-11-14-live-rtsp-udp.md, released up to 0.15.0) play a camera's first video track and never set up
its audio track. Audio is the last numbered point of issue #462 not done ("AAC by RFC 3640").

## What audio changes

- **A second track.** The SDP has an `m=audio` section beside the video's, with a control URL of its own. It gets its
  own SETUP in the same session (`interleaved=2-3` on the RTSP connection, or a second pair of UDP ports), and one
  PLAY starts both.
- **AAC over RTP (RFC 3640)**, `a=rtpmap:97 MPEG4-GENERIC/48000/2`. The `a=fmtp` gives the decoder's
  AudioSpecificConfig as hex (`config=`) and the sizes of the fields in front of the frames (`sizelength=13;
  indexlength=3;indexdeltalength=3` for `mode=AAC-hbr`, 6/2/2 for `AAC-lbr`). A packet starts with a 16-bit count of
  header bits, then one header per frame (its size, and an index that is 0 unless frames are interleaved), then the
  frames. A frame too large for one packet is alone in its packets, and the marker bit ends it.
- **G.711 over RTP (RFC 3551)**: payload types 0 (µ-law) and 8 (A-law), 8 kHz mono, one byte a sample, with no header
  and no `a=rtpmap`. `PcmDecoder` already decodes both.
- **No clock, but sound needs one.** `LivePlayer` shows the newest picture and drops the rest. Sound can't be dropped
  like that: every sample is heard in order. So the audio needs a queue, and a rule for when the caller doesn't take
  it, or takes it slower than the camera's clock sends it.
- **Lip sync.** The two tracks' RTP timestamps have separate origins. Only RTCP sender reports tie them together, and
  the player reads none (step 4, decision 3).

## Measured (2026-11-15)

FFmpeg 6.1.1's RTP muxer (`-f rtp`, its SDP by `-sdp_file`, the packets by tools/rtp-record.py):

| Input | SDP | Packets |
|---|---|---|
| AAC LC, 48 kHz stereo, default bitrate | `MPEG4-GENERIC/48000/2`, `mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3; config=119056E500` | 3 or 4 frames a packet (64 or 85 ms), up to 1380 bytes |
| AAC LC, 16 kHz mono, 32 kbit/s | `MPEG4-GENERIC/16000/1`, `config=140856E500` | 5 frames a packet: 320 ms |
| the same with `-max_delay 0` | the same | 1 frame a packet (64 ms) |
| AAC LC, 5.1, 3000 kbit/s asked | | 1 frame a packet, 847 bytes at most: never a fragment |
| AAC with `-rtpflags latm` | `MP4A-LATM/16000/1`, `profile-level-id=40;cpresent=0;config=400028103fc0` (RFC 6416) | not recorded |
| A-law, µ-law | `m=audio ... RTP/AVP 8` and `0`, no `a=rtpmap` | not recorded |

- The marker bit is set on every packet, and the `config=` has a space before it (`; config=`).
- **FFmpeg's encoder never writes a frame larger than a packet**, so no recording has a fragmented frame.
- **FFmpeg packs several frames into a packet**, so its 16 kHz stream arrives a third of a second at a time. From
  memory, cameras send one frame a packet. No camera was in reach.
- From memory, not measured: most cameras send G.711 unless set to AAC (ONVIF's Profile S requires G.711 and leaves AAC
  optional), and some send G.726. If the survey's users leave the camera at its defaults, AAC alone plays nothing.

## Proposal

1. **Which audio** (decision 1): the first audio track in the SDP that can play, as for video.
   - AAC as `MPEG4-GENERIC` with `mode=AAC-hbr` or `AAC-lbr` and a `config=`, decoded by `AacDecoder` (AAC LC and
     HE-AAC, as from a file).
   - G.711 as payload type 0 or 8, or an `a=rtpmap` naming `PCMU/8000` or `PCMA/8000`.
   - Anything else (LATM, G.726, Opus, interleaved AAC) is not played, and `LivePlayer` says which
     (`SkippedAudioCodec`, as `MediaPlayer` has).
2. **Asked for, not automatic** (decision 2): `RtspSource.Audio` (init, false by default). Without it nothing changes:
   no second SETUP, no decoding, no queue.
3. **The session**: with `Audio` set and a playable track, a second SETUP after the video's, on the transport the
   video's got (the RTSP connection, or UDP after a 461). Over UDP the audio has its own port pair and its own SSRC
   filter and reorder window.
4. **Audio never stops the picture**: if the audio's SETUP is refused, or its `config=` can't be read, the video plays
   on without it and `Error` says why. Only video packets feed the stall watchdog, so a camera sending sound but no
   picture still reconnects.
5. **Taking it**: `LivePlayer.TakeAudio()` returns the next `AudioChunk` (float stereo at the decoder's rate, as
   `MediaPlayer.TakeAudio()` does), or null. `LivePlayer.Audio` describes the stream once connected (codec, rate,
   channels), or is null.
6. **The queue and latency** (decision 3): decoded chunks wait in a queue bounded by time. When it is full the oldest
   chunk is dropped and counted. So a caller that never takes the audio costs only the decoding, and one whose sound
   card runs slower than the camera's clock loses a chunk now and then, not ever more latency.
7. **Lip sync** (decision 3): none in this step. Each picture is shown as soon as it is decoded and each sound is
   handed over as soon as it is decoded. On a LAN the two are within the decoding times of each other, plus whatever
   the caller's sound output buffers.
8. **Loss**: a lost packet's frames are missing, and the sound goes on with the next one (the caller's output
   underruns for that long, or plays on with less latency). No silence is inserted. A fragmented frame missing a packet
   is dropped. After a gap the AAC decoder's next frame overlaps with the frame before the gap, which can click.
9. **Decoding** runs on a thread of its own, so a slow H.264 picture doesn't hold the sound back.
10. **Statistics**: `LostPackets` counts both tracks. `LiveStatistics` gains `DecodedAudioFrames`,
    `DroppedAudioFrames` (the queue was full) and `DamagedAudioFrames` (rejected by the decoder, or a fragment
    missing).
11. **Not in step 5**:
    - A stream with audio only (the player still needs a video track).
    - LATM (`MP4A-LATM`, RFC 6416), G.726, Opus, MP3 over RTP, and RFC 3640's interleaving.
    - Lip sync by RTCP sender reports.
    - Sound in `mclive` and `mclive-wpf`: neither Avalonia nor WPF plays raw samples without a native library or a
      platform API. They stay silent. A MonoGame live sample could play it (`DynamicSoundEffectInstance`), later.
    - Talking back to the camera (ONVIF's backchannel).

## Checks

- **`RtpAacDepacketizerTests`** against FFmpeg's packetizer: make-samples.sh records `-c copy -f rtp` of an AAC sample
  (several frames a packet) and again with `-max_delay 0` (one a packet). The frames that come out are the MP4's
  packets, byte for byte, and the SDP's `config=` is the MP4's AudioSpecificConfig.
- **Hand-made packets** for what FFmpeg never sends: a frame fragmented over packets, a fragment lost, `AAC-lbr`'s
  field sizes, a header that runs past the packet, a non-zero index (interleaving, refused).
- **G.711**: FFmpeg's recording of an A-law and a µ-law stream, decoded, equals FFmpeg's decoding of the same.
- **Lockstep through `LivePlayer`**: `RtspTestServer` serves a video and an audio recording as two tracks, over the
  connection and over UDP. The chunks taken are the decoder's output for the frames in order, and the pictures are
  unchanged by the audio.
- **Rules**: no audio SETUP without `Audio`, the audio's SETUP refused leaves the video playing, a full queue drops
  the oldest, video stalling with audio flowing reconnects, an SDP with only an audio track fails as now.
- **Mutation entries** for the rules no recording reaches: the fragment rule, the queue's bound, the watchdog
  ignoring audio.
- **Nightly**: tools/rtsp-nightly.py gains a clip with AAC sound through mediamtx, and `mcdec --live` compares the
  audio taken with the clip's decoded audio.
- **By hand**: tools/gst-rtsp-server.py gains an audio branch (`rtpmp4gpay`, one frame a packet, and `rtppcmapay`).

## Decided

Asked with the multiple-choice prompt; the owner took each recommendation (2026-11-15).

1. **Which audio. Decided: (a), AAC and G.711.**
   - **(a) AAC and G.711** (recommended). G.711 is what most cameras send at their defaults, its packets have no
     format to parse, and `PcmDecoder` already decodes it: a few dozen lines beyond AAC.
   - (b) AAC only, as the issue says. A camera at its defaults then stays silent, and `SkippedAudioCodec` says why.
   - (c) (a) plus LATM (RFC 6416), which some older cameras send in place of RFC 3640. It needs a StreamMuxConfig
     parser, and nothing in reach sends it but FFmpeg.
2. **How audio is turned on. Decided: (a), `RtspSource.Audio`, off by default.**
   - **(a) `RtspSource.Audio`, off by default** (recommended). A wall of cameras pays nothing, and an application
     that played video before 0.16 gets exactly the session it had.
   - (b) On whenever the SDP has a playable audio track, dropped if never taken. No new member on `RtspSource`, but
     every viewer decodes sound it doesn't play, and a camera that mishandles a second SETUP changes for everyone.
3. **Latency and lip sync. Decided: (a), no sync and a queue of half a second.**
   - **(a) No sync, a queue of half a second** (recommended). Both tracks come out as soon as they are decoded. Simple,
     and the lowest latency for each.
   - (b) The same with the bound a public setting (`RtspSource.MaxAudioDelay`). More API for a number few will change.
   - (c) Sync by RTCP sender reports: the picture is held back to the sound's time. It is what a player does, but it
     adds latency to the picture, which a live view exists to avoid, and needs RTCP parsing and a clock.

## Since

- Built (2026-11-15):
  - `RtpAudioDepacketizer` and the SDP's audio track (internal, Playback).
  - `RtspSource.Audio`, `LivePlayer.TakeAudio`, `Audio` and `SkippedAudioCodec`, and the three audio counts of
    `LiveStatistics`.
  - `RtspTestServer` with a second track. The checks are in docs/conformance.md, Audio over RTSP.
  - The nightly run gained an H.264 clip with AAC sound through mediamtx, over the connection and over UDP
    (`mcdec --live --audio`). mediamtx passes FFmpeg's `MPEG4-GENERIC` on as it is.
- Differs from the plan:
  - `LostPackets` counts the audio's lost packets too, and `DamagedAudioFrames` its dropped fragments, as planned.
    Bad packets were added to it: one whose AU headers don't fit its payload.
  - The marker bit's rule for a fragment (the last one carries it) turned out redundant with the size check and the
    next unit's timestamp. It is kept, without a mutation entry.
  - tools/gst-rtsp-server.py has no audio branch yet: GStreamer wasn't installed where this was built, and an untried
    pipeline isn't worth committing. `rtpmp4gpay` (one access unit a packet) and `rtppcmapay` remain to try by hand.
- Tried (2026-11-15, issue #527): GStreamer 1.24's `rtpmp4gpay`, also fragmenting every access unit (`mtu=200`),
  `rtppcmapay` and `rtppcmupay`.
  - Recorded with what went into the packetizer (tools/gst-audio-record.py, committed in samples/gstreamer/): the
    depacketizer gives the encoder's access units and bytes back as they were. Nothing had to change in it.
  - Live: tools/gst-rtsp-server.py has the audio branch (`AUDIO=aac|aac-fragmented|alaw|mulaw|latm`), and `mcplay`
    played each beside the H.264 pattern; LATM is named and skipped. docs/conformance.md, Audio over RTSP.
  - Unlike FFmpeg's packetizer (64 to 320 ms of AAC a packet), GStreamer's sends each frame as it is encoded.

- AAC as LATM (2026-11-16, issue #527): decision 1's option (c), left out then, built after all from #527's list.
  `MP4A-LATM` with `cpresent=0` plays: the SDP's StreamMuxConfig is read for the AudioSpecificConfig (`LatmConfig`,
  internal: one program, one layer, AAC LC, frames of a length given in each element, as cameras, FFmpeg and GStreamer
  send), and each audioMuxElement, whole in a packet or over packets up to the marker bit, is taken apart into its
  access units. No public API. Checked against FFmpeg's packetizer and GStreamer's by hand (docs/conformance.md,
  Audio over RTSP).

New public API (`RtspSource.Audio`, `LivePlayer.TakeAudio`, `Audio` and `SkippedAudioCodec`, the statistics), so the
next minor version, with a CHANGELOG line under "Unreleased" when it is built.
