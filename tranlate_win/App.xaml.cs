using System;
using System.IO;
using System.Windows;

namespace ScreenTranslator
{
    public partial class App : Application
    {
        private static System.Threading.Mutex _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "ScreenTranslatorUniqueMutexName";
            bool createdNew;
            _mutex = new System.Threading.Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show("Ứng dụng Screen Translator đang chạy dưới nền rồi! Bạn có thể sử dụng phím tắt Ctrl+Shift+F để vẽ vùng dịch mới.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            MessageBox.Show("Đã xảy ra lỗi hệ thống (Dispatcher):\n" + e.Exception.Message + "\n\nChi tiết lỗi đã được ghi vào file crash_log.txt", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                LogCrash(ex);
                MessageBox.Show("Đã xảy ra lỗi hệ thống (AppDomain):\n" + ex.Message + "\n\nChi tiết lỗi đã được ghi vào file crash_log.txt", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Environment.Exit(1);
        }

        private void LogCrash(Exception ex)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
                string content = string.Format("[{0}] Lỗi:\n{1}\n\nStack Trace:\n{2}\n----------------------------------------\n", 
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ex.Message, ex.StackTrace);
                File.AppendAllText(logPath, content);
            }
            catch
            {
            }
        }
    }
}
