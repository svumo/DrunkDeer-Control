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
        private readonly Dictionary<ProfileItem, KeyHandler> directHandlers = [];
        private readonly ProfileManager ProfileManager;
        private readonly WinEventHook WinEventHook;
        private readonly KeyboardManager KeyboardManager;
        private ProcessSelector? processSelectorWindow;
        private ProfileItem? selectedProfile;
        private bool suppressToggleEvents;
        private bool isRecordingDirectKeybind = false;

        public MainWindow(ProfileManager profileManager, WinEventHook winEventHook, TrayIcon icon, KeyboardManager keyboardManager, Settings settings)
        {
            this.settings = settings;
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
            UpdateQuickSwitchLabel();

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

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            OptionsPopup.IsOpen = !OptionsPopup.IsOpen;
        }

        private void OnOpenGitHubClicked(object sender, RoutedEventArgs e)
        {
            OptionsPopup.IsOpen = false;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/svumo/DrunkDeer-Control/issues",
                UseShellExecute = true
            });
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

        private void UpdateActivateButton()
        {
            if (ActivateButton is null) return;
            bool isActive = selectedProfile?.IsActiveProfile == true;
            ActivateButton.Content = isActive ? "Activated" : "Activate";
            ActivateButton.Foreground = isActive
                ? (SolidColorBrush)FindResource("GreenDot")
                : (SolidColorBrush)FindResource("TextW");
        }

        private void UpdateDetailPanel()
        {
            if (selectedProfile is null) return;

            DetailProfileName.Text = selectedProfile.Name;
            DetailProfileSubtitle.Text = selectedProfile.Profile.Showname;

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

            ProfileNoteTextBox.Text = selectedProfile.Note;
            UpdateActivateButton();
            UpdateDirectKeybindLabel();

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

            }
            else
            {
                KeyboardStatusText.Text = "No keyboard";
                ConnectionDot.Fill = (SolidColorBrush)FindResource("GrayDot");

            }
        }

        // ===== Profile Lifecycle =====

        private void ProfileChanged(int index, ProfileItem item)
        {
            foreach (var p in ProfileManager.Profiles)
                p.IsActiveProfile = false;
            item.IsActiveProfile = true;
            ActiveProfileText.Text = "Active: " + item.Name;
            ActiveProfileDot.Fill = (SolidColorBrush)FindResource("GreenDot");
            ProfileListBox.SelectedItem = item;
            UpdateActivateButton();
            ProfileManager.PushCurrentProfile();
        }

        private void ProfilesChanged(ProfileItem[] changed)
        {
            ProfileListBox.ItemsSource = null;
            ProfileListBox.ItemsSource = ProfileManager.Profiles;
            // Register direct handlers for newly added profiles
            foreach (var p in changed)
                RegisterDirectHandler(p);
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

            // Register any saved direct-access keybinds
            foreach (var p in ProfileManager.Profiles)
                RegisterDirectHandler(p);

            if (ProfileManager.Profiles.Count > 0)
            {
                ProfileListBox.SelectedIndex = 0;
            }
        }

        private readonly Settings settings;
        private bool isRecordingKeybind = false;

        private void RegisterKeyHandler()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(windowHandle);

            RegisterQuickSwitchHandler(windowHandle, source);
        }

        private void RegisterQuickSwitchHandler(nint windowHandle, HwndSource source)
        {
            // Unregister existing quick-switch handler if present
            if (handlers.TryGetValue(QuickSwitchHandlerKey(), out var old))
            {
                old.Unregiser();
                handlers.Remove(QuickSwitchHandlerKey());
            }

            var handler = new KeyHandler(settings.QuickSwitchKey, windowHandle, source,
                settings.QuickSwitchModifiers | KeyHandler.MOD_NOREPEAT)
            {
                Callback = ProfileManager.QuickSwitchProfile,
            };
            handlers[handler.GetHashCode()] = handler;
            handler.Register();
            UpdateQuickSwitchLabel();
        }

        private int QuickSwitchHandlerKey()
        {
            // Matches GetHashCode() in KeyHandler: key ^ modifiers ^ hWnd
            var windowHandle = new WindowInteropHelper(this).Handle;
            return settings.QuickSwitchKey ^ (settings.QuickSwitchModifiers | KeyHandler.MOD_NOREPEAT) ^ windowHandle.ToInt32();
        }

        private void UpdateQuickSwitchLabel()
        {
            if (QuickSwitchKeybindLabel is null) return;
            QuickSwitchKeybindLabel.Text = FormatKeybind(settings.QuickSwitchKey, settings.QuickSwitchModifiers);
        }

        private static string FormatKeybind(int vk, int mods)
        {
            var parts = new System.Text.StringBuilder();
            if ((mods & KeyHandler.MOD_CONTROL) != 0) parts.Append("Ctrl+");
            if ((mods & KeyHandler.MOD_ALT) != 0) parts.Append("Alt+");
            if ((mods & KeyHandler.MOD_SHIFT) != 0) parts.Append("Shift+");
            var key = KeyInterop.KeyFromVirtualKey(vk);
            parts.Append(key.ToString());
            return parts.ToString();
        }

        private void StartKeybindRecording()
        {
            isRecordingKeybind = true;
            QuickSwitchKeybindLabel.Text = "Press keys…";
        }

        private void OnKeybindBadgeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!isRecordingKeybind) StartKeybindRecording();
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!isRecordingKeybind && !isRecordingDirectKeybind) return;
            e.Handled = true;

            // Ignore pure modifiers
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
                return;

            var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;

            // Escape clears the direct keybind
            if (actualKey == Key.Escape && isRecordingDirectKeybind && selectedProfile is { } item)
            {
                isRecordingDirectKeybind = false;
                UnregisterDirectHandler(item);
                item.DirectSwitchKey = 0;
                item.DirectSwitchModifiers = 0;
                UpdateDirectKeybindLabel();
                return;
            }

            int mods = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= KeyHandler.MOD_CONTROL;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= KeyHandler.MOD_ALT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= KeyHandler.MOD_SHIFT;
            int vk = KeyHandler.ToKeycode(actualKey);

            if (isRecordingKeybind)
            {
                isRecordingKeybind = false;
                var windowHandle = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(windowHandle);
                foreach (var h in handlers.Values.ToList()) { h.Unregiser(); }
                handlers.Clear();
                settings.QuickSwitchKey = vk;
                settings.QuickSwitchModifiers = mods;
                RegisterQuickSwitchHandler(windowHandle, source);
            }
            else if (isRecordingDirectKeybind && selectedProfile is { } profile)
            {
                isRecordingDirectKeybind = false;
                profile.DirectSwitchKey = vk;
                profile.DirectSwitchModifiers = mods;
                RegisterDirectHandler(profile);
                UpdateDirectKeybindLabel();
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
                handler.Unregiser();
            foreach (var handler in directHandlers.Values)
                handler.Unregiser();
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

        private ProfileItem? renamingProfile;

        private void ShowRenameOverlay(ProfileItem item)
        {
            renamingProfile = item;
            RenameTextBox.Text = item.Name;
            RenameOverlay.Visibility = Visibility.Visible;
            RenameTextBox.Focus();
            RenameTextBox.SelectAll();
        }

        private void CommitRename()
        {
            var newName = RenameTextBox.Text?.Trim();
            if (renamingProfile is { } item && !string.IsNullOrEmpty(newName) && newName != item.Name)
            {
                item.Name = newName;
                if (selectedProfile == item)
                    DetailProfileName.Text = newName;
                if (item.IsActiveProfile)
                    ActiveProfileText.Text = "Active: " + newName;
                ProfileListBox.Items.Refresh();
            }
            RenameOverlay.Visibility = Visibility.Collapsed;
            renamingProfile = null;
        }

        private void RenameConfirm_Click(object sender, RoutedEventArgs e) => CommitRename();

        private void RenameCancel_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            renamingProfile = null;
        }

        private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitRename();
            else if (e.Key == Key.Escape) RenameCancel_Click(sender, e);
        }

        private void OnSidebarRenameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is ProfileItem item)
                ShowRenameOverlay(item);
        }

        private void OnListBoxPreviewRightClick(object sender, MouseButtonEventArgs e)
        {
            var container = ItemsControl.ContainerFromElement(ProfileListBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container?.DataContext is ProfileItem item)
                ProfileListBox.SelectedItem = item;
        }

        private void OnContextMenuRenameClicked(object sender, RoutedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem item)
                ShowRenameOverlay(item);
        }

        private void OnProfileNoteChanged(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is null) return;
            selectedProfile.Note = ProfileNoteTextBox.Text ?? string.Empty;
        }

        // ===== Direct Switch Keybind =====

        private void RegisterDirectHandler(ProfileItem item)
        {
            if (item.DirectSwitchKey == 0) return;
            var windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(windowHandle);
            var captured = item; // avoid closure capture issue
            var h = new KeyHandler(item.DirectSwitchKey, windowHandle, source,
                item.DirectSwitchModifiers | KeyHandler.MOD_NOREPEAT)
            {
                Callback = () => ProfileManager.SwitchTo(captured),
            };
            UnregisterDirectHandler(item);
            directHandlers[item] = h;
            h.Register();
        }

        private void UnregisterDirectHandler(ProfileItem item)
        {
            if (directHandlers.TryGetValue(item, out var old))
            {
                old.Unregiser();
                directHandlers.Remove(item);
            }
        }

        private void OnDirectKeybindBadgeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (selectedProfile is null) return;
            isRecordingDirectKeybind = true;
            isRecordingKeybind = false;
            DirectKeybindLabel.Text = "Press keys…";
        }

        private void UpdateDirectKeybindLabel()
        {
            if (DirectKeybindLabel is null || selectedProfile is null) return;
            DirectKeybindLabel.Text = selectedProfile.DirectSwitchKey == 0
                ? "None"
                : FormatKeybind(selectedProfile.DirectSwitchKey, selectedProfile.DirectSwitchModifiers);
            DirectKeybindLabel.Foreground = selectedProfile.DirectSwitchKey == 0
                ? (SolidColorBrush)FindResource("TextW3")
                : (SolidColorBrush)FindResource("TextW");
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
