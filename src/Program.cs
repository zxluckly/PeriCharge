using System;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PeripheralBattery
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "PeripheralBattery_SingleInstance", out created))
            {
                if (!created)
                {
                    Forms.MessageBox.Show(
                        "外设电量监控已在运行。",
                        "外设电量监控",
                        Forms.MessageBoxButtons.OK,
                        Forms.MessageBoxIcon.Information);
                    return;
                }

                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);

                // 全局异常兜底：未捕获异常写入日志，避免只弹默认对话框而看不到堆栈
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
                Forms.Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log.Exception("未处理异常（WinForms 线程）", e.Exception);
                };

                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log.Exception("未处理异常（AppDomain）", e.ExceptionObject as Exception);
                };

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Exception("未处理异常（WPF 线程）", e.Exception);
                    e.Handled = true; // 托盘工具：记录后继续运行
                };
                AppController controller = new AppController(app);
                app.Run();
                GC.KeepAlive(controller);
                GC.KeepAlive(mutex);
            }
        }
    }
}
