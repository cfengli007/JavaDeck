using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace JavaDeck.Services;

/// <summary>
/// 轻量转圈遮罩。
/// 约束（重要）：
///   • 只覆盖指定容器内部，绝不替换 Window.Content（否则会盖住标题栏的关闭/最大化按钮）
///   • 不设 owner.IsEnabled = false（那会让整个窗口包括标题栏都点不动）
///   • 遮罩本身对关闭按钮区域不做命中测试（IsHitTestVisible 仅覆盖其父容器范围）
/// </summary>
public sealed class BusyOverlay : IDisposable
{
    private readonly Panel _host;
    private readonly Border _overlay;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    /// <param name="host">要覆盖的容器（通常是窗口里某个 Grid）。不要传 Window。</param>
    /// <param name="message">提示文字</param>
    public BusyOverlay(Panel host, string message = "正在应用…")
    {
        _host = host;

        var ring = new Ellipse
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2.5,
            Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE0)),
            StrokeDashArray = new DoubleCollection { 12, 8 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        var angle = 0.0;
        _timer.Tick += (_, _) =>
        {
            angle = (angle + 18) % 360;
            if (ring.RenderTransform is RotateTransform rt) rt.Angle = angle;
        };
        _timer.Start();

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(ring);
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x26)),
        });

        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(22, 16, 22, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = stack,
            },
            Visibility = Visibility.Visible,
        };

        _host.Children.Add(_overlay);
        Panel.SetZIndex(_overlay, int.MaxValue);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer.Stop();
            _host.Children.Remove(_overlay);
        }
        catch { }
    }
}
