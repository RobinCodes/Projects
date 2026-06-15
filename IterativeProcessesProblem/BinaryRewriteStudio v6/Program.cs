using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Fonts.Inter;

namespace BinaryRewrite
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());

            // Keep the app visibly styled; change to Default if you want system theme.
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();

            base.OnFrameworkInitializationCompleted();
        }
    }

    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Wipe any scratch directories left behind by a previous run that crashed or was
            // killed before it could clean up. (A concurrently-running instance is detected by
            // its held lock file and left alone.)
            DiskWorkspace.SweepStaleSessions();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}