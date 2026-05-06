using System;
using System.Windows;
using System.Windows.Input;
using Driver;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace WpfApp.Components
{
    public partial class ErrorWindow : Window
    {
        private readonly string detailsText;

        public ErrorWindow(string title, Exception ex)
        {
            InitializeComponent();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            detailsText =
                $"{title}\r\n" +
                $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"App version: {version}\r\n" +
                $".NET: {Environment.Version}\r\n" +
                $"OS: {Environment.OSVersion}\r\n" +
                $"\r\n" +
                $"{ex}";

            DetailsBox.Text = detailsText;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        private void Continue_Click(object sender, RoutedEventArgs e) => Close();

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Close();
            Application.Current.Shutdown();
        }

        private void CopyDetails_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(detailsText);
                CopyButtonText.Text = "Copied!";
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"ErrorWindow.CopyDetails: clipboard set failed: {ex.Message}");
                CopyButtonText.Text = "Copy failed";
            }
        }
    }
}
