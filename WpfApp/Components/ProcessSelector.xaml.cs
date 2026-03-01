using Driver;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WpfApp.Extensions;
using Path = System.IO.Path;

namespace WpfApp.Components;

public sealed record ProcessRow
{
    public string ProcessPath { get; set; }
    public string ProcessName { get => Path.GetFileName(ProcessPath); }
    public Process? Process { get; set; }

    public ProcessRow(string processPath)
    {
        ProcessPath = processPath;
    }

    public ProcessRow(Process process)
    {
        Process = process;
        ProcessPath = process.GetPathFromProcessId();
    }
}

public partial class ProcessSelector : Window
{
    private ObservableCollection<ProcessRow> ActiveProcesses { get; set; } = [];
    public ObservableCollection<ProcessRow> StoredProcesses { get; set; } = [];
    public ProfileItem ProfileItem { get; set; }

    public ProcessSelector(ProfileItem profileItem)
    {
        InitializeComponent();
        ProfileItem = profileItem;
        DialogTitle.Text = $"Triggers — {profileItem.Name}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AcrylicHelper.EnableAcrylic(this, 0xC0281A1A);
        RefreshActiveProcesses();
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public void SetStoredProcesses(string[] processes)
    {
        StoredProcesses.Clear();
        storedProcesses.ItemsSource = StoredProcesses;
        foreach (var process in processes)
        {
            StoredProcesses.Add(new ProcessRow(process));
        }
    }

    private void AddProcessManually_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            DefaultExt = ".exe",
            Filter = "Executables (.exe)|*.exe",
            Multiselect = true,
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
            {
                StoredProcesses.Add(new ProcessRow(path));
            }
        }
    }

    private void RefreshActiveProcesses()
    {
        ActiveProcesses.Clear();
        activeProcesses.ItemsSource = ActiveProcesses;
        var processes = ProcessExtensions.ActiveProcessesFiltered()
            .Where(p => !p.IsThisProcess() && p.IsWindowedProcess())
            .Select(p => new ProcessRow(p));
        foreach (var process in processes.Where(p => p.ProcessPath != string.Empty))
        {
            ActiveProcesses.Add(process);
        }
    }

    private void RefreshActiveProcesses_Click(object sender, RoutedEventArgs e)
    {
        RefreshActiveProcesses();
    }

    private void ActiveToStoredClick(object sender, RoutedEventArgs e)
    {
        if (activeProcesses.SelectedItem is ProcessRow item)
        {
            StoredProcesses.Add(item);
        }
    }

    private void RemoveStoredClick(object sender, RoutedEventArgs e)
    {
        if (storedProcesses.SelectedItem is ProcessRow item)
        {
            StoredProcesses.Remove(item);
        }
    }
}
