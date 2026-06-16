using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OWTranslatorLiteUpdater
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Dictionary<string, string> options = ParseArgs(args);
            string rootDirectory = GetOption(options, "root", AppDomain.CurrentDomain.BaseDirectory);
            string downloadUrl = GetOption(options, "download-url", "");
            string sha256Url = GetOption(options, "sha256-url", "");
            string releasePage = GetOption(options, "release-page", "https://github.com/reverieach/ow-translate-lite/releases/latest");
            string launcherPath = GetOption(options, "launcher", Path.Combine(rootDirectory, "OWTranslatorLite.exe"));
            int waitPid = ParseInt(GetOption(options, "pid", ""));

            UpdateProgressForm progress = new UpdateProgressForm();
            progress.Show();
            progress.SetStatus("准备更新...", 5);
            try
            {
                Directory.CreateDirectory(rootDirectory);
                string zipPath = string.IsNullOrWhiteSpace(downloadUrl)
                    ? FindManualZip(rootDirectory)
                    : DownloadZip(rootDirectory, downloadUrl, sha256Url, releasePage, progress);

                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    progress.Close();
                    MessageBox.Show(
                        "没有找到更新包。\n\n请从 GitHub Release 下载最新的 OWTranslatorLite-v*-portable-win-x64.zip，并把它放到当前 OWTranslatorLite 文件夹，再重新运行 OWTranslatorLiteUpdater.exe。",
                        "OW Translator Lite Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return 1;
                }

                progress.SetStatus("等待主程序退出...", 35);
                WaitForProcessExit(waitPid);
                InstallZip(rootDirectory, zipPath, progress);
                progress.SetStatus("清理更新包...", 90);
                DeleteUpdatePackage(zipPath);
                progress.SetStatus("更新完成。", 100);
                MessageBox.Show(
                    progress,
                    "更新完成，程序将重新启动。\n\n本次更新包已删除，旧版本 app 已保留为最近一次备份。",
                    "OW Translator Lite Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                progress.Close();
                if (File.Exists(launcherPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = launcherPath,
                        WorkingDirectory = rootDirectory,
                        UseShellExecute = false
                    });
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (!progress.IsDisposed)
                {
                    progress.Close();
                }

                MessageBox.Show(
                    "更新失败：\n" + ex.Message + "\n\n如果自动更新不稳定，请手动下载最新 zip，放到当前 OWTranslatorLite 文件夹后重新运行 updater。",
                    "OW Translator Lite Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }
        }

        private static string DownloadZip(
            string rootDirectory,
            string downloadUrl,
            string sha256Url,
            string releasePage,
            UpdateProgressForm progress)
        {
            string updatesDirectory = Path.Combine(rootDirectory, "updates");
            Directory.CreateDirectory(updatesDirectory);
            string zipPath = Path.Combine(updatesDirectory, Path.GetFileName(new Uri(downloadUrl).LocalPath));
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "OWTranslatorLiteUpdater");
                    progress.SetStatus("正在下载更新包...", 10);
                    DownloadFileWithProgress(client, downloadUrl, zipPath, progress);
                    if (!string.IsNullOrWhiteSpace(sha256Url))
                    {
                        progress.SetStatus("正在校验更新包...", 32);
                        string shaText = client.DownloadString(sha256Url);
                        string expected = ExtractSha256(shaText);
                        if (!string.IsNullOrWhiteSpace(expected))
                        {
                            string actual = ComputeSha256(zipPath);
                            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(zipPath);
                                throw new InvalidOperationException("更新包校验失败，请重新下载。");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "自动下载更新失败：\n" + ex.Message + "\n\n请打开发布页手动下载最新 zip，放到当前 OWTranslatorLite 文件夹后，再运行 OWTranslatorLiteUpdater.exe。\n\n发布页：\n" + releasePage,
                    "OW Translator Lite Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                OpenUrl(releasePage);
                return "";
            }

            return zipPath;
        }

        private static void DownloadFileWithProgress(
            WebClient client,
            string downloadUrl,
            string destinationPath,
            UpdateProgressForm progress)
        {
            Exception error = null;
            using (ManualResetEvent completed = new ManualResetEvent(false))
            {
                client.DownloadProgressChanged += delegate (object sender, DownloadProgressChangedEventArgs e)
                {
                    int value = 10 + (e.ProgressPercentage * 20 / 100);
                    progress.SetStatus("正在下载更新包... " + e.ProgressPercentage + "%", value);
                };
                client.DownloadFileCompleted += delegate (object sender, System.ComponentModel.AsyncCompletedEventArgs e)
                {
                    if (e.Error != null)
                    {
                        error = e.Error;
                    }
                    else if (e.Cancelled)
                    {
                        error = new OperationCanceledException("更新包下载已取消。");
                    }

                    completed.Set();
                };
                client.DownloadFileAsync(new Uri(downloadUrl), destinationPath);
                while (!completed.WaitOne(100))
                {
                    Application.DoEvents();
                }
            }

            if (error != null)
            {
                throw error;
            }
        }

        private static string FindManualZip(string rootDirectory)
        {
            string[] files = Directory.GetFiles(rootDirectory, "OWTranslatorLite-v*-portable-win-x64.zip", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                return "";
            }

            Array.Sort(files, delegate (string left, string right)
            {
                return File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left));
            });
            return files[0];
        }

        private static void InstallZip(string rootDirectory, string zipPath, UpdateProgressForm progress)
        {
            string extractDirectory = Path.Combine(Path.GetTempPath(), "OWTranslatorLiteUpdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDirectory);
            try
            {
                progress.SetStatus("正在解压更新包...", 45);
                ZipFile.ExtractToDirectory(zipPath, extractDirectory);
                string packageRoot = FindPackageRoot(extractDirectory);
                string packageApp = Path.Combine(packageRoot, "app");
                if (!Directory.Exists(packageApp))
                {
                    throw new InvalidOperationException("更新包结构不正确：找不到 app 目录。");
                }

                string backupRoot = Path.Combine(rootDirectory, ".update-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                string currentApp = Path.Combine(rootDirectory, "app");
                string backupApp = Path.Combine(backupRoot, "app");
                Directory.CreateDirectory(backupRoot);
                try
                {
                    progress.SetStatus("正在备份当前版本...", 58);
                    if (Directory.Exists(currentApp))
                    {
                        Directory.Move(currentApp, backupApp);
                    }

                    progress.SetStatus("正在安装新版本...", 70);
                    CopyDirectory(packageApp, currentApp);
                    progress.SetStatus("正在更新启动文件...", 82);
                    CopyIfExists(Path.Combine(packageRoot, "README-BETA.md"), Path.Combine(rootDirectory, "README-BETA.md"));
                    CopyIfExists(Path.Combine(packageRoot, "OWTranslatorLite.exe"), Path.Combine(rootDirectory, "OWTranslatorLite.exe"));
                    CleanOldBackups(rootDirectory, backupRoot);
                }
                catch
                {
                    if (!Directory.Exists(currentApp) && Directory.Exists(backupApp))
                    {
                        Directory.Move(backupApp, currentApp);
                    }

                    throw;
                }
            }
            finally
            {
                TryDeleteDirectory(extractDirectory);
            }
        }

        private static void CleanOldBackups(string rootDirectory, string keepBackupRoot)
        {
            string backupDirectory = Path.Combine(rootDirectory, ".update-backup");
            if (!Directory.Exists(backupDirectory))
            {
                return;
            }

            string keepFullPath = Path.GetFullPath(keepBackupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string directory in Directory.GetDirectories(backupDirectory))
            {
                string currentFullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(currentFullPath, keepFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteDirectory(directory);
            }
        }

        private static void DeleteUpdatePackage(string zipPath)
        {
            TryDeleteFile(zipPath);
            TryDeleteFile(zipPath + ".sha256.txt");
        }

        private static string FindPackageRoot(string extractDirectory)
        {
            string directApp = Path.Combine(extractDirectory, "app");
            if (Directory.Exists(directApp))
            {
                return extractDirectory;
            }

            string namedRoot = Path.Combine(extractDirectory, "OWTranslatorLite");
            if (Directory.Exists(Path.Combine(namedRoot, "app")))
            {
                return namedRoot;
            }

            foreach (string directory in Directory.GetDirectories(extractDirectory))
            {
                if (Directory.Exists(Path.Combine(directory, "app")))
                {
                    return directory;
                }
            }

            return extractDirectory;
        }

        private static void WaitForProcessExit(int processId)
        {
            if (processId <= 0)
            {
                return;
            }

            try
            {
                Process process = Process.GetProcessById(processId);
                if (!process.WaitForExit(15000))
                {
                    MessageBox.Show(
                        "请先关闭正在运行的 OW Translator Lite，更新器会继续等待。",
                        "OW Translator Lite Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    process.WaitForExit();
                }
            }
            catch
            {
                // The process may have already exited.
            }

            Thread.Sleep(500);
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
            }

            foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destination = Path.Combine(destinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static void CopyIfExists(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Copy(sourcePath, destinationPath, true);
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string ExtractSha256(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length < 64)
            {
                return "";
            }

            return trimmed.Substring(0, 64);
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = arg.Substring(2);
                string value = index + 1 < args.Length ? args[++index] : "";
                options[key] = value;
            }

            return options;
        }

        private static string GetOption(Dictionary<string, string> options, string key, string fallback)
        {
            string value;
            return options.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        private static int ParseInt(string value)
        {
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Convenience-only.
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Temporary cleanup is best-effort.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup is best-effort.
            }
        }
    }

    internal sealed class UpdateProgressForm : Form
    {
        private readonly Label statusLabel;
        private readonly ProgressBar progressBar;

        public UpdateProgressForm()
        {
            Text = "OW Translator Lite Updater";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(420, 132);

            Label titleLabel = new Label
            {
                AutoSize = false,
                Text = "正在更新 OW Translator Lite",
                Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold),
                Left = 24,
                Top = 20,
                Width = 372,
                Height = 24
            };
            Controls.Add(titleLabel);

            statusLabel = new Label
            {
                AutoSize = false,
                Text = "准备更新...",
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F),
                Left = 24,
                Top = 54,
                Width = 372,
                Height = 22
            };
            Controls.Add(statusLabel);

            progressBar = new ProgressBar
            {
                Left = 24,
                Top = 86,
                Width = 372,
                Height = 18,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(progressBar);
        }

        public void SetStatus(string message, int progress)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, int>(SetStatus), message, progress);
                return;
            }

            statusLabel.Text = message;
            progressBar.Value = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, progress));
            Refresh();
            Application.DoEvents();
        }
    }
}
