using System;
using System.IO;

namespace PeripheralBattery
{
    internal static class Log
    {
        private static readonly object Sync = new object();

        /// <summary>单个日志文件的体积上限，超过后轮转为 log.1.txt（只保留一份备份）。</summary>
        private const long MaxBytes = 1024 * 1024;

        private static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeripheralBattery");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(Folder, "log.txt"); }
        }

        internal static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    string folder = Folder;
                    Directory.CreateDirectory(folder);

                    string path = FilePath;
                    RotateIfNeeded(path);

                    File.AppendAllText(
                        path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 超过上限就把当前日志改名成 log.1.txt，旧的备份直接覆盖。
        /// 长期运行也只占两倍上限的空间，不会无限增长。
        /// </summary>
        private static void RotateIfNeeded(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length <= MaxBytes)
                {
                    return;
                }

                string backup = Path.Combine(Folder, "log.1.txt");
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }

                File.Move(path, backup);
            }
            catch
            {
                // 轮转失败就继续往原文件追加，不能因为日志影响主流程
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
