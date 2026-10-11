using System.Globalization;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class NativeWorkerStartedMarkerTests
{
    [Fact]
    public void PublishedPidIsCompleteAndRepeatedNotificationDoesNotRewriteAnOpenReader()
    {
        using var f = new Fixture(); NativeWorkerStartedMarker.Publish(f.Directory);
        var expected = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(expected, File.ReadAllText(f.Marker)); var stamp = File.GetLastWriteTimeUtc(f.Marker);
        using var reader = new FileStream(f.Marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        NativeWorkerStartedMarker.Publish(f.Directory);
        using var text = new StreamReader(reader); Assert.Equal(expected, text.ReadToEnd());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(f.Marker)); Assert.Empty(System.IO.Directory.GetFiles(f.Directory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentPublicationAndPollingExposeOnlyTheCompleteCurrentPid()
    {
        using var f = new Fixture(); using var release = new ManualResetEventSlim();
        var expected = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var publishers = Enumerable.Range(0, 32).Select(_ => Task.Run(() => { release.Wait(); NativeWorkerStartedMarker.Publish(f.Directory); })).ToArray();
        var observer = Task.Run(async () =>
        {
            release.Wait();
            while (!Task.WhenAll(publishers).IsCompleted)
            {
                if (File.Exists(f.Marker)) Assert.Equal(expected, await ReadPublishedMarker(f.Marker));
                await Task.Yield();
            }
        });
        release.Set(); await Task.WhenAll(publishers); await observer;
        Assert.Equal(expected, await ReadPublishedMarker(f.Marker)); Assert.Empty(System.IO.Directory.GetFiles(f.Directory, "*.tmp"));
    }

    [Fact]
    public void FailedPublicationLeavesNoFalseMarkerOrScratchFile()
    {
        using var f = new Fixture(); System.IO.Directory.CreateDirectory(f.Marker);
        Assert.Throws<IOException>(() => NativeWorkerStartedMarker.Publish(f.Directory));
        Assert.False(File.Exists(f.Marker)); Assert.Empty(System.IO.Directory.GetFiles(f.Directory, "*.tmp"));
    }

    internal static async Task<string> ReadPublishedMarker(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CadoryxNativeMarkerTests"));
        internal string Directory { get; }
        internal string Marker => Path.Combine(Directory, "native-started.txt");
        internal Fixture() { Directory = Path.Combine(root, Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Directory); }
        public void Dispose()
        {
            Assert.Equal(root, Path.GetDirectoryName(Path.GetFullPath(Directory)));
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
