using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MixerOnline.Core.Update;
using MessageBox = System.Windows.MessageBox;

namespace MixerOnline.Views
{
    public partial class UpdateDialog : Window
    {
        private readonly UpdateReleaseInfo _releaseInfo;
        private readonly string _currentVersion;
        private readonly bool _isBeta;
        private readonly UpdateService _updateService;
        private CancellationTokenSource? _downloadCts;

        public UpdateDialog(UpdateReleaseInfo releaseInfo, string currentVersion, bool isBeta = true)
        {
            InitializeComponent();
            _releaseInfo = releaseInfo ?? throw new ArgumentNullException(nameof(releaseInfo));
            _currentVersion = currentVersion;
            _isBeta = isBeta;
            _updateService = new UpdateService();

            TxtLatestVersion.Text = releaseInfo.TagName;
            TxtCurrentVersion.Text = currentVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? currentVersion : $"v{currentVersion}";
            TxtChangelog.Text = !string.IsNullOrWhiteSpace(releaseInfo.Changelog) ? releaseInfo.Changelog.Trim() : "No release notes provided.";

            string meta = $"Released on GitHub";
            if (releaseInfo.PublishedAt.HasValue)
            {
                meta += $" • {releaseInfo.PublishedAt.Value.ToLocalTime():yyyy-MM-dd}";
            }
            if (releaseInfo.DownloadSize > 0)
            {
                double mb = releaseInfo.DownloadSize / (1024.0 * 1024.0);
                meta += $" • {mb:F1} MB";
            }
            TxtReleaseMeta.Text = meta;

            if (!string.IsNullOrEmpty(releaseInfo.DownloadUrl))
            {
                BtnInstall.IsEnabled = true;
                BtnInstall.ToolTip = "Download update and restart application automatically";
            }
            else
            {
                BtnInstall.IsEnabled = false;
                BtnInstall.ToolTip = "No direct downloadable package found in release assets; click 'View on GitHub'";
            }
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            _downloadCts?.Cancel();
            Close();
        }

        private void OnViewOnGitHubClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = !string.IsNullOrEmpty(_releaseInfo.HtmlUrl)
                    ? _releaseInfo.HtmlUrl
                    : "https://github.com/novascatia/MixerOnline/releases";

                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open browser: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void OnInstallClicked(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_releaseInfo.DownloadUrl)) return;

            BtnInstall.IsEnabled = false;
            PanelProgress.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "Preparing download...";
            TxtProgressPercent.Text = "0%";
            ProgressDownload.Value = 0;

            _downloadCts = new CancellationTokenSource();

            string tempDir = Path.Combine(Path.GetTempPath(), "MixerOnline", "Updates");
            string extension = _releaseInfo.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".exe";
            string destPath = Path.Combine(tempDir, $"update_{Guid.NewGuid():N}{extension}");

            var progress = new Progress<double>(pct =>
            {
                int val = (int)Math.Round(pct * 100);
                ProgressDownload.Value = val;
                TxtProgressPercent.Text = $"{val}%";
                TxtProgressStatus.Text = $"Downloading update... ({val}%)";
            });

            try
            {
                await _updateService.DownloadUpdateAsync(_releaseInfo.DownloadUrl, destPath, progress, _downloadCts.Token);

                TxtProgressStatus.Text = "Extracting & applying update...";
                await Task.Delay(400);

                // Execute handoff script and restart
                UpdateApplyHelper.ApplyAndRestart(destPath, _isBeta);
            }
            catch (OperationCanceledException)
            {
                TxtProgressStatus.Text = "Download canceled.";
                BtnInstall.IsEnabled = true;
            }
            catch (Exception ex)
            {
                TxtProgressStatus.Text = $"Download failed: {ex.Message}";
                BtnInstall.IsEnabled = true;
                MessageBox.Show($"Update download failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
