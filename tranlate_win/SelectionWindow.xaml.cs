using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTranslator
{
    public partial class SelectionWindow : Window
    {
        private Point _startPoint;
        private bool _isDragging = false;

        // Vùng chọn vật lý (sau khi nhân DPI)
        public System.Drawing.Rectangle SelectedRect { get; private set; }

        public SelectionWindow()
        {
            InitializeComponent();

            // Bao phủ toàn bộ màn hình ảo (bao gồm cả đa màn hình)
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isDragging = true;
                _startPoint = e.GetPosition(CanvasOverlay);
                
                Canvas.SetLeft(SelectionBorder, _startPoint.X);
                Canvas.SetTop(SelectionBorder, _startPoint.Y);
                SelectionBorder.Width = 0;
                SelectionBorder.Height = 0;
                SelectionBorder.Visibility = Visibility.Visible;
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                // Nhấp chuột phải để hủy bỏ chọn
                DialogResult = false;
                Close();
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point currentPoint = e.GetPosition(CanvasOverlay);
                
                double x = Math.Min(_startPoint.X, currentPoint.X);
                double y = Math.Min(_startPoint.Y, currentPoint.Y);
                double w = Math.Abs(_startPoint.X - currentPoint.X);
                double h = Math.Abs(_startPoint.Y - currentPoint.Y);

                Canvas.SetLeft(SelectionBorder, x);
                Canvas.SetTop(SelectionBorder, y);
                SelectionBorder.Width = w;
                SelectionBorder.Height = h;
            }
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _isDragging)
            {
                _isDragging = false;
                
                double x = Canvas.GetLeft(SelectionBorder);
                double y = Canvas.GetTop(SelectionBorder);
                double w = SelectionBorder.Width;
                double h = SelectionBorder.Height;

                if (w > 5 && h > 5)
                {
                    // Lấy tỷ lệ DPI để quy đổi sang pixel vật lý
                    double dpiX = 1.0;
                    double dpiY = 1.0;
                    PresentationSource source = PresentationSource.FromVisual(this);
                    if (source != null && source.CompositionTarget != null)
                    {
                        dpiX = source.CompositionTarget.TransformToDevice.M11;
                        dpiY = source.CompositionTarget.TransformToDevice.M22;
                    }

                    // Tọa độ màn hình ảo thực tế cần tính thêm Left/Top của màn hình ảo
                    // X vật lý = (X wpf + Left wpf) * DPI
                    double physicalX = (x + Left) * dpiX;
                    double physicalY = (y + Top) * dpiY;
                    double physicalW = w * dpiX;
                    double physicalH = h * dpiY;

                    SelectedRect = new System.Drawing.Rectangle(
                        (int)Math.Round(physicalX), 
                        (int)Math.Round(physicalY), 
                        (int)Math.Round(physicalW), 
                        (int)Math.Round(physicalH)
                    );

                    DialogResult = true;
                }
                else
                {
                    DialogResult = false;
                }
                Close();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                // Bấm Esc để thoát
                DialogResult = false;
                Close();
            }
        }
    }
}
