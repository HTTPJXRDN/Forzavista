namespace ForzavistaFreeRoam;

// Closing a thread handle does not stop the remote thread. If its wait fails or
// times out, retain the small allocation until the game exits rather than free
// code/data that the game may still execute. Setup failures and completed calls
// release normally.
internal sealed class RemoteCallMemoryLease(Action release, Action retained) : IDisposable
{
    private bool started, completed, disposed;
    internal void ThreadStarted() => started = true;
    internal void ThreadCompleted() => completed = true;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!started || completed) release();
        else retained();
    }
}
