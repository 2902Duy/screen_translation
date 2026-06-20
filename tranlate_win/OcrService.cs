using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Globalization;
using Windows.Storage.Streams;

namespace ScreenTranslator
{
    public class OcrTextLine
    {
        public string Text { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string TranslatedText { get; set; }
        public System.Drawing.Color BackgroundColor { get; set; }
    }

    public class OcrService
    {
        // Chụp ảnh màn hình tại vùng chỉ định
        public static Bitmap CaptureScreen(int left, int top, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(left, top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        // Chuyển đổi Bitmap của GDI+ thành SoftwareBitmap của WinRT (tối ưu hóa trực tiếp bộ nhớ, ~0ms)
        public static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bmp)
        {
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height), 
                ImageLockMode.ReadOnly, 
                PixelFormat.Format32bppArgb);
            try
            {
                int bytes = bmpData.Stride * bmp.Height;
                byte[] rgbValues = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytes);
                
                // Chuyển mảng byte thành WinRT IBuffer
                Windows.Storage.Streams.IBuffer buffer = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(rgbValues);
                
                // Tạo SoftwareBitmap trực tiếp
                SoftwareBitmap softwareBitmap = new SoftwareBitmap(
                    BitmapPixelFormat.Bgra8, 
                    bmp.Width, 
                    bmp.Height, 
                    BitmapAlphaMode.Premultiplied);
                
                softwareBitmap.CopyFromBuffer(buffer);
                return softwareBitmap;
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        // Thuật toán lấy mẫu dò màu nền ở viền bounding box (chạy trên luồng phụ)
        public static System.Drawing.Color SampleBackgroundColor(Bitmap bmp, int x, int y, int w, int h)
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

            return System.Drawing.Color.FromArgb(255, avgR, avgG, avgB);
        }

        private static void Log(string msg)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");
                File.AppendAllText(logPath, string.Format("[{0}] [OCR] {1}\r\n", DateTime.Now.ToString("HH:mm:ss.fff"), msg));
            }
            catch {}
        }

        // Chạy nhận diện chữ trên ảnh Bitmap với ngôn ngữ tùy chọn
        public static async Task<List<OcrTextLine>> RecognizeText(Bitmap bmp, string langTag)
        {
            Log("RecognizeText started. Lang: " + langTag + ", Size: " + bmp.Width + "x" + bmp.Height);
            List<OcrTextLine> lines = new List<OcrTextLine>();
            SoftwareBitmap softwareBitmap = null;

            try
            {
                softwareBitmap = ConvertToSoftwareBitmap(bmp);
                Log("Converted GDI+ Bitmap to SoftwareBitmap.");
                
                OcrEngine engine;
                if (!string.IsNullOrEmpty(langTag) && OcrEngine.IsLanguageSupported(new Language(langTag)))
                {
                    engine = OcrEngine.TryCreateFromLanguage(new Language(langTag));
                }
                else
                {
                    Log("Lang not supported or empty: " + langTag + ". Falling back to UserProfileLanguages.");
                    engine = OcrEngine.TryCreateFromUserProfileLanguages();
                }

                if (engine == null)
                {
                    Log("OcrEngine is null!");
                    return lines;
                }
                Log("OcrEngine initialized with language: " + engine.RecognizerLanguage.LanguageTag);

                Log("Invoking RecognizeAsync...");
                OcrResult result = await engine.RecognizeAsync(softwareBitmap).AsTask().ConfigureAwait(false);
                Log("RecognizeAsync completed.");
                if (result == null)
                {
                    Log("OcrResult is null!");
                    return lines;
                }
                if (result.Lines == null)
                {
                    Log("OcrResult.Lines is null!");
                    return lines;
                }

                Log("Processing lines: " + result.Lines.Count);
                foreach (var line in result.Lines)
                {
                    if (line.Words == null || line.Words.Count == 0)
                        continue;

                    // 1. Sắp xếp các từ theo thứ tự từ trái qua phải để xử lý khoảng cách
                    var sortedWords = new List<OcrWord>(line.Words);
                    sortedWords.Sort((w1, w2) => w1.BoundingRect.Left.CompareTo(w2.BoundingRect.Left));

                    // 2. Phân nhóm các từ thành các phân đoạn (segments) dựa trên khoảng cách ngang (horizontal gap)
                    var segments = new List<List<OcrWord>>();
                    var currentSegment = new List<OcrWord> { sortedWords[0] };
                    segments.Add(currentSegment);

                    for (int i = 1; i < sortedWords.Count; i++)
                    {
                        var prevWord = sortedWords[i - 1];
                        var currWord = sortedWords[i];

                        double gap = currWord.BoundingRect.Left - prevWord.BoundingRect.Right;
                        double prevHeight = prevWord.BoundingRect.Height;

                        // Nếu khoảng cách ngang vượt quá 2.0 lần chiều cao từ trước đó, tách thành phân đoạn mới (cột mới)
                        if (gap > 2.0 * prevHeight)
                        {
                            currentSegment = new List<OcrWord> { currWord };
                            segments.Add(currentSegment);
                        }
                        else
                        {
                            currentSegment.Add(currWord);
                        }
                    }

                    // 3. Tạo OcrTextLine riêng biệt cho mỗi phân đoạn
                    foreach (var segment in segments)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        System.Text.StringBuilder segmentText = new System.Text.StringBuilder();

                        foreach (var word in segment)
                        {
                            var rect = word.BoundingRect;
                            if (rect.Left < minX) minX = rect.Left;
                            if (rect.Top < minY) minY = rect.Top;
                            if (rect.Right > maxX) maxX = rect.Right;
                            if (rect.Bottom > maxY) maxY = rect.Bottom;

                            segmentText.Append(word.Text).Append(" ");
                        }

                        lines.Add(new OcrTextLine
                        {
                            Text = segmentText.ToString().Trim(),
                            X = minX,
                            Y = minY,
                            Width = maxX - minX,
                            Height = maxY - minY,
                            BackgroundColor = SampleBackgroundColor(bmp, (int)minX, (int)minY, (int)(maxX - minX), (int)(maxY - minY))
                        });
                    }
                }
                Log("Finished processing lines. Count: " + lines.Count);
            }
            catch (Exception ex)
            {
                Log("OCR ERROR: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                if (softwareBitmap != null)
                {
                    softwareBitmap.Dispose();
                }
            }

            return lines;
        }

        // Lấy danh sách ngôn ngữ OCR được hỗ trợ trên máy
        public static List<KeyValuePair<string, string>> GetSupportedLanguages()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                {
                    list.Add(new KeyValuePair<string, string>(lang.LanguageTag, lang.DisplayName));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("GetLanguages Error: " + ex.Message);
            }

            // Nếu trống, thêm mặc định
            if (list.Count == 0)
            {
                list.Add(new KeyValuePair<string, string>("en-US", "English (United States)"));
            }
            return list;
        }
    }
}
