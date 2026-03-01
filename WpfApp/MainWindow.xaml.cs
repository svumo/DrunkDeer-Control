using Driver;
using System.Collections.Specialized;
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

            // Enable acrylic glass blur effect (tint: AABBGGRR)
            AcrylicHelper.EnableAcrylic(this, 0xB0281A1A);

            DiscoverProfiles();
            RegisterKeyHandler();

            StartOnWindowsStartupToggle.IsChecked = StartupShortcutHelper.StartupFileExists();
            StartOnWindowsStartupToggle.Click += OnCheckChanged;

            WinEventHook.WinEventHookHandler += OnWinEventHook;

            KeyboardManager.ConnectedKeyboardChanged += OnConnectedKeyboardChanged;
            OnConnectedKeyboardChanged(KeyboardManager.KeyboardWithSpecs);
        }

        // ===== Custom Title Bar =====

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ===== Profile Selection (BUG FIX: use e.AddedItems instead of SelectedItem) =====

        private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ProfileItem item)
            {
                selectedProfile = item;
                EmptyState.Visibility = Visibility.Collapsed;
                DetailScrollViewer.Visibility = Visibility.Visible;
                UpdateDetailPanel();
            }
            else if (ProfileListBox.SelectedItem is null)
            {
                selectedProfile = null;
                EmptyState.Visibility = Visibility.Visible;
                DetailScrollViewer.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateDetailPanel()
        {
            if (selectedProfile is null) return;

            DetailProfileName.Text = selectedProfile.Name;
            DetailProfileSubtitle.Text = selectedProfile.Profile.Showname;
            ProfileNameTextBox.Text = selectedProfile.Name;

            var keys = selectedProfile.Profile.Keys_Array;
            if (keys.Length > 0)
            {
                var minAp = keys.Min(k => k.Action_Point);
                var maxAp = keys.Max(k => k.Action_Point);
                ActuationRangeText.Text = minAp == maxAp
                    ? $"{minAp:F1}mm"
                    : $"{minAp:F1}mm — {maxAp:F1}mm";
            }
            else
            {
                ActuationRangeText.Text = "No key data";
            }

            RtpStatusText.Text = selectedProfile.Profile.RTP is not null ? "Enabled" : "Disabled";

            RemapStatusText.Text = selectedProfile.RemapProfile is { } remap
                ? remap.Showname
                : "None";

            suppressToggleEvents = true;
            QuickSwitchToggle.IsChecked = selectedProfile.SelectedForQuickSwitch;
            DefaultProfileToggle.IsChecked = selectedProfile.IsDefault;
            suppressToggleEvents = false;

            // Force refresh of triggers ItemsControl
            ProcessTriggersPanel.ItemsSource = null;
            ProcessTriggersPanel.ItemsSource = selectedProfile.ProcessTriggers;
        }

        // ===== Connection Status =====

        private void OnConnectedKeyboardChanged(KeyboardWithSpecs? keyboardWithSpecs)
        {
            if (keyboardWithSpecs is { } kws)
            {
                KeyboardStatusText.Text = $"{kws.Keyboard.GetFriendlyName()}  v{kws.Specs.FirmwareVersion}";
                ConnectionDot.Fill = (SolidColorBrush)FindResource("GreenDot");
                StatusFirmware.Text = $"v{kws.Specs.FirmwareVersion}";
            }
            else
            {
                KeyboardStatusText.Text = "No keyboard";
                ConnectionDot.Fill = (SolidColorBrush)FindResource("GrayDot");
                StatusFirmware.Text = "";
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
            ProfileListBox.ItemsSource = null;
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
                Filter = "JSON files (.json)|*.json",
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
                Filter = "JSON files (.json)|*.json",
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
                ProfileManager.SwitchTo(item);
        }

        private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Delete '{item.Name}'?",
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
                    $"Delete '{item.Name}'?",
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
                ShowInTaskbar = false;
            }
            else
            {
                ShowInTaskbar = true;
                // Adjust corner radius for maximized state
                OuterBorder.CornerRadius = WindowState == WindowState.Maximized
                    ? new CornerRadius(0)
                    : new CornerRadius(12);
            }
        }

        private void OnCheckChanged(object? sender, EventArgs e)
        {
            StartupShortcutHelper.OnCheckChanged(StartOnWindowsStartupToggle.IsChecked ?? false);
        }
    }
}
