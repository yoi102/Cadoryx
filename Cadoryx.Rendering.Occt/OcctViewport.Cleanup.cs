using System.Runtime.ExceptionServices;

namespace Cadoryx.Rendering.Occt;

public sealed partial class OcctViewport
{
    private const string CleanupFailureDataKey = "Cadoryx.Rendering.CleanupFailures";

    private static void RecordCleanupFailure(ref Exception? failure, Exception next)
    {
        if (failure is null) { failure = next; return; }
        if (ReferenceEquals(failure, next)) return;
        if (failure.Data[CleanupFailureDataKey] is not List<Exception> additional)
        { additional = []; failure.Data[CleanupFailureDataKey] = additional; }
        additional.Add(next);
    }

    private static void DisposeOwnedResource(IDisposable? resource, ref Exception? failure)
    {
        try { resource?.Dispose(); }
        catch (Exception error) { RecordCleanupFailure(ref failure, error); }
    }

    private static void RunCleanup(Action action, ref Exception? failure)
    {
        try { action(); }
        catch (Exception error) { RecordCleanupFailure(ref failure, error); }
    }

    private static void ThrowCleanupFailure(Exception? failure)
    {
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void AbortAfterFailure(Exception failure)
    {
        PreserveGraphicsFailure(failure);
        Exception? retained = failure;
        try { AbortProgressiveScene(); }
        catch (Exception cleanup) { RecordCleanupFailure(ref retained, cleanup); }
    }
}
