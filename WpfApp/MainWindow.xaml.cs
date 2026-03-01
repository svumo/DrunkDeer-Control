using Driver;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WpfApp.Components;
using WpfApp.Extensions;
using WpfApp.Hooks;
using WpfApp.Profile;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace WpfApp
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public static bool ShouldStartMinimized { get; set; } = false;
        private readonly Dictionary<int, KeyHandler> handlers = [];
        public ProfileManager ProfileManager { get; }
        private readonly WinEventHook WinEventHook;
        private readonly KeyboardManager KeyboardManager;
        private ProcessSelector? processSelectorWindow;

        private ProfileItem? selectedProfile;
        public ProfileItem? SelectedProfile
        {
            get => selectedProfile;
            set
            {
                if (selectedProfile != value)
                {
                    selectedProfile = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfile)));
                    UpdateDetailPanel();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainWindow(ProfileManager profileManager, WinEventHook winEventHook, TrayIcon icon, KeyboardManager keyboardManager)
        {
            WinEventHook = winEventHook;
            ProfileManager = profileManager;
            KeyboardManager = keyboardManager;
            InitializeComponent();
            icon.DoubleClick = () => Restore();
            icon.AppShouldClose = () => Close();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DiscoverProfiles();
            RegisterKeyHandler();

            StartOnWindowsStartupToggle.IsChecked = StartupShortcutHelper.StartupFileExists();
            StartOnWindowsStartupToggle.Click += OnCheckChanged;

            WinEventHook.WinEventHookHandler += OnWinEventHook;

            KeyboardManager.ConnectedKeyboardChanged += OnConnectedKeyboardChanged;
            OnConnectedKeyboardChanged(KeyboardManager.KeyboardWithSpecs);
        }

        private void OnConnectedKeyboardChanged(KeyboardWithSpecs? keyboardWithSpecs)
        {
            if (keyboardWithSpecs is { } kws)
            {
                KeyboardStatusText.Text = $"{kws.Keyboard.GetFriendlyName()} - Firmware v{kws.Specs.FirmwareVersion}";
                KeyboardStatusIcon.Opacity = 1.0;
                StatusFirmware.Text = $"Firmware v{kws.Specs.FirmwareVersion}";
            }
            else
            {
                KeyboardStatusText.Text = "No keyboard connected";
                KeyboardStatusIcon.Opacity = 0.5;
                StatusFirmware.Text = "";
            }
        }

        private void ProfileChanged(int index, ProfileItem item)
        {
            StatusCurrentProfile.Text = $"Active: {item.Name}";
            ProfileManager.PushCurrentProfile();
            UpdateActiveIndicators();
        }

        private void ProfilesChanged(ProfileItem[] _)
        {
            // Select the first profile if none selected
            if (SelectedProfile is null && ProfileManager.Profiles.Count > 0)
            {
                SelectedProfile = ProfileManager.Profiles[0];
            }
        }

        private void DiscoverProfiles()
        {
            ProfileManager.CurrentProfileChanged += ProfileChanged;
            ProfileManager.ProfileCollectionChanged += ProfilesChanged;
            ProfileManager.DiscoverProfiles();

            // Auto-select first profile if available
            if (ProfileManager.Profiles.Count > 0)
            {
                SelectedProfile = ProfileManager.Profiles[0];
            }
        }

        private void UpdateActiveIndicators()
        {
            // Force the ListBox to re-evaluate - the active indicator in the DataTemplate
            // needs to reflect which profile is currently active on the keyboard
            ProfileListBox.Items.Refresh();
        }

        private void UpdateDetailPanel()
        {
            if (SelectedProfile is null) return;

            // Update actuation range display
            var keys = SelectedProfile.Profile.Keys_Array;
            if (keys.Length > 0)
            {
                var minAp = keys.Min(k => k.Action_Point);
                var maxAp = keys.Max(k => k.Action_Point);
                ActuationRangeText.Text = minAp == maxAp
                    ? $"Actuation: {minAp:F1}mm"
                    : $"Actuation: {minAp:F1}mm - {maxAp:F1}mm";
            }
            else
            {
                ActuationRangeText.Text = "No key data";
            }

            // Update RTP status
            RtpStatusText.Text = SelectedProfile.Profile.RTP is not null
                ? "Rapid Trigger: Enabled"
                : "Rapid Trigger: Disabled";

            // Update remap status
            RemapStatusText.Text = SelectedProfile.RemapProfile is { } remap
                ? $"Remap: {remap.Showname}"
                : "No remap profile";
        }

        private void RegisterKeyHandler()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(windowHandle);

            var enterHandler = new KeyHandler(KeyHandler.ToKeycode(Key.Enter), windowHandle, source, KeyHandler.MOD_CONTROL | KeyHandler.MOD_ALT | KeyHandler.MOD_NOREPEAT)
            {
                Callback = ProfileManager.QuickSwitchProfile,
            };
            handlers[enterHandler.GetHashCode()] = enterHandler;
            foreach (var handler in handlers.Values)
            {
                handler.Register();
            }
        }

        private void OnWinEventHook(object? sender, WinEventHookEventArgs e)
        {
            var path = e.Process.GetPathFromProcessId();
            var profileToSwitchTo = ProfileManager.Profiles.FirstOrDefault(p => p.ProcessTriggers.Any(pt => pt.Equals(path, StringComparison.OrdinalIgnoreCase)));
            if (profileToSwitchTo is { } profile)
            {
                ProfileManager.SwitchTo(profile);
            }
            else if (ProfileManager.Profiles.FirstOrDefault(p => p.IsDefault) is { } defaultProfile)
            {
                ProfileManager.SwitchTo(defaultProfile);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var handler in handlers.Values)
            {
                handler.Unregiser();
            }
            processSelectorWindow?.Close();
            base.OnClosed(e);
        }

        // ===== Event Handlers =====

        protected void OnImportButtonClicked(object sender, EventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "Text documents (.json)|*.json",
                Multiselect = true,
            };

            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                foreach (var path in dialog.FileNames)
                {
                    ProfileManager.ImportProfile(path);
                }
            }
        }

        protected void OnImportRemapButtonClicked(object sender, EventArgs e)
        {
            var profileItem = SelectedProfile;
            if (profileItem is null) return;

            var dialog = new OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "Text documents (.json)|*.json",
            };

            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                ProfileManager.ImportAndLinkRemaps(profileItem, dialog.FileName);
                UpdateDetailPanel();
            }
        }

        private void OnActivateProfileClicked(object sender, RoutedEventArgs e)
        {
            if (SelectedProfile is { } profileItem)
            {
                ProfileManager.SwitchTo(profileItem);
            }
        }

        private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
        {
            if (SelectedProfile is { } profileItem)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Are you sure you want to delete profile '{profileItem.Name}'?",
                    "Delete Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    ProfileManager.RemoveProfileItems(profileItem);
                    SelectedProfile = ProfileManager.Profiles.FirstOrDefault();
                }
            }
        }

        private void OnContextMenuDeleteClicked(object sender, RoutedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem profileItem)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Are you sure you want to delete profile '{profileItem.Name}'?",
                    "Delete Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    ProfileManager.RemoveProfileItems(profileItem);
                    SelectedProfile = ProfileManager.Profiles.FirstOrDefault();
                }
            }
        }

        private void OnAddProcessTriggerClicked(object sender, RoutedEventArgs e)
        {
            if (SelectedProfile is { } profileItem)
            {
                processSelectorWindow?.Close();
                processSelectorWindow = null;
                processSelectorWindow = new ProcessSelector(profileItem);
                processSelectorWindow.Owner = this;
                processSelectorWindow.SetStoredProcesses(profileItem.ProcessTriggers);
                processSelectorWindow.StoredProcesses.CollectionChanged += HandleStoredProcessesCollectionChanged;
                processSelectorWindow.ShowDialog();
                // Refresh triggers display after dialog closes
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfile)));
            }
        }

        private void OnRemoveTriggerClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string triggerPath && SelectedProfile is { } profileItem)
            {
                profileItem.ProcessTriggers = profileItem.ProcessTriggers.Where(t => t != triggerPath).ToArray();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfile)));
            }
        }

        private void HandleStoredProcessesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (processSelectorWindow is { } window)
            {
                window.ProfileItem.ProcessTriggers = window.StoredProcesses.Select(pr => pr.ProcessPath).ToArray();
            }
        }

        // ===== Window Lifecycle =====

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (ShouldStartMinimized)
            {
                WindowState = WindowState.Minimized;
            }
        }

        public void Restore()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState is WindowState.Minimized)
            {
                WindowStyle = WindowStyle.ToolWindow;
                ShowInTaskbar = false;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ShowInTaskbar = true;
            }
        }

        private void OnCheckChanged(object? sender, EventArgs e)
        {
            StartupShortcutHelper.OnCheckChanged(StartOnWindowsStartupToggle.IsChecked ?? false);
        }
    }
}
