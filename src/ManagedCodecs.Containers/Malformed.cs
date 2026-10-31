namespace ManagedCodecs.Containers;

/// <summary>
/// The demuxers' contract for malformed files: <see cref="InvalidDataException"/> (or <see cref="NotSupportedException"/>
/// for what is valid but not handled), never another exception. Their checked arithmetic is a range check on the
/// file's fields, and reading past the end of a truncated file is a malformed file too: both are turned into
/// <see cref="InvalidDataException"/> at the public entry points, which keep the original as the inner exception.
/// </summary>
internal static class Malformed
{
    /// <summary>Whether <paramref name="e"/> comes from a file that breaks its format's limits.</summary>
    public static bool IsMalformedInput(Exception e) => e is OverflowException or EndOfStreamException;

    public static InvalidDataException Wrap(Exception e, string format) =>
        new($"Malformed {format} file: {e.Message}", e);

    /// <summary>
    /// <paramref name="packets"/>, with the exceptions <see cref="IsMalformedInput"/> names turned into
    /// <see cref="InvalidDataException"/> (an iterator cannot catch around its own yield).
    /// </summary>
    public static IEnumerable<T> Guard<T>(IEnumerable<T> packets, string format)
    {
        using var enumerator = packets.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }
            }
            catch (Exception e)
            {
                if (!IsMalformedInput(e))
                {
                    throw;
                }

                throw Wrap(e, format);
            }

            yield return enumerator.Current;
        }
    }
}
