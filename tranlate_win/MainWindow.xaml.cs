using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;

namespace ScreenTranslator
{
    public partial class MainWindow : Window
    {
        private HotkeyManager _hotkeyManager;
        private NotifyIcon _notifyIcon;
        private OverlayWindow _overlayWindow;
        private System.Drawing.Rectangle _lastSelectedRect;
        private bool _isRealShutdown = false;

        private const int HOTKEY_SELECT_ROI = 1;
        private const int HOTKEY_RETRANSLATE = 2;

        public MainWindow()
        {
            InitializeComponent();
            
            Loaded += MainWindow_Loaded;
            InitializeTrayIcon();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Load ngôn ngữ nguồn từ Windows OCR
            var ocrLangs = OcrService.GetSupportedLanguages();
            ComboSourceLang.ItemsSource = ocrLangs;
            ComboSourceLang.DisplayMemberPath = "Value";
            ComboSourceLang.SelectedValuePath = "Key";
            ComboSourceLang.SelectedIndex = 0; // Chọn cái đầu tiên mặc định (thường là en-US)

            // 2. Load ngôn ngữ đích
            ComboTargetLang.ItemsSource = TranslationService.TargetLanguages;
            ComboTargetLang.DisplayMemberPath = "Value";
            ComboTargetLang.SelectedValuePath = "Key";
            ComboTargetLang.SelectedValue = "vi"; // Mặc định dịch sang tiếng Việt

            // Lắng nghe sự kiện đổi ngôn ngữ để cập nhật Overlay đang mở
            ComboSourceLang.SelectionChanged += SettingChanged;
            ComboTargetLang.SelectionChanged += SettingChanged;

            // 3. Đăng ký Hotkey toàn hệ thống
            try
            {
                _hotkeyManager = new HotkeyManager(this);
                _hotkeyManager.HotkeyPressed += OnHotkeyPressed;

                // Register Ctrl + Shift + F (VK_F = 0x46) -> Chọn vùng ROI mới
                // Modifier = MOD_CONTROL (2) | MOD_SHIFT (4) = 6
                bool regSelect = _hotkeyManager.Register(HOTKEY_SELECT_ROI, HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_SHIFT, 0x46);
                
                // Register Ctrl + Shift + R (VK_R = 0x52) -> Dịch lại vùng cũ
                bool regRedo = _hotkeyManager.Register(HOTKEY_RETRANSLATE, HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_SHIFT, 0x52);

                if (!regSelect || !regRedo)
                {
                    System.Windows.MessageBox.Show("Không thể đăng ký phím tắt. Có thể phím tắt đã bị ứng dụng khác chiếm giữ.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Hotkey init error: " + ex.Message);
            }
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new NotifyIcon();
                // Lấy icon ứng dụng mặc định của hệ thống
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                _notifyIcon.Text = "Screen Translator";
                _notifyIcon.Visible = true;
                _notifyIcon.DoubleClick += (s, args) => ShowControlPanel();

                System.Windows.Forms.ContextMenu contextMenu = new System.Windows.Forms.ContextMenu();
                contextMenu.MenuItems.Add("Cấu hình", (s, args) => ShowControlPanel());
                contextMenu.MenuItems.Add("Thoát", (s, args) => ShutdownApplication());
                _notifyIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("NotifyIcon init error: " + ex.Message);
            }
        }

        private void ShowControlPanel()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ShutdownApplication()
        {
            _isRealShutdown = true;
            
            // Đóng Overlay nếu đang mở
            if (_overlayWindow != null)
            {
                _overlayWindow.Close();
            }

            // Dọn dẹp hotkey và tray icon
            if (_hotkeyManager != null)
            {
                _hotkeyManager.Dispose();
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            this.Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isRealShutdown)
            {
                // Thay vì đóng app, ta ẩn cửa sổ và thu xuống tray
                e.Cancel = true;
                this.Hide();
            }
        }

        // Xử lý các sự kiện bấm Hotkey hệ thống
        private void OnHotkeyPressed(int hotkeyId)
        {
            if (hotkeyId == HOTKEY_SELECT_ROI)
            {
                StartRegionSelection();
            }
            else if (hotkeyId == HOTKEY_RETRANSLATE)
            {
                ReTranslateActiveRegion();
            }
        }

        private void BtnSelectRegion_Click(object sender, RoutedEventArgs e)
        {
            StartRegionSelection();
        }

        // Khởi chạy cửa sổ chọn vùng ROI
        private void StartRegionSelection()
        {
            // Ẩn bảng điều khiển tạm thời để không chắn màn hình chụp
            this.Hide();

            // Đóng overlay hiện tại nếu có
            if (_overlayWindow != null)
            {
                _overlayWindow.Close();
                _overlayWindow = null;
            }

            // Mở SelectionWindow
            SelectionWindow selWin = new SelectionWindow();
            bool? result = selWin.ShowDialog();

            if (result == true)
            {
                _lastSelectedRect = selWin.SelectedRect;
                TextStatus.Text = string.Format("Vùng chọn: {0}x{1} tại ({2},{3})", 
                    _lastSelectedRect.Width, _lastSelectedRect.Height, _lastSelectedRect.X, _lastSelectedRect.Y);

                // Tạo cửa sổ hiển thị bản dịch
                var srcObj = ComboSourceLang.SelectedValue;
                string srcLang = srcObj != null ? srcObj.ToString() : "en-US";
                var targetObj = ComboTargetLang.SelectedValue;
                string targetLang = targetObj != null ? targetObj.ToString() : "vi";

                _overlayWindow = new OverlayWindow(_lastSelectedRect, srcLang, targetLang);
                _overlayWindow.Show();

                // Dịch ngay lập tức
                var task = _overlayWindow.TranslateAsync();

                // Nếu có cài đặt Auto-scan thì kích hoạt luôn
                if (CheckAutoScan.IsChecked == true)
                {
                    int interval = (int)SliderInterval.Value;
                    _overlayWindow.StartAutoScan(interval);
                }
            }
            else
            {
                // Nếu người dùng hủy chọn, hiển thị lại bảng điều khiển
                this.Show();
            }
        }

        // Quét lại vùng cũ
        private void ReTranslateActiveRegion()
        {
            if (_overlayWindow != null && _overlayWindow.IsVisible)
            {
                var task = _overlayWindow.TranslateAsync();
            }
            else if (_lastSelectedRect.Width > 0 && _lastSelectedRect.Height > 0)
            {
                // Nếu overlay đã đóng nhưng vẫn còn tọa độ cũ, mở lại overlay
                var srcObj = ComboSourceLang.SelectedValue;
                string srcLang = srcObj != null ? srcObj.ToString() : "en-US";
                var targetObj = ComboTargetLang.SelectedValue;
                string targetLang = targetObj != null ? targetObj.ToString() : "vi";

                _overlayWindow = new OverlayWindow(_lastSelectedRect, srcLang, targetLang);
                _overlayWindow.Show();

                var task = _overlayWindow.TranslateAsync();

                if (CheckAutoScan.IsChecked == true)
                {
                    int interval = (int)SliderInterval.Value;
                    _overlayWindow.StartAutoScan(interval);
                }
            }
        }

        // Cập nhật cài đặt khi người dùng đổi trên UI
        private void SettingChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_overlayWindow != null)
            {
                var srcObj = ComboSourceLang.SelectedValue;
                _overlayWindow.SourceLanguage = srcObj != null ? srcObj.ToString() : "en-US";
                var targetObj = ComboTargetLang.SelectedValue;
                _overlayWindow.TargetLanguage = targetObj != null ? targetObj.ToString() : "vi";
                
                // Dịch lại ngay với cài đặt mới
                var task = _overlayWindow.TranslateAsync();
            }
        }

        private void CheckAutoScan_Checked(object sender, RoutedEventArgs e)
        {
            PanelInterval.Visibility = Visibility.Visible;
            if (_overlayWindow != null)
            {
                int interval = (int)SliderInterval.Value;
                _overlayWindow.StartAutoScan(interval);
            }
        }

        private void CheckAutoScan_Unchecked(object sender, RoutedEventArgs e)
        {
            PanelInterval.Visibility = Visibility.Collapsed;
            if (_overlayWindow != null)
            {
                _overlayWindow.StopAutoScan();
            }
        }

        private void SliderInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextIntervalValue == null) return;

            double valInSeconds = e.NewValue / 1000.0;
            TextIntervalValue.Text = valInSeconds.ToString("0.0") + "s";

            if (_overlayWindow != null && CheckAutoScan.IsChecked == true)
            {
                // Cập nhật lại chu kỳ quét bằng cách khởi động lại Auto-scan
                _overlayWindow.StopAutoScan();
                _overlayWindow.StartAutoScan((int)e.NewValue);
            }
        }
    }
}
