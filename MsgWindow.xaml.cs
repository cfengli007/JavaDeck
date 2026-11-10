using System.Windows;
using System.Windows.Media;

namespace JavaDeck;

/// <summary>深色主题的消息框，内容可选中/复制（替代系统 MessageBox）。</summary>
public partial class MsgWindow : Window
{
    public MsgWindow(string title, string body, bool isError = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;

        IconBox.Background = new SolidColorBrush(isError
            ? Color.FromRgb(0xE0, 0x5C, 0x5C)
            : Color.FromRgb(0x4C, 0x8D, 0xFF));

        if (isError) Title = "JavaDeck - 错误";
    }

    public static void Show(Window? owner, string title, string body, bool isError = false)
    {
        var w = new MsgWindow(title, body, isError);
        if (owner != null && owner.IsVisible) w.Owner = owner;
        w.ShowDialog();
    }

    public bool Confirmed { get; private set; }

    /// <summary>深色确认框，返回 true 表示用户点了"确定"。</summary>
    public static bool Confirm(Window? owner, string title, string body)
    {
        var w = new MsgWindow(title, body, false);
        if (owner != null && owner.IsVisible) w.Owner = owner;
        w.CancelButton.Visibility = Visibility.Visible;
        w.ShowDialog();
        return w.Confirmed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TitleText.Text + "\n\n" + BodyText.Text);
            // 给个视觉反馈
            if (sender is System.Windows.Controls.Button b)
            {
                b.Content = "已复制 ✓";
                var t = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (_, _) =>
                {
                    b.Content = "复制全部";
                    t.Stop();
                };
                t.Start();
            }
        }
        catch { }
    }
}