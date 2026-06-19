using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace OWTranslatorLiteUninstaller
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string rootDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string appPath = Path.Combine(rootDirectory, "app", "OWTranslatorLite.exe");
            string appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OWTranslatorLite");

            if (!IsPortablePackageRoot(rootDirectory, appPath))
            {
                MessageBox.Show(
                    "当前目录不像 OW Translator Lite 发布包根目录，卸载器已取消。\n\n为避免误删源码或其他文件夹，请只从发布包根目录运行 OWTranslatorLiteUninstall.exe。",
                    "OW Translator Lite Uninstall",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }

            if (IsAppRunning(rootDirectory))
            {
                MessageBox.Show(
                    "OW Translator Lite 仍在运行。\n\n请先关闭主程序和更新器，再重新运行卸载器。卸载器不会强制结束进程，以免丢失未保存状态。",
                    "OW Translator Lite Uninstall",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 2;
            }

            DialogResult confirm = MessageBox.Show(
                "即将卸载 OW Translator Lite，并删除以下内容：\n\n" +
                rootDirectory + "\n" +
                appDataDirectory + "\n\n" +
                "这会清除 API Key、设置、日志、诊断包和 overlay 历史。继续？",
                "OW Translator Lite Uninstall",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return 0;
            }

            string scriptPath = Path.Combine(Path.GetTempPath(), "ow-translator-lite-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(scriptPath, BuildDeleteScript(rootDirectory, appDataDirectory), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + scriptPath + "\"",
                WorkingDirectory = Path.GetTempPath(),
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
            return 0;
        }

        private static bool IsPortablePackageRoot(string rootDirectory, string appPath)
        {
            if (!File.Exists(appPath))
            {
                return false;
            }

            if (Directory.Exists(Path.Combine(rootDirectory, ".git")) ||
                File.Exists(Path.Combine(rootDirectory, "OwTranslateLite.csproj")) ||
                Directory.Exists(Path.Combine(rootDirectory, "Core")))
            {
                return false;
            }

            return File.Exists(Path.Combine(rootDirectory, "OWTranslatorLite.exe")) &&
                   Directory.Exists(Path.Combine(rootDirectory, "app"));
        }

        private static bool IsAppRunning(string rootDirectory)
        {
            foreach (Process process in Process.GetProcessesByName("OWTranslatorLite"))
            {
                try
                {
                    string fileName = process.MainModule == null ? "" : process.MainModule.FileName;
                    if (fileName.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // If process details cannot be read, err on the side of not blocking uninstall.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return false;
        }

        private static string BuildDeleteScript(string rootDirectory, string appDataDirectory)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("@echo off");
            builder.AppendLine("chcp 65001 > nul");
            builder.AppendLine("ping 127.0.0.1 -n 2 > nul");
            builder.AppendLine("rmdir /s /q " + Quote(appDataDirectory));
            builder.AppendLine("cd /d %TEMP%");
            builder.AppendLine("rmdir /s /q " + Quote(rootDirectory));
            builder.AppendLine("del /f /q \"%~f0\"");
            return builder.ToString();
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
