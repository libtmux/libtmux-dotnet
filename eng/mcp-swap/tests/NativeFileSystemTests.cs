using System.Text;

namespace LibTmux.McpSwap.Tests;

public sealed class NativeFileSystemTests
{
    [Fact]
    public void PublishNeverOverwritesALateDestination()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        string source = Write(paths.Root, "source", "owned");
        string destination = Write(paths.Root, "destination", "human");
        FileSnapshot expected = NativeFileSystem.ReadStable(source, 100);

        Assert.Throws<IOException>(
            () => NativeFileSystem.PublishNoReplace(source, destination, expected));

        Assert.Equal("owned", File.ReadAllText(source));
        Assert.Equal("human", File.ReadAllText(destination));
    }

    [Fact]
    public void PublishRetainsASubstitutedSource()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        string source = Write(paths.Root, "source", "owned");
        string destination = Path.Combine(paths.Root, "destination");
        FileSnapshot expected = NativeFileSystem.ReadStable(source, 100);
        File.Delete(source);
        File.WriteAllText(source, "human");

        Assert.Throws<IOException>(
            () => NativeFileSystem.PublishNoReplace(source, destination, expected));

        Assert.Equal("human", File.ReadAllText(source));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ExactRemovalRetainsASamePathReplacement()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        string path = Write(paths.Root, "artifact", "owned");
        FileSnapshot expected = NativeFileSystem.ReadStable(path, 100);
        File.Delete(path);
        File.WriteAllText(path, "human");

        Assert.Throws<IOException>(() => NativeFileSystem.RemoveExact(path, expected));

        Assert.Equal("human", File.ReadAllText(path));
    }

    [Fact]
    public void DirectoryBindingDetectsASymlinkRetargetEvenToTheSameDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestPaths paths = new();
        string target = Path.Combine(paths.Root, "target");
        string link = Path.Combine(paths.Root, "link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        DirectoryBinding before = NativeFileSystem.CaptureDirectory(link);
        File.Delete(link);
        Directory.CreateSymbolicLink(link, target);
        DirectoryBinding after = NativeFileSystem.CaptureDirectory(link);

        Assert.False(NativeFileSystem.SameDirectory(before, after));
    }

    [Fact]
    public void StableReadRejectsOversizedInputsBeforeAllocation()
    {
        using TestPaths paths = new();
        string path = Write(paths.Root, "large", new string('x', 101));

        IOException failure = Assert.Throws<IOException>(
            () => NativeFileSystem.ReadStable(path, 100));

        Assert.Contains("exceeds 100", failure.Message, StringComparison.Ordinal);
    }

    private static string Write(string root, string name, string content)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }
}
