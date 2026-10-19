using System.IO;
using System.Text;

namespace SunChat;

// Wire protocol for TCP frames. Manual length-prefixed reads/writes so a file
// payload can stream straight off the socket without a buffering reader eating
// ahead into the raw bytes.
public enum MessageType
{
    Text = 1,
    File = 2,
    Hello = 3,   // identity exchange for manual (cross-subnet) connect
}

public static class Protocol
{
    // ponytail: all-LAN trust model, no auth/encryption on the wire. Add a shared
    // key + AES over the stream if this ever leaves a trusted network.
    public const int DiscoveryPort = 51000;
    public const string DiscoveryMagic = "SunChat1";

    public static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(b, off, n - off);
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }
        return b;
    }

    public static int ReadInt32(Stream s) => BitConverter.ToInt32(ReadExact(s, 4));
    public static long ReadInt64(Stream s) => BitConverter.ToInt64(ReadExact(s, 8));

    public static string ReadStr(Stream s)
    {
        int len = ReadInt32(s);
        if (len < 0 || len > 1 << 20) throw new InvalidDataException("string too long");
        return Encoding.UTF8.GetString(ReadExact(s, len));
    }

    public static void WriteInt32(Stream s, int v) => s.Write(BitConverter.GetBytes(v));
    public static void WriteInt64(Stream s, long v) => s.Write(BitConverter.GetBytes(v));

    public static void WriteStr(Stream s, string v)
    {
        var b = Encoding.UTF8.GetBytes(v ?? "");
        WriteInt32(s, b.Length);
        s.Write(b);
    }
}
