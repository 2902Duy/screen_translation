using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenTranslator
{
    public class HotkeyManager : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;

        // Định nghĩa các Modifier Keys
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        private IntPtr _hWnd;
        private HwndSource _source;

        public event Action<int> HotkeyPressed;

        public HotkeyManager(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hWnd = helper.EnsureHandle();
            _source = HwndSource.FromHwnd(_hWnd);
            _source.AddHook(HwndHook);
        }

        // Đăng ký phím tắt toàn hệ thống
        public bool Register(int id, uint modifiers, uint key)
        {
            Unregister(id); // Hủy đăng ký phím tắt cũ nếu trùng ID
            return RegisterHotKey(_hWnd, id, modifiers, key);
        }

        // Hủy đăng ký phím tắt
        public void Unregister(int id)
        {
            UnregisterHotKey(_hWnd, id);
        }

        public void Dispose()
        {
            if (_source != null)
            {
                _source.RemoveHook(HwndHook);
                _source = null;
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                var handler = HotkeyPressed;
                if (handler != null)
                {
                    handler(id);
                }
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
