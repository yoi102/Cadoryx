using System.Globalization;

namespace Cadoryx.Kernel.Occt;

internal static class NativeWorkerStartedMarker
{
    internal static void Publish(string directory)
    {
        var destination = Path.Combine(directory, "native-started.txt");
        if (File.Exists(destination)) return;
        var temporary = Path.Combine(directory, ".native-started-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Observers may read as soon as the final name exists. Publish only
            // after the complete PID file has been closed, and never rewrite it.
            File.WriteAllText(temporary, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
