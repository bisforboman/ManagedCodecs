// Temporary (#345): how Mono compiles patterns the decoders use, against CoreCLR. Each test does the same work
// several ways; times are the best of 5 runs.
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static class Program
{
    const int N = 4096;
    static byte[] a = new byte[N + 64], b = new byte[N + 64], d = new byte[N + 64];
    static short[] s1 = new short[N], s2 = new short[N], s3 = new short[N];
    static int sink;

    static void Time(string name, Action f, int reps)
    {
        f();
        double best = double.MaxValue;
        for (int r = 0; r < 5; r++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) f();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / reps / N);
        }
        Console.WriteLine($"{name,-48} {best,8:F3} ns an element");
    }

    static void Main()
    {
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Vector.IsHardwareAccelerated: {Vector.IsHardwareAccelerated}, Vector<byte>.Count: {Vector<byte>.Count}");
        var rnd = new Random(1);
        rnd.NextBytes(a); rnd.NextBytes(b);
        for (int i = 0; i < N; i++) { s1[i] = (short)rnd.Next(-2000, 2000); s2[i] = (short)rnd.Next(-2000, 2000); }
        int reps = 2000;

        Time("short add: array index", () => { for (int i = 0; i < N; i++) s3[i] = (short)(s1[i] + s2[i]); }, reps);
        Time("short add: Span index", () => { Span<short> x = s1, y = s2, z = s3; for (int i = 0; i < z.Length; i++) z[i] = (short)(x[i] + y[i]); }, reps);
        Time("short add: Unsafe.Add on refs", () =>
        {
            ref short x = ref s1[0], y = ref s2[0], z = ref s3[0];
            for (int i = 0; i < N; i++) Unsafe.Add(ref z, i) = (short)(Unsafe.Add(ref x, i) + Unsafe.Add(ref y, i));
        }, reps);
        Time("short add: MemoryMarshal.GetReference + Add", () =>
        {
            Span<short> zs = s3;
            ref short x = ref MemoryMarshal.GetReference((Span<short>)s1), y = ref MemoryMarshal.GetReference((Span<short>)s2), z = ref MemoryMarshal.GetReference(zs);
            for (int i = 0; i < N; i++) Unsafe.Add(ref z, i) = (short)(Unsafe.Add(ref x, i) + Unsafe.Add(ref y, i));
        }, reps);
        Time("short add: pointers", () => AddPointers(), reps);
        Time("short add: Vector<short>", () =>
        {
            int w = Vector<short>.Count;
            for (int i = 0; i <= N - w; i += w) (new Vector<short>(s1, i) + new Vector<short>(s2, i)).CopyTo(s3, i);
        }, reps);

        Time("clip add byte: Math.Min/Max", () => { for (int i = 0; i < N; i++) d[i] = (byte)Math.Min(255, Math.Max(0, a[i] + s1[i])); }, reps);
        Time("clip add byte: (uint) test", () => { for (int i = 0; i < N; i++) { int v = a[i] + s1[i]; d[i] = (byte)((uint)v > 255 ? (~v >> 31) & 255 : v); } }, reps);
        Time("clip add byte: lookup table", () => { byte[] t = Clip; for (int i = 0; i < N; i++) d[i] = t[a[i] + s1[i] + 4096]; }, reps);

        Time("generic typeof(T)==typeof(byte) read<byte>", () => { sink = SumGeneric<byte>(a); }, reps);
        Time("direct byte read", () => { sink = SumByte(a); }, reps);
        Time("generic via Unsafe.As<T,byte>", () => { sink = SumGenericAs<byte>(a); }, reps);

        Time("struct 32 bytes by value", () => { var p = new Big { A = 1 }; int t = 0; for (int i = 0; i < N; i++) t += ByValue(p, i); sink = t; }, reps);
        Time("struct 32 bytes by in", () => { var p = new Big { A = 1 }; int t = 0; for (int i = 0; i < N; i++) t += ByIn(in p, i); sink = t; }, reps);
        Time("small static call (AggressiveInlining)", () => { int t = 0; for (int i = 0; i < N; i++) t += Clamp255(i - 100); sink = t; }, reps);
        Time("same, written inline", () => { int t = 0; for (int i = 0; i < N; i++) { int v = i - 100; t += v < 0 ? 0 : v > 255 ? 255 : v; } sink = t; }, reps);
        Time("BitOperations-like LeadingZeroCount (loop)", () => { int t = 0; for (int i = 0; i < N; i++) t += Lzc((uint)(i * 2654435761u)); sink = t; }, reps);
        Time("table: static ReadOnlySpan<byte> property", () => { int t = 0; for (int i = 0; i < N; i++) t += Table[i & 63]; sink = t; }, reps);
        Time("table: same, getter AggressiveInlining", () => { int t = 0; for (int i = 0; i < N; i++) t += TableInlined[i & 63]; sink = t; }, reps);
        Time("table: same, Unsafe.Add(GetReference(prop))", () => { int t = 0; for (int i = 0; i < N; i++) t += Unsafe.Add(ref MemoryMarshal.GetReference(Table), i & 63); sink = t; }, reps);
        Time("table: same, hoisted to a local span", () => { int t = 0; ReadOnlySpan<byte> tb = Table; for (int i = 0; i < N; i++) t += tb[i & 63]; sink = t; }, reps);
        Time("table: static readonly byte[]", () => { int t = 0; for (int i = 0; i < N; i++) t += TableArray[i & 63]; sink = t; }, reps);
        Time("call taking ReadOnlySpan, passed a Span", () => { Span<byte> sp = a; int t = 0; for (int i = 0; i < N; i++) t += First(sp); sink = t; }, reps);
        Time("call taking Span, passed a Span", () => { Span<byte> sp = a; int t = 0; for (int i = 0; i < N; i++) t += FirstSpan(sp); sink = t; }, reps);
        Time("call taking ReadOnlySpan, passed a ROS", () => { ReadOnlySpan<byte> sp = a; int t = 0; for (int i = 0; i < N; i++) t += First(sp); sink = t; }, reps);
        Time("Span.Slice(i, 4)[0]", () => { Span<byte> sp = a; int t = 0; for (int i = 0; i < N; i++) t += sp.Slice(i, 4)[0]; sink = t; }, reps);
        Time("LZC polyfill: bit loop", () => { int t = 0; for (int i = 0; i < N; i++) t += Lzc((uint)(i * 2654435761u) >> (i & 15)); sink = t; }, reps);
        Time("LZC polyfill: binary search", () => { int t = 0; for (int i = 0; i < N; i++) t += Lzc2((uint)(i * 2654435761u) >> (i & 15)); sink = t; }, reps);
        Time("2D index into byte[] via stride", () => { int t = 0; for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) t += a[y * 64 + x]; sink = t; }, reps);
    }

    static readonly byte[] Clip = MakeClip();
    static byte[] MakeClip() { var t = new byte[8192 + 512]; for (int i = 0; i < t.Length; i++) t[i] = (byte)Math.Min(255, Math.Max(0, i - 4096)); return t; }

    static unsafe void AddPointers()
    {
        fixed (short* x = s1, y = s2, z = s3)
            for (int i = 0; i < N; i++) z[i] = (short)(x[i] + y[i]);
    }

    static int SumGeneric<T>(T[] src) where T : unmanaged
    {
        int t = 0;
        for (int i = 0; i < N; i++)
        {
            if (typeof(T) == typeof(byte)) t += (byte)(object)src[i];
            else t += (ushort)(object)src[i];
        }
        return t;
    }

    static int SumGenericAs<T>(T[] src) where T : unmanaged
    {
        int t = 0;
        ref T r = ref src[0];
        for (int i = 0; i < N; i++)
        {
            if (typeof(T) == typeof(byte)) t += Unsafe.As<T, byte>(ref Unsafe.Add(ref r, i));
            else t += Unsafe.As<T, ushort>(ref Unsafe.Add(ref r, i));
        }
        return t;
    }

    static int SumByte(byte[] src) { int t = 0; for (int i = 0; i < N; i++) t += src[i]; return t; }

    struct Big { public long A, B, C, D; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int ByValue(Big p, int i) => (int)p.A + i;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int ByIn(in Big p, int i) => (int)p.A + i;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    static ReadOnlySpan<byte> Table => new byte[] { 1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64 };
    static ReadOnlySpan<byte> TableInlined { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new byte[] { 1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64 }; }
    static readonly byte[] TableArray = Table.ToArray();
    [MethodImpl(MethodImplOptions.NoInlining)] static int First(ReadOnlySpan<byte> s) => s[0];
    [MethodImpl(MethodImplOptions.NoInlining)] static int FirstSpan(Span<byte> s) => s[0];
    static int Lzc2(uint v)
    {
        if (v == 0) return 32;
        int n = 0;
        if (v <= 0x0000FFFFu) { n += 16; v <<= 16; }
        if (v <= 0x00FFFFFFu) { n += 8; v <<= 8; }
        if (v <= 0x0FFFFFFFu) { n += 4; v <<= 4; }
        if (v <= 0x3FFFFFFFu) { n += 2; v <<= 2; }
        if (v <= 0x7FFFFFFFu) { n += 1; }
        return n;
    }
    static int Lzc(uint v) { int n = 0; if (v == 0) return 32; while ((v & 0x80000000u) == 0) { v <<= 1; n++; } return n; }
}
