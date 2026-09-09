using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace AutoMakeTimeline;

public sealed record Hit(string Job, long? Damage, string Statuses, uint TargetId = 0);
public sealed record TimelineRow(int No, double Seconds, string Enemy, string Action, Hit Hit,
    uint ActionId = 0, uint SourceId = 0, uint Sequence = 0, string Origin = "ActionEffect");
public static class RecordingRules
{
    public static string ActionName(uint id, uint category, string? name) =>
        category == 1 || id == 7 ? "攻撃（AA)" : string.IsNullOrEmpty(name) ? $"Action#{id}" : name;
    public static bool IncludeStatus(uint id, bool isFcBuff) => id != 0 && id != 48 && !isFcBuff;
    public static TimelineRow NormalizeHistory(TimelineRow row, ISet<string> autoAttacks, ISet<string> excludedStatuses) =>
        row with
        {
            Action = autoAttacks.Contains(row.Action) ? "攻撃（AA)" : row.Action,
            Hit = row.Hit with { Statuses = string.Join(",", row.Hit.Statuses.Split(',').Where(s => !excludedStatuses.Contains(s))) },
        };
}
public sealed class AttackGrouper
{
    private readonly Dictionary<(uint Source, uint Sequence, uint Action, long Fallback, string Origin), (int No, double Seconds)> groups = [];
    private readonly HashSet<(int No, uint Target)> recorded = [];
    private int nextNo;
    public IEnumerable<TimelineRow> Add(uint source, uint sequence, uint actionId, long tick, double seconds, string enemy, string action, IEnumerable<Hit> hits, string origin = "ActionEffect")
    {
        var key = (source, sequence, actionId, sequence == 0 ? tick : 0, origin);
        if (!groups.TryGetValue(key, out var group)) groups[key] = group = (++nextNo, seconds);
        foreach (var hit in hits)
            if (recorded.Add((group.No, hit.TargetId))) yield return new(group.No, group.Seconds, enemy, action, hit, actionId, source, sequence, origin);
    }
}
public sealed class Encounter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Content { get; set; } = "不明";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public double DurationSeconds { get; set; }
    public string EndReason { get; set; } = "記録中";
    public string CsvPath { get; set; } = "";
    public List<TimelineRow> Rows { get; set; } = [];
}

public static class Csv
{
    public const string Header = "No,時間,エネミー名称,攻撃名称,被ダメージ,被ダメージ対象のジョブ,バフ・デバフステータス情報";
    public static string Time(double seconds) => $"{(long)Math.Max(0, seconds) / 60:00}:{(long)Math.Max(0, seconds) % 60:00}";
    // Neutralize spreadsheet formulas without changing ordinary game names.
    public static string Quote(string s)
    {
        if (s.Length > 0 && ("=+-@".Contains(s.TrimStart().FirstOrDefault()) || s[0] is '\t' or '\r' or '\n')) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
    public static string Line(TimelineRow r) => string.Join(",", new[]
    {
        r.No.ToString(CultureInfo.InvariantCulture), Time(r.Seconds), r.Enemy, r.Action,
        r.Hit.Damage?.ToString("N0", CultureInfo.InvariantCulture) ?? "", r.Hit.Job, r.Hit.Statuses,
    }.Select(Quote));
    public static StreamWriter Open(string path)
    {
        var w = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
        try { w.WriteLine(Header); return w; }
        catch { w.Dispose(); throw; }
    }
    public static string Safe(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(s.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (result.Length > 70) result = result[..70];
        return result.Length == 0 ? "不明" : result;
    }
    public static string FileName(Encounter e) => $"{Safe(e.Content)}_{e.Start:yyyyMMdd-HHmmss-fff}_{e.End:yyyyMMdd-HHmmss-fff}_{(int)e.DurationSeconds / 3600:00}h{(int)e.DurationSeconds / 60 % 60:00}m{(int)e.DurationSeconds % 60:00}s_AMTLOG.csv";
    public static string Unique(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + $"_{i}.csv");
        return path;
    }
    public static string Export(Encounter e, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Unique(folder, FileName(e));
        using var writer = Open(path);
        foreach (var r in e.Rows) writer.WriteLine(Line(r));
        return path;
    }
}

public static class DamageDecoder
{
    public static long? Decode(byte type, byte param3, byte param4, ushort value)
    {
        // Recipient/source flags are handled by DamageBatch, not discarded here.
        return type switch
        {
            3 or 5 or 6 => value + ((param4 & 0x40) != 0 ? (long)param3 << 16 : 0),
            1 or 2 or 7 => 0,
            _ => null,
        };
    }
}

public sealed record RoutedDamage(uint Source, uint Target, long Amount, bool Redirected);
public sealed class DamageBatch(ISet<uint> partyIds)
{
    private readonly Dictionary<(uint Source, uint Target), RoutedDamage> entries = [];
    public IEnumerable<RoutedDamage> Entries => entries.Values;
    public void Add(uint caster, uint listedTarget, byte type, byte param3, byte param4, ushort value)
    {
        var amount = DamageDecoder.Decode(type, param3, param4, value);
        if (!amount.HasValue) return;
        var recipient = (param4 & 0x80) != 0 ? caster : listedTarget;
        if (!partyIds.Contains(recipient)) return;
        var origin = (param4 & 0x20) != 0 ? listedTarget : caster;
        var key = (origin, recipient);
        var previous = entries.GetValueOrDefault(key);
        entries[key] = new(origin, recipient, (previous?.Amount ?? 0) + amount.Value,
            (previous?.Redirected ?? false) || (param4 & 0xA0) != 0);
    }
}

public static class PeriodicDamage
{
    // ActorControl category in game 7.1+; param2 is the damage amount.
    public const uint Category = 0x605;
    public static long? Decode(uint category, uint amount) => category == Category ? amount : null;
    public static string Name(uint statusId, string? statusName) => statusId == 0 ? "継続ダメージ（合算）" :
        $"{(string.IsNullOrWhiteSpace(statusName) ? $"Status#{statusId}" : statusName)}（継続ダメージ）";
}

public sealed class HistoryStore(string folder)
{
    public List<Encounter> Items { get; } = [];
    public string? Error { get; private set; }
    public void Load()
    {
        Directory.CreateDirectory(folder);
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var e = JsonSerializer.Deserialize<Encounter>(File.ReadAllText(path));
                if (e != null) Items.Add(e);
            }
            catch (Exception ex) { Error = $"履歴の読み込み失敗: {ex.Message}"; }
        }
        Items.Sort((a, b) => b.Start.CompareTo(a.Start));
        Trim();
    }
    public void Save(Encounter e)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, e.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(e));
        File.Move(path + ".tmp", path, true);
        Items.RemoveAll(x => x.Id == e.Id);
        Items.Insert(0, e);
        Trim();
    }
    private void Trim()
    {
        while (Items.Count > 100)
        {
            var old = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            try { File.Delete(Path.Combine(folder, old.Id + ".json")); }
            catch (Exception ex) { Error = "古い履歴を削除できません: " + ex.Message; }
        }
    }
}

public static class DownloadsFolder
{
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
    public static string Get()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var hr = SHGetKnownFolderPath(in id, 0, 0, out var ptr);
        Marshal.ThrowExceptionForHR(hr);
        try { return Marshal.PtrToStringUni(ptr) ?? throw new IOException("ダウンロードフォルダを取得できません。"); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }
}
