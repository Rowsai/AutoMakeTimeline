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
    Check(CapturePolicy.Allows(CaptureMode.Normal, false, false), "Normal mode accepts live combat");
    Check(!CapturePolicy.Allows(CaptureMode.Normal, true, false), "Normal mode excludes replay events");
    Check(!CapturePolicy.Allows(CaptureMode.Replay, false, false), "Replay mode excludes live combat");
    Check(CapturePolicy.Allows(CaptureMode.Replay, true, false), "Replay mode accepts playback");
    Check(!CapturePolicy.Allows(CaptureMode.Replay, true, true), "Paused playback does not capture restoration events");
    var cursor = new ReplayCursor();
    Check(!cursor.Observe(100, 0, 1), "Starting midway establishes cursor without false seek");
    Check(!cursor.Observe(101, 1, 1), "Normal playback continues same segment");
    Check(!cursor.Observe(103, 2, 2), "Double speed does not cause false seek");
    Check(!cursor.Observe(103, 12, 2), "Stationary playback position does not falsely trigger seek");
    Check(cursor.Observe(50, 13, 1), "Rewind creates a new segment");
    Check(cursor.Observe(150, 14, 1), "Forward chapter jump creates a new segment");
    cursor.Reset();
    Check(!cursor.Observe(1, 15, 1), "New playback resets cursor");
    var replayEncounter = new Encounter { Mode = CaptureMode.Replay, ReplayStartSeconds = 50 };
    var replayJson = System.Text.Json.JsonSerializer.Serialize(replayEncounter);
    var restoredReplay = System.Text.Json.JsonSerializer.Deserialize<Encounter>(replayJson)!;
    Check(restoredReplay.Mode == CaptureMode.Replay && restoredReplay.ReplayStartSeconds == 50, "Replay metadata survives history reload");
    Check(System.Text.Json.JsonSerializer.Deserialize<Encounter>("{}")!.Mode == CaptureMode.Normal, "Legacy history defaults to normal mode");
    var catalog = new StatusCatalog([
        new(0, "", 0, 0), new(48, "食事", 216202, 1),
        new(360, "カンパニーアクション：食事効果時間延長", 216508, 1, true),
        new(1084, "食事効果時間延長", 216508, 1),
        new(365, "カンパニーアクション：討伐経験値アップ", 216513, 1, true),
        new(1080, "討伐経験値アップ", 216513, 1),
        new(1191, "ランパート", 212751, 1), new(18, "毒", 210001, 2),
        new(999, "テストデバフ", 216508, 2)]);
    var splitHit = catalog.FromIds("DRK", 100, 10, [48, 360, 1084, 365, 1080, 1191, 18]);
    Check(splitHit.Buffs == "ランパート" && splitHit.Debuffs == "毒", "Buff and debuff columns split by category");
    Check(splitHit.Effects!.Count == 2, "Food and FC equivalents without flag excluded");
    Check(splitHit.Effects![0].Icon == 212751 && splitHit.Effects[1].Icon == 210001, "Each effect preserves name and game icon");
    Check(catalog.FromIds("DRK", 1, 10, [999]).Debuffs == "テストデバフ", "Shared icon alone does not exclude a debuff");
    var migrated = catalog.Normalize(new Hit("DRK", 1, "食事効果時間延長,ランパート,毒"));
    Check(migrated.Buffs == "ランパート" && migrated.Debuffs == "毒" && migrated.Effects!.All(s => s.Icon != 0), "Legacy history gains split columns and icons");
    Check(catalog.Normalize(new Hit("DRK", 1, "未知の効果")).Effects!.Any(s => s.Category == 0), "Unknown legacy effect retained and labelled");
    Check(catalog.Normalize(splitHit) == splitHit || catalog.Normalize(splitHit).Effects!.SequenceEqual(splitHit.Effects!), "New history retains effect metadata on reload");
    Check(Csv.Line(new(1, 10, "敵", "攻撃", splitHit)).Contains("\"ランパート\",\"毒\""), "CSV separates buffs and debuffs");
    Check(Csv.Header.Split(',').Length == 18, "CSV has eighteen integration analysis columns");
    var effectRoundTrip = System.Text.Json.JsonSerializer.Deserialize<Hit>(System.Text.Json.JsonSerializer.Serialize(splitHit))!;
    Check(effectRoundTrip.Effects!.SequenceEqual(splitHit.Effects!), "Icons and categories persist in JSON");
    var descriptionCatalog = new AbilityRateCatalog([
        new(7531, "ランパート", "一定時間、自身の被ダメージを20％軽減させる。さらに、自身が受けるＨＰ回復効果を15％上昇させる。"),
        new(25746, "ホーリーシェルトロン", "自身の被ダメージを15％軽減させる。\n追加効果：ナイトの堅守を付与。\nナイトの堅守効果：対象の被ダメージを15％軽減する。"),
        new(7382, "インターベンション", "対象の被ダメージを10％軽減する。\n追加効果：ナイトの堅守を付与。\nナイトの堅守効果：対象の被ダメージを10％軽減する。"),
        new(188, "野戦治療の陣", "範囲内のパーティメンバーの被ダメージを10％軽減し、かつＨＰを継続回復する。"),
        new(16889, "タクティシャン", "自身と周囲のパーティメンバーの被ダメージを15％軽減させる。"),
        new(16471, "ダークミッショナリー", "自身と周囲のパーティメンバーの被物理ダメージを5％、被魔法ダメージを10％軽減させる。"),
    ], new Dictionary<uint,string>());
    StatusInfo Described(uint id, string name, uint origin = 0) => descriptionCatalog.Resolve(new(id, name, 1, 1), origin);
    var rampart = Described(1191, "ランパート");
    Check(rampart.ReductionPhysical == 20 && rampart.ReductionMagical == 20 && rampart.ReductionSource!.Contains("7531"), "Rate and provenance extracted from ability description");
    Check(ReductionDescription.Parse("被ダメージを１４．５％軽減する。") is { Physical: 14.5m, Magical: 14.5m }, "Full-width decimal reduction supported");
    Check(ReductionDescription.Parse("回復量を20％上昇し、最大HPの25％のバリアを張る。") == null, "Healing and barrier percentages are not mitigation rates");
    Check(ReductionDescription.Parse("被ダメージを20％上昇させる。") == null, "Damage increase is not parsed as reduction");
    Check(ReductionDescription.Parse("被魔法ダメージを5％軽減する。") is { Physical: 0, Magical: 5 }, "Magic-only reduction is zero for physical");
    Check(ReductionDescription.Parse("被物理ダメージと魔法ダメージを10％軽減する。") is { Physical: 10, Magical: 10 }, "Shared physical/magical subject supported");
    Check(ReductionDescription.Parse("被ダメージを10％軽減する。被ダメージを20％軽減する。") == null, "Conflicting rates never pick an arbitrary percentage");
    Check(Described(2675, "ナイトの堅守").ReductionPhysical == null, "Ambiguous secondary buff waits for source ability");
    Check(Described(2675, "ナイトの堅守", 25746).ReductionPhysical == 15, "Holy Sheltron secondary effect selects 15 percent clause");
    Check(Described(2675, "ナイトの堅守", 7382).ReductionPhysical == 10, "Intervention secondary effect selects 10 percent clause");
    Check(Described(2674, "ホーリーシェルトロン").ReductionPhysical == 15, "Parent does not sum its additional secondary buff");
    var changedDescriptions = new AbilityRateCatalog([new(7531, "ランパート", "被ダメージを17％軽減する。")], new Dictionary<uint,string>());
    Check(changedDescriptions.Resolve(new(1191, "ランパート", 1, 1)).ReductionPhysical == 17, "Changing description changes result without changing hard-coded rates");
    var translated = new AbilityRateCatalog([new(7531, "ランパート", "被ダメージを20％軽減する。")], new Dictionary<uint,string> { [1191] = "ランパート" });
    Check(translated.Resolve(new(1191, "Rampart", 1, 1)).ReductionPhysical == 20, "Japanese description lookup remains independent of display language");
    Check(AdditiveRates.Percent(new(1191, "ランパート", 1, 1), 1) == null, "No fallback to old hard-coded rate if description is missing");
    var persistedRate = catalog.Normalize(rampart is { } persisted ? new Hit("PLD", 1, "", Effects: [persisted]) : throw new Exception());
    Check(persistedRate.Effects![0].ReductionPhysical == 20, "Captured description rate survives catalog normalization");
    var observedBuffs = new BuffApplicationTracker();
    observedBuffs.Observe(10, 2675, 20, 7382, 1000);
    Check(observedBuffs.Find(10, 2675, 20, 1100) == 7382, "Buff source action keyed by target, status and caster");
    Check(observedBuffs.Find(11, 2675, 20, 1100) == 0 && observedBuffs.Find(10, 2675, 21, 1100) == 0, "Another target or caster cannot supply the ability");
    Check(observedBuffs.Find(10, 2675, 20, 121001) == 0, "Expired application provenance rejected");
    observedBuffs.Clear();
    Check(observedBuffs.Find(10, 2675, 20, 1100) == 0, "Replay seek / mode boundaries reset provenance");
    var shield = new StatusInfo(297, "鼓舞", 212801, 1);
    var offense = new StatusInfo(9999, "攻撃力アップ", 123, 1);
    var analysisHit = new Hit("DRK", 800, "", 10, [rampart, offense], 5000, 10000, 0,
        DamageType: 5, CaptureTick: 1000);
    var analysisRow = new TimelineRow(1, 1, "敵", "攻撃", analysisHit, Sequence: 123, EnemyEffects: []);
    Check(analysisHit.Buffs == "ランパート", "Party buff column excludes offensive buffs");
    var reprisal = new StatusInfo(1193, "リプライザル", 1, 2);
    Check(DamageEstimate.Calculate(10000, 20, 2000) == 14000, "Confirmed formula: original damage plus percent increase plus barrier");
    Check(DamageEstimate.Calculate(1, 15, 0) == 2, "Fractional specified result rounds upward");
    Check(DamageEstimate.Calculate(10000, 0, 0) == 10000, "Zero mitigation and barrier retain base damage");
    Check(DamageEstimate.Calculate(10000, 120, 2000) == 24000, "Additive percentage above 100 is not clamped");
    var estimateRow = analysisRow with { Hit = analysisHit with { Damage = 10000, BarrierTotal = 2000 }, EnemyEffects = null };
    Check(DamageEstimate.For(estimateRow).Value == 14000, "New estimate works with missing enemy statuses and an active barrier");
    Check(DamageEstimate.For(estimateRow with { Hit = estimateRow.Hit with { SpecialDamage = true, DamageType = 0 } }).Value == 14000,
        "Type-independent buffs calculate despite unknown attack type or special flag");
    Check(DamageEstimate.For(estimateRow with { Hit = estimateRow.Hit with { BarrierTotal = null } }).Value == null, "Missing devLibra value not fabricated as zero");
    Check(DamageEstimate.For(estimateRow with { Hit = estimateRow.Hit with { Effects = [new(999, "未設定軽減", 1, 1, Defensive: true)] } }).Value == null,
        "Estimate explains unconfigured buff rates instead of inventing them");
    var shieldHit = analysisHit with { Effects = [shield], ShieldPercent = 12 };
    Check(shieldHit.BarrierEstimate == 1200 && shieldHit.Buffs == "鼓舞", "Single shield shows name only");
    Check((shieldHit with { Effects = [shield, new(1178, "ブラックナイト", 1, 1)] }).Buffs == "鼓舞,ブラックナイト",
        "Stacked barrier buffs no longer show individual amounts");
    Check(DefenseRules.EnemyText(analysisRow with { EnemyEffects = [offense, reprisal] }, false) == "攻撃力アップ" &&
        DefenseRules.EnemyText(analysisRow with { EnemyEffects = [offense, reprisal] }, true) == "リプライザル", "Enemy columns preserve offensive buffs and debuffs separately");
    var hp = new HpResult(123, 10, 0, 0, 1100);
    byte[] fullHp = new byte[4 + 2 * 0x58]; fullHp[0] = 2;
    BitConverter.GetBytes(123u).CopyTo(fullHp, 4); BitConverter.GetBytes(10u).CopyTo(fullHp, 8);
    BitConverter.GetBytes(123456u).CopyTo(fullHp, 12); fullHp[24] = 25;
    BitConverter.GetBytes(124u).CopyTo(fullHp, 4 + 0x58); BitConverter.GetBytes(11u).CopyTo(fullHp, 8 + 0x58);
    var parsedHp = HpPackets.Read(fullHp, false, 1100);
    Check(parsedHp.Count == 2 && parsedHp[0] == new HpResult(123, 10, 123456, 25, 1100) && parsedHp[1].Target == 11,
        "Full HP packet decodes multi-entry stride, uint HP and shield");
    byte[] basicHp = new byte[20]; basicHp[0] = 1;
    BitConverter.GetBytes(123u).CopyTo(basicHp, 4); BitConverter.GetBytes(10u).CopyTo(basicHp, 8);
    Check(HpPackets.Read(basicHp, true, 1100).Single() == hp with { Shield = null }, "Basic HP packet supplies HP without inventing shield");
    Check(HpPackets.Read(fullHp.AsSpan(0, 12), false, 1100).Count == 0, "Truncated HP packets rejected");
    fullHp[0] = 255;
    Check(HpPackets.Read(fullHp, false, 1100).Count == 0, "Invalid HP packet count rejected");
    var fatalRow = HpCorrelation.Apply(analysisRow, hp, 1000);
    Check(fatalRow.Hit.RemainingHp == 0 && fatalRow.Hit.Fatal, "Matching HP result marks damaging death red");
    Check(HpCorrelation.Apply(analysisRow, hp with { Sequence = 124 }, 1000) == analysisRow, "Different attack sequence cannot supply HP");
    Check(HpCorrelation.Apply(analysisRow, hp with { Target = 11 }, 1000) == analysisRow, "Another party member cannot supply HP");
    Check(HpCorrelation.Apply(analysisRow, hp with { Tick = 999 }, 1000) == analysisRow, "HP before attack is rejected");
    Check(HpCorrelation.Apply(analysisRow, hp with { Tick = 20000 }, 1000) == analysisRow, "Old sequence reuse is rejected");
    Check(!HpCorrelation.Apply(analysisRow with { Hit = analysisHit with { Damage = 0 } }, hp, 1000).Hit.Fatal,
        "Zero damage never claims to kill");
    Check(!HpCorrelation.Apply(analysisRow with { Hit = analysisHit with { HpBefore = 0 } }, hp, 1000).Hit.Fatal,
        "Already dead target not claimed as a new death");
    var unordered = new[] { analysisRow with { No = 2 }, analysisRow with { Hit = analysisHit with { Damage = 100, TargetId = 11 } },
        analysisRow, analysisRow with { Hit = analysisHit with { Damage = 800, TargetId = 12 } }, analysisRow with { Hit = analysisHit with { Damage = null } } };
    var ordered = TimelineOrder.Sort(unordered);
    Check(ordered.Select(r => r.No).SequenceEqual([1, 1, 1, 1, 2]), "Sorting never mixes attack groups");
    Check(ordered.Take(4).Select(r => r.Hit.Damage).SequenceEqual(new long?[] {800, 800, 100, null}), "Descending damage with null last within No");
    Check(ordered[0].Hit.TargetId == 10 && ordered[1].Hit.TargetId == 12, "Equal damage preserves original target order");
    var analysisEncounter = new Encounter { Rows = unordered.ToList(), Start = DateTimeOffset.Now, End = DateTimeOffset.Now };
    var analysisCsv = Csv.Export(analysisEncounter, folder);
    Check(File.ReadAllLines(analysisCsv)[1].Contains("\"800\""), "CSV uses same grouped descending order as UI");
    analysisEncounter.Rows = [fatalRow];
    Csv.Rewrite(analysisEncounter, analysisCsv);
    Check(File.ReadAllLines(analysisCsv).Length == 2 && File.ReadAllText(analysisCsv).Contains("\"致死\""), "Final CSV rewrite includes correlated HP and death");
    Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Atomic CSV rewrite leaves no temporary file");
    var restoredAnalysis = System.Text.Json.JsonSerializer.Deserialize<TimelineRow>(System.Text.Json.JsonSerializer.Serialize(fatalRow))!;
    Check(restoredAnalysis.Hit.Fatal && restoredAnalysis.EnemyEffects != null && restoredAnalysis.Hit.MaxHp == 10000, "HP and enemy status metadata survive history reload");
    var linkedSample = new BarrierSample(1, 10, 5000, 10000, 12, 1234, "Galvanize (observed recovery x 180%)", 2000);
    string Wire(BarrierSample sample) => System.Text.Json.JsonSerializer.Serialize(sample);
    Check(BarrierWire.Read(Wire(linkedSample), 10, 10000, 2100).Amount == 1234,
        "devLibra Barrier value is used unchanged rather than locally recalculated 1200 or HP-plus-barrier");
    Check(BarrierWire.Read(Wire(linkedSample with { BarrierHp = 0 }), 10, 10000, 2100).Amount == 0, "Connected zero barrier is a measured zero");
    Check(BarrierWire.Read("", 10, 10000, 2100).Amount == null, "Missing provider data is not zero");
    Check(BarrierWire.Read("bad json", 10, 10000, 2100).Amount == null, "Bad IPC data handled without disrupting recording");
    Check(BarrierWire.Read(Wire(linkedSample), 11, 10000, 2100).Amount == null, "Another party slot cannot provide target barrier");
    Check(BarrierWire.Read(Wire(linkedSample), 10, 9999, 2100).Amount == null, "Stale member maximum rejected");
    Check(BarrierWire.Read(Wire(linkedSample), 10, 10000, 2600).Amount == null, "Older than 500ms barrier rejected");
    Check(BarrierWire.Read(Wire(linkedSample), 10, 10000, 1999).Amount == null, "Future timestamp rejected");
    Check(BarrierWire.Read(Wire(linkedSample with { Version = 2 }), 10, 10000, 2100).Amount == null, "Unsupported IPC contract rejected");
    Check(BarrierWire.Read(Wire(linkedSample), 10, 10000, 2100, true).Amount == null, "Live barrier sample not used during replay");
    Check(BarrierWire.Read(Wire(linkedSample with { IsReplay = true }), 10, 10000, 2100, true).Amount == 1234, "Replay barrier snapshot accepted only in replay mode");
    var linkedRow = analysisRow with { Hit = analysisHit with { BarrierTotal = 1234, BarrierSource = "devLibra" } };
    Check(Csv.Line(linkedRow).Contains("\"1234\"") && Csv.Line(linkedRow).Contains("devLibra"), "CSV persists linked barrier and provenance");
    Check(DamageEstimate.For(linkedRow).Value == 2194, "Specified formula adds linked barrier total");
    var linkedRestored = System.Text.Json.JsonSerializer.Deserialize<TimelineRow>(System.Text.Json.JsonSerializer.Serialize(linkedRow))!;
    Check(linkedRestored.Hit.BarrierTotal == 1234 && linkedRestored.Hit.BarrierSource == "devLibra", "History stores snapshot independently of provider lifetime");
    var enemyFiltered = analysisRow with { EnemyEffects = [reprisal, new(18, "毒", 1, 2), new(2586, "デスデザイン", 1, 2), offense] };
    Check(DefenseRules.EnemyText(enemyFiltered, true) == "リプライザル", "Enemy DoT and Death's Design excluded but mitigation kept");
    Check(EnemyStatusRules.Display(enemyFiltered.EnemyEffects)!.Count == 2, "UI and CSV enemy filters agree");
    Check(EnemyStatusRules.Display(null) == null && EnemyStatusRules.Display([])!.Count == 0,
        "Missing enemy differs from successfully read empty status list");
    Check((analysisHit with { Effects = [new(18, "毒", 1, 2)] }).Debuffs == "毒", "Party DoTs remain visible");
    Check(MitigationSummary.For(analysisRow).Percent == 20, "Single Rampart shows 20 percent");
    Check(MitigationSummary.For(analysisRow with { EnemyEffects = [reprisal] }).Percent == 20, "Enemy effects are excluded from the party-buff sum");
    Check(MitigationSummary.For(analysisRow with { Hit = analysisHit with { Effects = [rampart, shield] } }).Percent == 20,
        "Barriers do not add fictional percentage mitigation");
    Check(MitigationSummary.For(analysisRow with { Hit = analysisHit with { Effects = [] } }).Percent == 0,
        "Known absence of mitigation shows zero percent");
    Check(MitigationSummary.For(analysisRow with { Hit = analysisHit with { DamageType = 0 } }).Percent == 20, "Type-independent Rampart sums even without attack type");
    Check(!MitigationSummary.For(analysisRow with { Hit = analysisHit with { Effects = [new(999, "不明軽減", 1, 1, Defensive: true)] } }).Complete,
        "Unknown reduction is not silently discarded");
    Check(MitigationSummary.For(enemyFiltered).Percent == 20, "DoT and Death's Design do not change mitigation percentage");
    var pldBuffs = analysisHit with { Effects = [rampart, Described(2674, "ホーリーシェルトロン"), Described(2675, "ナイトの堅守", 25746)] };
    Check(MitigationSummary.For(analysisRow with { Hit = pldBuffs, EnemyEffects = null }).Percent == 50,
        "Requested Rampart 20 + Holy Sheltron 15 + Knight's Resolve 15 equals 50");
    var raidBuffs = analysisHit with { Effects = [Described(299, "野戦治療の陣：効果"), Described(1951, "タクティシャン"), Described(1894, "ダークミッショナリー")] };
    Check(MitigationSummary.For(analysisRow with { Hit = raidBuffs with { DamageType = 1 } }).Percent == 30,
        "Description physical: Soil 10 + Tactician 15 + Missionary 5 equals 30");
    Check(MitigationSummary.For(analysisRow with { Hit = raidBuffs with { DamageType = 5 } }).Percent == 35,
        "Description magic: Soil 10 + Tactician 15 + Missionary 10 equals 35");
    var partial = MitigationSummary.For(analysisRow with { Hit = analysisHit with { Effects = [rampart, new(999, "未設定", 1, 1, Defensive: true)] } });
    Check(partial.Percent == 20 && !partial.Complete && partial.Display.Contains("未設定"), "Unknown buff preserves known subtotal with explicit marker");
    Check(AttackKind.Physical(1) && AttackKind.Physical(7) && !AttackKind.Physical(5), "Physical icon classification matches packet types");
    Check(AttackKind.Magical(5) && !AttackKind.Magical(0), "Staff icon only for known magic damage");
    var hiddenColumns = new HashSet<int>();
    TimelineColumns.SetVisible(hiddenColumns, 12, false);
    Check(hiddenColumns.SetEquals([12]), "Column checkbox hides selected logical column");
    TimelineColumns.SetVisible(hiddenColumns, 12, true);
    Check(hiddenColumns.Count == 0, "Hidden column can be restored from header");
    for (var column = 0; column < TimelineColumns.Names.Length; column++) TimelineColumns.SetVisible(hiddenColumns, column, false);
    Check(hiddenColumns.Count == 14, "All columns may be hidden without losing header controls");
    var hiddenRestored = System.Text.Json.JsonSerializer.Deserialize<HashSet<int>>(System.Text.Json.JsonSerializer.Serialize(hiddenColumns))!;
    TimelineColumns.SetVisible(hiddenRestored, 0, true);
    Check(hiddenRestored.Count == 13 && !hiddenRestored.Contains(0), "Persisted hidden columns can be restored after restart");
    TimelineColumns.SetVisible(hiddenRestored, 99, false);
    Check(!hiddenRestored.Contains(99), "Invalid column index ignored");
    var petOwners = new HashSet<uint> { 10, 11 };
    Check(RecordingRules.IsPetAction(10, 2, petOwners), "Local player's pet actions excluded");
    Check(RecordingRules.IsPetAction(11, 2, petOwners), "Party member's pet actions excluded");
    Check(RecordingRules.IsPetAction(10, 0, petOwners), "Owner identifies pet when source kind unavailable");
    Check(RecordingRules.IsPetAction(0xE0000000, 2, petOwners), "Known pet excluded even without owner lookup");
    Check(RecordingRules.IsPetAction(10, 3, petOwners), "Companion actions excluded");
    Check(!RecordingRules.IsPetAction(0x40001000, 5, petOwners), "Enemy helper damage retained");
    Check(!RecordingRules.IsPetAction(0, 0, petOwners), "Unknown attack source retained");
    Check(!RecordingRules.IsPetAction(0xE0000000, 0, petOwners), "Player action without owner retained");
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
    Check(lines.Length == 10 && lines[0] == Csv.Header, "Current header and nine target rows");
    Check(lines.Skip(1).Take(8).All(s => s.StartsWith("\"1\",\"00:10\"")), "Eight party members share attack No");
    Check(lines[1].StartsWith("\"1\",\"00:10\",\"ケフカ\",\"裁きの光\",\"100,000\",\"DRK\",\"取得不可\",\"\""), "Unmigrated legacy status metadata is explicitly unavailable");
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


