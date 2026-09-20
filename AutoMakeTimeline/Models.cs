using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace AutoMakeTimeline;

public enum CaptureMode { Normal, Replay }
public static class CapturePolicy
{
    public static bool Allows(CaptureMode mode, bool replayActive, bool paused) =>
        mode == CaptureMode.Replay ? replayActive && !paused : !replayActive;
}
public sealed class ReplayCursor
{
    private double? position;
    private double wall;
    public bool Observe(double seconds, double wallSeconds, double speed)
    {
        var seek = position.HasValue && (seconds < position.Value - .05 ||
            seconds - position.Value > Math.Max(2, Math.Max(0, wallSeconds - wall) * Math.Max(1, speed) + 2));
        position = seconds; wall = wallSeconds;
        return seek;
    }
    public void Reset() { position = null; wall = 0; }
}

public sealed record StatusInfo(uint Id, string Name, uint Icon, byte Category, bool IsCompany = false,
    bool Defensive = false, bool Barrier = false, bool AltersDamage = false,
    decimal? ReductionPhysical = null, decimal? ReductionMagical = null, string? ReductionSource = null, uint ReductionActionId = 0);
public sealed record Hit(string Job, long? Damage, string Statuses, uint TargetId = 0, List<StatusInfo>? Effects = null,
    uint? HpBefore = null, uint? MaxHp = null, byte? ShieldPercent = null, uint? RemainingHp = null,
    byte? ShieldAfter = null, byte DamageType = 0, bool SpecialDamage = false, long CaptureTick = 0,
    long? BarrierTotal = null, string BarrierSource = "未取得（旧履歴または未連携）")
{
    public string Buffs => Effects == null ? "取得不可" : string.Join(",", Effects.Where(s => s.Category == 1 && DefenseRules.IsDefensive(s)).Select(s => DefenseRules.StatusLabel(s, this)));
    public string Debuffs => Effects == null ? "" : string.Join(",", Effects.Where(s => s.Category == 2).Select(s => s.Name));
    public bool Fatal => RemainingHp == 0 && HpBefore > 0 && Damage > 0;
    public long? BarrierEstimate => MaxHp.HasValue && ShieldPercent.HasValue ? (long)MaxHp.Value * ShieldPercent.Value / 100 : null;
}
public sealed class StatusCatalog
{
    private readonly Dictionary<uint, StatusInfo> byId;
    private readonly Dictionary<string, StatusInfo> byName;
    private readonly HashSet<uint> excluded;
    public HashSet<string> ExcludedNames { get; }
    public StatusCatalog(IEnumerable<StatusInfo> source)
    {
        var all = source.ToList();
        byId = all.ToDictionary(s => s.Id);
        // Squadron/consumable versions share the FC icon but lack IsFcBuff.
        var companyIcons = all.Where(s => s.IsCompany && s.Icon != 0).Select(s => s.Icon).ToHashSet();
        excluded = all.Where(s => !RecordingRules.IncludeStatus(s.Id, s.IsCompany) ||
            (s.Category == 1 && companyIcons.Contains(s.Icon))).Select(s => s.Id).ToHashSet();
        ExcludedNames = all.Where(s => excluded.Contains(s.Id)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        byName = all.Where(s => !string.IsNullOrEmpty(s.Name)).GroupBy(s => s.Name).ToDictionary(g => g.Key,
            g => g.Select(s => s.Category).Distinct().Count() == 1 ? g.First() : new StatusInfo(0, g.Key, 0, 0), StringComparer.Ordinal);
    }
    public Hit FromIds(string job, long? damage, uint target, IEnumerable<uint> ids) => Apply(
        new(job, damage, "", target), ids.Where(id => id != 0 && id != 48 && !excluded.Contains(id))
            .Select(id => byId.GetValueOrDefault(id) ?? new StatusInfo(id, $"Status#{id}", 0, 0)));
    public Hit Normalize(Hit hit)
    {
        var effects = hit.Effects ?? hit.Statuses.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => byName.GetValueOrDefault(name) ?? new StatusInfo(0, name, 0, 0)).ToList();
        return Apply(hit, effects.Where(s => s.Id != 48 && (s.Id == 0 || !excluded.Contains(s.Id)) && !ExcludedNames.Contains(s.Name))
            .Select(s => byId.GetValueOrDefault(s.Id) is { } known && s.Id != 0 ?
                s.ReductionSource != null ? known with { ReductionPhysical = s.ReductionPhysical, ReductionMagical = s.ReductionMagical,
                    ReductionSource = s.ReductionSource, ReductionActionId = s.ReductionActionId } : known : s));
    }
    private static Hit Apply(Hit hit, IEnumerable<StatusInfo> effects)
    {
        var list = effects.ToList();
        return hit with { Effects = list, Statuses = string.Join(",", list.Select(s => s.Name)) };
    }
}
public sealed record TimelineRow(int No, double Seconds, string Enemy, string Action, Hit Hit,
    uint ActionId = 0, uint SourceId = 0, uint Sequence = 0, string Origin = "ActionEffect", List<StatusInfo>? EnemyEffects = null);
public static class RecordingRules
{
    public static bool IsPetAction(uint ownerId, byte battleNpcKind, ISet<uint> partyIds) =>
        battleNpcKind is 2 or 3 || (ownerId != 0 && ownerId != 0xE0000000 && partyIds.Contains(ownerId));
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
    public CaptureMode Mode { get; set; }
    public double ReplayStartSeconds { get; set; }
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
    public const string Header = "No,時間,エネミー名称,攻撃名称,被ダメージ,被ダメージ対象のジョブ,バフ情報,デバフ情報,エネミーバフ情報,エネミーデバフ情報,残りHP,致死,バリア合計,想定ダメージ,算出情報,軽減%,軽減情報,バリア情報";
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
        r.Hit.Damage?.ToString("N0", CultureInfo.InvariantCulture) ?? "", r.Hit.Job, r.Hit.Buffs, r.Hit.Debuffs,
        DefenseRules.EnemyText(r, false), DefenseRules.EnemyText(r, true),
        r.Hit.RemainingHp?.ToString(CultureInfo.InvariantCulture) ?? "取得不可",
        r.Hit.RemainingHp.HasValue ? (r.Hit.Fatal ? "致死" : "") : "未確認",
        r.Hit.BarrierTotal?.ToString(CultureInfo.InvariantCulture) ?? "取得不可",
        DamageEstimate.For(r).Display, DamageEstimate.For(r).Reason,
        MitigationSummary.For(r).Display, MitigationSummary.For(r).Reason, r.Hit.BarrierSource,
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
        foreach (var r in TimelineOrder.Sort(e.Rows)) writer.WriteLine(Line(r));
        return path;
    }
    public static void Rewrite(Encounter e, string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = Open(temp))
                foreach (var row in TimelineOrder.Sort(e.Rows)) output.WriteLine(Line(row));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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

public sealed record RoutedDamage(uint Source, uint Target, long Amount, bool Redirected, byte DamageType = 0, bool Special = false);
public sealed class DamageBatch(ISet<uint> partyIds)
{
    private readonly Dictionary<(uint Source, uint Target), RoutedDamage> entries = [];
    public IEnumerable<RoutedDamage> Entries => entries.Values;
    public void Add(uint caster, uint listedTarget, byte type, byte param3, byte param4, ushort value, byte param1 = 0)
    {
        var amount = DamageDecoder.Decode(type, param3, param4, value);
        if (!amount.HasValue) return;
        var recipient = (param4 & 0x80) != 0 ? caster : listedTarget;
        if (!partyIds.Contains(recipient)) return;
        var origin = (param4 & 0x20) != 0 ? listedTarget : caster;
        var key = (origin, recipient);
        var previous = entries.GetValueOrDefault(key);
        entries[key] = new(origin, recipient, (previous?.Amount ?? 0) + amount.Value,
            (previous?.Redirected ?? false) || (param4 & 0xA0) != 0,
            previous != null && previous.DamageType != (param1 & 15) ? (byte)0 : (byte)(param1 & 15),
            (previous?.Special ?? false) || type != 3 || (param4 & 0xB0) != 0);
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
