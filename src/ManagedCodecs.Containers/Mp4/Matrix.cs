using System.Buffers.Binary;

namespace ManagedCodecs.Containers;

/// <summary>
/// The rotation and scale part (a, b, c, d) of an MP4 transformation matrix ('tkhd', 'mvhd'; 14496-12 6.2.2), each a
/// 16.16 fixed-point value: a point (x, y) is shown at (a x + c y, b x + d y).
/// </summary>
internal readonly record struct Matrix(long A, long B, long C, long D)
{
    public static readonly Matrix Identity = new(0x10000, 0, 0, 0x10000);

    /// <summary>The matrix at <paramref name="offset"/> of a box's payload (a, b, u, c, d, ...), the identity if it is cut short.</summary>
    public static Matrix Read(ReadOnlySpan<byte> payload, int offset) =>
        payload.Length < offset + 20
            ? Identity
            : new(BinaryPrimitives.ReadInt32BigEndian(payload[offset..]),
                BinaryPrimitives.ReadInt32BigEndian(payload[(offset + 4)..]),
                BinaryPrimitives.ReadInt32BigEndian(payload[(offset + 12)..]),
                BinaryPrimitives.ReadInt32BigEndian(payload[(offset + 16)..]));

    /// <summary>
    /// The counter-clockwise rotation of a track matrix followed by the movie's (FFmpeg multiplies them), as ffprobe's
    /// display matrix <c>rotation</c> but in 0 to 359: 0, 90, 180 or 270 for a rotation by a multiple of 90 degrees at
    /// any scale, else (another angle, a mirror) 0.
    /// </summary>
    public static int RotationOf(Matrix track, Matrix movie)
    {
        // Row vectors, as the box's layout: the product's rotation part, the scales unreduced (only signs and equality matter).
        long a = (track.A * movie.A) + (track.B * movie.C);
        long b = (track.A * movie.B) + (track.B * movie.D);
        long c = (track.C * movie.A) + (track.D * movie.C);
        long d = (track.C * movie.B) + (track.D * movie.D);
        if (a != d || b != -c || (a == 0) == (b == 0))
        {
            return 0;
        }

        return a > 0 ? 0 : a < 0 ? 180 : b < 0 ? 90 : 270;
    }
}
