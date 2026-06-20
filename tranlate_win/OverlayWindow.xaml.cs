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

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        public System.Drawing.Rectangle SelectedRect { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }

        private double _dpiX = 1.0;
        private double _dpiY = 1.0;

        private bool _isAutoScanning = false;
        private int _scanIntervalMs = 1000;
        private bool _isTranslating = false;
        private string _lastLayoutSignature = string.Empty;
        private string _lastTextSignature = string.Empty;
        private Dictionary<string, string> _translationCache = new Dictionary<string, string>();
        private byte[] _lastLowResPixels = null;

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

            // Ẩn cửa sổ hiển thị dịch này khỏi tất cả các phần mềm quay/chụp màn hình (để tránh tự dịch chính nó)
            try
            {
                SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);
            }
            catch (Exception ex)
            {
                Log("SetWindowDisplayAffinity failed: " + ex.Message);
            }
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

        private static bool HasAlphanumeric(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetterOrDigit(text[i]))
                    return true;
            }
            return false;
        }

        // Lấy mẫu ảnh thu nhỏ 16x16 để so sánh nhanh sự thay đổi điểm ảnh
        private byte[] GetLowResPixels(Bitmap bmp)
        {
            using (Bitmap small = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                    g.DrawImage(bmp, 0, 0, 16, 16);
                }
                
                System.Drawing.Imaging.BitmapData data = small.LockBits(new Rectangle(0, 0, 16, 16), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = data.Stride * 16;
                    byte[] pixels = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, bytes);
                    return pixels;
                }
                finally
                {
                    small.UnlockBits(data);
                }
            }
        }

        // Kiểm tra xem màn hình có thay đổi pixel nào đáng kể không
        private bool HasScreenChanged(byte[] current, byte[] last)
        {
            if (last == null || current == null) return true;
            if (current.Length != last.Length) return true;
            
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != last[i])
                    return true;
            }
            return false;
        }

        // Thực hiện chụp màn hình và dịch (thủ công)
        public async Task TranslateAsync()
        {
            if (_isTranslating) return;

            Log("TranslateAsync triggered. ROI Rect: " + SelectedRect.X + "," + SelectedRect.Y + "," + SelectedRect.Width + "x" + SelectedRect.Height);

            Dispatcher.Invoke(() => {
                OverlayCanvas.Children.Clear(); // Xóa sạch chữ dịch cũ ngay lập tức khi dịch thủ công
                LoadingIndicator.Visibility = Visibility.Visible;
            });

            try
            {
                Bitmap bmp = await Task.Run(() => OcrService.CaptureScreen(SelectedRect.Left, SelectedRect.Top, SelectedRect.Width, SelectedRect.Height));
                Log("Screen captured successfully.");

                // Lưu mẫu pixel độ phân giải thấp cho lần quét thủ công này
                _lastLowResPixels = GetLowResPixels(bmp);
                
                var lines = await Task.Run(() => OcrService.RecognizeText(bmp, SourceLanguage));
                bmp.Dispose(); // Giải phóng ảnh ngay lập tức sau khi OCR xong
                
                // Cập nhật các signature để đồng bộ hóa với auto-scan (đã lọc ký tự rác)
                StringBuilder sbText = new StringBuilder();
                StringBuilder sbLayout = new StringBuilder();
                if (lines != null)
                {
                    foreach (var line in lines)
                    {
                        if (HasAlphanumeric(line.Text))
                        {
                            sbText.Append(line.Text).Append("|");
                            int rx = (int)(Math.Round(line.X / 3.0) * 3);
                            int ry = (int)(Math.Round(line.Y / 3.0) * 3);
                            sbLayout.Append(line.Text).Append("@").Append(rx).Append(",").Append(ry).Append("|");
                        }
                    }
                }
                _lastTextSignature = sbText.ToString();
                _lastLayoutSignature = sbLayout.ToString();

                if (lines != null && lines.Count > 0)
                {
                    await TranslateWithOcrLinesAsync(lines);
                }
                else
                {
                    Dispatcher.Invoke(() => {
                        OverlayCanvas.Children.Clear();
                        LoadingIndicator.Visibility = Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                Log("TranslateAsync ERROR: " + ex.Message + "\n" + ex.StackTrace);
                Dispatcher.Invoke(() => {
                    LoadingIndicator.Visibility = Visibility.Collapsed;
                });
            }
        }

        // Tiến trình xử lý chính dựa trên danh sách dòng chữ đã có sẵn từ OCR
        private async Task TranslateWithOcrLinesAsync(List<OcrTextLine> lines)
        {
            _isTranslating = true;
            Log("TranslateWithOcrLinesAsync started. Lines: " + lines.Count);

            try
            {
                // 1. Gom các dòng chữ lại để dịch trong 1 request duy nhất (tối ưu hóa API)
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

                    // Lưu vào cache dịch thuật
                    if (!string.IsNullOrEmpty(lines[i].Text))
                    {
                        _translationCache[lines[i].Text] = lines[i].TranslatedText;
                    }
                }

                // 2. Vẽ đè lên Canvas
                DrawTranslationLines(lines);
            }
            catch (Exception ex)
            {
                Log("TranslateWithOcrLinesAsync ERROR: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                Dispatcher.Invoke(() => {
                    LoadingIndicator.Visibility = Visibility.Collapsed;
                });
                _isTranslating = false;
            }
        }

        // Tách biệt logic vẽ chữ dịch lên Canvas (chạy đồng bộ trên UI thread)
        private void DrawTranslationLines(List<OcrTextLine> lines)
        {
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

                    // Sử dụng màu nền đã được dò sẵn trên luồng phụ
                    System.Drawing.Color gdiColor = line.BackgroundColor;
                    System.Windows.Media.Color bgColor = System.Windows.Media.Color.FromArgb(gdiColor.A, gdiColor.R, gdiColor.G, gdiColor.B);
                    
                    // Quyết định màu chữ dựa trên độ sáng màu nền (Luminance) để tăng độ tương phản
                    double luminance = 0.299 * bgColor.R + 0.587 * bgColor.G + 0.114 * bgColor.B;
                    var textColor = luminance > 128 
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)) 
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));

                    Log(string.Format("Vẽ chữ: '{0}' -> '{1}' tại ({2},{3}) [{4}x{5}], BG Color: {6}", 
                        line.Text, line.TranslatedText, wpfX, wpfY, wpfW, wpfH, bgColor.ToString()));

                    // Tạo Border đè lên chữ cũ
                    Border border = new Border
                    {
                        Width = wpfW + 6,
                        Height = wpfH + 6,
                        Background = new SolidColorBrush(bgColor),
                        CornerRadius = new CornerRadius(2),
                        Padding = new Thickness(2)
                    };

                    // Tạo TextBlock chữ dịch
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
                    // Chụp ảnh màn hình ở luồng phụ để không block UI Thread
                    Bitmap currentBmp = await Task.Run(() => OcrService.CaptureScreen(SelectedRect.Left, SelectedRect.Top, SelectedRect.Width, SelectedRect.Height));
                    
                    // So sánh nhanh pixel độ phân giải thấp để xem có thay đổi gì trên màn hình không (Tier 1)
                    byte[] currentLowRes = GetLowResPixels(currentBmp);
                    if (!HasScreenChanged(currentLowRes, _lastLowResPixels))
                    {
                        currentBmp.Dispose();
                        continue;
                    }
                    _lastLowResPixels = currentLowRes;
                    
                    // 1. Chạy OCR cục bộ trước (rất nhanh, ~30ms, offline hoàn toàn, không tốn mạng)
                    var lines = await Task.Run(() => OcrService.RecognizeText(currentBmp, SourceLanguage));
                    currentBmp.Dispose(); // Giải phóng ảnh ngay lập tức sau khi OCR xong
                    
                    // 2. Tạo chữ ký đại diện cho nội dung chữ và vị trí (đã lọc ký tự rác)
                    StringBuilder sbText = new StringBuilder();
                    StringBuilder sbLayout = new StringBuilder();
                    if (lines != null)
                    {
                        foreach (var line in lines)
                        {
                            if (HasAlphanumeric(line.Text))
                            {
                                sbText.Append(line.Text).Append("|");
                                int rx = (int)(Math.Round(line.X / 3.0) * 3);
                                int ry = (int)(Math.Round(line.Y / 3.0) * 3);
                                sbLayout.Append(line.Text).Append("@").Append(rx).Append(",").Append(ry).Append("|");
                            }
                        }
                    }
                    string currentTextSig = sbText.ToString();
                    string currentLayoutSig = sbLayout.ToString();

                    // 3. Kiểm tra thay đổi bố cục/vị trí hoặc nội dung chữ
                    if (currentLayoutSig != _lastLayoutSignature)
                    {
                        _lastLayoutSignature = currentLayoutSig;

                        if (lines != null && lines.Count > 0)
                        {
                            // 4. Nếu vị trí thay đổi nhưng NỘI DUNG CHỮ vẫn giữ nguyên, dịch offline từ Cache lập tức (Không gọi API dịch)
                            if (currentTextSig == _lastTextSignature)
                            {
                                Log("Text signature matches. Updating text coordinates offline from cache.");
                                foreach (var line in lines)
                                {
                                    string translatedText;
                                    if (_translationCache.TryGetValue(line.Text, out translatedText))
                                    {
                                        line.TranslatedText = translatedText;
                                    }
                                    else
                                    {
                                        line.TranslatedText = line.Text;
                                    }
                                }
                                // Vẽ lại vị trí mới ngay lập tức
                                DrawTranslationLines(lines);
                            }
                            else
                            {
                                // Nếu nội dung chữ thực sự thay đổi, gọi API dịch mới
                                _lastTextSignature = currentTextSig;
                                await TranslateWithOcrLinesAsync(lines);
                            }
                        }
                        else
                        {
                            // Nếu không có chữ nào, xóa sạch canvas ngay lập tức
                            _lastTextSignature = currentTextSig;
                            Dispatcher.Invoke(() => {
                                OverlayCanvas.Children.Clear();
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Auto-scan loop error: " + ex.Message + "\n" + ex.StackTrace);
                }
            }
        }

        // Dừng chế độ Auto-scan
        public void StopAutoScan()
        {
            _isAutoScanning = false;
        }

        private void OverlayWindow_Closed(object sender, EventArgs e)
        {
            StopAutoScan();
        }
    }
}
