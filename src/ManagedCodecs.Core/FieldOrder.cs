namespace ManagedCodecs;

/// <summary>
/// The order of an interlaced picture's fields, as FFmpeg's <c>AVFieldOrder</c> (the <c>field_order</c> that
/// <c>ffprobe -show_streams</c> prints), with the same values.
/// </summary>
public enum FieldOrder
{
    /// <summary>Not known (<c>unknown</c>): FFmpeg's H.264 parser gives it to a picture coded as a lone field.</summary>
    Unknown = 0,

    /// <summary>Not interlaced (<c>progressive</c>).</summary>
    Progressive = 1,

    /// <summary>Top field coded first, top field displayed first (<c>tt</c>).</summary>
    TT = 2,

    /// <summary>Bottom field coded first, bottom field displayed first (<c>bb</c>).</summary>
    BB = 3,

    /// <summary>Top field coded first, bottom field displayed first (<c>tb</c>); no decoder here gives it.</summary>
    TB = 4,

    /// <summary>Bottom field coded first, top field displayed first (<c>bt</c>); no decoder here gives it.</summary>
    BT = 5,
}
