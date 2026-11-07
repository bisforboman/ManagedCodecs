#if UNITY_BUILD
using System.Reflection;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.Unity;

/// <summary>
/// The Unity build's own System.Numerics.BitOperations (src/ManagedCodecs.Unity/Polyfills.cs, plain arithmetic for
/// netstandard2.1) against .NET's: it has the same name, so it is reached through reflection on the Unity assembly.
/// </summary>
public class PolyfillTests
{
    private static readonly Type DotNet = typeof(object).Assembly.GetType("System.Numerics.BitOperations", throwOnError: true)!;

    private static readonly Type Polyfill = typeof(H264Decoder).Assembly.GetType("System.Numerics.BitOperations", throwOnError: true)!;

    public static TheoryData<string> Operations => ["LeadingZeroCount", "TrailingZeroCount", "Log2"];

    [Theory]
    [MemberData(nameof(Operations))]
    public void BitOperations_MatchDotNet(string name)
    {
        var polyfill32 = Polyfill.GetMethod(name, BindingFlags.Public | BindingFlags.Static, [typeof(uint)])!;
        var polyfill64 = Polyfill.GetMethod(name, BindingFlags.Public | BindingFlags.Static, [typeof(ulong)])!;
        var dotnet32 = DotNet.GetMethod(name, [typeof(uint)])!;
        var dotnet64 = DotNet.GetMethod(name, [typeof(ulong)])!;

        // Every single bit, every run of low or high ones, and random values of every width.
        var values = new List<ulong> { 0, ulong.MaxValue };
        for (int k = 0; k < 64; k++)
        {
            values.Add(1UL << k);
            values.Add((1UL << k) - 1);
            values.Add(ulong.MaxValue << k);
        }

        var random = new Random(7);
        for (int n = 0; n < 20000; n++)
        {
            values.Add((ulong)random.NextInt64() >> random.Next(64));
        }

        foreach (ulong v in values)
        {
            Assert.Equal(dotnet32.Invoke(null, [(uint)v]), polyfill32.Invoke(null, [(uint)v]));
            Assert.Equal(dotnet64.Invoke(null, [v]), polyfill64.Invoke(null, [v]));
        }
    }
}
#endif
