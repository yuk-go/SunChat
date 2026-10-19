using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SunChat;

// A small bottom-right notification popup (KakaoTalk-style). Fades in, lingers,
// fades out. Clicking it invokes OnClick (used to open the main window).
public partial class ToastWindow : Window
{
    public event Action Clicked;

    private const double EdgeGap = 12;

    public ToastWindow(string title, string body, int index)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;

        var wa = SystemParameters.WorkArea;   // excludes taskbar
        Left = wa.Right - Width - EdgeGap;
        Top = wa.Bottom - Height - EdgeGap - index * (Height + 4); // stack upward

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            life.Tick += (_, _) => { life.Stop(); FadeOut(); };
            life.Start();
        };
    }

    private void FadeOut()
    {
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    private void Border_Click(object sender, RoutedEventArgs e)
    {
        Clicked?.Invoke();
        Close();
    }
}
