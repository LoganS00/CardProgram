using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CardProgram
{
    public partial class RegionSelectOverlay : Window
    {
        private System.Windows.Point _start;
        private bool _dragging;

        public System.Drawing.Rectangle SelectedRegion { get; private set; }
        public bool Confirmed { get; private set; }

        public RegionSelectOverlay()
        {
            InitializeComponent();
            WindowState = WindowState.Maximized;
            Focusable = true;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Focus();
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(RootCanvas);
            _dragging = true;
            SelectionRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRect, _start.X);
            Canvas.SetTop(SelectionRect, _start.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            CaptureMouse();
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var pos = e.GetPosition(RootCanvas);
            var x = Math.Min(pos.X, _start.X);
            var y = Math.Min(pos.Y, _start.Y);
            var w = Math.Abs(pos.X - _start.X);
            var h = Math.Abs(pos.Y - _start.Y);
            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY;

            var left = Canvas.GetLeft(SelectionRect);
            var top = Canvas.GetTop(SelectionRect);
            var w = SelectionRect.Width;
            var h = SelectionRect.Height;

            if (w < 10 || h < 10) return;

            SelectedRegion = new System.Drawing.Rectangle(
                (int)(left * scaleX),
                (int)(top * scaleY),
                (int)(w * scaleX),
                (int)(h * scaleY));
            Confirmed = true;
            Close();
        }

        private void Overlay_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }
    }
}
