using System;
using System.Windows;
using System.Windows.Input;
using MixerOnline.Core.Config;
using MixerOnline.Core.Update;

namespace MixerOnline.Views
{
    public partial class SettingsDialog : Window
    {
        private const string CurrentAppVersion = "1.2.1-beta";
        private readonly AppSettings _settings;
        private readonly UpdateService _updateService = new();

        public SettingsDialog(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            // Load values into UI
            switch (_settings.LatencyMs)
            {
                case <= 20: ComboLatency.SelectedIndex = 0; break;
                case <= 35: ComboLatency.SelectedIndex = 1; break;
                case <= 60: ComboLatency.SelectedIndex = 2; break;
                default: ComboLatency.SelectedIndex = 3; break;
            }

            CheckMinimizeToTray.IsChecked = _settings.MinimizeToTray;
            CheckAutoReconnect.IsChecked = _settings.AutoReconnect;
            CheckHearTabInBrowser.IsChecked = _settings.HearTabInBrowser;
            CheckAutoCheckUpdates.IsChecked = _settings.AutoCheckForUpdates;
            CheckIncludeBeta.IsChecked = _settings.CheckForBetaUpdates;
            TxtPort.Text = _settings.BridgePort.ToString();
            TxtCurrentVersionLabel.Text = $"Installed: v{CurrentAppVersion}";

            MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdates.IsEnabled = false;
            TxtUpdateStatus.Visibility = Visibility.Visible;
            TxtUpdateStatus.Text = "Checking GitHub Releases...";

            try
            {
                bool includeBeta = CheckIncludeBeta.IsChecked ?? true;
                var result = await _updateService.CheckForUpdatesAsync(CurrentAppVersion, includeBeta);

                if (result.IsUpdateAvailable && result.LatestRelease != null)
                {
                    TxtUpdateStatus.Text = $"Update found: {result.LatestRelease.TagName}!";
                    var updateDlg = new UpdateDialog(result.LatestRelease, CurrentAppVersion, isBeta: true)
                    {
                        Owner = this
                    };
                    updateDlg.ShowDialog();
                }
                else if (!string.IsNullOrEmpty(result.Error))
                {
                    TxtUpdateStatus.Text = $"Check failed: {result.Error}";
                }
                else
                {
                    TxtUpdateStatus.Text = $"MixerOnline v{CurrentAppVersion} is up to date!";
                }
            }
            catch (Exception ex)
            {
                TxtUpdateStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                BtnCheckUpdates.IsEnabled = true;
            }
        }

        private void OnSaveClicked(object sender, RoutedEventArgs e)
        {
            switch (ComboLatency.SelectedIndex)
            {
                case 0: _settings.LatencyMs = 15; break;
                case 1: _settings.LatencyMs = 30; break;
                case 2: _settings.LatencyMs = 50; break;
                case 3: _settings.LatencyMs = 100; break;
            }

            _settings.MinimizeToTray = CheckMinimizeToTray.IsChecked ?? true;
            _settings.AutoReconnect = CheckAutoReconnect.IsChecked ?? true;
            _settings.HearTabInBrowser = CheckHearTabInBrowser.IsChecked ?? true;
            _settings.AutoCheckForUpdates = CheckAutoCheckUpdates.IsChecked ?? true;
            _settings.CheckForBetaUpdates = CheckIncludeBeta.IsChecked ?? true;

            if (int.TryParse(TxtPort.Text, out int port) && port > 1024 && port < 65535)
            {
                _settings.BridgePort = port;
            }

            SettingsManager.Save(_settings);
            DialogResult = true;
            Close();
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
