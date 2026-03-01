using Driver;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WpfApp.Components;
using WpfApp.Extensions;
using WpfApp.Hooks;
using WpfApp.Profile;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace WpfApp
{
    public partial class MainWindow : Window
    {
        public static bool ShouldStartMinimized { get; set; } = false;
        private readonly Dictionary<int, KeyHandler> handlers = [];
        private readonly ProfileManager ProfileManager;
        private readonly WinEventHook WinEventHook;
        private readonly KeyboardManager KeyboardManager;
        private ProcessSelector? processSelectorWindow;
        private ProfileItem? selectedProfile;
        private bool suppressToggleEvents;

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

        // ===== Profile Selection =====

        private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem item)
            {
                selectedProfile = item;
                EmptyState.Visibility = Visibility.Collapsed;
                DetailScrollViewer.Visibility = Visibility.Visible;
                UpdateDetailPanel();
            }
            else
            {
                selectedProfile = null;
                EmptyState.Visibility = Visibility.Visible;
                DetailScrollViewer.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateDetailPanel()
        {
            if (selectedProfile is null) return;

            // Header
            DetailProfileName.Text = selectedProfile.Name;
            DetailProfileSubtitle.Text = selectedProfile.Profile.Showname;

            // Profile name textbox
            ProfileNameTextBox.Text = selectedProfile.Name;

            // Actuation range
            var keys = selectedProfile.Profile.Keys_Array;
            if (keys.Length > 0)
            {
                var minAp = keys.Min(k => k.Action_Point);
                var maxAp = keys.Max(k => k.Action_Point);
                ActuationRangeText.Text = minAp == maxAp
                    ? $"{minAp:F1}mm"
                    : $"{minAp:F1}mm - {maxAp:F1}mm";
            }
            else
            {
                ActuationRangeText.Text = "No key data";
            }

            // RTP
            RtpStatusText.Text = selectedProfile.Profile.RTP is not null ? "Enabled" : "Disabled";

            // Remap
            RemapStatusText.Text = selectedProfile.RemapProfile is { } remap
                ? remap.Showname
                : "None";

            // Toggles - suppress events while setting programmatically
            suppressToggleEvents = true;
            QuickSwitchToggle.IsChecked = selectedProfile.SelectedForQuickSwitch;
            DefaultProfileToggle.IsChecked = selectedProfile.IsDefault;
            suppressToggleEvents = false;

            // Process triggers
            ProcessTriggersPanel.ItemsSource = selectedProfile.ProcessTriggers;
        }

        // ===== Connection Status =====

        private void OnConnectedKeyboardChanged(KeyboardWithSpecs? keyboardWithSpecs)
        {
            if (keyboardWithSpecs is { } kws)
            {
                KeyboardStatusText.Text = $"{kws.Keyboard.GetFriendlyName()}  v{kws.Specs.FirmwareVersion}";
                ConnectionDot.Fill = new SolidColorBrush((System.Windows.Media.Color)FindResource("SuccessColor"));
                StatusFirmware.Text = $"Firmware v{kws.Specs.FirmwareVersion}";
                FirmwareSeparator.Visibility = Visibility.Visible;
            }
            else
            {
                KeyboardStatusText.Text = "No keyboard";
                ConnectionDot.Fill = new SolidColorBrush((System.Windows.Media.Color)FindResource("TextTertiaryColor"));
                StatusFirmware.Text = "";
                FirmwareSeparator.Visibility = Visibility.Collapsed;
            }
        }

        // ===== Profile Lifecycle =====

        private void ProfileChanged(int index, ProfileItem item)
        {
            StatusCurrentProfile.Text = $"Active: {item.Name}";
            ProfileManager.PushCurrentProfile();
        }

        private void ProfilesChanged(ProfileItem[] _)
        {
            ProfileListBox.ItemsSource = ProfileManager.Profiles;
            if (selectedProfile is null && ProfileManager.Profiles.Count > 0)
            {
                ProfileListBox.SelectedIndex = 0;
            }
        }

        private void DiscoverProfiles()
        {
            ProfileManager.CurrentProfileChanged += ProfileChanged;
            ProfileManager.ProfileCollectionChanged += ProfilesChanged;
            ProfileListBox.ItemsSource = ProfileManager.Profiles;
            ProfileManager.DiscoverProfiles();

            if (ProfileManager.Profiles.Count > 0)
            {
                ProfileListBox.SelectedIndex = 0;
            }
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

            if (dialog.ShowDialog() == true)
            {
                foreach (var path in dialog.FileNames)
                {
                    ProfileManager.ImportProfile(path);
                }
            }
        }

        protected void OnImportRemapButtonClicked(object sender, EventArgs e)
        {
            if (selectedProfile is null) return;

            var dialog = new OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "Text documents (.json)|*.json",
            };

            if (dialog.ShowDialog() == true)
            {
                ProfileManager.ImportAndLinkRemaps(selectedProfile, dialog.FileName);
                UpdateDetailPanel();
            }
        }

        private void OnActivateProfileClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item)
            {
                ProfileManager.SwitchTo(item);
            }
        }

        private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Are you sure you want to delete '{item.Name}'?",
                    "Delete Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    ProfileManager.RemoveProfileItems(item);
                    selectedProfile = null;
                    if (ProfileManager.Profiles.Count > 0)
                        ProfileListBox.SelectedIndex = 0;
                    else
                    {
                        EmptyState.Visibility = Visibility.Visible;
                        DetailScrollViewer.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private void OnContextMenuDeleteClicked(object sender, RoutedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem item)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Are you sure you want to delete '{item.Name}'?",
                    "Delete Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    ProfileManager.RemoveProfileItems(item);
                    if (ProfileManager.Profiles.Count > 0)
                        ProfileListBox.SelectedIndex = 0;
                }
            }
        }

        private void OnQuickSwitchChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents || selectedProfile is null) return;
            selectedProfile.SelectedForQuickSwitch = QuickSwitchToggle.IsChecked == true;
        }

        private void OnDefaultProfileChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents || selectedProfile is null) return;
            selectedProfile.IsDefault = DefaultProfileToggle.IsChecked == true;
        }

        private void OnProfileNameChanged(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is null) return;
            var newName = ProfileNameTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != selectedProfile.Name)
            {
                selectedProfile.Name = newName;
                DetailProfileName.Text = newName;
                ProfileListBox.Items.Refresh();
            }
        }

        private void OnAddProcessTriggerClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item)
            {
                processSelectorWindow?.Close();
                processSelectorWindow = new ProcessSelector(item);
                processSelectorWindow.Owner = this;
                processSelectorWindow.SetStoredProcesses(item.ProcessTriggers);
                processSelectorWindow.StoredProcesses.CollectionChanged += HandleStoredProcessesCollectionChanged;
                processSelectorWindow.ShowDialog();
                UpdateDetailPanel();
            }
        }

        private void OnRemoveTriggerClicked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string triggerPath && selectedProfile is { } item)
            {
                item.ProcessTriggers = item.ProcessTriggers.Where(t => t != triggerPath).ToArray();
                UpdateDetailPanel();
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
