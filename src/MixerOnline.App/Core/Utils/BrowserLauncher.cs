using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MixerOnline.Core.Utils
{
    /// <summary>
    /// Detects and launches installed Chromium-based browsers to their extensions page,
    /// and resolves the local unpacked extension folder across development and release layouts.
    /// </summary>
    public static class BrowserLauncher
    {
        private sealed record ChromiumBrowser(
            string ProcessName,
            string DisplayName,
            string ExtensionsUrl,
            string ExecutableName,
            string[] ProgIdPrefixes,
            string[] RelativeInstallPaths);

        private static readonly ChromiumBrowser Brave = new(
            ProcessName: "brave",
            DisplayName: "Brave",
            ExtensionsUrl: "brave://extensions",
            ExecutableName: "brave.exe",
            ProgIdPrefixes: new[] { "BraveHTML", "BraveBHTML", "Brave" },
            RelativeInstallPaths: new[]
            {
                @"BraveSoftware\Brave-Browser\Application\brave.exe",
                @"BraveSoftware\Brave-Browser-Beta\Application\brave.exe"
            });

        private static readonly ChromiumBrowser Chrome = new(
            ProcessName: "chrome",
            DisplayName: "Google Chrome",
            ExtensionsUrl: "chrome://extensions",
            ExecutableName: "chrome.exe",
            ProgIdPrefixes: new[] { "ChromeHTML" },
            RelativeInstallPaths: new[]
            {
                @"Google\Chrome\Application\chrome.exe"
            });

        private static readonly ChromiumBrowser Edge = new(
            ProcessName: "msedge",
            DisplayName: "Microsoft Edge",
            ExtensionsUrl: "edge://extensions",
            ExecutableName: "msedge.exe",
            ProgIdPrefixes: new[] { "MSEdgeHTM", "MSEdgeBHTML", "MSEdgeDHTML", "MSEdge" },
            RelativeInstallPaths: new[]
            {
                @"Microsoft\Edge\Application\msedge.exe"
            });

        private static readonly ChromiumBrowser Vivaldi = new(
            ProcessName: "vivaldi",
            DisplayName: "Vivaldi",
            ExtensionsUrl: "vivaldi://extensions",
            ExecutableName: "vivaldi.exe",
            ProgIdPrefixes: new[] { "VivaldiHTM", "Vivaldi" },
            RelativeInstallPaths: new[]
            {
                @"Vivaldi\Application\vivaldi.exe"
            });

        private static readonly ChromiumBrowser Opera = new(
            ProcessName: "opera",
            DisplayName: "Opera",
            ExtensionsUrl: "opera://extensions",
            ExecutableName: "opera.exe",
            ProgIdPrefixes: new[] { "OperaStable", "OperaGXStable", "OperaNext", "OperaDeveloper", "Opera" },
            RelativeInstallPaths: new[]
            {
                @"Programs\Opera\opera.exe",
                @"Programs\Opera GX\opera.exe",
                @"Opera\opera.exe"
            });

        private static readonly ChromiumBrowser[] RunningCheckOrder =
        {
            Brave, Chrome, Edge, Vivaldi, Opera
        };

        private static readonly ChromiumBrowser[] InstallProbeOrder =
        {
            Chrome, Edge, Brave, Vivaldi, Opera
        };

        public static string GetExtensionsUrlSummary() =>
            "chrome://extensions • brave://extensions • edge://extensions";

        public static (bool Launched, string BrowserName, string ExtensionsUrl) OpenExtensionsPage()
        {
            var attemptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Prefer an active Chromium browser window so the extensions tab opens in their current session
            foreach (var browser in RunningCheckOrder)
            {
                string? exePath = GetRunningExecutablePath(browser, requireVisibleWindow: true);
                if (!string.IsNullOrEmpty(exePath) && attemptedPaths.Add(exePath) && TryLaunch(exePath, browser.ExtensionsUrl))
                {
                    return (true, browser.DisplayName, browser.ExtensionsUrl);
                }
            }

            // 2. Check the user's default Windows HTTPS browser ProgId
            var defaultBrowser = GetDefaultBrowserFromRegistry();
            if (defaultBrowser is var (defBrowser, defExePath) &&
                attemptedPaths.Add(defExePath) &&
                TryLaunch(defExePath, defBrowser.ExtensionsUrl))
            {
                return (true, defBrowser.DisplayName, defBrowser.ExtensionsUrl);
            }

            // 1b. Fall back to any running Chromium process (even if minimized to tray / no visible window)
            foreach (var browser in RunningCheckOrder)
            {
                string? exePath = GetRunningExecutablePath(browser, requireVisibleWindow: false);
                if (!string.IsNullOrEmpty(exePath) && attemptedPaths.Add(exePath) && TryLaunch(exePath, browser.ExtensionsUrl))
                {
                    return (true, browser.DisplayName, browser.ExtensionsUrl);
                }
            }

            // 3. Probe standard Windows installation folders
            foreach (var browser in InstallProbeOrder)
            {
                string? exePath = FindInstalledExecutable(browser);
                if (!string.IsNullOrEmpty(exePath) && attemptedPaths.Add(exePath) && TryLaunch(exePath, browser.ExtensionsUrl))
                {
                    return (true, browser.DisplayName, browser.ExtensionsUrl);
                }
            }

            return (false, "Chromium Browser", "chrome://extensions");
        }

        public static string? FindExtensionFolder()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "extension");
                if (Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        public static bool OpenExtensionFolder()
        {
            string? extPath = FindExtensionFolder();
            if (string.IsNullOrEmpty(extPath))
            {
                return false;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo("explorer.exe", extPath)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowserLauncher] Failed to open extension folder '{extPath}': {ex.Message}");
                return false;
            }
        }

        private static string? GetRunningExecutablePath(ChromiumBrowser browser, bool requireVisibleWindow)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(browser.ProcessName);
            }
            catch
            {
                return null;
            }

            try
            {
                if (processes.Length == 0)
                {
                    return null;
                }

                bool hasMatchingProcess = false;
                foreach (var proc in processes)
                {
                    try
                    {
                        if (requireVisibleWindow && proc.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        hasMatchingProcess = true;
                        string? fileName = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
                        {
                            return fileName;
                        }
                    }
                    catch
                    {
                        // MainModule can throw if the process is elevated or exited concurrently
                    }
                }

                return hasMatchingProcess ? FindInstalledExecutable(browser) : null;
            }
            finally
            {
                foreach (var proc in processes)
                {
                    proc.Dispose();
                }
            }
        }

        private static (ChromiumBrowser Browser, string ExePath)? GetDefaultBrowserFromRegistry()
        {
            try
            {
                string? progId = null;
                using (var httpsKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                {
                    progId = httpsKey?.GetValue("ProgId") as string;
                }

                if (string.IsNullOrWhiteSpace(progId))
                {
                    using var httpKey = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                    progId = httpKey?.GetValue("ProgId") as string;
                }

                if (string.IsNullOrWhiteSpace(progId))
                {
                    return null;
                }

                var browser = RunningCheckOrder.FirstOrDefault(b =>
                    b.ProgIdPrefixes.Any(prefix => progId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

                if (browser == null)
                {
                    return null;
                }

                string? exePath = FindExecutableFromProgIdCommand(progId, browser.ExecutableName)
                               ?? FindInstalledExecutable(browser);

                return !string.IsNullOrEmpty(exePath) ? (browser, exePath) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? FindExecutableFromProgIdCommand(string progId, string expectedExecutableName)
        {
            try
            {
                string? command = null;
                using (var userClassesKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell\open\command"))
                {
                    command = userClassesKey?.GetValue(null) as string;
                }

                if (string.IsNullOrWhiteSpace(command))
                {
                    using var rootKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                    command = rootKey?.GetValue(null) as string;
                }

                if (string.IsNullOrWhiteSpace(command))
                {
                    return null;
                }

                string trimmed = command.Trim();
                string candidate;
                if (trimmed.StartsWith('"'))
                {
                    int endQuote = trimmed.IndexOf('"', 1);
                    if (endQuote <= 1)
                    {
                        return null;
                    }
                    candidate = trimmed.Substring(1, endQuote - 1);
                }
                else
                {
                    int space = trimmed.IndexOf(' ');
                    candidate = space > 0 ? trimmed.Substring(0, space) : trimmed;
                }

                if (File.Exists(candidate) &&
                    string.Equals(Path.GetFileName(candidate), expectedExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore registry access failures
            }

            return null;
        }

        private static string? FindInstalledExecutable(ChromiumBrowser browser)
        {
            string[] baseFolders =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            foreach (string root in baseFolders)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                foreach (string relativePath in browser.RelativeInstallPaths)
                {
                    string fullPath = Path.Combine(root, relativePath);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }

            return null;
        }

        private static bool TryLaunch(string exePath, string extensionsUrl)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exePath, extensionsUrl)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowserLauncher] Failed to launch '{exePath}': {ex.Message}");
                return false;
            }
        }
    }
}
