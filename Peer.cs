using System.ComponentModel;
using System.Net;

namespace SunChat;

// One discovered user on the LAN. Identity is the Guid, so the same person is
// still "the same peer" after an IP change.
public class Peer : INotifyPropertyChanged
{
    public Guid Id { get; init; }
    public IPAddress Address { get; set; }
    public int TcpPort { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Manual { get; set; }   // added via "IP로 연결" -> remembered across restarts

    private string _name;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); OnChanged(nameof(Display)); } }
    }

    private string _group;
    public string Group
    {
        get => _group;
        set { if (_group != value) { _group = value; OnChanged(nameof(Group)); } }
    }

    private bool _online = true;
    public bool Online
    {
        get => _online;
        set { if (_online != value) { _online = value; OnChanged(nameof(Online)); OnChanged(nameof(Display)); } }
    }

    public string Display => _online
        ? $"{Name}  ({Address}:{TcpPort})"
        : $"{Name}  ({Address}:{TcpPort}) - 비활성";

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
