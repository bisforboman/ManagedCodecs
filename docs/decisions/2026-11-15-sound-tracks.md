# Sound tracks after the first (2026-11-15)

Issue #535, split from #469 (2026-11-15-mxf-colour-and-what-is-left.md). Every demuxer gave one `Audio`, the file's
first sound track, where FFmpeg gives a stream a track. It matters most for MXF: broadcast files usually carry a mono
track a channel (4, 8 or 16), so a player got channel 1 alone, on both speakers. MP4, Matroska and AVI have the same
limit for files with several languages or a commentary.

## What the code said

- `MxfHeader` already read every track; `MxfDemuxer` listed the packets of two of them when opening (one walk of the
  file), so listing every sound track's costs no further read, and a selection is a filter over that list.
- `Mp4Demuxer`, `MatroskaDemuxer` and `AviDemuxer` each hold one video and one audio table and interleave the two:
  several tracks at once means generalising that in each, a pull request apiece.
- Callers tell packets apart by `Audio.StreamIndex`, but nothing says a packet that is not the video's is `Audio`'s:
  a demuxer that suddenly gave every track would break such callers.
- `AudioChunk` is stereo: "more channels give their first two". Two mono tracks as left and right is the same rule,
  read across tracks.
- FFmpeg's packets, a stream a track, are an oracle for a list of tracks. For MXF's mono tracks folded into one
  multichannel stream inside the demuxer there is none: FFmpeg never gives such packets.

## Decided (the owner, 2026-11-15)

1. **Shape: a list of tracks in every demuxer, and the players fold on top.** `IDemuxer.AudioTracks` lists the sound
   tracks, `IDemuxer.SelectAudio` chooses which `ReadPackets` gives, and `Audio` is the first one chosen (the file's
   first until then). The demuxers' packets stay FFmpeg's. (Not taken: the list alone, which leaves MXF playing one
   channel; `MxfDemuxer` folding its mono tracks into one stream, which needs no new API but has no oracle and does
   nothing for the other containers.)
2. **Several tracks in one pass, when asked for.** `SelectAudio(params int[] streamIndexes)`: the video and the named
   tracks, interleaved in file order. Unasked, the first track alone, as before, so no caller sees packets it did not
   expect. (Not taken: one track at a time, which makes the players open the file twice to fold; every track always, as
   FFmpeg's demuxers, a break.)
3. **The players:** `PlaybackOptions.AudioTrack`, an index into `AudioTracks` (0, the first, as ffplay plays). Where
   the chosen track is mono and the next is mono of the same codec and rate (MXF's layout), the two play as left and
   right, by default. `Audio` is the chosen track. Such MXF files then sound different: stereo, where channel 1 was
   on both speakers (a CHANGELOG "Changed" line).
4. **Order:** MXF first (this record, the interface and `MxfDemuxer`), then the players, then MP4, Matroska and AVI,
   a pull request each. Until a demuxer has its own, the interface's defaults answer for it: its one audio stream is
   the list.

## The interface

```csharp
public interface IDemuxer
{
    AudioStreamInfo? Audio { get; }                       // the first selected track
    IReadOnlyList<AudioStreamInfo> AudioTracks { get; }   // default: Audio alone, or none
    void SelectAudio(params int[] streamIndexes);         // default: Audio's index only
}
```

- Both are default interface members, as `Seek` and `ReadPacketsAsync` are: an `IDemuxer` written elsewhere still
  compiles and lists its one stream. New public API, so the next version is a minor one; nothing breaks.
- `SelectAudio` takes effect at the next read started, as `Seek` does; a read under way keeps its tracks. It refuses
  an empty list or a track named twice (`ArgumentException`) and an index that is no audio stream's
  (`ArgumentOutOfRangeException`), and then changes nothing.
- A track's packets (their `Sequence`, times and data) are the same whichever others are read with it.
- `Seek` takes any listed track's index and moves every track, selected or not, so a choice made after it starts
  there.
- Stream indexes are FFmpeg's: the track's place among the file's streams.

## MXF (the first pull request)

- `MxfDemuxer.AudioTracks`: the material package's sound tracks with essence in the file. A track whose descriptor
  gives no rate, channels or sample size is left out (before, such a first track meant no sound at all); of two tracks
  naming the same essence elements the first is read (no file shows it).
- `Refresh` gives the tracks selected when it is called.
- **Sample:** `mxf-h264-mono4` (make-samples.sh): H.264 and four mono 24-bit tracks, each a sine of its own frequency.
  `mxf-prores-pcm16` already had two stereo tracks.
- **Checks:** `MxfConformanceTests` compares every sample's packets with FFmpeg's (`.packets.csv`, every stream) for
  the first track alone, each track alone, all of them and all named backwards; the list against ffprobe's streams
  (`.headers.json`); selection, its refusals and seeking by hand; `MxfFollowTests` a growing file with two of four
  tracks selected; the interface's defaults on a one-stream demuxer.

## The players (the second pull request)

- `PlaybackOptions.AudioTrack` (an `int?`) counts the demuxer's `AudioTracks` from 0; null, the default, plays the
  demuxer's own `Audio`, so a demuxer that picks its default track as FFmpeg does (MP4's and Matroska's, to come)
  plays that one. It was an `int` defaulting to 0 when #554 merged and became nullable before 0.18.0 shipped it (the
  owner, 2026-11-16), so the later default rule breaks nothing. A file with tracks but not that many is
  refused when opened (`ArgumentOutOfRangeException`); a file with no audio plays silent (`MediaPlayer`) or is refused
  as before (`AudioPlayer`), whatever the option says. `Audio` and `SkippedAudioCodec` speak of the chosen track.
- **Folding is MXF's alone** (`demuxer is MxfDemuxer`): in MP4 or Matroska two mono tracks are two languages as likely
  as two channels, so once those demuxers list their tracks a mono one still plays on both speakers. The pair is the
  chosen track and the one after it in the list, both mono, of one codec and rate: tracks 1 and 2 unasked, 3 and 4
  for `AudioTrack = 2`; `AudioTrack = 1` pairs 2 with 3, and the last track plays on both.
- The two tracks' samples are paired as they are decoded, whatever their packets' sizes and order; what one has past
  the other's end plays beside silence. `Audio` is the left track; the right one is not exposed.
- **Not built, to decide if it is missed:** a way to turn the folding off (channel 1 alone on both speakers, as
  0.17 played such files), and more than two channels out of a player (`AudioChunk` is stereo). The owner
  (2026-11-17): no switch for now. Nobody has missed one, and an option once shipped can't be taken back without a
  break; it waits for a request.
- **Checks:** mxf-h264-mono4 through `AudioPlayer` and `MediaPlayer` against FFmpeg's `join` of the two tracks
  (name.pair01.f32, name.pair23.f32), sample for sample, for each track as the left one, and looped; the pairing on
  hand-made packets; mxf-prores-pcm16's second stereo track; the refusals.

## Later (the owner, 2026-11-16)

Closing #548, another session's plan for the same issue, the owner kept two of its answers on top of this API:

- **Which track `Audio` is when nobody chooses:** the one FFmpeg's command line plays: of the tracks flagged default
  (MP4's 'tkhd' track_enabled, Matroska's FlagDefault), or of them all where none is, the one with the most channels,
  the first of equals. MXF has no flag and keeps its first track.
- **`AudioStreamInfo.Language`, `Title` and `IsDefault`,** as ffprobe reports them.

The #469 session builds both, with `Mp4Demuxer`'s and `MatroskaDemuxer`'s lists (its branch
`audio-tracks-mkv-mp4-535`, moved onto this API).

## AVI (the third pull request)

- `AviDemuxer.AudioTracks`: every audio stream whose 'strf' is a WAVEFORMATEX, in the file's order (before, the first
  such stream). Each keeps its own layout (a packet a chunk, pieces timed by bytes, or CBR MP3 re-split), its own
  count and, after a seek, its own held packet; `Seek` takes any of their indexes.
- AVI has no default flag, so `Audio` stayed the first stream, as in MXF; the next pull request (below) made it the
  one with the most channels, as FFmpeg's command line plays it.
- **Sample:** `avi-mjpeg-three-audio` (MP3 stereo, 16-bit PCM mono, AAC), and make-samples.sh's `.packets.framemd5`
  now maps every video and audio stream (`-map 0:v? -map 0:a?`), for each stream's extradata; no other sample has
  several of a kind, so no other reference changes.
- **Checks:** `AviConformanceTests` compares each stream read alone with ffprobe's packets and extradata (every AVI
  sample), all three in one pass in ffprobe's file order, the refusals, and a seek by one stream before choosing
  others; `AudioPlayerTests` plays each stream by `PlaybackOptions.AudioTrack`.

## MP4, Matroska, and AVI's default (the fourth pull request)

`Mp4Demuxer`'s and `MatroskaDemuxer`'s lists, and the two answers above (Later), built:

5. **Which track `Audio` is until the caller chooses: the one FFmpeg's command line plays.** Measured with FFmpeg 6.1
   on Matroska and MP4 files with a stereo and a 5.1 AAC track in each order and every combination of default flags:
   of the tracks marked default (or of them all, where none is), the one with the most channels, the first of
   equals. ffplay plays the same. MXF marks none and its tracks are alike, so it still plays its first. AVI marks none
   either, so its stream with the most channels plays (the first of equals), as FFmpeg's command line plays it. The
   players follow: `PlaybackOptions.AudioTrack` became `int?` before 0.18.0 shipped it (#558), null playing the
   demuxer's `Audio`.
6. **`AudioStreamInfo.Language`, `Title` and `IsDefault`**, each as ffprobe reports it (its `language` and `title`
   tags and `disposition:default`).

What the files say, as FFmpeg reads it (measured by patching values into samples and reading ffprobe):

- **Matroska:** FlagDefault, set where the element is missing (RFC 9559's default; FFmpeg's muxer writes 0 on every
  track not marked default, a lone track included); Language, "eng" where missing (the default), and "und" reported
  as no language; Name as the title. LanguageBCP47 isn't read (FFmpeg 6.1 doesn't).
- **MP4:** the `tkhd` track_enabled flag is the default disposition (FFmpeg's muxer sets it on the tracks marked
  default, and on the first of a kind where none is). The `mdhd` language: three packed letters ("und" reported as it
  is, the top bit ignored), 0x7FFF none, and below 0x400 a Macintosh language code, which QuickTime files use (FFmpeg's
  `.mov` muxer writes 0 for eng, 11 for jpn): FFmpeg turns those into ISO 639-2 by a table, read here code by code from
  ffprobe (`Mp4Demuxer.MacintoshLanguages`: 0 to 138, 34, 58 and 95 to 127 unnamed). MP4 gives no title (FFmpeg's
  muxer writes none).
- An audio track whose content encoding Matroska can't read is left out of the list while another can be read (before,
  such a first track refused the file).

**Samples:** mp4-audio-tracks (stereo eng, then 5.1 swe marked default: the second plays), mov-audio-tracks (Macintosh
language codes, neither marked: the muxer enables the first), mkv-audio-tracks (stereo marked default, eng, "Main",
before 5.1 swe "Commentary": the first plays though it has fewer channels) and mkv-audio-nodefault (neither marked: the
5.1 plays), and avi-audio-channels (AVI: a mono stream before a stereo one, so the second plays). References: name.audiotracks.json (ffprobe's tracks, channels, tags and disposition) and name.audioplayed
(the stream FFmpeg's command line maps), for every MP4, QuickTime, Matroska and AVI sample.

**Checks:** `AudioTracksTests` against those references on every sample, each track's packets alone and several in one
pass, a track chosen after a seek, the refusals, Matroska's missing elements, every Macintosh code and the choice on
every measured combination; `Mp4ConformanceTests` and `MatroskaConformanceTests` compare every track's packets with
FFmpeg's. ffprobe's channels are its decoder's (HE-AAC's parametric stereo makes a mono track two), so channels are
compared on the samples made for the choice only; on such a file FFmpeg's choice by channels could differ.

