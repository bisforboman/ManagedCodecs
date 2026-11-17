# Live cameras: a stream with sound only (2026-11-16)

Issue #527 ("A stream with audio only"), left out of step 5 (docs/decisions/2026-11-15-live-rtsp-audio.md, 11). Decided
by the owner and built in the same pull request.

## The gap

`LivePlayer` over RTSP set up the SDP's first H.264 or JPEG video track and, with `RtspSource.Audio`, its first AAC or
G.711 audio track beside it. An SDP with no video track ended the player ("no H.264 or JPEG video track"), whatever
sound it had: a microphone, an intercom, a radio or an audio-only restream (mediamtx serves those) couldn't be played,
though the player decodes their sound.

The video drove the player: its packets made it `Playing`, and only they counted against `StallTimeout` (sound alone
doesn't keep a stalled picture's connection, `ConnectsAgainWhenTheVideoStallsThoughAudioStillComes`).

## What was built

- **The sound as the session's one track.** With `Audio` on and no video track, the sound's SETUP is the first: RTP
  on channels 0 and 1 of the RTSP connection, or over UDP after a 461, and refused as for a video over RTSPS. Its
  decoder is made before the SETUP. Everything after (PLAY, keep-alives, the RTCP receiver reports, the reorder window
  over UDP, capture times from its sender reports) is the video's path with the sound in the video's place.
- **The sound drives the player.** Its packets make it `Playing`, and they are what `StallTimeout` watches. A
  connection that played sound resets the wait between attempts, as one that showed a frame does.
- **What would only turn the sound off ends the player.** Beside a video, interleaved AAC access units or a decoder
  that refuses the SDP's config leave the picture playing without sound. With sound only, there is nothing left to
  play, and a retry gets the same SDP: final (`Error`).
- **An SDP with neither** fails as before, naming the audio format when there is one that doesn't play
  (`opus`).

## Decided

Asked with the multiple-choice prompt (2026-11-16); the owner took each recommendation.

1. **Sound only without `Audio`. Decided: (a), fail, naming `Audio`.**
   - **(a) Fail with an error that says the stream has sound only and `RtspSource.Audio` is off** (recommended):
     nothing plays that the application didn't ask for.
   - (b) Play the sound anyway, as if `Audio` were on: sound comes unasked through `TakeAudio`, which an application
     that never calls it drops.
2. **How an application tells there is no picture. Decided: (a), no new API.**
   - **(a) None** (recommended): `State` is `Playing`, `LivePlayer.Audio` is set and `TakeLatest()` stays null; the
     documentation says so.
   - (b) A `LivePlayer.HasVideo` (`bool?`, null until the SDP is read): explicit, but new public API.

## Checks

- `LiveRtspAudioOnlyTests`, against the loopback RTSP server with FFmpeg's audio recordings as the only track:
  - the chunks are the decoder's output for the access units in order (AAC LC, HE-AAC, A-law; over the connection
    and over UDP);
  - one SETUP, on channels 0 and 1 or after a 461 over UDP;
  - `TakeLatest()` null and no video frame counted;
  - without `Audio`, a final error naming it and no SETUP;
  - Opus, or a config the decoder refuses, final with no SETUP;
  - interleaved access units final;
  - the stall timeout counts from the sound's last packet, over the connection and over UDP.
- Mutation entries (tools/mutation/mutations.py) for the three rules no recording reaches otherwise: the refusal
  without `Audio`, `Playing` on the sound's packets, and interleaved AAC being final.
- By hand against mediamtx 1.21.3 (tools/rtsp-nightly.py's pinned build), with FFmpeg 6.1.1 publishing only the AAC
  of aac-stereo-48k.mp4 (`-vn -c:a copy`, looped, `-re`): mediamtx's SDP has the one MPEG-4 Audio track, and the
  player was `Playing` with `Audio` AAC/48000 and no frame. In 5 seconds it took 4.84 seconds of sound over the
  connection and 5.06 over UDP (mediamtx with `rtspTransports: [udp]`; its log: "reading ... with UDP, 1 track"), with
  no packet lost or frame damaged. Without `Audio` it failed with the error naming it. The nightly check doesn't run
  this: `mcdec --live` checks frames, of which there are none.
