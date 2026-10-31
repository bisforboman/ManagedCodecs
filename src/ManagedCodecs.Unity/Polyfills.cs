// What the sources use of .NET's newer runtime and libraries that netstandard2.1 lacks, for the Unity build alone
// (docs/decisions/2026-10-31-unity-netstandard.md). Static members come as C# 14 extension members on the type they
// belong to, so the sources call them as they are.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    /// <summary>Init accessors and records.</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>Accepted and ignored: Mono and IL2CPP zero locals regardless.</summary>
    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event, Inherited = false)]
    internal sealed class SkipLocalsInitAttribute : Attribute
    {
    }

    /// <summary>The argument's expression as the caller wrote it, for the ThrowIf helpers' messages.</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
    {
        public string ParameterName { get; } = parameterName;
    }
}

namespace System.Threading.Tasks
{
    /// <summary>The non-generic TaskCompletionSource of .NET 5, over the generic one.</summary>
    internal sealed class TaskCompletionSource
    {
        private readonly TaskCompletionSource<bool> _source;

        public TaskCompletionSource(TaskCreationOptions options) => _source = new TaskCompletionSource<bool>(options);

        public Task Task => _source.Task;

        public void SetResult() => _source.SetResult(true);

        public bool TrySetResult() => _source.TrySetResult(true);

        public void SetException(Exception exception) => _source.SetException(exception);
    }
}

namespace System.Numerics
{
    /// <summary>The bit operations the sources use, in plain arithmetic (.NET's are intrinsics).</summary>
    internal static class BitOperations
    {
        public static int LeadingZeroCount(uint value)
        {
            if (value == 0)
            {
                return 32;
            }

            int count = 0;
            while ((value & 0x80000000u) == 0)
            {
                value <<= 1;
                count++;
            }

            return count;
        }

        public static int LeadingZeroCount(ulong value) =>
            (value >> 32) != 0 ? LeadingZeroCount((uint)(value >> 32)) : 32 + LeadingZeroCount((uint)value);

        public static int Log2(uint value) => 31 - LeadingZeroCount(value | 1);

        public static int Log2(ulong value) => 63 - LeadingZeroCount(value | 1);

        public static int TrailingZeroCount(uint value)
        {
            if (value == 0)
            {
                return 32;
            }

            int count = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }

            return count;
        }

        public static int TrailingZeroCount(ulong value) =>
            (uint)value != 0 ? TrailingZeroCount((uint)value) : 32 + TrailingZeroCount((uint)(value >> 32));
    }
}

namespace ManagedCodecs
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    internal static class Polyfills
    {
        [ThreadStatic]
        private static Random? t_random;

        extension(ArgumentOutOfRangeException)
        {
            public static void ThrowIfNegative(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(name, value, "Must not be negative.");
                }
            }

            public static void ThrowIfNegative(long value, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(name, value, "Must not be negative.");
                }
            }

            public static void ThrowIfNegativeOrZero(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
                }
            }

            public static void ThrowIfGreaterThan(int value, int other, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value > other)
                {
                    throw new ArgumentOutOfRangeException(name, value, $"Must be at most {other}.");
                }
            }

            public static void ThrowIfGreaterThanOrEqual(int value, int other, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value >= other)
                {
                    throw new ArgumentOutOfRangeException(name, value, $"Must be below {other}.");
                }
            }

            public static void ThrowIfLessThan(int value, int other, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value < other)
                {
                    throw new ArgumentOutOfRangeException(name, value, $"Must be at least {other}.");
                }
            }

            public static void ThrowIfLessThan(long value, long other, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value < other)
                {
                    throw new ArgumentOutOfRangeException(name, value, $"Must be at least {other}.");
                }
            }

            public static void ThrowIfNotEqual(int value, int other, [CallerArgumentExpression(nameof(value))] string? name = null)
            {
                if (value != other)
                {
                    throw new ArgumentOutOfRangeException(name, value, $"Must be {other}.");
                }
            }
        }

        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(object? argument, [CallerArgumentExpression(nameof(argument))] string? name = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(name);
                }
            }
        }

        extension(GC)
        {
            public static T[] AllocateUninitializedArray<T>(int length, bool pinned = false) => new T[length];
        }

        extension(MemoryMarshal)
        {
            /// <summary>The first element's place; for an empty array a reference that must not be read.</summary>
            public static ref T GetArrayDataReference<T>(T[] array) => ref MemoryMarshal.GetReference(array.AsSpan());
        }

        extension(Math)
        {
            public static double ScaleB(double x, int n) => x * Math.Pow(2, n);

            /// <summary>Not the correctly rounded log2 of .NET's: the conformance tests on this build show whether it matters.</summary>
            public static double Log2(double x) => Math.Log(x, 2);
        }

        extension(char)
        {
            public static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
        }

        extension(Random)
        {
            /// <summary>A generator for each thread (only the tests' random delays use it).</summary>
            public static Random Shared => t_random ??= new Random();
        }

        extension(MemoryMarshal)
        {
            public static void Write<T>(Span<byte> destination, T value)
                where T : struct => MemoryMarshal.Write(destination, ref value);
        }

        extension<T>(IEnumerable<T> source)
        {
            public T MinBy<TKey>(Func<T, TKey> key)
                where TKey : IComparable<TKey>
            {
                using var e = source.GetEnumerator();
                if (!e.MoveNext())
                {
                    throw new InvalidOperationException("Sequence contains no elements.");
                }

                T best = e.Current;
                TKey bestKey = key(best);
                while (e.MoveNext())
                {
                    TKey k = key(e.Current);
                    if (k.CompareTo(bestKey) < 0)
                    {
                        best = e.Current;
                        bestKey = k;
                    }
                }

                return best;
            }
        }

        extension<T>(Span<T> span)
            where T : IComparable<T>
        {
            public void Sort()
            {
                var copy = span.ToArray();
                Array.Sort(copy);
                copy.CopyTo(span);
            }
        }

        extension(Span<byte> span)
        {
            public int LastIndexOfAnyExcept(byte value)
            {
                for (int i = span.Length - 1; i >= 0; i--)
                {
                    if (span[i] != value)
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        extension(Span<int> span)
        {
            public bool ContainsAnyExceptInRange(int low, int high)
            {
                foreach (int v in span)
                {
                    if (v < low || v > high)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        extension(Array)
        {
            public static void Clear(Array array) => Array.Clear(array, 0, array.Length);
        }

        extension(System.Text.Encoding)
        {
            public static System.Text.Encoding Latin1 => System.Text.Encoding.GetEncoding(28591);
        }

        extension<T>(T[] array)
        {
            /// <summary>Enumerable's, as .NET 10 binds an array's Reverse(); C# 14 would otherwise take the in-place span one.</summary>
            public IEnumerable<T> Reverse() => Enumerable.Reverse(array);
        }

        extension<T>(Span<T> span)
            where T : IEquatable<T>
        {
            public int IndexOfAnyExcept(T value) => ((ReadOnlySpan<T>)span).IndexOfAnyExcept(value);
        }

        extension<T>(ReadOnlySpan<T> span)
            where T : IEquatable<T>
        {
            public int IndexOfAnyExcept(T value)
            {
                for (int i = 0; i < span.Length; i++)
                {
                    if (!span[i].Equals(value))
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        extension(Stream stream)
        {
            public void ReadExactly(Span<byte> buffer)
            {
                while (buffer.Length > 0)
                {
                    int read = stream.Read(buffer);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    buffer = buffer.Slice(read);
                }
            }

            public void ReadExactly(byte[] buffer, int offset, int count) => stream.ReadExactly(buffer.AsSpan(offset, count));

            public int ReadAtLeast(Span<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true)
            {
                int total = 0;
                while (total < minimumBytes)
                {
                    int read = stream.Read(buffer.Slice(total));
                    if (read == 0)
                    {
                        if (throwOnEndOfStream)
                        {
                            throw new EndOfStreamException();
                        }

                        break;
                    }

                    total += read;
                }

                return total;
            }

            public async System.Threading.Tasks.ValueTask ReadExactlyAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            {
                while (buffer.Length > 0)
                {
                    int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    buffer = buffer.Slice(read);
                }
            }
        }
    }
}
