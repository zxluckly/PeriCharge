using System;
using System.IO;

namespace PeripheralBattery
{
    internal static class Log
    {
        private static readonly object Sync = new object();

        private static string FilePath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeripheralBattery");
                return Path.Combine(folder, "log.txt");
            }
        }

        internal static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    string folder = Path.GetDirectoryName(FilePath);
                    Directory.CreateDirectory(folder);
                    File.AppendAllText(
                        FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        internal static void Error(string message, Exception exception)
        {
            string text = message;
            if (exception != null)
            {
                text += "：" + exception.GetType().Name + " " + exception.Message;
            }

            Write(text);
        }

        internal static void Exception(string message, Exception exception)
        {
            string text = message;
            if (exception != null)
            {
                // exception.ToString() 会带上完整堆栈与内部异常，便于定位
                text += "：" + exception;
            }

            Write(text);
        }
    }
}
