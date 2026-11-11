using System.Windows;
using System.Windows.Input;
using MixerOnline.Core.Config;

namespace MixerOnline.Views
{
    public partial class SettingsDialog : Window
    {
        private readonly AppSettings _settings;

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
            TxtPort.Text = _settings.BridgePort.ToString();

            MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragMove();
            };
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
