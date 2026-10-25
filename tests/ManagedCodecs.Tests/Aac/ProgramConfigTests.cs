using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// The output order of a program config element's channels (<see cref="ProgramConfigElement.OutputOrder"/>): the shapes
/// the samples check against FFmpeg (aac-fdk-pce, aac-pce-front, aac-adts-pce and aac-pce-8ch) and the ones no encoder at
/// hand writes, each element a single channel (false) or a pair (true), listed front, side, back, then LFE.
/// </summary>
public class ProgramConfigTests
{
    public static TheoryData<bool[], bool[], bool[], int, int[]> Shapes() => new()
    {
        // The spec's 5.1 (the FDK encoder's): C, L R, Ls Rs, LFE -> FL FR FC LFE BL BR, as configuration 6.
        { [false, true], [], [true], 1, [1, 2, 0, 5, 3, 4] },

        // Two back pairs, the first a side pair (the FDK encoder's 7.1): as configuration 7.
        { [false, true], [], [true, true], 1, [1, 2, 0, 7, 5, 6, 3, 4] },

        // Two front pairs, the inner listed first (FFmpeg's 6.0(front)): FL FR FLC FRC SL SR.
        { [true, true], [true], [], 0, [2, 3, 0, 1, 4, 5] },

        // The front centre, both front pairs, a back pair and an LFE (the FDK encoder's 7.1 front centre):
        // FL FR FC LFE BL BR FLC FRC.
        { [false, true, true], [], [true], 1, [3, 4, 0, 7, 5, 6, 1, 2] },

        // A side pair, a back pair and a back centre: FL FR FC BL BR BC SL SR.
        { [false, true], [true], [true, false], 0, [1, 2, 0, 5, 6, 7, 3, 4] },

        // As listed: FFmpeg's encoder's tables, with the front pair before the centre and the LFE coded as a side
        // channel (its 5.1), which FFmpeg outputs as listed too.
        { [true, false], [false], [true], 0, [0, 1, 2, 3, 4, 5] },

        // As listed: a single channel after a back pair is the back centre, but one before it has no place.
        { [true], [], [false, true], 0, [0, 1, 2, 3, 4] },

        // As listed: three front pairs, two back pairs next to a side pair, two LFEs.
        { [true, true, true], [], [], 0, [0, 1, 2, 3, 4, 5] },
        { [true], [true], [true, true], 0, [0, 1, 2, 3, 4, 5, 6, 7] },
        { [true], [], [], 2, [0, 1, 2, 3] },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void OutputOrder(bool[] front, bool[] side, bool[] back, int lfe, int[] expected)
    {
        var pce = new ProgramConfigElement(front, side, back, lfe);
        Assert.Equal(expected.Length, pce.Channels);
        Assert.Equal(expected, pce.OutputOrder());
    }
}
