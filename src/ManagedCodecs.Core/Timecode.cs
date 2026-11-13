namespace ManagedCodecs;

/// <summary>
/// A SMPTE timecode (ST 12-1) as a container stores a start: a count of frames since midnight at a rounded frame rate
/// (<see cref="Base"/>: 25, 30 for 29.97, 60 for 59.94), and whether it drops frame numbers. Such as an MXF material
/// package's start timecode, which ffprobe reports as the <c>timecode</c> tag.
/// </summary>
/// <param name="Frames">Frames since 00:00:00:00, counted at <paramref name="Base"/> (dropped numbers not counted).</param>
/// <param name="Base">The rounded frames a second the codes count; 0 or less where unknown.</param>
/// <param name="DropFrame">Whether codes skip frame numbers 0 and 1 (at a base of 30, more at multiples) each minute but every tenth.</param>
public readonly record struct Timecode(long Frames, int Base, bool DropFrame)
{
    /// <summary>
    /// hh:mm:ss:ff, or hh:mm:ss;ff for drop frame (as ffprobe writes it): the frame count turned into codes, adding back
    /// the numbers drop frame skips (two at a base of 30, scaled for 60), hours modulo 24. "00:00:00:00" without a base.
    /// </summary>
    public override string ToString()
    {
        if (Base <= 0)
        {
            return "00:00:00:00";
        }

        long frames = Frames;
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
        return $"{(seconds / 3600) % 24:00}:{(seconds / 60) % 60:00}:{seconds % 60:00}{(DropFrame ? ';' : ':')}{ff:00}";
    }
}
