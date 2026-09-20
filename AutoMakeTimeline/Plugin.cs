using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Party;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using Status = Lumina.Excel.Sheets.Status;

namespace AutoMakeTimeline;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public CaptureMode Mode { get; set; }
    public string OutputFolder { get; set; } = "";
    public HashSet<int> HiddenColumns { get; set; } = [];
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly IPartyList party;
    private readonly IDataManager data;
    private readonly IDutyState duty;
    private readonly IPluginLog log;
    private readonly StatusCatalog statusCatalog;
    private readonly AbilityRateCatalog abilityRates;
    private readonly BuffApplicationTracker buffApplications = new();
    private Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? periodicHook;
    private delegate void EffectResultDelegate(uint target, byte* packet, byte replaying);
    private Hook<EffectResultDelegate>? resultHook;
    private Hook<EffectResultDelegate>? basicResultHook;
    private readonly ConcurrentQueue<HpResult> hpResults = new();
    private readonly ConcurrentQueue<Packet> queue = new();
    private AttackGrouper grouper = new();
    private readonly MainWindow window;
    private StreamWriter? writer;
    private long startTick;
    private long? outOfCombatTick;
    private bool seenCombat;
    private bool waitForCombatClear;
    private bool disposed;
    private string pendingPath = "";
    private readonly ReplayCursor replayCursor = new();
    private double replayPositionSeconds;
    internal bool ReplayActive { get; private set; }
    internal bool ReplayPaused { get; private set; }
    // Both capture modes use real elapsed time. Playback position is only
    // used for seek detection and metadata; it may remain zero in some replays.
    private long ClockTick => Stopwatch.GetTimestamp();
    internal string ModeLabel => Config.Mode == CaptureMode.Replay ? "リプレイモード" : "通常モード";
    internal string StateLabel => !HookReady ? "監視エラー" : !Config.Enabled ? "停止中" :
        Config.Mode == CaptureMode.Replay && !ReplayActive ? "リプレイ待機中" :
        Config.Mode == CaptureMode.Normal && ReplayActive ? "通常戦闘待機中" :
        ReplayPaused && Config.Mode == CaptureMode.Replay ? "一時停止中" : Current != null ? "記録中" : "戦闘開始待機中";
    private record Packet(long Tick, DateTimeOffset At, uint Source, uint Sequence, uint ActionId, string Enemy, string Action, List<Hit> Hits, string Origin = "ActionEffect", List<StatusInfo>? EnemyEffects = null);

    internal Configuration Config { get; }
    internal DevLibraLink Libra { get; }
    internal HistoryStore History { get; }
    internal Encounter? Current { get; private set; }
    internal Encounter? Selected { get; set; }
    internal string Message { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal bool HookReady => hook?.IsEnabled == true && periodicHook?.IsEnabled == true;
    internal double Elapsed => Current == null ? 0 : Math.Max(0, Stopwatch.GetElapsedTime(startTick, ClockTick).TotalSeconds);
    private bool PartyInCombat => (Config.Mode == CaptureMode.Normal && condition[ConditionFlag.InCombat]) || ActiveMembers().Any(m =>
        m.GameObject is IBattleChara b && (b.StatusFlags & StatusFlags.InCombat) != 0);
    internal string ContentName
    {
        get
        {
            if (Config.Mode == CaptureMode.Replay && ReplayActive)
            {
                var replay = ContentsReplayManager.Instance();
                if (replay != null)
                {
                    var title = data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(replay->Header.ContentFinderConditionId)?.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(title)) return title;
                }
            }
            var name = duty.ContentFinderCondition.ValueNullable?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return data.GetExcelSheet<TerritoryType>().GetRowOrDefault(client.TerritoryType)?.PlaceName.Value.Name.ToString() ?? $"Territory-{client.TerritoryType}";
        }
    }

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework,
        ICondition condition, IClientState client, IObjectTable objects, IPartyList party,
        IDataManager data, IDutyState duty, IGameInteropProvider interop, IPluginLog log, ITextureProvider textures)
    {
        this.pi = pi; this.commands = commands; this.framework = framework; this.condition = condition;
        this.client = client; this.objects = objects; this.party = party; this.data = data; this.duty = duty; this.log = log;
        var descriptions = data.GetExcelSheet<ActionTransient>(Dalamud.Game.ClientLanguage.Japanese);
        abilityRates = new(data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Dalamud.Game.ClientLanguage.Japanese)
            .Where(a => !a.IsPvP && a.ClassJobCategory.RowId != 0)
            .Select(a => new AbilityText(a.RowId, a.Name.ToString(), descriptions.GetRowOrDefault(a.RowId)?.Description.ToString() ?? ""))
            .Where(a => a.Description.Length > 0),
            data.GetExcelSheet<Status>(Dalamud.Game.ClientLanguage.Japanese).ToDictionary(s => s.RowId, s => s.Name.ToString()));
        statusCatalog = new(data.GetExcelSheet<Status>().Select(s =>
        {
            var info = DefenseRules.Describe(s.RowId, s.Name.ToString(), s.Icon, s.StatusCategory, s.IsFcBuff, s.Description.ToString());
            return info.Category == 1 && DefenseRules.IsDefensive(info) && !DefenseRules.IsBarrier(info) ? abilityRates.Resolve(info) : info;
        }));
        Config = pi.GetPluginConfig() as Configuration ?? new();
        Config.HiddenColumns ??= [];
        Libra = new(pi);
        if (string.IsNullOrWhiteSpace(Config.OutputFolder))
        {
            try { Config.OutputFolder = DownloadsFolder.Get(); }
            catch (Exception ex) { Message = ex.Message; }
        }
        History = new(Path.Combine(pi.GetPluginConfigDirectory(), "history"));
        try { History.Load(); if (History.Error != null) Message = History.Error; }
        catch (Exception ex) { Fail("履歴読み込み", ex); }
        try
        {
            var aaNames = data.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                .Where(a => a.ActionCategory.RowId == 1 || a.RowId == 7)
                .Select(a => a.Name.ToString()).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            var excludedNames = statusCatalog.ExcludedNames;
            foreach (var encounter in History.Items)
                encounter.Rows = TimelineOrder.Sort(encounter.Rows.Select(r => RecordingRules.NormalizeHistory(r, aaNames, excludedNames))
                    .Select(r => r with { Hit = statusCatalog.Normalize(r.Hit) }));
        }
        catch (Exception ex) { Fail("履歴の表示情報更新", ex); }
        Selected = History.Items.FirstOrDefault();
        window = new(this, textures);
        try
        {
            hook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(ActionEffectHandler.Addresses.Receive.Value, Receive);
            periodicHook = interop.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                PacketDispatcher.Addresses.HandleActorControlPacket.Value, ReceiveActorControl);
            hook.Enable();
            periodicHook.Enable();
        }
        catch (Exception ex)
        {
            hook?.Disable(); periodicHook?.Disable();
            Config.Enabled = false; Fail("攻撃監視を初期化できません。ゲーム更新との互換性を確認してください", ex);
        }
        try
        {
            resultHook = interop.HookFromSignature<EffectResultDelegate>("48 8B C4 44 88 40 18 89 48 08", ReceiveResult);
            basicResultHook = interop.HookFromSignature<EffectResultDelegate>("40 53 41 54 41 55 48 83 EC 40 83 3D ?? ?? ?? ?? ??", ReceiveBasicResult);
            resultHook.Enable(); basicResultHook.Enable();
        }
        catch (Exception ex)
        {
            resultHook?.Disable(); basicResultHook?.Disable();
            Fail("HP更新監視を初期化できません。残りHPは取得不可になります", ex);
        }
        commands.AddHandler("/amt", new CommandInfo(Command) { HelpMessage = "画面表示: /amt | 記録開始: /amt on | 記録停止: /amt off" });
        framework.Update += Update;
        client.TerritoryChanged += TerritoryChanged;
        client.Logout += Logout;
        duty.DutyWiped += Wiped;
        duty.DutyCompleted += Completed;
        pi.UiBuilder.Draw += window.Draw;
        pi.UiBuilder.OpenMainUi += window.Open;
        pi.UiBuilder.OpenConfigUi += window.Open;
    }

    private void Command(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "": window.Open(); break;
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            default: Message = "使用方法: /amt、/amt on、/amt off"; window.Open(); break;
        }
    }
    internal void SetEnabled(bool enabled)
    {
        if (enabled && !HookReady) { Message = "攻撃監視が利用できません。Dalamudログを確認してください。"; return; }
        if (!enabled) { Drain(); Finish("手動停止"); }
        Config.Enabled = enabled;
        pi.SavePluginConfig(Config);
        Message = enabled ? "自動作成を起動しました。戦闘開始を待機中です。" : "自動作成を停止しました。";
    }
    internal void SaveFolder(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            Directory.CreateDirectory(full);
            Config.OutputFolder = full; pi.SavePluginConfig(Config); Message = "出力先を保存しました。次の戦闘から適用します。";
        }
        catch (Exception ex) { Fail("出力先設定", ex); }
    }
    internal void SetColumnVisible(int column, bool visible)
    {
        TimelineColumns.SetVisible(Config.HiddenColumns, column, visible);
        pi.SavePluginConfig(Config);
    }
    internal void Export()
    {
        if (Selected == null || Selected.End == null) return;
        try { Message = "CSV保存: " + Csv.Export(Selected, Config.OutputFolder); }
        catch (Exception ex) { Fail("CSV出力", ex); }
    }

    internal void SetMode(CaptureMode mode)
    {
        if (mode == Config.Mode) return;
        Drain(); Finish("モード変更");
        Config.Mode = mode; waitForCombatClear = false; outOfCombatTick = null; buffApplications.Clear();
        replayCursor.Reset(); pi.SavePluginConfig(Config);
        Message = ModeLabel + "に切り替えました。";
    }

    private void SyncPlayback()
    {
        var replay = ContentsReplayManager.Instance();
        var active = replay != null && (replay->PlaybackControls & ContentsReplayPlaybackControl.InPlayback) != 0;
        var paused = active && (replay->PlaybackControls & ContentsReplayPlaybackControl.Paused) != 0;
        if (active != ReplayActive)
        {
            buffApplications.Clear();
            Drain(); Finish(active ? "リプレイ開始" : "リプレイ終了", ClockTick);
            replayCursor.Reset(); waitForCombatClear = false; outOfCombatTick = null;
        }
        if (active)
        {
            var seconds = Math.Max(0, replay->PositionMs / 1000.0);
            if (replayCursor.Observe(seconds, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, replay->PlaybackSpeed) &&
                Config.Mode == CaptureMode.Replay)
            {
                buffApplications.Clear();
                Drain(); Finish("リプレイ位置変更", ClockTick);
                waitForCombatClear = false; outOfCombatTick = null;
            }
            replayPositionSeconds = seconds;
        }
        else replayCursor.Reset();
        ReplayActive = active; ReplayPaused = paused;
    }

    private bool CanCapture
    {
        get
        {
            SyncPlayback();
            return !disposed && !waitForCombatClear && Config.Enabled && client.IsLoggedIn && !client.IsPvP &&
                CapturePolicy.Allows(Config.Mode, ReplayActive, ReplayPaused);
        }
    }

    private List<IPartyMember> ActiveMembers()
    {
        if (Config.Mode != CaptureMode.Replay) return party.ToList();
        var result = new List<IPartyMember>();
        if (!ReplayActive) return result;
        var manager = GroupManager.Instance();
        if (manager == null) return result;
        var group = &manager->ReplayGroup;
        for (var i = 0; i < Math.Min((int)group->MemberCount, 8); i++)
        {
            fixed (FFXIVClientStructs.FFXIV.Client.Game.Group.PartyMember* native = &group->PartyMembers[i])
            {
                var member = party.CreatePartyMemberReference((nint)native);
                if (member != null) result.Add(member);
            }
        }
        return result;
    }

    private HashSet<uint> PartyIds()
    {
        var ids = ActiveMembers().Select(m => m.EntityId).Where(id => id != 0 && id != 0xE0000000).ToHashSet();
        if (Config.Mode == CaptureMode.Normal && objects.LocalPlayer is { } local) ids.Add(local.EntityId);
        return ids;
    }

    private Hit Snapshot(uint targetId, long? amount)
    {
        var member = ActiveMembers().FirstOrDefault(m => m.EntityId == targetId);
        var battle = objects.SearchByEntityId(targetId) as IBattleChara;
        var statuses = battle?.StatusList ?? member?.Statuses;
        var job = battle?.ClassJob.Value.Abbreviation.ToString() ?? member?.ClassJob.Value.Abbreviation.ToString() ?? "不明";
        var hit = statuses == null ? new Hit(job, amount, "取得不可", targetId) :
            statusCatalog.FromIds(job, amount, targetId, statuses.Select(s => s.StatusId));
        if (hit.Effects != null && statuses != null)
            hit = hit with { Effects = hit.Effects.Select(effect =>
            {
                var live = statuses.FirstOrDefault(s => s.StatusId == effect.Id);
                var originAction = live == null ? 0 : buffApplications.Find(targetId, effect.Id, live.SourceId, Environment.TickCount64);
                return originAction != 0 && effect.Category == 1 && DefenseRules.IsDefensive(effect) ? abilityRates.Resolve(effect, originAction) : effect;
            }).ToList() };
        var maxHp = battle?.MaxHp ?? member?.MaxHP;
        var barrier = Libra.Read(targetId, maxHp, ReplayActive);
        return hit with { HpBefore = battle?.CurrentHp ?? member?.CurrentHP, MaxHp = maxHp,
            ShieldPercent = battle == null ? null : ((NativeCharacter*)battle.Address)->CharacterData.ShieldValue,
            CaptureTick = ClockTick, BarrierTotal = barrier.Amount, BarrierSource = barrier.Source };
    }

    private List<StatusInfo>? EnemySnapshot(uint id, NativeCharacter* caster = null)
    {
        // The receive callback owns a valid caster pointer even when the managed object-table
        // entry is absent (replay restoration / enemy helpers). Read before Original runs.
        if (caster != null && (caster->EntityId == id || id == 0) &&
            caster->ObjectKind is FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc or FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc)
        {
            var manager = caster->GetStatusManager();
            if (manager != null)
            {
                var ids = new List<uint>();
                foreach (ref var status in manager->Status)
                    if (status.StatusId != 0) ids.Add(status.StatusId);
                return statusCatalog.FromIds("", null, id, ids).Effects;
            }
        }
        if (objects.SearchByEntityId(id) is IBattleChara enemy)
            return statusCatalog.FromIds("", null, id, enemy.StatusList.Select(s => s.StatusId)).Effects;
        return null; // Missing actor is different from a successfully read, empty status list.
    }

    // Packet layout: four-byte count header, then 0x58-byte full or 0x10-byte basic entries.
    // Sequence + actor identify the hit; unrelated HP updates must never paint a row red.
    private void ReadResults(byte* packet, bool basic, byte replaying)
    {
        if (packet == null || disposed || !Config.Enabled || !client.IsLoggedIn || client.IsPvP ||
            !CapturePolicy.Allows(Config.Mode, ReplayActive, ReplayPaused) || (replaying != 0 && Config.Mode != CaptureMode.Replay)) return;
        var count = packet[0];
        if (count > (basic ? 64 : 16)) return;
        var ids = PartyIds();
        foreach (var result in HpPackets.Read(new ReadOnlySpan<byte>(packet, 4 + count * (basic ? 0x10 : 0x58)), basic, ClockTick))
            if (ids.Contains(result.Target)) hpResults.Enqueue(result);
    }
    private void ReceiveResult(uint target, byte* packet, byte replaying)
    {
        try { ReadResults(packet, false, replaying); }
        catch (Exception ex) { Fail("HP結果取得", ex); }
        finally { resultHook!.Original(target, packet, replaying); }
    }
    private void ReceiveBasicResult(uint target, byte* packet, byte replaying)
    {
        try { ReadResults(packet, true, replaying); }
        catch (Exception ex) { Fail("HP結果取得", ex); }
        finally { basicResultHook!.Original(target, packet, replaying); }
    }

    private void Receive(uint source, NativeCharacter* caster, Vector3* pos, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try
        {
            if (CanCapture && header != null && header->NumTargets <= 64 &&
                (header->NumTargets == 0 || (effects != null && targets != null)))
            {
                var tick = ClockTick;
                var at = DateTimeOffset.Now;
                var ids = PartyIds();
                // Observe status provenance even for pet actions, without logging the pet action.
                for (var i = 0; i < header->NumTargets; i++)
                {
                    var rawStatus = (ActionEffectHandler.Effect*)(effects + i);
                    for (var j = 0; j < 8; j++)
                    {
                        var effect = rawStatus[j];
                        if (effect.Type is not (14 or 15)) continue;
                        var recipient = effect.Type == 15 || (effect.Param4 & 0x80) != 0 ? source : (uint)(ulong)targets[i];
                        if (ids.Contains(recipient)) buffApplications.Observe(recipient, effect.Value, source, header->ActionId, Environment.TickCount64);
                    }
                }
                var sourceObject = objects.SearchByEntityId(source);
                var ownerId = sourceObject?.OwnerId ?? (caster != null ? caster->OwnerId : 0);
                var npcKind = sourceObject is IBattleNpc npc ? (byte)npc.BattleNpcKind :
                    caster != null && caster->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc ? caster->SubKind : (byte)0;
                // Exclude the complete pet action, including damage-less buff/heal rows.
                // Returning here still calls the game's original function in finally.
                if (RecordingRules.IsPetAction(ownerId, npcKind, ids)) return;
                var batch = new DamageBatch(ids);
                // Do not filter by actor kind: invisible helpers, unknown sources and
                // effects directed back at a player can all damage a party member.
                for (var i = 0; i < header->NumTargets; i++)
                {
                    var target = (uint)(ulong)targets[i];
                    var raw = (ActionEffectHandler.Effect*)(effects + i);
                    for (var j = 0; j < 8; j++)
                    {
                        var effect = raw[j];
                        batch.Add(source, target, effect.Type, effect.Param3, effect.Param4, effect.Value, effect.Param1);
                    }
                }
                var action = data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(header->ActionId);
                var name = RecordingRules.ActionName(header->ActionId, action?.ActionCategory.RowId ?? 0, action?.Name.ToString());
                var sourceName = sourceObject?.Name.TextValue;
                if (string.IsNullOrEmpty(sourceName) && caster != null) sourceName = caster->NameString;
                if (string.IsNullOrEmpty(sourceName)) sourceName = $"不明（ID:{source:X8}）";
                var anyDamage = false;
                foreach (var group in batch.Entries.GroupBy(d => d.Source))
                {
                    anyDamage = true;
                    var originName = group.Key == source ? sourceName :
                        objects.SearchByEntityId(group.Key)?.Name.TextValue ?? $"不明（ID:{group.Key:X8}）";
                    var eventName = group.Any(d => d.Redirected) ? name + "（反射・反撃等）" : name;
                    queue.Enqueue(new(tick, at, group.Key, header->GlobalSequence, header->ActionId, originName,
                        eventName, group.Select(d => Snapshot(d.Target, d.Amount) with { DamageType = d.DamageType, SpecialDamage = d.Special }).ToList(),
                        EnemyEffects: EnemySnapshot(group.Key, group.Key == source ? caster : null)));
                }
                if (!anyDamage && (Current != null || PartyInCombat) && sourceObject is IBattleNpc)
                {
                    var hits = new List<Hit>();
                    for (var i = 0; i < header->NumTargets; i++)
                    {
                        var target = (uint)(ulong)targets[i];
                        if (ids.Contains(target)) hits.Add(Snapshot(target, null));
                    }
                    queue.Enqueue(new(tick, at, source, header->GlobalSequence, header->ActionId, sourceName, name, hits, EnemyEffects: EnemySnapshot(source, caster)));
                }
            }
        }
        catch (Exception ex) { Fail("攻撃結果取得", ex); }
        finally { hook!.Original(source, caster, pos, header, effects, targets); }
    }

    private void ReceiveActorControl(uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        try
        {
            var amount = PeriodicDamage.Decode(category, arg2);
            if (CanCapture && (!isRecorded || Config.Mode == CaptureMode.Replay) && amount.HasValue && PartyIds().Contains(entityId))
            {
                var tick = ClockTick;
                var statusName = arg1 == 0 ? null : data.GetExcelSheet<Status>().GetRowOrDefault(arg1)?.Name.ToString();
                // A periodic tick may aggregate several status sources. Never assign
                // the total to one guessed enemy or split it into fabricated amounts.
                queue.Enqueue(new(tick, DateTimeOffset.Now, 0, 0, arg1, "不明（継続ダメージ）",
                    PeriodicDamage.Name(arg1, statusName), [Snapshot(entityId, amount)], "ActorControlDoT"));
            }
        }
        catch (Exception ex) { Fail("継続ダメージ取得", ex); }
        finally { periodicHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded); }
    }

    private void Update(IFramework _)
    {
        try
        {
            SyncPlayback();
            if (!Config.Enabled) return;
            Drain();
            if (!CapturePolicy.Allows(Config.Mode, ReplayActive, ReplayPaused)) return;
            if (!client.IsLoggedIn || client.IsPvP) { Finish("監視対象外"); return; }
            var inCombat = PartyInCombat;
            if (waitForCombatClear)
            {
                if (inCombat) return;
                waitForCombatClear = false;
            }
            if (inCombat)
            {
                if (Current == null) Start(ClockTick, DateTimeOffset.Now);
                seenCombat = true; outOfCombatTick = null;
            }
            else if (Current != null)
            {
                outOfCombatTick ??= ClockTick;
                // Debounce short flag changes. Timestamp ends at the first clear frame.
                if (Stopwatch.GetElapsedTime(outOfCombatTick.Value, ClockTick).TotalSeconds >= (seenCombat ? 1.5 : 5))
                    Finish("戦闘終了", outOfCombatTick.Value);
            }
        }
        catch (Exception ex) { Fail("戦闘監視", ex); }
    }
    private void Start(long tick, DateTimeOffset at)
    {
        Current = new() { Content = ContentName, Start = at, Mode = Config.Mode, ReplayStartSeconds = Config.Mode == CaptureMode.Replay ? replayPositionSeconds : 0 };
        Selected = Current; startTick = tick; grouper = new(); seenCombat = false; outOfCombatTick = null;
        pendingPath = "";
        try
        {
            Directory.CreateDirectory(Config.OutputFolder);
            pendingPath = Csv.Unique(Config.OutputFolder, $"{Csv.Safe(Current.Content)}_{at:yyyyMMdd-HHmmss-fff}_記録中_AMTLOG.csv");
            writer = Csv.Open(pendingPath);
            Current.CsvPath = pendingPath;
        }
        catch (Exception ex) { Fail("CSV書き込み開始（内部記録は継続）", ex); }
    }
    private void Drain()
    {
        var added = false;
        while (queue.TryDequeue(out var packet))
        {
            if (Current == null) Start(packet.Tick, packet.At);
            outOfCombatTick = null;
            var hits = packet.Hits.Count > 0 ? packet.Hits : [new Hit("", null, "")];
            foreach (var row in grouper.Add(packet.Source, packet.Sequence, packet.ActionId, packet.Tick,
                         Math.Max(0, Stopwatch.GetElapsedTime(startTick, packet.Tick).TotalSeconds), packet.Enemy, packet.Action, hits, packet.Origin))
            {
                var enriched = row with { EnemyEffects = packet.EnemyEffects };
                Current!.Rows.Add(enriched);
                added = true;
                try { writer?.WriteLine(Csv.Line(enriched)); }
                catch (Exception ex) { try { writer?.Dispose(); } catch { } writer = null; Fail("CSV書き込み（内部記録は継続）", ex); }
            }
        }
        if (added && Current != null) Current.Rows = TimelineOrder.Sort(Current.Rows);
        var changed = new HashSet<Encounter>();
        while (hpResults.TryDequeue(out var result))
        {
            // Late resolution after duty completion can still update the most recent encounter.
            var encounters = (Current != null ? new[] { Current }.Concat(History.Items.Take(1)) : History.Items.Take(1)).ToList();
            foreach (var encounter in encounters)
                for (var i = 0; i < encounter.Rows.Count; i++)
                {
                    var row = encounter.Rows[i];
                    var updated = HpCorrelation.Apply(row, result, Stopwatch.Frequency);
                    if (updated != row) { encounter.Rows[i] = updated; if (encounter.End != null) changed.Add(encounter); }
                }
        }
        foreach (var encounter in changed)
        {
            try { History.Save(encounter); if (!string.IsNullOrEmpty(encounter.CsvPath)) Csv.Rewrite(encounter, encounter.CsvPath); }
            catch (Exception ex) { Fail("確定HPの履歴更新", ex); }
        }
    }
    private void Finish(string reason, long? tick = null)
    {
        var e = Current;
        if (e == null) return;
        Current = null;
        e.DurationSeconds = Math.Max(e.Rows.LastOrDefault()?.Seconds ?? 0, Stopwatch.GetElapsedTime(startTick, tick ?? ClockTick).TotalSeconds);
        e.End = e.Start.AddSeconds(e.DurationSeconds); e.EndReason = reason;
        try
        {
            var complete = writer != null;
            writer?.Dispose(); writer = null;
            if (complete)
            {
                var final = Csv.Unique(Path.GetDirectoryName(pendingPath)!, Csv.FileName(e));
                Csv.Rewrite(e, pendingPath);
                File.Move(pendingPath, final); e.CsvPath = final;
            }
            else e.CsvPath = Csv.Export(e, Config.OutputFolder);
        }
        catch (Exception ex) { Fail("CSV確定（履歴から再出力できます）", ex); }
        finally { writer = null; }
        try { History.Save(e); }
        catch (Exception ex)
        {
            Fail("履歴保存", ex);
            if (!History.Items.Contains(e)) History.Items.Insert(0, e);
            if (History.Items.Count > 100) History.Items.RemoveRange(100, History.Items.Count - 100);
        }
    }
    private void Boundary(string reason) { waitForCombatClear = Config.Mode == CaptureMode.Normal; Drain(); Finish(reason); }
    private void TerritoryChanged(uint _) { buffApplications.Clear(); Boundary("エリア移動"); }
    private void Logout(int type, int code) => Boundary("ログアウト");
    private void Wiped(Dalamud.Game.DutyState.IDutyStateEventArgs _) => Boundary("全滅");
    private void Completed(Dalamud.Game.DutyState.IDutyStateEventArgs _) => Boundary("コンテンツ終了");
    private void Fail(string context, Exception ex) { Error = context + ": " + ex.Message; log.Error(ex, context); }
    public void Dispose()
    {
        disposed = true; hook?.Disable(); periodicHook?.Disable(); resultHook?.Disable(); basicResultHook?.Disable();
        framework.Update -= Update; client.TerritoryChanged -= TerritoryChanged; client.Logout -= Logout;
        duty.DutyWiped -= Wiped; duty.DutyCompleted -= Completed;
        pi.UiBuilder.Draw -= window.Draw; pi.UiBuilder.OpenMainUi -= window.Open; pi.UiBuilder.OpenConfigUi -= window.Open;
        commands.RemoveHandler("/amt"); Drain(); Finish("プラグイン終了"); hook?.Dispose(); periodicHook?.Dispose(); resultHook?.Dispose(); basicResultHook?.Dispose(); window.Dispose();
    }
}
