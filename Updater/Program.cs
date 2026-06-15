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
            Dictionary<string, string> options = ParseArgs(args);
            string rootDirectory = GetOption(options, "root", AppDomain.CurrentDomain.BaseDirectory);
            string downloadUrl = GetOption(options, "download-url", "");
            string sha256Url = GetOption(options, "sha256-url", "");
            string releasePage = GetOption(options, "release-page", "https://github.com/reverieach/ow-translate-lite/releases/latest");
            string launcherPath = GetOption(options, "launcher", Path.Combine(rootDirectory, "OWTranslatorLite.exe"));
            int waitPid = ParseInt(GetOption(options, "pid", ""));

            try
            {
                Directory.CreateDirectory(rootDirectory);
                string zipPath = string.IsNullOrWhiteSpace(downloadUrl)
                    ? FindManualZip(rootDirectory)
                    : DownloadZip(rootDirectory, downloadUrl, sha256Url, releasePage);

                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    MessageBox.Show(
                        "没有找到更新包。\n\n请从 GitHub Release 下载最新的 OWTranslatorLite-v*-portable-win-x64.zip，并把它放到当前 OWTranslatorLite 文件夹，再重新运行 OWTranslatorLiteUpdater.exe。",
                        "OW Translator Lite Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return 1;
                }

                WaitForProcessExit(waitPid);
                InstallZip(rootDirectory, zipPath);
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
                MessageBox.Show(
                    "更新失败：\n" + ex.Message + "\n\n如果自动更新不稳定，请手动下载最新 zip，放到当前 OWTranslatorLite 文件夹后重新运行 updater。",
                    "OW Translator Lite Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }
        }

        private static string DownloadZip(string rootDirectory, string downloadUrl, string sha256Url, string releasePage)
        {
            string updatesDirectory = Path.Combine(rootDirectory, "updates");
            Directory.CreateDirectory(updatesDirectory);
            string zipPath = Path.Combine(updatesDirectory, Path.GetFileName(new Uri(downloadUrl).LocalPath));
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "OWTranslatorLiteUpdater");
                    client.DownloadFile(downloadUrl, zipPath);
                    if (!string.IsNullOrWhiteSpace(sha256Url))
                    {
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

        private static void InstallZip(string rootDirectory, string zipPath)
        {
            string extractDirectory = Path.Combine(Path.GetTempPath(), "OWTranslatorLiteUpdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDirectory);
            try
            {
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
                    if (Directory.Exists(currentApp))
                    {
                        Directory.Move(currentApp, backupApp);
                    }

                    CopyDirectory(packageApp, currentApp);
                    CopyIfExists(Path.Combine(packageRoot, "README-BETA.md"), Path.Combine(rootDirectory, "README-BETA.md"));
                    CopyIfExists(Path.Combine(packageRoot, "OWTranslatorLite.exe"), Path.Combine(rootDirectory, "OWTranslatorLite.exe"));
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
    }
}
