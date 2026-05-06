using Driver;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WpfApp.Components;
using WpfApp.Hooks;
using WpfApp.Profile;
using Application = System.Windows.Application;

namespace WpfApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static ServiceProvider? serviceProvider;
        public static ServiceProvider ServiceProvider => serviceProvider ?? throw new NullReferenceException();

        public App()
        {
            ServiceCollection serviceCollection = new();
            serviceCollection.ConfigureServices();

            serviceProvider = serviceCollection.BuildServiceProvider();

            // Catch every unhandled exception we can reach so the user sees a
            // styled error dialog (with the Discord report link) instead of the
            // app silently dying. Each handler covers a different surface:
            //   - DispatcherUnhandledException: WPF UI thread exceptions (most
            //     of what we hit). We mark these Handled so the app survives.
            //   - AppDomain.UnhandledException: terminal exceptions on any
            //     thread; we can't actually stop the process from dying here,
            //     but we can log and show the dialog before it does.
            //   - TaskScheduler.UnobservedTaskException: background Task faults
            //     no one awaited. Mark Observed so they don't promote to fatal.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        public static void Application_Exit()
        {
            var icon = ServiceProvider.GetRequiredService<TrayIcon>();
            icon?.Dispose();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DebugLogger.Log($"DispatcherUnhandledException: {e.Exception}");
            ShowErrorWindow("UI thread exception", e.Exception);
            e.Handled = true; // keep the app alive
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            DebugLogger.Log($"AppDomain UnhandledException (terminating={e.IsTerminating}): {ex}");
            // We may already be on a background thread that's about to die — try
            // to marshal to the UI thread, but don't block forever if we can't.
            try
            {
                Dispatcher?.Invoke(() => ShowErrorWindow("Background thread exception", ex), DispatcherPriority.Send);
            }
            catch { }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            DebugLogger.Log($"UnobservedTaskException: {e.Exception}");
            try
            {
                Dispatcher?.BeginInvoke(() => ShowErrorWindow("Unobserved task exception", e.Exception));
            }
            catch { }
            e.SetObserved();
        }

        // Re-entrancy guard: if showing the error dialog itself throws (e.g. the
        // dialog's own resources fail to load), don't loop forever.
        private bool _showingError;

        private void ShowErrorWindow(string title, Exception ex)
        {
            if (_showingError)
            {
                DebugLogger.Log("ShowErrorWindow: already showing, suppressing nested error");
                return;
            }
            _showingError = true;
            try
            {
                var win = new ErrorWindow(title, ex);
                if (Current?.MainWindow is { IsVisible: true } owner)
                {
                    win.Owner = owner;
                    win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                win.ShowDialog();
            }
            catch (Exception ex2)
            {
                DebugLogger.Log($"ShowErrorWindow itself threw: {ex2}");
            }
            finally
            {
                _showingError = false;
            }
        }
    }

    public static class ServiceCollectionExtensions
    {
        public static void ConfigureServices(this IServiceCollection services)
        {
            var settings = Settings.FromFile() ?? new Settings() { SaveOnDirty = true };
            services.AddSingleton(settings);
            services.AddSingleton<WinEventHook>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<ProfileManager>();
            services.AddSingleton<TrayIcon>();
            services.AddSingleton<KeyboardManager>();
        }
    }
}
