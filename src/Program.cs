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

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                AppController controller = new AppController(app);
                app.Run();
                GC.KeepAlive(controller);
                GC.KeepAlive(mutex);
            }
        }
    }
}
