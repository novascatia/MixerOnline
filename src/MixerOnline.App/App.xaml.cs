using System;
using System.IO;
using System.Windows;

namespace MixerOnline.App
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                    File.WriteAllText(logPath, $"[AppDomain Crash] {args.ExceptionObject}");
                }
                catch { }
            };

            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                    File.WriteAllText(logPath, $"[Dispatcher Crash] {args.Exception}");
                }
                catch { }
            };

            base.OnStartup(e);
        }
    }
}
