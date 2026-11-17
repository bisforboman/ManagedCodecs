namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// The StreamMuxConfig of AAC sent as LATM over RTP (RFC 6416's <c>MP4A-LATM</c>, the SDP's <c>config=</c> with
/// <c>cpresent=0</c>; ISO/IEC 14496-3, 1.7.3.2.3): the AudioSpecificConfig inside it, for the decoder, and how many
/// access units each audioMuxElement carries. Only what cameras and FFmpeg's and GStreamer's packetizers send is read:
/// one program with one layer of AAC LC (with SBR signalled or not), all streams framed together, and payloads whose
/// length is sent with each (frameLengthType 0).
/// </summary>
internal static class LatmConfig
{
    /// <summary>Reads a StreamMuxConfig. False when it is cut short or is one this doesn't read (the track then isn't played).</summary>
    /// <param name="config">The StreamMuxConfig's bytes.</param>
    /// <param name="audioSpecificConfig">The AudioSpecificConfig in it, from its first bit, padded with zero bits.</param>
    /// <param name="subFrames">The access units in each audioMuxElement (numSubFrames + 1).</param>
    public static bool TryParse(ReadOnlySpan<byte> config, out byte[] audioSpecificConfig, out int subFrames)
    {
        audioSpecificConfig = [];
        subFrames = 0;
        var r = new Bits(config);
        int version = r.Read(1);
        if (version == 1 && r.Read(1) != 0)
        {
            return false; // audioMuxVersionA 1: reserved
        }

        if (version == 1)
        {
            r.Value(); // taraBufferFullness
        }

        bool together = r.Read(1) == 1;
        subFrames = r.Read(6) + 1;
        int programs = r.Read(4);
        int layers = r.Read(3);
        if (!together || programs != 0 || layers != 0)
        {
            return false;
        }

        // The AudioSpecificConfig: its length is given (version 1), or is what its own fields add up to (version 0).
        long length = version == 1 ? r.Value() : ConfigLength(r);
        if (length < 13 || length > r.Left)
        {
            return false;
        }

        audioSpecificConfig = new byte[(length + 7) / 8];
        for (int i = 0; i < length; i++)
        {
            audioSpecificConfig[i >> 3] |= (byte)(r.Read(1) << (7 - (i & 7)));
        }

        int frameLengthType = r.Read(3);
        return !r.Overrun && frameLengthType == 0;
    }

    // The bits of an AudioSpecificConfig of AAC LC (1.6.2.1, with GASpecificConfig, 4.4.1), read ahead without moving
    // on; 0 for another object type or a program config element, whose length isn't worked out here. A sync extension
    // after it (1.6.5.2, backward-compatible SBR signalling, which GStreamer's packetizer passes on) belongs to it:
    // its first bits, 0x2B7, can't be the frameLengthType 0 that otherwise follows.
    private static long ConfigLength(Bits at)
    {
        var r = at;
        int start = r.Position;
        int objectType = ObjectType(ref r);
        SampleRate(ref r);
        int channels = r.Read(4);
        if (objectType is 5 or 29)
        {
            SampleRate(ref r);
            objectType = ObjectType(ref r);
        }

        if (objectType != 2 || channels == 0)
        {
            return 0;
        }

        r.Read(1); // frameLengthFlag
        if (r.Read(1) == 1)
        {
            r.Read(14); // coreCoderDelay
        }

        r.Read(1); // extensionFlag
        var ahead = r;
        if (ahead.Read(11) == 0x2B7 && !ahead.Overrun)
        {
            r = ahead;
            if (ObjectType(ref r) == 5 && r.Read(1) == 1)
            {
                SampleRate(ref r);
                ahead = r;
                if (ahead.Read(11) == 0x548 && !ahead.Overrun)
                {
                    r = ahead;
                    r.Read(1); // psPresentFlag
                }
            }
        }

        return r.Overrun ? 0 : r.Position - start;
    }

    private static int ObjectType(ref Bits r)
    {
        int type = r.Read(5);
        return type == 31 ? 32 + r.Read(6) : type;
    }

    private static void SampleRate(ref Bits r)
    {
        if (r.Read(4) == 15)
        {
            r.Read(24);
        }
    }

    // Bits from the most significant on; past the end they read as 0 and Overrun says so.
    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public bool Overrun { get; private set; }

        public readonly long Left => ((long)_data.Length * 8) - Position;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, Position++)
            {
                int bit = 0;
                if (Position < _data.Length * 8)
                {
                    bit = (_data[Position >> 3] >> (7 - (Position & 7))) & 1;
                }
                else
                {
                    Overrun = true;
                }

                value = (value << 1) | bit;
            }

            return value;
        }

        // LatmGetValue (1.7.3.2.3): two bits counting the bytes less one, then the bytes.
        public long Value()
        {
            int bytes = Read(2) + 1;
            long value = 0;
            for (int i = 0; i < bytes; i++)
            {
                value = (value << 8) | (uint)Read(8);
            }

            return value;
        }
    }
}
