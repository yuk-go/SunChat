using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SunChat;

public partial class MainWindow : Window
{
    private readonly NetworkService _net;
    private readonly Settings _settings;
    private readonly ObservableCollection<Peer> _peers = new();
    private readonly ICollectionView _peerView;
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(15);

    private System.Windows.Forms.NotifyIcon _tray;
    private bool _reallyExit;
    private bool _toldAboutTray;

    private readonly ChatLog _log = new();
    private Guid _selectedPeerId = Guid.Empty;

    public MainWindow()
    {
        InitializeComponent();

        _settings = Settings.Load();
        _log.RetentionDays = _settings.RetentionDays;
        _log.PruneAll();
        _net = new NetworkService(_settings.DisplayName)
        {
            Group = _settings.Group,
            DownloadDir = _settings.DownloadDir,
            RequestedPort = _settings.TcpPort,
        };
        SameGroupOnly.IsChecked = _settings.SameGroupOnly;
        RefreshInfo();

        _peerView = CollectionViewSource.GetDefaultView(_peers);
        _peerView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Peer.Group)));
        // active peers first, then by name (grey/inactive sink to the bottom)
        _peerView.SortDescriptions.Add(new SortDescription(nameof(Peer.Online), ListSortDirection.Descending));
        _peerView.SortDescriptions.Add(new SortDescription(nameof(Peer.Name), ListSortDirection.Ascending));
        _peerView.Filter = PeerFilter;
        PeerList.ItemsSource = _peerView;

        _net.PeerSeen += OnPeerSeen;
        _net.TextReceived += OnTextReceived;
        _net.FileReceived += OnFileReceived;
        _net.Error += msg => Dispatch(() => SetStatus(msg));

        _expiry.Tick += (_, _) => ExpirePeers();

        Loaded += async (_, _) =>
        {
            try
            {
                _net.Start();
                _expiry.Start();
                SetStatus($"대기 중 — TCP 포트 {_net.TcpPort}, 발견 포트 {Protocol.DiscoveryPort}");
                await Task.Delay(500);
                await RefreshPeersAsync();
            }
            catch (Exception ex) { SetStatus($"시작 실패: {ex.Message}"); }
        };
        // Refresh the online/offline state when the window is restored or re-focused.
        StateChanged += async (_, _) => { if (WindowState != WindowState.Minimized) await RefreshPeersAsync(); };
        Activated += async (_, _) => await RefreshPeersAsync();
        SetupTray();
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _settings.Save();
            foreach (var t in _toasts.ToArray()) t.Close();
            _tray?.Dispose();
            _net.Dispose();
        };
    }

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => ShowFromTray());
        menu.Items.Add("종료", null, (_, _) => { _reallyExit = true; Close(); });

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "SunChat",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/sun.ico");
            using var s = System.Windows.Application.GetResourceStream(uri).Stream;
            return new System.Drawing.Icon(s);
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    // Close button (X) hides to tray instead of exiting; real exit goes through the
    // tray menu's 종료, which sets _reallyExit.
    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_reallyExit) return;
        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray.ShowBalloonTip(3000, "SunChat", "백그라운드에서 계속 실행 중입니다. 트레이 아이콘을 두 번 눌러 엽니다.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // bring to front
    }

    private void RefreshInfo() =>
        InfoLabel.Text = $"내 정보:  {_net.DisplayName}   /   그룹  {_net.Group}";

    // ---- network events (background threads) ----

    private void OnPeerSeen(Guid id, string name, string group, IPAddress addr, int port) => Dispatch(() =>
    {
        var p = _peers.FirstOrDefault(x => x.Id == id);
        if (p == null)
        {
            _peers.Add(new Peer { Id = id, Name = name, Group = group, Address = addr, TcpPort = port, LastSeen = DateTime.UtcNow });
        }
        else
        {
            bool resort = p.Group != group || !p.Online;
            p.Name = name; p.Group = group; p.Address = addr; p.TcpPort = port; p.LastSeen = DateTime.UtcNow;
            p.Online = true;
            if (resort) _peerView.Refresh(); // re-bucket / move back up to active
        }
    });

    private void OnTextReceived(Guid senderId, string sender, string content) => Dispatch(() =>
    {
        _log.Append(senderId, sent: false, content);
        if (senderId == _selectedPeerId) AppendLive("상대", content);
        Notify(senderId, sender, content);
    });

    private void OnFileReceived(Guid senderId, string sender, string savedPath, long size) => Dispatch(() =>
    {
        string note = $"[파일 수신] {System.IO.Path.GetFileName(savedPath)} ({size:N0} 바이트) → {savedPath}";
        _log.Append(senderId, sent: false, note);
        if (senderId == _selectedPeerId) AppendLive("상대", note);
        Notify(senderId, $"{sender} (파일)", System.IO.Path.GetFileName(savedPath));
    });

    private readonly List<ToastWindow> _toasts = new();
    private const int MaxToasts = 4;

    // KakaoTalk-style toast at the bottom-right, unless this window is already the
    // foreground window with that conversation open.
    private void Notify(Guid senderId, string title, string body)
    {
        bool focusedOnSender = IsActive && IsVisible && senderId == _selectedPeerId;
        if (focusedOnSender) return;

        if (_toasts.Count >= MaxToasts) { _toasts[0].Close(); }
        var toast = new ToastWindow(title, body, _toasts.Count);
        toast.Clicked += () => { SelectPeer(senderId); ShowFromTray(); };
        toast.Closed += (_, _) => { _toasts.Remove(toast); RestackToasts(); };
        _toasts.Add(toast);
        toast.Show();
    }

    private void RestackToasts()
    {
        var wa = SystemParameters.WorkArea;
        for (int i = 0; i < _toasts.Count; i++)
            _toasts[i].Top = wa.Bottom - _toasts[i].Height - 12 - i * (_toasts[i].Height + 4);
    }

    private void SelectPeer(Guid id)
    {
        var p = _peers.FirstOrDefault(x => x.Id == id);
        if (p != null) PeerList.SelectedItem = p;
    }

    // ---- UI handlers ----

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var r = dlg.Result;
        _settings.DisplayName = r.DisplayName;
        _settings.Group = r.Group;
        _settings.DownloadDir = r.DownloadDir;
        _settings.RetentionDays = r.RetentionDays;
        _settings.AutoStart = r.AutoStart;
        _settings.Save();
        _settings.ApplyAutoStart();
        _log.RetentionDays = r.RetentionDays;

        _net.DisplayName = r.DisplayName;
        _net.Group = r.Group;
        _net.DownloadDir = r.DownloadDir;

        RefreshInfo();
        _peerView.Refresh(); // "same group only" filter keys off my group
        SetStatus("환경설정 저장됨");
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshPeersAsync(force: true);

    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _refreshing;

    // Actively probe every peer's TCP port to see if SunChat is really running
    // there (black = online, grey = off). Works across subnets unlike broadcast.
    // Debounced so focus/restore events don't spam probes; the button bypasses it.
    private async Task RefreshPeersAsync(bool force = false)
    {
        if (_refreshing) return;
        if (!force && DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(2)) return;
        _refreshing = true;
        _lastRefresh = DateTime.UtcNow;
        try { await ProbeAllAsync(); } finally { _refreshing = false; }
    }

    private async Task ProbeAllAsync()
    {
        var snapshot = _peers.ToArray();
        if (snapshot.Length == 0) { SetStatus("접속자 없음"); return; }
        SetStatus("새로고침 중...");
        var checks = snapshot.Select(async p =>
        {
            bool alive = await NetworkService.IsAliveAsync(p.Address, p.TcpPort);
            Dispatch(() => { if (p.Online != alive) { p.Online = alive; } if (alive) p.LastSeen = DateTime.UtcNow; });
        });
        await Task.WhenAll(checks);
        Dispatch(() => { _peerView.Refresh(); SetStatus("새로고침 완료"); });
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetStatus($"연결 중: {dlg.Host}:{dlg.Port} ...");
        try
        {
            await _net.ConnectManualAsync(dlg.Host, dlg.Port);
            SetStatus($"연결됨: {dlg.Host}:{dlg.Port}");
        }
        catch (Exception ex) { SetStatus($"연결 실패: {ex.Message}"); }
    }

    private void SameGroupOnly_Changed(object sender, RoutedEventArgs e)
    {
        _peerView?.Refresh();
        if (_settings != null) { _settings.SameGroupOnly = SameGroupOnly.IsChecked == true; _settings.Save(); }
    }

    private bool PeerFilter(object o)
    {
        if (SameGroupOnly?.IsChecked != true) return true;
        return o is Peer p &&
               string.Equals(p.Group?.Trim(), _net.Group?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void PeerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PeerList.SelectedItem is not Peer p) { _selectedPeerId = Guid.Empty; return; }
        _selectedPeerId = p.Id;
        SetStatus($"대상: {p.Display}");
        LoadConversation(p.Id);
    }

    private void LoadConversation(Guid peerId)
    {
        ChatBox.Clear();
        foreach (var line in _log.Load(peerId))
            ChatBox.AppendText(line + Environment.NewLine);
        ChatBox.ScrollToEnd();
    }

    // Append to the on-screen chat for the currently open conversation.
    private void AppendLive(string who, string text)
    {
        ChatBox.AppendText($"[{DateTime.Now:MM-dd HH:mm}] {who}: {text}{Environment.NewLine}");
        ChatBox.ScrollToEnd();
    }

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendBtn_Click(sender, e); e.Handled = true; }
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PeerList.SelectedItem is not Peer p) { SetStatus("받을 사람을 먼저 선택하세요."); return; }
        string text = InputBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            await _net.SendTextAsync(p, text);
            _log.Append(p.Id, sent: true, text);
            if (p.Id == _selectedPeerId) AppendLive("나", text);
            InputBox.Clear();
        }
        catch (Exception ex) { SetStatus($"전송 실패: {ex.Message}"); }
    }

    private async void FileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PeerList.SelectedItem is not Peer p) { SetStatus("받을 사람을 먼저 선택하세요."); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() != true) return;
        try
        {
            var fi = new FileInfo(dlg.FileName);
            SetStatus($"전송 중: {fi.Name} (0%)");
            var progress = new Progress<long>(sent =>
                SetStatus($"전송 중: {fi.Name} ({(fi.Length == 0 ? 100 : sent * 100 / fi.Length)}%)"));
            await _net.SendFileAsync(p, dlg.FileName, progress);
            string note = $"[파일 전송] {fi.Name} ({fi.Length:N0} 바이트)";
            _log.Append(p.Id, sent: true, note);
            if (p.Id == _selectedPeerId) AppendLive("나", note);
            SetStatus("전송 완료");
        }
        catch (Exception ex) { SetStatus($"파일 전송 실패: {ex.Message}"); }
    }

    // ---- helpers ----

    // Peers that stop signaling go inactive (grey, listed last) instead of being
    // removed — so a manually IP-connected peer on another subnet stays visible.
    private void ExpirePeers()
    {
        var now = DateTime.UtcNow;
        bool changed = false;
        foreach (var p in _peers)
        {
            bool online = now - p.LastSeen <= PeerTimeout;
            if (p.Online != online) { p.Online = online; changed = true; }
        }
        if (changed) _peerView.Refresh();
    }

    private void SetStatus(string s) => StatusText.Text = s;
    private void Dispatch(Action a) => Dispatcher.Invoke(a);
}
