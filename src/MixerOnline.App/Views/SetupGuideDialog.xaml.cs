using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MixerOnline.Core.Utils;

namespace MixerOnline.Views
{
    public partial class SetupGuideDialog : Window
    {
        public SetupGuideDialog(bool isVbCableInstalled)
        {
            InitializeComponent();

            if (!isVbCableInstalled)
            {
                VbStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 69, 26, 3));
                VbStatusText.Text = "Not Detected";
                VbStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 251, 191, 36));
                BtnDownloadVb.Visibility = Visibility.Visible;
            }

            MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private void OnOpenBrowserExtensionsClicked(object sender, RoutedEventArgs e)
        {
            var (launched, browserName, extensionsUrl) = BrowserLauncher.OpenExtensionsPage();
            if (launched)
            {
                TxtExtensionInstruction.Text =
                    $"Opened {extensionsUrl} in {browserName}. Enable Developer mode, click 'Load unpacked', and select the 'extension' folder.";
                TxtExtensionInstruction.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 161, 161, 170));
            }
            else
            {
                TxtExtensionInstruction.Text =
                    $"Could not auto-launch a browser. Open {BrowserLauncher.GetExtensionsUrlSummary()} manually, enable Developer mode, click 'Load unpacked', and select the 'extension' folder.";
                TxtExtensionInstruction.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 251, 191, 36));
            }
        }

        private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
        {
            if (!BrowserLauncher.OpenExtensionFolder())
            {
                TxtExtensionInstruction.Text = "Extension folder not found next to the application executable.";
                TxtExtensionInstruction.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 251, 191, 36));
            }
        }

        private void OnDownloadVbClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
            }
            catch { }
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
