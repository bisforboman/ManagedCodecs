# Sound tracks after the first (2026-11-16)

A plan, decided on by the owner (Decided, below); to be built in the pull requests listed there. Issue #535, split
from #469 (docs/decisions/2026-11-15-mxf-colour-and-what-is-left.md).

## What happens today

Every demuxer gives one `IDemuxer.Audio`: the file's first sound track in file order. Its packets are the only sound
packets `ReadPackets` gives; the other tracks are skipped and not described. The players (`MediaPlayer`,
`AudioPlayer`), `VideoProbe`, `VideoThumbnail` and mcdec take `Audio` and filter packets by `StreamIndex`.

| Container | Several sound tracks in practice | What a track says about itself |
|---|---|---|
| Matroska, WebM | Languages, commentary, a stereo and a 5.1 mix | Language (and BCP 47), name, FlagDefault, FlagForced, FlagCommentary and others |
| MP4, MOV | Languages; Apple's alternate groups | `mdhd` language, `tkhd` enabled flag and alternate group, `udta` name |
| AVI | Rare (dubbed films) | `strn` name |
| MXF | Broadcast: one mono track a channel (4, 8 or 16), or stereo pairs | MCA labels (SMPTE ST 377-4), where the writer adds them |
| WAV, ADTS, MP3, raw H.264 | One track at most | |

## What FFmpeg does (6.1, measured 2026-11-16)

Files from FFmpeg's muxers with two AAC tracks (stereo and 5.1, each in turn flagged default) and an MXF file with four
mono PCM tracks:

- **ffprobe lists every track** as a stream, with `channels`, `channel_layout`, `tag:language`, `tag:title` and
  `disposition:default` / `disposition:comment` (MP4's language and Matroska's language, name and flags all come
  through).
- **The `ffmpeg` command line plays the track flagged default**, ahead of the one with more channels: stereo-default
  before 5.1 → the stereo; 5.1 first and stereo-default second → the stereo; both stereo, the second default → the
  second. Without a default flag, the one with more channels. This library takes the first in file order instead, so
  on the second and third files it plays another track than `ffmpeg` and `ffplay` do.
- **MXF's mono tracks:** FFmpeg lists four mono streams and its command line plays the first only. It folds nothing
  by itself; `-filter_complex amerge=inputs=4` does, after decoding.

## Two shapes, not exclusive

1. **A track list and a choice of track**, in every demuxer: `IDemuxer.AudioStreams` lists every sound track's
   `AudioStreamInfo`, and the caller picks the one `Audio` and `ReadPackets` give. New public API (a minor version).
   Checked against FFmpeg as today, a track at a time (`ffprobe -show_packets -select_streams a:N`).
2. **MXF folding mono tracks into one stream**: one multichannel `Audio` whose packets interleave the tracks' samples.
   No new API, but no packets of FFmpeg's to compare with: the decoded sound would be compared with FFmpeg's
   `amerge` output instead. Which tracks to fold, and in what channel order, comes from MCA labels where a file has
   them, else from the track order.

The first covers every container and matches FFmpeg's model; the second covers what a broadcast MXF player needs.
Both can be built, the list first.

## The rest of the design (proposed; questions below where it is not obvious)

- **The list:** `IReadOnlyList<AudioStreamInfo> AudioStreams { get; }` on `IDemuxer`, a default member giving
  `[Audio]` (or empty) so that other implementations keep compiling; this library's demuxers list every sound track
  they can describe, in file order, with `StreamIndex` as FFmpeg numbers streams.
- **Choosing:** a method on each demuxer, `SelectAudio(int streamIndex)`, before reading or after a `Seek`; `Audio`
  then names that track and `ReadPackets` gives its packets. `ReadPackets` keeps giving one sound track's packets, so
  code that doesn't choose sees nothing change but which track is first (decision 2).
- **What a track says:** `AudioStreamInfo` gains `Language` (ISO 639-2 or BCP 47, as the file has it, else null),
  `Title` (null without one) and `IsDefault`, each as ffprobe reports it. Commentary, forced, hearing-impaired and
  the others wait for a use.
- **Players:** `PlaybackOptions.AudioStreamIndex` (null: the demuxer's `Audio`) for `MediaPlayer` and `AudioPlayer`;
  switching while playing later, as a seek to the current time with the new track.
- **Samples:** FFmpeg's muxers write all of this (`-map`, `-metadata:s:a:N language=`, `-disposition:a:N`): MP4,
  Matroska, AVI and MXF files with two or more sound tracks, the default flag on each in turn. Packets per track
  against `ffprobe -show_packets -show_data_hash MD5 -select_streams a:N`, the facts against `-show_entries
  stream_tags=language,title:stream_disposition=default`.
- **Order of pull requests:** the API and Matroska with MP4 first (where languages are), then AVI and MXF, then the
  players, then MXF folding if wanted.

## Decided (the owner, 2026-11-16)

1. **Shape:** the track list and a choice of track, in every demuxer. MXF folding is not planned.
2. **Which track `Audio` is when nobody chooses:** FFmpeg's choice, the default-flagged track, else the one with the
   most channels, else the first. A change a consumer notices on files whose first track isn't the default: a
   CHANGELOG line.
3. **Packets:** `ReadPackets` gives the chosen track's only; `SelectAudio(streamIndex)` changes which.
4. **What a track says:** `Language`, `Title` and `IsDefault`, as ffprobe reports them.

The rest (the default interface member, `PlaybackOptions.AudioStreamIndex`, the samples, the order of pull
requests) as proposed above. New public API throughout, so it ships in a minor version.

## Still to settle while building

- **Most channels as the tie-break:** measured with one track flagged default at a time; a file with none flagged, or
  two, gets its own sample before the rule is written down.
- **MP4's default:** MP4 has no default flag of Matroska's kind (the `tkhd` enabled flag and alternate groups are the
  nearest); what ffprobe reports as `disposition:default` for MP4 is to measure, and followed.
- **MXF:** every sound track gets listed; the default is the first (MXF has no flag), as FFmpeg's command line plays
  it.
