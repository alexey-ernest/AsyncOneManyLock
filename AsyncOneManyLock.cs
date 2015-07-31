public enum OneManyMode
{
    Exclusive,
    Shared
}

/// <summary>
///     Non-blocking synchronization construct with reader-writer semantics from Jeffrey Richter.
/// </summary>
public sealed class AsyncOneManyLock
{
    #region Lock code

    private SpinLock _lock = new SpinLock(true); // Don't use readonly with a SpinLock

    private void Lock()
    {
        Boolean taken = false;
        _lock.Enter(ref taken);
    }

    private void Unlock()
    {
        _lock.Exit();
    }

    #endregion

    #region Lock state and helper methods

    private Int32 _state;

    private Boolean IsFree
    {
        get { return _state == 0; }
    }

    private Boolean IsOwnedByWriter
    {
        get { return _state == -1; }
    }

    private Boolean IsOwnedByReaders
    {
        get { return _state > 0; }
    }

    private void AddReaders(Int32 count)
    {
        _state += count;
    }

    private void SubtractReader()
    {
        --_state;
    }

    private void MakeWriter()
    {
        _state = -1;
    }

    private void MakeFree()
    {
        _state = 0;
    }

    #endregion

    // For the no-contention case to improve performance and reduce memory consumption
    private readonly Task _noContentionAccessGranter;

    // Each waiting writers wakes up via their own TaskCompletionSource queued here
    private readonly Queue<TaskCompletionSource<Object>> _qWaitingWriters =
        new Queue<TaskCompletionSource<Object>>();

    // All waiting readers wake up by signaling a single TaskCompletionSource
    private Int32 _numWaitingReaders;
    private TaskCompletionSource<Object> _waitingReadersSignal = new TaskCompletionSource<Object>();

    public AsyncOneManyLock()
    {
        _noContentionAccessGranter = Task.FromResult<Object>(null);
    }

    public Task WaitAsync(OneManyMode mode)
    {
        Task accressGranter = _noContentionAccessGranter; // Assume no contention
        Lock();

        switch (mode)
        {
            case OneManyMode.Exclusive:
                if (IsFree)
                {
                    MakeWriter(); // No contention
                }
                else
                {
                    // Contention: Queue new writer task & return it so writer waits
                    var tcs = new TaskCompletionSource<Object>();
                    _qWaitingWriters.Enqueue(tcs);
                    accressGranter = tcs.Task;
                }
                break;
            case OneManyMode.Shared:
                if (IsFree || (IsOwnedByReaders && _qWaitingWriters.Count == 0))
                {
                    AddReaders(1); // No contention
                }
                else
                {
                    // Contention: Increment waiting readers & return reader task so reader waits
                    _numWaitingReaders++;
                    accressGranter = _waitingReadersSignal.Task.ContinueWith(t => t.Result);
                }
                break;
        }

        Unlock();
        return accressGranter;
    }

    public void Release()
    {
        TaskCompletionSource<Object> accessGranter = null; // Assume no code is released
        Lock();

        if (IsOwnedByWriter)
        {
            // The writer left
            MakeFree();
        }
        else
        {
            // A reader left
            SubtractReader();
        }

        if (IsFree)
        {
            // If free, wake 1 waiting writer or all waiting readers
            if (_qWaitingWriters.Count > 0)
            {
                MakeWriter();
                accessGranter = _qWaitingWriters.Dequeue();
            }
            else if (_numWaitingReaders > 0)
            {
                AddReaders(_numWaitingReaders);
                _numWaitingReaders = 0;
                accessGranter = _waitingReadersSignal;

                // Create a new TCS for future readers that need to wait
                _waitingReadersSignal = new TaskCompletionSource<Object>();
            }
        }

        Unlock();
            
        // Wake the writer/reader outside the lock to reduce
        // chance of contention improving performance
        if (accessGranter != null)
        {
            accessGranter.SetResult(null);
        }
    }
}
