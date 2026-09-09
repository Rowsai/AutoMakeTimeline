using AutoMakeTimeline;
using System.Text;

var folder = Path.Combine(Path.GetTempPath(), "AMT-tests-" + Guid.NewGuid());
Directory.CreateDirectory(folder);
var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); count++;
}
try
{
    const string bahamutAa = "(仮)バハムート：霊体：オートアタック：バハムートダンジョン3";
    Check(RecordingRules.ActionName(2988, 1, bahamutAa) == "攻撃（AA)", "Bahamut enemy-specific AA (2988) uses category 1");
    Check(RecordingRules.ActionName(7, 0, "攻撃") == "攻撃（AA)", "Standard AA fallback");
    Check(RecordingRules.ActionName(123, 4, "フレアブレス") == "フレアブレス", "Non-AA ability name preserved");
    Check(!RecordingRules.IncludeStatus(48, false), "Food status excluded");
    Check(!RecordingRules.IncludeStatus(353, true), "Company action excluded by metadata");
    Check(RecordingRules.IncludeStatus(1191, false), "Combat buffs retained");
    var normalized = RecordingRules.NormalizeHistory(new(1, 10, "バハムート・プライム", bahamutAa,
        new("BLM", 3886, "カンパニーアクション：討伐経験値アップ,超える力,食事,ランパート")),
        new HashSet<string> { bahamutAa }, new HashSet<string> { "カンパニーアクション：討伐経験値アップ", "食事" });
    Check(normalized.Action == "攻撃（AA)" && normalized.Hit.Statuses == "超える力,ランパート", "Existing history normalization retains combat statuses");
    Check(Csv.Line(normalized).Contains("攻撃（AA)") && !Csv.Line(normalized).Contains("カンパニー"), "History re-export uses corrected names and filters");
    Check(DamageDecoder.Decode(3, 1, 0x40, 34464) == 100000, "100,000 damage (24-bit)");
    Check(DamageDecoder.Decode(5, 7, 0x40, 41248) == 500000, "500,000 blocked damage");
    Check(DamageDecoder.Decode(6, 2, 0, 99) == 99, "Ignore upper byte without large-value flag");
    Check(DamageDecoder.Decode(3, 0, 0x80, 100) == 100, "Decode source-directed damage before routing");
    Check(DamageDecoder.Decode(3, 0, 0x20, 100) == 100, "Decode target-origin damage before routing");
    Check(DamageDecoder.Decode(4, 0, 0, 100) == null, "Exclude healing");
    Check(DamageDecoder.Decode(7, 0, 0, 0) == 0, "Invulnerability is zero, not missing");
    var damageParty = new HashSet<uint> { 10, 11 };
    var earthshaker = new DamageBatch(damageParty);
    earthshaker.Add(0x40001234, 10, 3, 1, 0x40, 34464);
    earthshaker.Add(0x40001234, 11, 3, 0, 0, 2345);
    Check(earthshaker.Entries.Count() == 2 && earthshaker.Entries.Sum(d => d.Amount) == 102345,
        "Helper-origin Earthshaker-shaped packet retains both party hits");
    var unknown = new DamageBatch(damageParty);
    unknown.Add(0, 10, 3, 0, 0, 1200);
    Check(unknown.Entries.Single().Amount == 1200, "Missing source object does not discard damage");
    var outgoing = new DamageBatch(damageParty);
    outgoing.Add(10, 0x40001234, 3, 0, 0, 2000);
    Check(!outgoing.Entries.Any(), "Outgoing damage to enemy is not counted as party damage");
    outgoing.Add(10, 0x40001234, 3, 0, 0xA0, 3000);
    Check(outgoing.Entries.Single() is { Source: 0x40001234, Target: 10, Amount: 3000 },
        "Enemy retaliation on player action routes back to player");
    var retaliation = new DamageBatch(damageParty);
    retaliation.Add(0x40001234, 10, 3, 0, 0xA0, 500);
    Check(!retaliation.Entries.Any(), "Player retaliation against enemy is not incoming damage");
    var repeated = new DamageBatch(damageParty);
    repeated.Add(0x40001234, 10, 3, 0, 0, 100);
    repeated.Add(0x40001234, 10, 5, 0, 0, 200);
    repeated.Add(0x40001234, 10, 4, 0, 0, 800);
    Check(repeated.Entries.Single().Amount == 300, "Multiple damage effects sum; simultaneous heal excluded");
    Check(PeriodicDamage.Decode(0x605, 90000) == 90000, "Periodic damage uses full uint amount");
    Check(PeriodicDamage.Decode(0x604, 90000) == null, "HoT notification is not damage");
    Check(PeriodicDamage.Decode(6, 0) == null, "Death notification is not a fabricated damage hit");
    Check(PeriodicDamage.Name(0, null) == "継続ダメージ（合算）", "Aggregated DoT remains explicitly unattributed");
    Check(PeriodicDamage.Name(123, "汚泥") == "汚泥（継続ダメージ）", "Named periodic damage retains status label");
    Check(Csv.Time(10) == "00:10" && Csv.Time(3661) == "61:01", "Elapsed minutes do not wrap at one hour");
    Check(Csv.Quote("a,\"b\"\nc") == "\"a,\"\"b\"\"\nc\"", "CSV quotes commas, quotes and newlines");
    Check(Csv.Quote("=1+1").StartsWith("\"'="), "Spreadsheet formula neutralization");
    var grouper = new AttackGrouper();
    var first = grouper.Add(1, 99, 7, 100, 10.9, "敵", "攻撃", [new("DRK", 100, "", 10)]).Single();
    var split = grouper.Add(1, 99, 7, 101, 11.1, "敵", "攻撃", [new("PLD", 200, "", 11)]).Single();
    Check(first.No == split.No && first.Seconds == split.Seconds, "Split target packets share first timestamp and No");
    Check(!grouper.Add(1, 99, 7, 102, 11.2, "敵", "攻撃", [new("DRK", 100, "", 10)]).Any(), "Duplicate target packets are ignored");
    Check(grouper.Add(1, 100, 7, 103, 12, "敵", "攻撃", [new("DRK", 100, "", 10)]).Single().No == 2, "Repeated attacks remain distinct");
    Check(grouper.Add(2, 100, 7, 104, 12, "敵2", "攻撃", [new("DRK", 100, "", 10)]).Single().No == 3, "Different enemies remain distinct");
    var zero1 = grouper.Add(1, 0, 7, 105, 13, "敵", "攻撃", [new("DRK", 100, "", 10)]).Single();
    var zero2 = grouper.Add(1, 0, 7, 106, 14, "敵", "攻撃", [new("DRK", 100, "", 10)]).Single();
    Check(zero1.No != zero2.No, "Missing server sequence does not merge repeated attacks");
    var routes = new AttackGrouper();
    var directRow = routes.Add(0, 0, 0, 1, 1, "不明", "攻撃", [new("DRK", 100, "", 10)]).Single();
    var periodicRow = routes.Add(0, 0, 0, 1, 1, "不明", "継続ダメージ（合算）", [new("DRK", 50, "", 10)], "ActorControlDoT").Single();
    Check(directRow.No != periodicRow.No && periodicRow.Origin == "ActorControlDoT", "Direct and periodic paths cannot collide in deduplication");
    var earthshakerRows = new AttackGrouper().Add(0x40001234, 54321, 2998, 1, 12, "不明", "アースシェイカー",
        earthshaker.Entries.Select(d => new Hit("BLM", d.Amount, "ランパート", d.Target))).ToList();
    Check(earthshakerRows.Count == 2 && earthshakerRows.All(r => r.No == 1 && r.ActionId == 2998 && r.Sequence == 54321),
        "Earthshaker packet to timeline preserves attack metadata and shared No");
    Check(Csv.Line(earthshakerRows[0]).Contains("\"アースシェイカー\",\"100,000\""), "Earthshaker damage reaches CSV");
    var start = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.FromHours(9));
    var e = new Encounter { Content = "次元の狭間:オメガ/テスト", Start = start, End = start.AddSeconds(30), DurationSeconds = 30 };
    var jobs = new[] { "DRK", "PLD", "WHM", "SCH", "DRG", "RPR", "MCH", "RDM" };
    foreach (var job in jobs) e.Rows.Add(new(1, 10, "ケフカ", "裁きの光", new(job, 100000, "ランパート,鼓舞激励の策")));
    e.Rows.Add(new(2, 15, "ケフカ", "攻撃", new("DRK", 100000, "ランパート")));
    var output = Csv.Export(e, folder);
    var bytes = File.ReadAllBytes(output);
    Check(bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Excel-friendly UTF-8 BOM");
    var lines = File.ReadAllLines(output);
    Check(lines.Length == 10 && lines[0] == Csv.Header, "Exact seven-column header and nine target rows");
    Check(lines.Skip(1).Take(8).All(s => s.StartsWith("\"1\",\"00:10\"")), "Eight party members share attack No");
    Check(lines[1] == "\"1\",\"00:10\",\"ケフカ\",\"裁きの光\",\"100,000\",\"DRK\",\"ランパート,鼓舞激励の策\"", "User's requested CSV row format");
    Check(Path.GetFileName(output).EndsWith("_00h00m30s_AMTLOG.csv") && !Path.GetFileName(output).Contains(':'), "Safe specified filename");
    Check(Csv.Export(e, folder) != output && File.ReadAllBytes(output).SequenceEqual(bytes), "Repeated exports never overwrite");
    var streaming = Path.Combine(folder, "stream.csv");
    using (var w = Csv.Open(streaming))
    {
        w.WriteLine(Csv.Line(e.Rows[0]));
        using var reader = new StreamReader(new FileStream(streaming, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Check(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "Rows readable before battle closes");
    }
    var histFolder = Path.Combine(folder, "history");
    var history = new HistoryStore(histFolder);
    for (var i = 0; i < 105; i++) history.Save(new Encounter { Start = start.AddMinutes(i), End = start.AddMinutes(i + 1), Rows = e.Rows });
    Check(history.Items.Count == 100 && Directory.GetFiles(histFolder, "*.json").Length == 100, "History capped at 100 on disk and in memory");
    var loaded = new HistoryStore(histFolder); loaded.Load();
    Check(loaded.Items.Count == 100 && loaded.Items[0].Start == start.AddMinutes(104) && loaded.Items[0].Rows.Count == 9, "History survives restart with newest first");
    File.WriteAllText(Path.Combine(histFolder, "broken.json"), "not-json");
    var recovery = new HistoryStore(histFolder); recovery.Load();
    Check(recovery.Items.Count == 100 && recovery.Error != null, "Corrupt history does not hide healthy records");
    Check(Directory.Exists(DownloadsFolder.Get()), "Windows redirected Downloads known folder resolves");
    Console.WriteLine($"SUCCESS: {count} checks");
}
finally { Directory.Delete(folder, true); }
