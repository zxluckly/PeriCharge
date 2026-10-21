using System;
using System.Threading;
using System.Windows.Forms;

namespace PeripheralBattery
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Log.Error("UI 线程异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.Error("未处理异常", e.ExceptionObject as Exception);
            };

            bool created;
            using (Mutex mutex = new Mutex(true, "PeripheralBattery_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show(
                        "外设电量监控已在运行。",
                        "外设电量监控",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Settings settings = Settings.Load();
                using (TrayApplicationContext context = new TrayApplicationContext(settings))
                {
                    Application.Run(context);
                }

                GC.KeepAlive(mutex);
            }
        }
    }
}
