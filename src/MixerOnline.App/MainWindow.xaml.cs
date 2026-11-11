using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using MixerOnline.Views;
using Application = System.Windows.Application;

namespace MixerOnline
{
    public partial class MainWindow : Window
    {
        private NotifyIcon? _trayIcon;
        private bool _isExplicitExit;

        public MainWindow()
        {
            InitializeComponent();

            ViewModel.RequestOpenSettings = OpenSettingsDialog;
            ViewModel.RequestOpenGuide = OpenGuideDialog;

            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _trayIcon = new NotifyIcon
                {
                    Text = "MixerOnline [BETA] — Windows Desktop Audio Mixer",
                    Visible = true,
                    Icon = LoadTrayIcon()
                };

                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("Open MixerOnline", null, (s, e) => ShowAndRestore());
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add("Toggle Master Mute", null, (s, e) => ViewModel.IsMasterMuted = !ViewModel.IsMasterMuted);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

                _trayIcon.ContextMenuStrip = contextMenu;
                _trayIcon.DoubleClick += (s, e) => ShowAndRestore();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayIcon] Init error: {ex.Message}");
            }
        }

        private static Icon LoadTrayIcon()
        {
            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
                var streamInfo = Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    using var s = streamInfo.Stream;
                    return new System.Drawing.Icon(s);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayIcon] Embedded resource load fallback: {ex.Message}");
            }

            return CreateFallbackTrayIcon();
        }

        private static Icon CreateFallbackTrayIcon()
        {
            using var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(12, 12, 15));
            using var barBrush = new SolidBrush(Color.FromArgb(244, 244, 245));
            g.FillRectangle(barBrush, 3, 6, 2, 7);
            g.FillRectangle(barBrush, 7, 3, 2, 10);
            g.FillRectangle(barBrush, 11, 5, 2, 8);
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        private void ShowAndRestore()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExplicitExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            ViewModel.Dispose();
            Application.Current.Shutdown();
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Settings.MinimizeToTray)
            {
                Hide();
                _trayIcon?.ShowBalloonTip(1500, "MixerOnline", "MixerOnline is running in the background.", ToolTipIcon.Info);
            }
            else
            {
                WindowState = WindowState.Minimized;
            }
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Settings.MinimizeToTray)
            {
                Hide();
                _trayIcon?.ShowBalloonTip(1500, "MixerOnline", "MixerOnline is running in the background. Right-click the tray icon to exit.", ToolTipIcon.Info);
            }
            else
            {
                ExitApplication();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitExit && ViewModel.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        private void OpenSettingsDialog()
        {
            var dialog = new SettingsDialog(ViewModel.Settings)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.Engine.UpdateLatency(ViewModel.Settings.LatencyMs);
                ViewModel.StatusText = $"Latency buffer set to {ViewModel.Settings.LatencyMs} ms.";
            }
        }

        private void OpenGuideDialog()
        {
            var dialog = new SetupGuideDialog(ViewModel.IsVirtualCableDetected)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
    }
}
