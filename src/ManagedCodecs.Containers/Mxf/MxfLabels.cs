// The labels below are from the SMPTE Metadata Registers (SMPTE ST 2123:2025-03, registry.smpte-ra.org), each checked
// against a second source: the set keys against SMPTE ST 377-1:2019 Table 17, the properties against its Annex H
// (static local tags), the rest against the registers' own hierarchy. Their notice:
//
// Copyright (c) 2025 SMPTE All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
// following conditions are met:
// 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
//    disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
//    disclaimer in the documentation and/or other materials provided with the distribution.
// 3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
//    products derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
// WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
// THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
namespace ManagedCodecs.Containers;

/// <summary>
/// The universal labels the MXF header metadata is read by (SMPTE ST 377-1 9.6, Annex H; the SMPTE Metadata Registers).
/// Labels compare with their byte 8, the registry version, ignored (<see cref="Klv.Matches"/>).
/// </summary>
internal static class MxfLabels
{
    /// <summary>The primer pack's key (ST 377-1 Table 13).</summary>
    public static ReadOnlySpan<byte> PrimerPack => [0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01, 0x0D, 0x01, 0x02, 0x01, 0x01, 0x05, 0x01, 0x00];

    /// <summary>
    /// A structural metadata set's key, its bytes 6 (the local set's length coding, 0x53 or 0x13), 8 (the version) and 14
    /// and 15 (the set) aside (ST 377-1 Table 16).
    /// </summary>
    private static ReadOnlySpan<byte> SetPrefix => [0x06, 0x0E, 0x2B, 0x34, 0x02, 0x00, 0x01, 0x00, 0x0D, 0x01, 0x01, 0x01, 0x01];

    /// <summary>
    /// The set <paramref name="key"/> names, as its bytes 14 and 15 (<see cref="MxfSetKind"/>), or 0 for a key that is no
    /// structural metadata set (a dark set, a descriptive metadata set).
    /// </summary>
    public static MxfSetKind SetKind(ReadOnlySpan<byte> key)
    {
        if (key.Length < Klv.KeyLength || !key[..5].SequenceEqual(SetPrefix[..5]) || key[6] != SetPrefix[6]
            || !key[8..13].SequenceEqual(SetPrefix[8..13]) || key[15] != 0)
        {
            return MxfSetKind.None;
        }

        return (MxfSetKind)((key[13] << 8) | key[14]);
    }

    /// <summary>Whether a set <paramref name="key"/> codes its properties' lengths as BER (byte 6 0x13) rather than in two bytes (0x53).</summary>
    public static bool HasBerLocalLengths(ReadOnlySpan<byte> key) => key[5] == 0x13;

    // ---- Properties (ST 377-1 Annex H's static local tag in each comment).

    /// <summary>InstanceUID (3C0A): every set's own ID, which strong references name.</summary>
    public static ReadOnlySpan<byte> InstanceUid => Element(0x01, 0x01, 0x01, 0x15, 0x02, 0x00, 0x00);

    public static ReadOnlySpan<byte> ContentStorage => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x02, 0x01); // 3B03

    public static ReadOnlySpan<byte> OperationalPattern => Element(0x05, 0x01, 0x02, 0x02, 0x03, 0x00, 0x00); // 3B09

    public static ReadOnlySpan<byte> Packages => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x05, 0x01); // 1901

    public static ReadOnlySpan<byte> EssenceContainerData => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x05, 0x02); // 1902

    public static ReadOnlySpan<byte> LinkedPackageUid => Element(0x02, 0x06, 0x01, 0x01, 0x06, 0x01, 0x00); // 2701

    public static ReadOnlySpan<byte> IndexSid => Element(0x04, 0x01, 0x03, 0x04, 0x05, 0x00, 0x00); // 3F06

    public static ReadOnlySpan<byte> BodySid => Element(0x04, 0x01, 0x03, 0x04, 0x04, 0x00, 0x00); // 3F07

    public static ReadOnlySpan<byte> PackageUid => Element(0x01, 0x01, 0x01, 0x15, 0x10, 0x00, 0x00); // 4401

    public static ReadOnlySpan<byte> Tracks => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x06, 0x05); // 4403

    public static ReadOnlySpan<byte> Descriptor => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x02, 0x03); // 4701

    public static ReadOnlySpan<byte> TrackId => Element(0x02, 0x01, 0x07, 0x01, 0x01, 0x00, 0x00); // 4801

    public static ReadOnlySpan<byte> TrackNumber => Element(0x02, 0x01, 0x04, 0x01, 0x03, 0x00, 0x00); // 4804

    public static ReadOnlySpan<byte> Sequence => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x02, 0x04); // 4803

    public static ReadOnlySpan<byte> EditRate => Element(0x02, 0x05, 0x30, 0x04, 0x05, 0x00, 0x00); // 4B01

    public static ReadOnlySpan<byte> Origin => Element(0x02, 0x07, 0x02, 0x01, 0x03, 0x01, 0x03); // 4B02

    public static ReadOnlySpan<byte> DataDefinition => Element(0x02, 0x04, 0x07, 0x01, 0x00, 0x00, 0x00); // 0201

    public static ReadOnlySpan<byte> Duration => Element(0x02, 0x07, 0x02, 0x02, 0x01, 0x01, 0x03); // 0202

    public static ReadOnlySpan<byte> StructuralComponents => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x06, 0x09); // 1001

    public static ReadOnlySpan<byte> StartPosition => Element(0x02, 0x07, 0x02, 0x01, 0x03, 0x01, 0x04); // 1201

    public static ReadOnlySpan<byte> SourcePackageId => Element(0x02, 0x06, 0x01, 0x01, 0x03, 0x01, 0x00); // 1101

    public static ReadOnlySpan<byte> SourceTrackId => Element(0x02, 0x06, 0x01, 0x01, 0x03, 0x02, 0x00); // 1102

    public static ReadOnlySpan<byte> StartTimecode => Element(0x02, 0x07, 0x02, 0x01, 0x03, 0x01, 0x05); // 1501

    public static ReadOnlySpan<byte> RoundedTimecodeBase => Element(0x02, 0x04, 0x04, 0x01, 0x01, 0x02, 0x06); // 1502

    public static ReadOnlySpan<byte> DropFrame => Element(0x01, 0x04, 0x04, 0x01, 0x01, 0x05, 0x00); // 1503

    public static ReadOnlySpan<byte> LinkedTrackId => Element(0x05, 0x06, 0x01, 0x01, 0x03, 0x05, 0x00); // 3006

    public static ReadOnlySpan<byte> SampleRate => Element(0x01, 0x04, 0x06, 0x01, 0x01, 0x00, 0x00); // 3001

    public static ReadOnlySpan<byte> ContainerDuration => Element(0x01, 0x04, 0x06, 0x01, 0x02, 0x00, 0x00); // 3002

    public static ReadOnlySpan<byte> EssenceContainer => Element(0x02, 0x06, 0x01, 0x01, 0x04, 0x01, 0x02); // 3004

    public static ReadOnlySpan<byte> SubDescriptors => Element(0x04, 0x06, 0x01, 0x01, 0x04, 0x06, 0x0B); // 3F01

    public static ReadOnlySpan<byte> FrameLayout => Element(0x01, 0x04, 0x01, 0x03, 0x01, 0x04, 0x00); // 320C

    public static ReadOnlySpan<byte> StoredWidth => Element(0x01, 0x04, 0x01, 0x05, 0x02, 0x02, 0x00); // 3203

    public static ReadOnlySpan<byte> StoredHeight => Element(0x01, 0x04, 0x01, 0x05, 0x02, 0x01, 0x00); // 3202

    public static ReadOnlySpan<byte> SampledWidth => Element(0x01, 0x04, 0x01, 0x05, 0x01, 0x08, 0x00); // 3205

    public static ReadOnlySpan<byte> SampledHeight => Element(0x01, 0x04, 0x01, 0x05, 0x01, 0x07, 0x00); // 3204

    public static ReadOnlySpan<byte> DisplayWidth => Element(0x01, 0x04, 0x01, 0x05, 0x01, 0x0C, 0x00); // 3209

    public static ReadOnlySpan<byte> DisplayHeight => Element(0x01, 0x04, 0x01, 0x05, 0x01, 0x0B, 0x00); // 3208

    public static ReadOnlySpan<byte> AspectRatio => Element(0x01, 0x04, 0x01, 0x01, 0x01, 0x01, 0x00); // 320E

    public static ReadOnlySpan<byte> PictureEssenceCoding => Element(0x02, 0x04, 0x01, 0x06, 0x01, 0x00, 0x00); // 3201

    public static ReadOnlySpan<byte> FieldDominance => Element(0x02, 0x04, 0x01, 0x03, 0x01, 0x06, 0x00); // 3212

    public static ReadOnlySpan<byte> ComponentDepth => Element(0x02, 0x04, 0x01, 0x05, 0x03, 0x0A, 0x00); // 3301

    public static ReadOnlySpan<byte> HorizontalSubsampling => Element(0x01, 0x04, 0x01, 0x05, 0x01, 0x05, 0x00); // 3302

    public static ReadOnlySpan<byte> VerticalSubsampling => Element(0x02, 0x04, 0x01, 0x05, 0x01, 0x10, 0x00); // 3308

    public static ReadOnlySpan<byte> AudioSamplingRate => Element(0x05, 0x04, 0x02, 0x03, 0x01, 0x01, 0x01); // 3D03

    public static ReadOnlySpan<byte> ChannelCount => Element(0x05, 0x04, 0x02, 0x01, 0x01, 0x04, 0x00); // 3D07

    public static ReadOnlySpan<byte> QuantizationBits => Element(0x04, 0x04, 0x02, 0x03, 0x03, 0x04, 0x00); // 3D01

    public static ReadOnlySpan<byte> SoundEssenceCoding => Element(0x02, 0x04, 0x02, 0x04, 0x02, 0x00, 0x00); // 3D06

    public static ReadOnlySpan<byte> BlockAlign => Element(0x05, 0x04, 0x02, 0x03, 0x02, 0x01, 0x00); // 3D0A

    // ---- Data definitions (a component's kind; the registers' EssenceTrackKinds and MetadataTrackKinds).

    /// <summary>The data definition label shared by the kinds below: their byte 14 tells them apart.</summary>
    public static ReadOnlySpan<byte> DataDefinitionPrefix => [0x06, 0x0E, 0x2B, 0x34, 0x04, 0x01, 0x01, 0x01, 0x01, 0x03, 0x02];

    /// <summary>
    /// The kind of data definition label <paramref name="label"/> is: picture (0x0201), sound (0x0202), data (0x0203)
    /// or a timecode (0x0101 to 0x0103), as its bytes 12 and 13; 0 for another label.
    /// </summary>
    public static int DataKind(ReadOnlySpan<byte> label) =>
        label.Length == Klv.KeyLength && Klv.Matches(label, DataDefinitionPrefix) && label[13..].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0])
            ? (label[11] << 8) | label[12]
            : 0;

    public const int PictureKind = 0x0201;

    public const int SoundKind = 0x0202;

    public const int DataKindValue = 0x0203;

    /// <summary>Whether a <see cref="DataKind"/> is a timecode (SMPTE ST 12-1 with inactive or active user bits, ST 309).</summary>
    public static bool IsTimecodeKind(int kind) => kind is >= 0x0101 and <= 0x0103;

    // ---- Codings and essence containers (the registers' Labels; bytes 9 on).

    /// <summary>
    /// ffprobe's name for the picture coding <paramref name="coding"/> names, else for the essence container
    /// <paramref name="container"/>, or null for neither known.
    /// </summary>
    public static string? PictureCodec(ReadOnlySpan<byte> coding, ReadOnlySpan<byte> container)
    {
        // 04.01.02.02: compressed picture coding.
        if (IsLabel(coding) && coding[8..12].SequenceEqual((ReadOnlySpan<byte>)[0x04, 0x01, 0x02, 0x02]))
        {
            string? codec = (coding[12], coding[13]) switch
            {
                (0x01, >= 0x01 and <= 0x09) => "mpeg2video", // MPEG2* (MP@ML ... HP@H14, Other)
                (0x01, 0x10) => "mpeg1video",
                (0x01, 0x20) => "mpeg4",
                (0x01, >= 0x30 and <= 0x3F) => "h264", // H264MPEG4AVC*
                (0x01, >= 0x40 and <= 0x4F) => "hevc", // H265HEVC*
                (0x03, 0x01) => "jpeg2000",
                (0x03, 0x06) => "prores",
                (0x71, _) => "dnxhd", // SMPTEVC3*
                _ => null,
            };
            if (codec is not null)
            {
                return codec;
            }
        }

        // A container mapping for one coding (0D.01.03.01.02 and the mapping).
        if (IsGenericContainer(container))
        {
            return container[13] switch
            {
                0x0F or 0x10 => "h264", // AVC NAL unit stream, AVC byte stream
                0x11 => "dnxhd", // VC-3 pictures
                0x1C => "prores",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// ffprobe's name for sound in the essence container <paramref name="container"/> at <paramref name="bits"/> a sample:
    /// the AES3 and Broadcast Wave mappings (SMPTE ST 382) hold little-endian PCM, and the D-10 mapping (ST 386, byte 14
    /// 01h) 8-channel AES3 elements, whose 16 or 24 bits <see cref="MxfDemuxer"/> gives as little-endian PCM. Null for
    /// another container.
    /// </summary>
    public static string? SoundCodec(ReadOnlySpan<byte> container, int bits) =>
        IsGenericContainer(container) && container[13] == 0x06 && bits is 8 or 16 or 24 or 32
            ? bits == 8 ? "pcm_u8" : $"pcm_s{bits}le"
            : IsGenericContainer(container) && container[13] == 0x01 && bits is 16 or 24
            ? $"pcm_s{bits}le"
            : null;

    private static bool IsLabel(ReadOnlySpan<byte> label) =>
        label.Length == Klv.KeyLength && label[..5].SequenceEqual((ReadOnlySpan<byte>)[0x06, 0x0E, 0x2B, 0x34, 0x04]);

    /// <summary>Whether <paramref name="label"/> is an MXF generic container mapping (MXFGenericContainer, 0D.01.03.01.02).</summary>
    private static bool IsGenericContainer(ReadOnlySpan<byte> label) =>
        IsLabel(label) && label[8..13].SequenceEqual((ReadOnlySpan<byte>)[0x0D, 0x01, 0x03, 0x01, 0x02]);

    /// <summary>A property's label: 06.0E.2B.34.01.01.01, the registry version, its bytes 9 to 14, then two zeros.</summary>
    private static byte[] Element(byte version, byte b9, byte b10, byte b11, byte b12, byte b13, byte b14) =>
        [0x06, 0x0E, 0x2B, 0x34, 0x01, 0x01, 0x01, version, b9, b10, b11, b12, b13, b14, 0x00, 0x00];
}
