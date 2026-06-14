using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace OWTranslatorLiteLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string rootDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string appDirectory = Path.Combine(rootDirectory, "app");
            string appPath = Path.Combine(appDirectory, "OWTranslatorLite.exe");

            if (!File.Exists(appPath))
            {
                MessageBox.Show(
                    "找不到 app\\OWTranslatorLite.exe，请确认发布包已完整解压。",
                    "OW Translator Lite",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = appPath,
                    WorkingDirectory = appDirectory,
                    UseShellExecute = false,
                    Arguments = BuildArgumentString(args)
                };
                Process.Start(startInfo);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "启动 OW Translator Lite 失败：" + ex.Message,
                    "OW Translator Lite",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }
        }

        private static string BuildArgumentString(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(args[i]));
            }

            return builder.ToString();
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            bool needsQuotes = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
            if (!needsQuotes)
            {
                return value;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
