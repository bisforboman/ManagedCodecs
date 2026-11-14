namespace ManagedCodecs;

/// <summary>
/// A SMPTE timecode (SMPTE ST 12-1): a frame count since midnight at a whole number of frames a second, and whether
/// it is drop-frame. <see cref="ToString"/> writes it as FFmpeg prints it, hh:mm:ss:ff, or hh:mm:ss;ff for drop frame.
/// </summary>
/// <param name="FrameNumber">Frames since midnight, as the file counts them (for drop frame, frames actually there, the dropped codes not counted).</param>
/// <param name="Base">The whole number of frames a second the timecode counts in (25, 30 for 29.97, 60 for 59.94), or 0 where the file gives none.</param>
/// <param name="DropFrame">Whether codes are dropped: frames 0 and 1 of each minute but every tenth (at a base of 30; 0 to 3 at 60).</param>
public readonly record struct Timecode(long FrameNumber, int Base, bool DropFrame)
{
    /// <summary>hh:mm:ss:ff, or hh:mm:ss;ff for drop frame (its frame codes counted as SMPTE ST 12-1 drops them, scaled to the base); 00:00:00:00 without a base.</summary>
    public override string ToString()
    {
        if (Base <= 0)
        {
            return "00:00:00:00";
        }

        long frames = FrameNumber;
        if (DropFrame && Base % 30 == 0)
        {
            // Frame numbers skip `drop` codes a minute, except every tenth minute: add them back to count codes.
            int drop = 2 * (Base / 30);
            long perTenMinutes = (Base * 600L) - (9L * drop);
            long perMinute = (Base * 60L) - drop;
            long tens = frames / perTenMinutes;
            long rest = frames % perTenMinutes;
            frames += (9L * drop * tens) + (rest > drop ? drop * ((rest - drop) / perMinute) : 0);
        }

        long ff = frames % Base;
        long seconds = frames / Base;
        return FormattableString.Invariant($"{(seconds / 3600) % 24:00}:{(seconds / 60) % 60:00}:{seconds % 60:00}{(DropFrame ? ';' : ':')}{ff:00}");
    }
}
