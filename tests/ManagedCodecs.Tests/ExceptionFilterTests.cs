using System.Text.RegularExpressions;

namespace ManagedCodecs.Tests;

/// <summary>
/// No exception filter (<c>catch ... when (...)</c>) in the libraries. Compiled ahead of time to WebAssembly (.NET 10's
/// Mono), a method whose filter had caught an exception read its arguments wrong afterwards (a string as empty, an int
/// as 1): <c>Demuxer.Open</c>'s filter then disposed a garbage stream and an MP4 fuzz finding brought the runtime down
/// (a Mono assertion); H.264's <c>when (ConcealErrors)</c> reads its <c>current</c> picture after the catch too. A plain
/// <see langword="catch"/> that rethrows what it doesn't handle keeps them (tools/ManagedCodecs.Wasm/CLAUDE.md;
/// tools/wasm-check.sh replays the fuzz findings there).
/// </summary>
public class ExceptionFilterTests
{
    private static readonly Regex Filter = new(@"\bcatch\b\s*(\([^)]*\))?\s*when\s*\(", RegexOptions.CultureInvariant);

    [Fact]
    public void LibrariesHaveNone()
    {
        string root = RepositoryRoot();
        var filters = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => Filter.IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.index + 1}")
            .ToList();
        Assert.True(filters.Count == 0, "Exception filters (catch ... when) in the libraries: " + string.Join(", ", filters));
    }

    [Theory]
    [InlineData("catch (Exception e) when (e is InvalidDataException)", true)]
    [InlineData("catch when (!leaveOpen)", true)]
    [InlineData("} catch (InvalidDataException) when (ConcealErrors)", true)]
    [InlineData("catch (Exception e)", false)]
    [InlineData("// when the catch below runs", false)]
    public void TheCheckSeesThem(string line, bool filter) => Assert.Equal(filter, Filter.IsMatch(line));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ManagedCodecs.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (ManagedCodecs.slnx) was not found above the test assembly.");
    }
}
