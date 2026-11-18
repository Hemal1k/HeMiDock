using Microsoft.UI.Dispatching;

namespace HeMiDock;

/// <summary>
/// A timer of the thread of the interface for the short ones made again and again (following the mouse after every press,
/// the coming-in of every pop-up). Each DispatcherQueueTimer holds a timer of Windows until the garbage collector frees it –
/// rarely, as the app makes little garbage – so every click left one more behind (some hundreds of handles a day). This one
/// takes a timer from a small store when it starts and gives it back when it stops (or fired once).
/// </summary>
sealed class QueueTimer
{
    [ThreadStatic] static Stack<DispatcherQueueTimer> _store;

    DispatcherQueueTimer _timer;
    TimeSpan _interval = TimeSpan.FromMilliseconds(16);
    bool _repeat = true;

    public event Windows.Foundation.TypedEventHandler<QueueTimer, object> Tick;

    public static QueueTimer Create() => new();

    public TimeSpan Interval
    {
        get => _interval;
        set { _interval = value; if (_timer != null) _timer.Interval = value; }
    }

    public bool IsRepeating
    {
        get => _repeat;
        set { _repeat = value; if (_timer != null) _timer.IsRepeating = value; }
    }

    public bool IsRunning => _timer?.IsRunning == true;

    public void Start()
    {
        if (_timer == null)
        {
            var store = _store ??= new Stack<DispatcherQueueTimer>();
            _timer = store.Count > 0 ? store.Pop() : DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Tick += OnTick;
        }
        _timer.Interval = _interval;
        _timer.IsRepeating = _repeat;
        _timer.Start();
    }

    /// <summary>Stops it; the timer of Windows goes back to the store (Start takes one again).</summary>
    public void Stop()
    {
        if (_timer == null) return;
        var timer = _timer;
        _timer = null;
        timer.Stop();
        timer.Tick -= OnTick;
        (_store ??= new Stack<DispatcherQueueTimer>()).Push(timer);
    }

    void OnTick(DispatcherQueueTimer sender, object e)
    {
        Tick?.Invoke(this, e);
        if (!_repeat && _timer == sender) Stop();                      // fired once: back to the store
    }
}
