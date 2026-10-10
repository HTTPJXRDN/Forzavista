namespace ForzavistaFreeRoam;

// Manual actions have no backlog. One press may take over after the current
// animation frame; further presses and background frames are rejected while
// that handoff is pending. Cleanup may wait for the current owner.
internal sealed class NonQueuedWorkGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private TaskCompletionSource? _backgroundFinished;
    private bool _manualWaiting;
    internal bool IsManualWaiting { get { lock (_state) return _manualWaiting; } }
    internal bool IsBusy { get { lock (_state) return _manualWaiting || _gate.CurrentCount == 0; } }
    internal bool IsManualBusy { get { lock (_state) return _manualWaiting ||
        (_gate.CurrentCount == 0 && _backgroundFinished is null); } }
    internal bool TryEnter()
    {
        lock (_state) return !_manualWaiting && _gate.Wait(0);
    }
    internal bool TryEnterBackground()
    {
        lock (_state)
        {
            if (_manualWaiting || !_gate.Wait(0)) return false;
            _backgroundFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }
    internal async Task<bool> TryEnterManualAsync()
    {
        Task frame;
        lock (_state)
        {
            if (_manualWaiting) return false;
            if (_gate.Wait(0)) return true;
            if (_backgroundFinished is null) return false;
            _manualWaiting = true;
            frame = _backgroundFinished.Task;
        }
        await frame;
        lock (_state)
        {
            _manualWaiting = false;
            return _gate.Wait(0);
        }
    }
    internal Task EnterForCleanupAsync() => _gate.WaitAsync();
    internal void Release()
    {
        TaskCompletionSource? finished;
        lock (_state)
        {
            finished = _backgroundFinished;
            _backgroundFinished = null;
            _gate.Release();
        }
        finished?.SetResult();
    }
}
