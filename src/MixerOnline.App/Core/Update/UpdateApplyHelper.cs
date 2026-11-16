using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Application = System.Windows.Application;

namespace MixerOnline.Core.Update
{
    /// <summary>
    /// Handles seamless zero-downtime application updates on Windows by extracting the downloaded package
    /// and replacing binaries after the host process exits.
    /// </summary>
    public static class UpdateApplyHelper
    {
        private static readonly string UpdatesDirectory = Path.Combine(
            Path.GetTempPath(),
            "MixerOnline",
            "Updates");

        /// <summary>
        /// Prepares and executes a detached handoff updater script, then terminates the current application.
        /// </summary>
        /// <param name="downloadedFilePath">Path to the downloaded .zip or .exe update file.</param>
        /// <param name="isBeta">Whether the application is the Beta edition.</param>
        public static void ApplyAndRestart(string downloadedFilePath, bool isBeta = true)
        {
            if (string.IsNullOrWhiteSpace(downloadedFilePath) || !File.Exists(downloadedFilePath))
            {
                throw new FileNotFoundException("Downloaded update file does not exist.", downloadedFilePath);
            }

            if (!Directory.Exists(UpdatesDirectory))
            {
                Directory.CreateDirectory(UpdatesDirectory);
            }

            int currentPid = Environment.ProcessId;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string exeName = isBeta ? "MixerOnline-beta.exe" : "MixerOnline.exe";

            // If the running executable filename is different, match it
            string? runningExePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(runningExePath))
            {
                exeName = Path.GetFileName(runningExePath);
            }

            string scriptPath = Path.Combine(UpdatesDirectory, "apply_update.ps1");
            bool isZip = downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            var scriptBuilder = new StringBuilder();
            scriptBuilder.AppendLine("# MixerOnline Automated Updater Handoff Script");
            scriptBuilder.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            scriptBuilder.AppendLine($"$TargetPid = {currentPid}");
            scriptBuilder.AppendLine($"$TargetDir = '{baseDir.Replace("'", "''")}'");
            scriptBuilder.AppendLine($"$UpdateFile = '{downloadedFilePath.Replace("'", "''")}'");
            scriptBuilder.AppendLine($"$ExeName = '{exeName.Replace("'", "''")}'");
            scriptBuilder.AppendLine();
            scriptBuilder.AppendLine("# 1. Wait for host process to terminate");
            scriptBuilder.AppendLine("try {");
            scriptBuilder.AppendLine("    $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue");
            scriptBuilder.AppendLine("    if ($proc) {");
            scriptBuilder.AppendLine("        $proc.WaitForExit(10000)");
            scriptBuilder.AppendLine("    }");
            scriptBuilder.AppendLine("} catch {}");
            scriptBuilder.AppendLine("Start-Sleep -Milliseconds 600");
            scriptBuilder.AppendLine();

            if (isZip)
            {
                scriptBuilder.AppendLine("# 2. Extract ZIP archive to staging folder");
                scriptBuilder.AppendLine("$Staging = Join-Path $PSScriptRoot 'staging'");
                scriptBuilder.AppendLine("if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }");
                scriptBuilder.AppendLine("New-Item -ItemType Directory -Path $Staging -Force | Out-Null");
                scriptBuilder.AppendLine("Expand-Archive -Path $UpdateFile -DestinationPath $Staging -Force");
                scriptBuilder.AppendLine();
                scriptBuilder.AppendLine("# 3. Overwrite files in target directory");
                scriptBuilder.AppendLine("Copy-Item -Path \"$Staging\\*\" -Destination $TargetDir -Recurse -Force");
                scriptBuilder.AppendLine();
                scriptBuilder.AppendLine("# 4. Cleanup staging and update zip");
                scriptBuilder.AppendLine("Remove-Item -Recurse -Force $Staging -ErrorAction SilentlyContinue");
                scriptBuilder.AppendLine("Remove-Item -Force $UpdateFile -ErrorAction SilentlyContinue");
            }
            else
            {
                scriptBuilder.AppendLine("# 2. Replace standalone executable");
                scriptBuilder.AppendLine("$TargetExe = Join-Path $TargetDir $ExeName");
                scriptBuilder.AppendLine("Copy-Item -Path $UpdateFile -Destination $TargetExe -Force");
                scriptBuilder.AppendLine("Remove-Item -Force $UpdateFile -ErrorAction SilentlyContinue");
            }

            scriptBuilder.AppendLine();
            scriptBuilder.AppendLine("# 5. Relaunch MixerOnline");
            scriptBuilder.AppendLine("$FinalExe = Join-Path $TargetDir $ExeName");
            scriptBuilder.AppendLine("if (Test-Path $FinalExe) {");
            scriptBuilder.AppendLine("    Start-Process -FilePath $FinalExe");
            scriptBuilder.AppendLine("}");

            File.WriteAllText(scriptPath, scriptBuilder.ToString(), Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Process.Start(psi);

            // Cleanly shutdown UI and Audio
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Application.Current.Shutdown();
                });
            }
            else
            {
                Environment.Exit(0);
            }
        }
    }
}
