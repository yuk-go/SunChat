using System.Windows;
using Microsoft.Win32;

namespace SunChat;

public partial class SettingsWindow : Window
{
    // Edits a copy; caller reads Result only when ShowDialog() returns true.
    public Settings Result { get; private set; }

    public SettingsWindow(Settings current)
    {
        InitializeComponent();
        NameBox.Text = current.DisplayName;
        GroupBox.Text = current.Group;
        DownloadBox.Text = current.DownloadDir;
        RetentionBox.Text = current.RetentionDays.ToString();
        AutoStartBox.IsChecked = current.AutoStart;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "수신 파일 폴더 선택" };
        if (!string.IsNullOrWhiteSpace(DownloadBox.Text)) dlg.InitialDirectory = DownloadBox.Text;
        if (dlg.ShowDialog() == true) DownloadBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) { System.Windows.MessageBox.Show("이름을 입력하세요.", "환경설정"); return; }
        if (!int.TryParse(RetentionBox.Text.Trim(), out int days) || days < 1 || days > Settings.MaxRetentionDays)
        {
            System.Windows.MessageBox.Show($"대화 보관일수는 1 ~ {Settings.MaxRetentionDays} 사이여야 합니다.", "환경설정");
            return;
        }

        Result = new Settings
        {
            DisplayName = name,
            Group = GroupBox.Text.Trim() is { Length: > 0 } g ? g : "기본",
            DownloadDir = DownloadBox.Text.Trim() is { Length: > 0 } d ? d : Settings.DefaultDownloadDir,
            RetentionDays = days,
            AutoStart = AutoStartBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
