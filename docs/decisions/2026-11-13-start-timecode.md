# A stream's start timecode (2026-11-13)

MXF files carry a start timecode (the material package's timecode track, SMPTE ST 377-1 B.17), which `MxfHeader` read
for its tests since #483 but nothing public showed. ffprobe reports it as the format's `timecode` tag; MP4 and
QuickTime files can carry one too (a 'tmcd' track), which no demuxer here reads yet.

## Decided (the owner, 2026-11-13)

1. **A Core type, not a string or an MXF-only member.** `Timecode` (ManagedCodecs.Core) is a record struct of what
   the containers store: `Frames` since midnight, the rounded `Base` (25, 30 for 29.97, 60 for 59.94) and
   `DropFrame`. `ToString()` gives the code as ffprobe writes it (hh:mm:ss:ff, hh:mm:ss;ff for drop frame, the
   skipped numbers of SMPTE ST 12-1 added back). A string alone would lose the frame count a player needs to show
   the timecode of later frames; an `MxfDemuxer` member would leave MP4's 'tmcd' to grow another.
2. **On the streams.** `VideoStreamInfo.StartTimecode` and `AudioStreamInfo.StartTimecode` (`Timecode?`, null where
   the container gives none), beside their other facts, so `IDemuxer` doesn't change. MXF's is the material
   package's, the same on both streams: an OP-Atom file holds sound alone, and its timecode stays on it.
3. **Not now:** MP4's 'tmcd' tracks (a later change can fill the same property), a timecode per frame, and the
   source package's timecode (ffprobe's stream tag, often the same).

Checked against ffprobe's `timecode` tag on every MXF sample (MxfHeaderTests): 01:00:00:00 (mxf-prores-pcm16),
01:00:00;02 drop frame at 29.97 (mxf-h264-interlaced), and 00:00:00:00, FFmpeg's muxer's default, on the rest.
