using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Driver;

namespace WpfApp.ViewModels;

// Observable adapter sitting between a HidStreamListener (background thread)
// and the WPF UI (dispatcher thread).
//
// - Subscribes to HidStreamListener.DepthChanged.
// - Marshals each event onto the UI thread via Dispatcher.BeginInvoke at
//   DataBind priority — keeps the canvas responsive even when the user is
//   mashing the keyboard.
// - Exposes SlotDepths keyed by firmware slot index. Values are in mm
//   (0.0 .. ~4.0). Slots not yet seen simply aren't in the dictionary.
// - Fires PropertyChanged on LastUpdate every time a depth event lands —
//   that single nudge is what the UI loop watches; the dictionary itself is
//   not WPF-observable, but in practice callers iterate via TryGet on the
//   slot indices they care about (one per KeyCap) after each nudge.
// - Idle decay: a DispatcherTimer ticks every 60ms; any slot whose last
//   update was more than IdleDecayMs ago gets reset to 0 and a synthetic
//   "depth 0" event is fired. Without this, released keys would visually
//   stick because the firmware only sends value-change events, not a
//   continuous stream.
public sealed class KeyDepthBroker : INotifyPropertyChanged, IDisposable
{
    // After this many ms without an update, a slot is considered released
    // and decays to 0. The driver's tracking packets fire on change at
    // ~50ms cadence; 150ms gives us ~3 missed packets before we give up.
    private const int IdleDecayMs = 150;

    private readonly HidStreamListener _listener;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<int, double> _depths = new();
    private readonly ConcurrentDictionary<int, long> _lastUpdate = new();
    private readonly DispatcherTimer _decayTimer;
    private bool _disposed;
    private long _lastUpdateTicks;

    public KeyDepthBroker(HidStreamListener listener, Dispatcher? dispatcher = null)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _dispatcher = dispatcher
            ?? System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        _listener.DepthChanged += OnDepthChanged;

        _decayTimer = new DispatcherTimer(DispatcherPriority.DataBind, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _decayTimer.Tick += OnDecayTick;
        _decayTimer.Start();
    }

    public IReadOnlyDictionary<int, double> SlotDepths => _depths;

    public DateTime LastUpdate { get; private set; }

    // Fired on the dispatcher thread once per applied event (including decay
    // zeros). Lets the canvas push the new depth into the right KeyCap.
    public event EventHandler<KeyDepthEventArgs>? DepthApplied;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decayTimer.Stop();
        _listener.DepthChanged -= OnDepthChanged;
    }

    // ---- internals --------------------------------------------------------

    private void OnDepthChanged(object? sender, KeyDepthEventArgs e)
    {
        if (_disposed) return;
        // Marshal to UI thread. BeginInvoke (not Invoke) so the listener loop
        // never blocks waiting on UI work.
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                new Action<KeyDepthEventArgs>(ApplyOnUiThread), e);
        }
        catch (System.Threading.Tasks.TaskCanceledException) { }
        catch (InvalidOperationException) { /* dispatcher shutting down */ }
    }

    private void ApplyOnUiThread(KeyDepthEventArgs e)
    {
        if (_disposed) return;
        _depths[e.SlotIndex] = e.DepthMm;
        _lastUpdate[e.SlotIndex] = Environment.TickCount64;
        BumpLastUpdate();
        DepthApplied?.Invoke(this, e);
    }

    private void OnDecayTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        long now = Environment.TickCount64;
        // Snapshot keys — we may mutate during iteration.
        foreach (var kvp in _lastUpdate)
        {
            int slot = kvp.Key;
            long last = kvp.Value;
            if (now - last < IdleDecayMs) continue;

            // Already decayed to zero? Stop touching it so we don't fire
            // a zero event every tick.
            if (_depths.TryGetValue(slot, out var current) && current <= 0.0)
            {
                _lastUpdate.TryRemove(slot, out _);
                continue;
            }

            _depths[slot] = 0.0;
            _lastUpdate[slot] = now; // park it; next real event resets timestamp
            DepthApplied?.Invoke(this, new KeyDepthEventArgs(slot, 0.0));
        }
        if (_lastUpdateTicks != 0)
        {
            // Also nudge the LastUpdate signal so polling consumers can tell
            // something happened (decay events arrive only via DepthApplied,
            // but the property is here for completeness).
            BumpLastUpdate();
        }
    }

    private void BumpLastUpdate()
    {
        LastUpdate = DateTime.UtcNow;
        _lastUpdateTicks = Environment.TickCount64;
        OnPropertyChanged(nameof(LastUpdate));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
