using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace AutoMakeTimeline;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public string OutputFolder { get; set; } = "";
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
    private Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? periodicHook;
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
    private record Packet(long Tick, DateTimeOffset At, uint Source, uint Sequence, uint ActionId, string Enemy, string Action, List<Hit> Hits, string Origin = "ActionEffect");

    internal Configuration Config { get; }
    internal HistoryStore History { get; }
    internal Encounter? Current { get; private set; }
    internal Encounter? Selected { get; set; }
    internal string Message { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal bool HookReady => hook?.IsEnabled == true && periodicHook?.IsEnabled == true;
    internal double Elapsed => Current == null ? 0 : Stopwatch.GetElapsedTime(startTick).TotalSeconds;
    private bool PartyInCombat => condition[ConditionFlag.InCombat] || party.Any(m =>
        m.GameObject is IBattleChara b && (b.StatusFlags & StatusFlags.InCombat) != 0);
    internal string ContentName
    {
        get
        {
            var name = duty.ContentFinderCondition.ValueNullable?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return data.GetExcelSheet<TerritoryType>().GetRowOrDefault(client.TerritoryType)?.PlaceName.Value.Name.ToString() ?? $"Territory-{client.TerritoryType}";
        }
    }

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework,
        ICondition condition, IClientState client, IObjectTable objects, IPartyList party,
        IDataManager data, IDutyState duty, IGameInteropProvider interop, IPluginLog log)
    {
        this.pi = pi; this.commands = commands; this.framework = framework; this.condition = condition;
        this.client = client; this.objects = objects; this.party = party; this.data = data; this.duty = duty; this.log = log;
        Config = pi.GetPluginConfig() as Configuration ?? new();
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
            var excludedNames = data.GetExcelSheet<Status>()
                .Where(s => !RecordingRules.IncludeStatus(s.RowId, s.IsFcBuff))
                .Select(s => s.Name.ToString()).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var encounter in History.Items)
                encounter.Rows = encounter.Rows.Select(r => RecordingRules.NormalizeHistory(r, aaNames, excludedNames)).ToList();
        }
        catch (Exception ex) { Fail("履歴の表示情報更新", ex); }
        Selected = History.Items.FirstOrDefault();
        window = new(this, pi);
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
    internal void Export()
    {
        if (Selected == null || Selected.End == null) return;
        try { Message = "CSV保存: " + Csv.Export(Selected, Config.OutputFolder); }
        catch (Exception ex) { Fail("CSV出力", ex); }
    }

    private bool CanCapture => !disposed && !waitForCombatClear && Config.Enabled && client.IsLoggedIn && !client.IsPvP;

    private HashSet<uint> PartyIds()
    {
        var ids = party.Select(m => m.EntityId).Where(id => id != 0 && id != 0xE0000000).ToHashSet();
        if (objects.LocalPlayer is { } local) ids.Add(local.EntityId);
        return ids;
    }

    private Hit Snapshot(uint targetId, long? amount)
    {
        var member = party.FirstOrDefault(m => m.EntityId == targetId);
        var battle = objects.SearchByEntityId(targetId) as IBattleChara;
        var statuses = battle?.StatusList ?? member?.Statuses;
        var statusText = statuses == null ? "取得不可" : string.Join(",", statuses.Where(s => s.StatusId != 0)
            .Select(s => (Id: s.StatusId, Row: data.GetExcelSheet<Status>().GetRowOrDefault(s.StatusId)))
            .Where(s => RecordingRules.IncludeStatus(s.Id, s.Row?.IsFcBuff ?? false))
            .Select(s => s.Row?.Name.ToString() ?? $"Status#{s.Id}"));
        var job = battle?.ClassJob.Value.Abbreviation.ToString() ?? member?.ClassJob.Value.Abbreviation.ToString() ?? "不明";
        return new(job, amount, statusText, targetId);
    }

    private void Receive(uint source, NativeCharacter* caster, Vector3* pos, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try
        {
            if (CanCapture && header != null && header->NumTargets <= 64 &&
                (header->NumTargets == 0 || (effects != null && targets != null)))
            {
                var tick = Stopwatch.GetTimestamp();
                var at = DateTimeOffset.Now;
                var ids = PartyIds();
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
                        batch.Add(source, target, effect.Type, effect.Param3, effect.Param4, effect.Value);
                    }
                }
                var action = data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(header->ActionId);
                var name = RecordingRules.ActionName(header->ActionId, action?.ActionCategory.RowId ?? 0, action?.Name.ToString());
                var sourceObject = objects.SearchByEntityId(source);
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
                        eventName, group.Select(d => Snapshot(d.Target, d.Amount)).ToList()));
                }
                if (!anyDamage && (Current != null || PartyInCombat) && sourceObject is IBattleNpc)
                {
                    var hits = new List<Hit>();
                    for (var i = 0; i < header->NumTargets; i++)
                    {
                        var target = (uint)(ulong)targets[i];
                        if (ids.Contains(target)) hits.Add(Snapshot(target, null));
                    }
                    queue.Enqueue(new(tick, at, source, header->GlobalSequence, header->ActionId, sourceName, name, hits));
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
            if (CanCapture && !isRecorded && amount.HasValue && PartyIds().Contains(entityId))
            {
                var tick = Stopwatch.GetTimestamp();
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
        if (!Config.Enabled) return;
        try
        {
            Drain();
            if (!client.IsLoggedIn || client.IsPvP) { Finish("監視対象外"); return; }
            var inCombat = PartyInCombat;
            if (waitForCombatClear)
            {
                if (inCombat) return;
                waitForCombatClear = false;
            }
            if (inCombat)
            {
                if (Current == null) Start(Stopwatch.GetTimestamp(), DateTimeOffset.Now);
                seenCombat = true; outOfCombatTick = null;
            }
            else if (Current != null)
            {
                outOfCombatTick ??= Stopwatch.GetTimestamp();
                // Debounce short flag changes. Timestamp ends at the first clear frame.
                if (Stopwatch.GetElapsedTime(outOfCombatTick.Value).TotalSeconds >= (seenCombat ? 1.5 : 5))
                    Finish("戦闘終了", outOfCombatTick.Value);
            }
        }
        catch (Exception ex) { Fail("戦闘監視", ex); }
    }
    private void Start(long tick, DateTimeOffset at)
    {
        Current = new() { Content = ContentName, Start = at };
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
        while (queue.TryDequeue(out var packet))
        {
            if (Current == null) Start(packet.Tick, packet.At);
            outOfCombatTick = null;
            var hits = packet.Hits.Count > 0 ? packet.Hits : [new Hit("", null, "")];
            foreach (var row in grouper.Add(packet.Source, packet.Sequence, packet.ActionId, packet.Tick,
                         Math.Max(0, Stopwatch.GetElapsedTime(startTick, packet.Tick).TotalSeconds), packet.Enemy, packet.Action, hits, packet.Origin))
            {
                Current!.Rows.Add(row);
                try { writer?.WriteLine(Csv.Line(row)); }
                catch (Exception ex) { try { writer?.Dispose(); } catch { } writer = null; Fail("CSV書き込み（内部記録は継続）", ex); }
            }
        }
    }
    private void Finish(string reason, long? tick = null)
    {
        var e = Current;
        if (e == null) return;
        Current = null;
        e.DurationSeconds = Math.Max(e.Rows.LastOrDefault()?.Seconds ?? 0, Stopwatch.GetElapsedTime(startTick, tick ?? Stopwatch.GetTimestamp()).TotalSeconds);
        e.End = e.Start.AddSeconds(e.DurationSeconds); e.EndReason = reason;
        try
        {
            var complete = writer != null;
            writer?.Dispose(); writer = null;
            if (complete)
            {
                var final = Csv.Unique(Path.GetDirectoryName(pendingPath)!, Csv.FileName(e));
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
    private void Boundary(string reason) { waitForCombatClear = true; Drain(); Finish(reason); }
    private void TerritoryChanged(uint _) => Boundary("エリア移動");
    private void Logout(int type, int code) => Boundary("ログアウト");
    private void Wiped(Dalamud.Game.DutyState.IDutyStateEventArgs _) => Boundary("全滅");
    private void Completed(Dalamud.Game.DutyState.IDutyStateEventArgs _) => Boundary("コンテンツ終了");
    private void Fail(string context, Exception ex) { Error = context + ": " + ex.Message; log.Error(ex, context); }
    public void Dispose()
    {
        disposed = true; hook?.Disable(); periodicHook?.Disable();
        framework.Update -= Update; client.TerritoryChanged -= TerritoryChanged; client.Logout -= Logout;
        duty.DutyWiped -= Wiped; duty.DutyCompleted -= Completed;
        pi.UiBuilder.Draw -= window.Draw; pi.UiBuilder.OpenMainUi -= window.Open; pi.UiBuilder.OpenConfigUi -= window.Open;
        commands.RemoveHandler("/amt"); Drain(); Finish("プラグイン終了"); hook?.Dispose(); periodicHook?.Dispose(); window.Dispose();
    }
}
