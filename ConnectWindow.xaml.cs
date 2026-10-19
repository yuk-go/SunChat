using System.Windows;

namespace SunChat;

public partial class ConnectWindow : Window
{
    public string Host { get; private set; }
    public int Port { get; private set; }

    public ConnectWindow() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string host = HostBox.Text.Trim();
        if (host.Length == 0) { System.Windows.MessageBox.Show("IP 또는 호스트를 입력하세요.", "IP로 연결"); return; }
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show("포트는 1~65535 사이여야 합니다.", "IP로 연결");
            return;
        }
        Host = host;
        Port = port;
        DialogResult = true;
    }
}
