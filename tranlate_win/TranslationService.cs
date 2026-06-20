using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator
{
    public class TranslationService
    {
        // Danh sách các ngôn ngữ đích phổ biến hỗ trợ bởi Google Translate
        public static readonly List<KeyValuePair<string, string>> TargetLanguages = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("vi", "Tiếng Việt (Vietnamese)"),
            new KeyValuePair<string, string>("en", "Tiếng Anh (English)"),
            new KeyValuePair<string, string>("ja", "Tiếng Nhật (Japanese)"),
            new KeyValuePair<string, string>("zh-CN", "Tiếng Trung Giản Thể (Chinese Simplified)"),
            new KeyValuePair<string, string>("zh-TW", "Tiếng Trung Phồn Thể (Chinese Traditional)"),
            new KeyValuePair<string, string>("ko", "Tiếng Hàn (Korean)"),
            new KeyValuePair<string, string>("fr", "Tiếng Pháp (French)"),
            new KeyValuePair<string, string>("de", "Tiếng Đức (German)"),
            new KeyValuePair<string, string>("ru", "Tiếng Nga (Russian)")
        };

        // Thực hiện dịch một chuỗi văn bản
        public static string Translate(string text, string sl, string tl)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            try
            {
                string url = string.Format(
                    "https://translate.googleapis.com/translate_a/single?client=gtx&sl={0}&tl={1}&dt=t&q={2}",
                    sl, tl, Uri.EscapeDataString(text));

                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    wc.Encoding = Encoding.UTF8;
                    string response = wc.DownloadString(url);
                    return ParseTranslation(response);
                }
            }
            catch (Exception ex)
            {
                return "[Dịch lỗi: " + ex.Message + "]";
            }
        }

        // Bộ parser JSON sử dụng giải thuật độ sâu ngoặc (Bracket-Depth Tracking) siêu nhẹ
        private static string ParseTranslation(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json))
                    return string.Empty;

                json = json.Trim();
                if (!json.StartsWith("[[["))
                    return string.Empty;

                StringBuilder sb = new StringBuilder();
                int depth = 0;
                bool insideSentence = false;
                bool readTranslation = false;

                for (int i = 0; i < json.Length; i++)
                {
                    char c = json[i];
                    if (c == '[')
                    {
                        depth++;
                        if (depth == 3)
                        {
                            insideSentence = true;
                            readTranslation = false;
                        }
                    }
                    else if (c == ']')
                    {
                        if (depth == 3)
                        {
                            insideSentence = false;
                        }
                        depth--;
                        if (depth == 1)
                        {
                            break; // Đã thoát khỏi mảng các câu chính
                        }
                    }
                    else if (c == '"' && depth == 3 && insideSentence && !readTranslation)
                    {
                        i++; // Bỏ qua ký tự '"' mở đầu
                        int start = i;
                        while (i < json.Length)
                        {
                            // Tìm ký tự '"' kết thúc mà không có dấu gạch chéo ngược '\' phía trước
                            if (json[i] == '"' && json[i - 1] != '\\')
                            {
                                break;
                            }
                            i++;
                        }
                        string translatedText = json.Substring(start, i - start);
                        // Giải mã ký tự unicode escape (ví dụ \u003c) và ký tự điều khiển
                        translatedText = Regex.Unescape(translatedText);
                        
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                        {
                            sb.Append('\n');
                        }
                        sb.Append(translatedText);
                        
                        readTranslation = true; // Đã đọc xong bản dịch của câu này
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "[Lỗi Parse: " + ex.Message + "]";
            }
        }
    }
}
