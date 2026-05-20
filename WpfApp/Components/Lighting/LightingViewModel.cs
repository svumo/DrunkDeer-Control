using System.ComponentModel;
using System.Runtime.CompilerServices;
using Driver;

namespace WpfApp.Components.Lighting;

// Phase 1 RGB editor state. One per LightingView instance; rehydrated from the
// active ProfileItem's LightingProfile on profile switch. Tracks an in-memory
// "last synced" snapshot so the Sync button can show a dirty dot.
//
// Per-mode control gating (Off/AlwaysOn/Breath only — Marquee/Neon stay locked
// in UI until hardware-verified):
//   Off       → brightness disabled, speed disabled
//   Always On → brightness live,    speed disabled
//   Breath    → brightness live,    speed live
public sealed class LightingViewModel : INotifyPropertyChanged
{
    private byte mode = LightingProfile.ModeOff;
    private byte brightness = 0;
    private byte speed = 5;
    private LightingProfile lastSyncedSnapshot;
    private string statusMessage = string.Empty;

    public LightingViewModel()
    {
        lastSyncedSnapshot = Snapshot();
    }

    public byte Mode
    {
        get => mode;
        set
        {
            if (mode == value) return;
            mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrightnessEnabled));
            OnPropertyChanged(nameof(IsSpeedEnabled));
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    // UI slider Max="9". The setter clamps anyway so a programmatic write
    // can't smuggle past — the firmware brick boundary is at most 10.
    public byte Brightness
    {
        get => brightness;
        set
        {
            byte clamped = value > 9 ? (byte)9 : value;
            if (brightness == clamped) return;
            brightness = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    // Speed slider range is 1..9 — 0 has no UX meaning for an animated mode
    // even though the firmware accepts it. Existing profiles with stored
    // Speed=0 get bumped to 1 on hydration / write.
    public byte Speed
    {
        get => speed;
        set
        {
            byte clamped = value < 1 ? (byte)1 : (value > 9 ? (byte)9 : value);
            if (speed == clamped) return;
            speed = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool IsBrightnessEnabled => mode != LightingProfile.ModeOff;
    public bool IsSpeedEnabled      => mode == LightingProfile.ModeBreath;

    public bool IsDirty =>
        mode       != lastSyncedSnapshot.Mode
        || brightness != lastSyncedSnapshot.Brightness
        || speed      != lastSyncedSnapshot.Speed;

    // Transient inline notice ("No keyboard connected", "Write failed", etc.).
    // Cleared on the next user action.
    public string StatusMessage
    {
        get => statusMessage;
        set
        {
            if (statusMessage == value) return;
            statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(statusMessage);

    public void Hydrate(LightingProfile? lp)
    {
        var src = lp ?? new LightingProfile();
        mode = src.Mode;
        brightness = src.Brightness > 9 ? (byte)9 : src.Brightness;
        // Speed 0 from older sessions → 1 to match the new slider min.
        speed = src.Speed < 1 ? (byte)1 : (src.Speed > 9 ? (byte)9 : src.Speed);
        Driver.DebugLogger.Log($"VM.Hydrate: src=({src.Mode}/{src.Brightness}/{src.Speed}) → cur=({mode}/{brightness}/{speed}) lastSynced=({lastSyncedSnapshot.Mode}/{lastSyncedSnapshot.Brightness}/{lastSyncedSnapshot.Speed}) IsDirty={IsDirty}");
        // Deliberately do NOT touch lastSyncedSnapshot here. It tracks what we
        // last actually pushed to the keyboard (Sync / Lights-off), which is a
        // hardware-side concept that doesn't reset on profile switch — the
        // keyboard keeps showing the last-pushed state regardless of which
        // profile is selected. So after a profile switch where the new
        // profile's saved settings differ from the last pushed state, the
        // Unsynced chip correctly appears.
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(IsBrightnessEnabled));
        OnPropertyChanged(nameof(IsSpeedEnabled));
        OnPropertyChanged(nameof(IsDirty));
    }

    public LightingProfile Snapshot() => new()
    {
        Mode = mode,
        Brightness = brightness,
        Speed = speed,
    };

    public void MarkSynced()
    {
        lastSyncedSnapshot = Snapshot();
        Driver.DebugLogger.Log($"VM.MarkSynced: lastSynced now ({lastSyncedSnapshot.Mode}/{lastSyncedSnapshot.Brightness}/{lastSyncedSnapshot.Speed})");
        OnPropertyChanged(nameof(IsDirty));
    }

    // Called by the Lights-off panic button. The page state (Mode/Brightness/
    // Speed) stays as-is (Lights-off is a transient hardware action, not a
    // profile edit), but the hardware is now in `pushed` state — so the
    // dirty calculation should compare current state against THAT, not the
    // previous sync snapshot.
    public void MarkExternalPush(LightingProfile pushed)
    {
        lastSyncedSnapshot = pushed;
        OnPropertyChanged(nameof(IsDirty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
