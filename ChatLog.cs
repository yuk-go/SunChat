using System.IO;

namespace SunChat;

// Per-peer chat history. One file per peer id under %AppData%\SunChat\chat\{id}.log.
// Each line: <ISO-8601 local time>\t<S|R>\t<single-line text>
// S = sent by me, R = received.
public class ChatLog
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SunChat", "chat");

    public int RetentionDays { get; set; } = 90;

    private static string FileFor(Guid peerId) => Path.Combine(Dir, peerId.ToString("N") + ".log");

    public void Append(Guid peerId, bool sent, string text)
    {
        if (peerId == Guid.Empty) return; // unknown sender -> nowhere reliable to file it
        try
        {
            Directory.CreateDirectory(Dir);
            // Keep each entry on one line so a multi-line message stays one record.
            string oneLine = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            string line = $"{DateTime.Now:o}\t{(sent ? 'S' : 'R')}\t{oneLine}";
            File.AppendAllText(FileFor(peerId), line + Environment.NewLine);
        }
        catch { /* logging must never break messaging */ }
    }

    // Returns display lines for the peer, after pruning entries older than retention.
    public IReadOnlyList<string> Load(Guid peerId)
    {
        var result = new List<string>();
        if (peerId == Guid.Empty) return result;
        string path = FileFor(peerId);
        if (!File.Exists(path)) return result;

        DateTime cutoff = DateTime.Now.AddDays(-Math.Max(1, RetentionDays));
        var kept = new List<string>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t', 3);
                if (parts.Length < 3) continue;
                if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    continue;
                if (t < cutoff) continue; // expired
                kept.Add(line);
                string who = parts[1] == "S" ? "나" : "상대";
                result.Add($"[{t:MM-dd HH:mm}] {who}: {parts[2]}");
            }
            // Rewrite the file with only the surviving entries (prune on load).
            File.WriteAllLines(path, kept);
        }
        catch { /* corrupt log -> show what parsed */ }
        return result;
    }

    // Prune every peer's log; call at startup.
    public void PruneAll()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.log"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var id))
                Load(id); // Load already rewrites pruned content
    }
}
