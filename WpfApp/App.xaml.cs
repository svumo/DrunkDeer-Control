using Driver;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;
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
