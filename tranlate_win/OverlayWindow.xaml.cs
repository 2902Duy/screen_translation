using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenTranslator
{
    public partial class OverlayWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        public System.Drawing.Rectangle SelectedRect { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }

        private double _dpiX = 1.0;
        private double _dpiY = 1.0;

        private bool _isAutoScanning = false;
        private Bitmap _lastCapturedBmp = null;
        private int _scanIntervalMs = 1000;
        private bool _isTranslating = false;

        public OverlayWindow(System.Drawing.Rectangle rect, string sourceLang, string targetLang)
        {
            InitializeComponent();
            SelectedRect = rect;
            SourceLanguage = sourceLang;
            TargetLanguage = targetLang;

            // Lấy DPI của hệ thống để căn chỉnh tọa độ chính xác
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                _dpiX = g.DpiX / 96.0;
                _dpiY = g.DpiY / 96.0;
            }

            // Thiết lập vị trí và kích thước cửa sổ khớp với vùng ROI (theo tọa độ WPF)
            Left = SelectedRect.Left / _dpiX;
            Top = SelectedRect.Top / _dpiY;
            Width = SelectedRect.Width / _dpiX;
            Height = SelectedRect.Height / _dpiY;

            this.Closed += OverlayWindow_Closed;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Bật tính năng Click-through (xuyên thấu chuột) để không cản trở việc nhấp chuột/chơi game bên dưới
            var helper = new WindowInteropHelper(this);
            int extendedStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
        }

        private void Log(string msg)
        {
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");
                System.IO.File.AppendAllText(logPath, string.Format("[{0}] [Overlay] {1}\r\n", DateTime.Now.ToString("HH:mm:ss.fff"), msg));
            }
            catch {}
        }

        // Thực hiện chụp màn hình và dịch
        public async Task TranslateAsync()
        {
            if (_isTranslating) return;
            _isTranslating = true;

            Log("TranslateAsync triggered. ROI Rect: " + SelectedRect.X + "," + SelectedRect.Y + "," + SelectedRect.Width + "x" + SelectedRect.Height);

            Dispatcher.Invoke(() => {
                LoadingIndicator.Visibility = Visibility.Visible;
            });

            try
            {
                Bitmap bmp = OcrService.CaptureScreen(SelectedRect.Left, SelectedRect.Top, SelectedRect.Width, SelectedRect.Height);
                Log("Screen captured successfully.");
                await TranslateWithBitmapAsync(bmp);
                
                // Lưu lại bitmap hiện tại để phục vụ so sánh ở Auto-scan
                if (_lastCapturedBmp != null)
                {
                    _lastCapturedBmp.Dispose();
                }
                _lastCapturedBmp = bmp;
            }
            catch (Exception ex)
            {
                Log("TranslateAsync ERROR: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                Dispatcher.Invoke(() => {
                    LoadingIndicator.Visibility = Visibility.Collapsed;
                });
                _isTranslating = false;
            }
        }

        // Tiến trình xử lý chính dựa trên hình ảnh chụp được
        private async Task TranslateWithBitmapAsync(Bitmap bmp)
        {
            Log("TranslateWithBitmapAsync started.");
            // 1. Nhận diện OCR lấy text và bounding box
            var lines = await Task.Run(() => OcrService.RecognizeText(bmp, SourceLanguage));
            if (lines == null || lines.Count == 0)
            {
                Log("No text lines recognized by OCR.");
                Dispatcher.Invoke(() => {
                    OverlayCanvas.Children.Clear();
                });
                return;
            }

            Log("Recognized " + lines.Count + " lines. Merging text for translation...");

            // 2. Gom các dòng chữ lại để dịch trong 1 request duy nhất (tối ưu hóa API)
            StringBuilder sbSource = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sbSource.Append(lines[i].Text);
                if (i < lines.Count - 1)
                    sbSource.Append("\n");
            }

            Log("Merged source text:\n" + sbSource.ToString());

            // Gửi API dịch
            Log("Requesting Google Translate (From: " + SourceLanguage + ", To: " + TargetLanguage + ")...");
            string translatedTextRaw = await Task.Run(() => 
                TranslationService.Translate(sbSource.ToString(), SourceLanguage, TargetLanguage));
            Log("Translation response received:\n" + translatedTextRaw);

            // Tách các dòng dịch tương ứng
            string[] translatedLines = translatedTextRaw.Split(new[] { '\n' }, StringSplitOptions.None);
            Log("Translated lines count: " + translatedLines.Length);

            for (int i = 0; i < lines.Count; i++)
            {
                if (i < translatedLines.Length)
                {
                    lines[i].TranslatedText = translatedLines[i].Trim();
                }
                else
                {
                    lines[i].TranslatedText = lines[i].Text; // Fallback
                }
            }

            // 3. Vẽ đè lên Canvas
            Log("Vẽ đè lên Canvas bắt đầu. Dòng cần vẽ: " + lines.Count);
            Dispatcher.Invoke(() => {
                OverlayCanvas.Children.Clear();

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line.TranslatedText))
                        continue;

                    // Chuyển đổi tọa độ vật lý sang tọa độ WPF
                    double wpfX = line.X / _dpiX;
                    double wpfY = line.Y / _dpiY;
                    double wpfW = line.Width / _dpiX;
                    double wpfH = line.Height / _dpiY;

                    // 3.1 Dò màu nền của hộp chữ gốc
                    System.Windows.Media.Color bgColor = SampleBackgroundColor(bmp, (int)line.X, (int)line.Y, (int)line.Width, (int)line.Height);
                    
                    // 3.2 Quyết định màu chữ dựa trên độ sáng màu nền (Luminance) để tăng độ tương phản
                    double luminance = 0.299 * bgColor.R + 0.587 * bgColor.G + 0.114 * bgColor.B;
                    var textColor = luminance > 128 
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)) 
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));

                    Log(string.Format("Vẽ chữ: '{0}' -> '{1}' tại ({2},{3}) [{4}x{5}], BG Color: {6}", 
                        line.Text, line.TranslatedText, wpfX, wpfY, wpfW, wpfH, bgColor.ToString()));

                    // 3.3 Tạo Border đè lên chữ cũ
                    Border border = new Border
                    {
                        Width = wpfW + 6,
                        Height = wpfH + 6,
                        Background = new SolidColorBrush(bgColor),
                        CornerRadius = new CornerRadius(2),
                        Padding = new Thickness(2)
                    };

                    // 3.4 Tạo TextBlock chữ dịch
                    TextBlock textBlock = new TextBlock
                    {
                        Text = line.TranslatedText,
                        Foreground = textColor,
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                        FontWeight = FontWeights.Medium,
                        FontSize = Math.Max(10, wpfH * 0.8),
                        Width = wpfW // Khống chế chiều rộng để tự động xuống dòng
                    };

                    Viewbox viewbox = new Viewbox
                    {
                        Child = textBlock,
                        Stretch = Stretch.Uniform,
                        StretchDirection = StretchDirection.DownOnly
                    };

                    border.Child = viewbox;

                    // Đưa lên Canvas tại đúng vị trí
                    Canvas.SetLeft(border, wpfX - 3);
                    Canvas.SetTop(border, wpfY - 3);
                    OverlayCanvas.Children.Add(border);
                }
                Log("Vẽ đè lên Canvas hoàn thành.");
            });
        }

        // Thuật toán lấy mẫu dò màu nền ở viền bounding box
        private System.Windows.Media.Color SampleBackgroundColor(Bitmap bmp, int x, int y, int w, int h)
        {
            int maxX = bmp.Width - 1;
            int maxY = bmp.Height - 1;

            List<System.Drawing.Color> colors = new List<System.Drawing.Color>();
            
            Action<int, int> addPixel = (px, py) => {
                int cx = Math.Max(0, Math.Min(px, maxX));
                int cy = Math.Max(0, Math.Min(py, maxY));
                colors.Add(bmp.GetPixel(cx, cy));
            };

            // Lấy mẫu 4 góc của bounding box
            addPixel(x, y);
            addPixel(x + w - 1, y);
            addPixel(x, y + h - 1);
            addPixel(x + w - 1, y + h - 1);

            // Lấy mẫu 4 điểm trung vị ở biên
            addPixel(x + w / 2, y);
            addPixel(x + w / 2, y + h - 1);
            addPixel(x, y + h / 2);
            addPixel(x + w - 1, y + h / 2);

            // Tính màu trung bình
            int sumR = 0, sumG = 0, sumB = 0;
            foreach (var c in colors)
            {
                sumR += c.R;
                sumG += c.G;
                sumB += c.B;
            }

            byte avgR = (byte)(sumR / colors.Count);
            byte avgG = (byte)(sumG / colors.Count);
            byte avgB = (byte)(sumB / colors.Count);

            return System.Windows.Media.Color.FromRgb(avgR, avgG, avgB);
        }

        // Bắt đầu vòng lặp quét Auto-scan
        public async void StartAutoScan(int intervalMs)
        {
            if (_isAutoScanning) return;
            _isAutoScanning = true;
            _scanIntervalMs = intervalMs;

            while (_isAutoScanning)
            {
                await Task.Delay(_scanIntervalMs);
                if (!_isAutoScanning) break;

                // Nếu đang bận dịch thì bỏ qua chu kỳ này
                if (_isTranslating) continue;

                try
                {
                    Bitmap currentBmp = OcrService.CaptureScreen(SelectedRect.Left, SelectedRect.Top, SelectedRect.Width, SelectedRect.Height);
                    
                    if (HasScreenChanged(currentBmp, _lastCapturedBmp))
                    {
                        // Màn hình thay đổi rõ rệt -> thực hiện dịch
                        await TranslateWithBitmapAsync(currentBmp);
                        
                        if (_lastCapturedBmp != null)
                        {
                            _lastCapturedBmp.Dispose();
                        }
                        _lastCapturedBmp = currentBmp;
                    }
                    else
                    {
                        // Không đổi -> giải phóng ảnh mới chụp
                        currentBmp.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Auto-scan loop error: " + ex.Message);
                }
            }
        }

        // Dừng chế độ Auto-scan
        public void StopAutoScan()
        {
            _isAutoScanning = false;
        }

        // Thuật toán Pixel Change Detection để kiểm tra màn hình thay đổi
        private bool HasScreenChanged(Bitmap bmp1, Bitmap bmp2)
        {
            if (bmp1 == null || bmp2 == null) return true;
            if (bmp1.Width != bmp2.Width || bmp1.Height != bmp2.Height) return true;

            // Thu nhỏ cả 2 bitmap về 32x32 pixel để loại bỏ nhiễu răng cưa và so sánh cực nhanh
            using (Bitmap small1 = ResizeBitmap(bmp1, 32, 32))
            using (Bitmap small2 = ResizeBitmap(bmp2, 32, 32))
            {
                double totalDiff = 0;
                int totalPixels = 32 * 32;

                for (int x = 0; x < 32; x++)
                {
                    for (int y = 0; y < 32; y++)
                    {
                        var c1 = small1.GetPixel(x, y);
                        var c2 = small2.GetPixel(x, y);

                        // Tính tổng độ lệch màu
                        totalDiff += Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B);
                    }
                }

                // Độ lệch trung bình của mỗi kênh màu trên mỗi pixel (thang 0-255)
                double avgDiff = totalDiff / (totalPixels * 3.0);
                
                // Nếu độ lệch lớn hơn 5.0 (tương đương thay đổi khoảng 2% pixel), coi như có thay đổi
                return avgDiff > 5.0;
            }
        }

        private Bitmap ResizeBitmap(Bitmap src, int width, int height)
        {
            Bitmap result = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                g.DrawImage(src, 0, 0, width, height);
            }
            return result;
        }

        private void OverlayWindow_Closed(object sender, EventArgs e)
        {
            StopAutoScan();
            if (_lastCapturedBmp != null)
            {
                _lastCapturedBmp.Dispose();
            }
            _lastCapturedBmp = null;
        }
    }
}
