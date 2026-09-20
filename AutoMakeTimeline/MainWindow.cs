using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace AutoMakeTimeline;

internal sealed class MainWindow : IDisposable
{
    private readonly Plugin plugin;
    private readonly ITextureProvider textures;
    private bool open;
    private string folder;
    private bool follow = true;
    private bool editColumns;
    private bool editVisibility;
    private static readonly Vector4 Accent = new(.22f, .85f, .77f, 1);
    private static readonly Vector4 Muted = new(.52f, .61f, .72f, 1);
    public MainWindow(Plugin plugin, ITextureProvider textures)
    {
        this.plugin = plugin; this.textures = textures; folder = plugin.Config.OutputFolder;
    }
    public void Open() => open = true;
    public void Dispose() { }
    public void Draw()
    {
        if (!open) return;
        ImGui.SetNextWindowSize(new(1380, 850), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new(1020, 620), new(float.MaxValue, float.MaxValue));
        var colors = new (ImGuiCol, Vector4)[]
        {
            (ImGuiCol.WindowBg, new(.035f,.047f,.073f,1)),
            (ImGuiCol.TitleBg, new(.055f,.075f,.11f,1)),
            (ImGuiCol.TitleBgActive, new(.075f,.12f,.17f,1)),
            (ImGuiCol.ChildBg, new(.055f,.073f,.105f,1)),
            (ImGuiCol.TableHeaderBg, new(.075f,.13f,.18f,1)),
            (ImGuiCol.TableBorderStrong, new(.15f,.23f,.29f,1)),
            (ImGuiCol.TableBorderLight, new(.10f,.15f,.21f,1)),
            (ImGuiCol.TableRowBgAlt, new(.08f,.11f,.16f,.65f)),
            (ImGuiCol.Button, new(.08f,.30f,.32f,1)),
            (ImGuiCol.ButtonHovered, new(.10f,.43f,.43f,1)),
            (ImGuiCol.ButtonActive, new(.08f,.53f,.48f,1)),
            (ImGuiCol.Header, new(.10f,.27f,.31f,1)),
            (ImGuiCol.HeaderHovered, new(.12f,.24f,.31f,1)),
            (ImGuiCol.HeaderActive, new(.13f,.34f,.38f,1)),
            (ImGuiCol.FrameBg, new(.075f,.11f,.16f,1)),
            (ImGuiCol.CheckMark, Accent),
            (ImGuiCol.Text, new(.88f,.93f,.98f,1)),
        };
        foreach (var (color, value) in colors) ImGui.PushStyleColor(color, value);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 18));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 7));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12, 9));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        try
        {
            var visible = ImGui.Begin("Auto Make Timeline v1.0.0.3###AMT", ref open);
            try { if (visible) DrawContents(); }
            finally { ImGui.End(); }
        }
        finally { ImGui.PopStyleVar(5); ImGui.PopStyleColor(colors.Length); }
    }
    private void DrawContents()
    {
        if (!ImGui.BeginTabBar("amtTabs")) return;
        if (ImGui.BeginTabItem("タイムライン"))
        {
            DrawTimelineContents();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("連携プラグイン"))
        {
            ImGui.Spacing();
            ImGui.TextColored(Accent, "devLibra / Barrier HP");
            ImGui.TextColored(plugin.Libra.Available ? Accent : new Vector4(1,.65f,.3f,1), plugin.Libra.State);
            ImGui.Separator();
            ImGui.TextWrapped("バリア合計の表示には、連携対応版devLibraを起動する必要があります。付属のdevLibra-latest.zipを導入し、既存のdevLibraと重複して読み込まないでください。");
            ImGui.TextWrapped("devLibraのBarrier HPタブで計算されるBarrier値を、被弾時に取得してバリア合計列へ保存します。現在HPを加えたDisplay HPではありません。個々のバリア名の横には量を表示しません。");
            ImGui.TextWrapped("devLibraの設定画面を開いておく必要はありません。パーティリストが更新され、対象者のデータを取得できる状態が必要です。HP表示への加算チェックは連携データの取得には不要です。");
            ImGui.TextWrapped("未起動・旧版・対象のデータなし・古いデータの場合は取得不可になります。devLibraの計算方法による推定誤差を含み、攻撃が吸収した量を示すものではありません。");
            ImGui.TextColored(Muted, "devLibra BarrierHP IPC v1 / AMT v1.0.0.3");
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }
    private void DrawTimelineContents()
    {
        ImGui.TextColored(Accent, "AUTO MAKE TIMELINE");
        ImGui.SameLine();
        ImGui.TextColored(Muted, "COMBAT RECORDER  /  v1.0.0.3");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(185);
        var mode = (int)plugin.Config.Mode;
        if (ImGui.Combo("取得モード", ref mode, "通常モード\0リプレイモード\0"))
            plugin.SetMode((CaptureMode)mode);
        ImGui.SameLine();
        if (ImGui.Button(plugin.Config.Enabled ? "記録を停止" : "記録を有効にする", new Vector2(150, 0)))
            plugin.SetEnabled(!plugin.Config.Enabled);
        ImGui.SameLine();
        ImGui.TextColored(plugin.Current != null ? Accent : Muted, "● " + plugin.StateLabel);
        if (plugin.Config.Mode == CaptureMode.Replay)
            ImGui.TextColored(Muted, "戦闘開始から実時間で計測  /  一時停止中も計測を継続  /  位置移動時は記録を分割");
        var selected = plugin.Selected;
        ImGui.Spacing();
        if (ImGui.BeginTable("summary", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("content", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("duration", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("events", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, "CONTENT / コンテンツ");
            ImGui.TextUnformatted(selected?.Content ?? plugin.ContentName);
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, "DURATION / 経過");
            ImGui.TextColored(Accent, Csv.Time(selected == plugin.Current ? plugin.Elapsed : selected?.DurationSeconds ?? 0));
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, "RECORDS / 記録行");
            ImGui.TextUnformatted((selected?.Rows.Count ?? 0).ToString("N0"));
            ImGui.EndTable();
        }
        ImGui.Separator();
        var workspaceHeight = Math.Max(240, ImGui.GetContentRegionAvail().Y - 100);
        if (ImGui.BeginTable("workspace", 2, ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("timeline", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("history", ImGuiTableColumnFlags.WidthFixed, 280);
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("timelinePanel", new Vector2(0, workspaceHeight), false))
            {
                ImGui.TextColored(Accent, "TIMELINE");
                ImGui.SameLine();
                ImGui.Checkbox("末尾を追従", ref follow);
                ImGui.SameLine();
                if (ImGui.SmallButton("現在の記録")) plugin.Selected = plugin.Current ?? plugin.History.Items.FirstOrDefault();
                ImGui.SameLine();
                if (ImGui.SmallButton(editColumns ? "列変更：許可中" : "列変更：ロック中")) editColumns = !editColumns;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("許可中は見出しをドラッグして列順を変更できます。横スクロールで全列を確認できます。");
                ImGui.SameLine();
                if (ImGui.SmallButton(editVisibility ? "列表示の編集を閉じる" : "列表示・非表示を変更")) editVisibility = !editVisibility;
                if (editVisibility) DrawVisibilityHeader();
                if (selected != null)
                    ImGui.TextColored(Muted, $"{(selected.Mode == CaptureMode.Replay ? "REPLAY" : "LIVE")}  /  {selected.Start:MM/dd HH:mm:ss}  /  {selected.EndReason}");
                if (selected == null || selected.Rows.Count == 0)
                {
                    ImGui.Spacing(); ImGui.Spacing();
                    ImGui.TextColored(Muted, "まだ攻撃の記録がありません");
                    ImGui.TextWrapped("取得モードを選び、記録を有効にしてください。戦闘が始まると、ここにタイムラインが表示されます。");
                }
                else DrawTimeline(selected);
            }
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("historyPanel", new Vector2(0, workspaceHeight), true))
            {
                ImGui.TextColored(Accent, "HISTORY");
                ImGui.SameLine(); ImGui.TextColored(Muted, $"{plugin.History.Items.Count} / 100");
                ImGui.Separator();
                if (ImGui.BeginChild("historyList", new Vector2(0, 0), false))
                {
                    foreach (var e in plugin.History.Items)
                    {
                        var label = $"{e.Start:MM/dd  HH:mm:ss}   {(e.Mode == CaptureMode.Replay ? "REPLAY" : "LIVE")}\n{e.Content}\n{Csv.Time(e.DurationSeconds)}  /  {e.Rows.Count:N0}行";
                        if (ImGui.Selectable(label + "###" + e.Id, selected?.Id == e.Id, ImGuiSelectableFlags.None, new Vector2(0, 70)))
                            plugin.Selected = e;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Csv.FileName(e));
                    }
                }
                ImGui.EndChild();
            }
            ImGui.EndChild();
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ImGui.BeginDisabled(plugin.Selected?.End == null);
        if (ImGui.Button("選択した記録をCSVに出力", new Vector2(250, 36))) plugin.Export();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("出力先設定", new Vector2(130, 36))) ImGui.OpenPopup("出力先設定");
        if (ImGui.BeginPopup("出力先設定"))
        {
            ImGui.SetNextItemWidth(550);
            ImGui.InputText("フォルダ", ref folder, 1024);
            if (ImGui.Button("保存")) { plugin.SaveFolder(folder); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("標準ダウンロード")) folder = DownloadsFolder.Get();
            ImGui.EndPopup();
        }
        ImGui.TextColored(Muted, string.IsNullOrEmpty(plugin.Message) ? "CSV / UTF-8  ·  履歴は自動保存されます" : plugin.Message);
        if (!string.IsNullOrEmpty(plugin.Error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1,.48f,.4f,1));
            ImGui.TextWrapped(plugin.Error); ImGui.PopStyleColor();
        }
    }
    private void DrawTimeline(Encounter selected)
    {
        if (Enumerable.Range(0, TimelineColumns.Names.Length).All(plugin.Config.HiddenColumns.Contains))
        { ImGui.TextColored(Muted, "すべての列が非表示です。ヘッダーのチェックを入れると再表示できます。"); return; }
        if (!ImGui.BeginTable("timelineDetails", TimelineColumns.Names.Length, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX,
            new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        var columnFlags = ImGuiTableColumnFlags.WidthFixed | (editColumns ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.NoReorder);
        for (var column = 0; column < TimelineColumns.Names.Length; column++)
        {
            ImGui.TableSetupColumn(TimelineColumns.Names[column], columnFlags, TimelineColumns.Widths[column], (uint)(column + 1));
            ImGui.TableSetColumnEnabled(column, !plugin.Config.HiddenColumns.Contains(column));
        }
        ImGui.TableHeadersRow();
        var lineHeight = Math.Max(20, ImGui.GetTextLineHeight());
        var rowHeight = (lineHeight + ImGui.GetStyle().ItemSpacing.Y) * 3 + ImGui.GetStyle().CellPadding.Y * 2;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(selected.Rows.Count, rowHeight);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var r = selected.Rows[i];
                var first = i == clipper.DisplayStart || selected.Rows[i - 1].No != r.No;
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                Cell(first ? r.No.ToString() : "", Muted); Cell(first ? Csv.Time(r.Seconds) : "", Accent);
                Cell(first ? r.Enemy : ""); ActionCell(first ? r.Action : "", r.Hit.DamageType);
                Cell(r.Hit.Job, JobColor(r.Hit.Job));
                Cell(r.Hit.Damage?.ToString("N0") ?? "—", r.Hit.Fatal ? new Vector4(1, .22f, .22f, 1) : null);
                var estimate = DamageEstimate.For(r);
                Cell(r.Hit.Damage.HasValue ? estimate.Display : "—", estimate.Value.HasValue ? Accent : Muted, estimate.Reason);
                Cell(r.Hit.RemainingHp?.ToString("N0") ?? "取得不可", r.Hit.Fatal ? new Vector4(1, .22f, .22f, 1) : null);
                StatusCell(r.Hit.Effects, false, lineHeight, r.Hit);
                StatusCell(r.Hit.Effects, true, lineHeight, r.Hit);
                StatusCell(EnemyStatusRules.Display(r.EnemyEffects), false, lineHeight);
                StatusCell(EnemyStatusRules.Display(r.EnemyEffects), true, lineHeight);
                Cell(r.Hit.BarrierTotal?.ToString("N0") ?? "取得不可", Muted, r.Hit.BarrierSource);
                var mitigation = MitigationSummary.For(r);
                Cell(mitigation.Display, mitigation.Percent.HasValue ? Accent : Muted, mitigation.Reason);
            }
        }
        clipper.End(); clipper.Destroy();
        if (follow && selected == plugin.Current) ImGui.SetScrollY(ImGui.GetScrollMaxY());
        ImGui.EndTable();
    }
    private void StatusCell(List<StatusInfo>? effects, bool debuff, float size, Hit? hit = null)
    {
        if (!ImGui.TableNextColumn()) return;
        if (effects == null)
        {
            ImGui.TextColored(Muted, "取得不可");
            return;
        }
        var list = effects.Where(s => debuff ? s.Category == 2 : hit == null ? s.Category != 2 : s.Category == 1 && DefenseRules.IsDefensive(s)).ToList();
        if (list.Count == 0) { ImGui.TextColored(Muted, "—"); return; }
        var shown = list.Count > 3 ? 2 : list.Count;
        var cellHovered = false;
        for (var i = 0; i < shown; i++) cellHovered |= DrawStatus(list[i], size, hit);
        if (list.Count > shown)
        {
            ImGui.TextColored(Muted, $"+{list.Count - shown} 件（マウスで一覧）");
            cellHovered |= ImGui.IsItemHovered();
        }
        if (cellHovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(debuff ? new Vector4(1,.62f,.55f,1) : Accent, debuff ? "デバフ" : "バフ");
            foreach (var status in list) DrawStatus(status, size, hit);
            ImGui.EndTooltip();
        }
    }
    private bool DrawStatus(StatusInfo status, float size, Hit? hit = null)
    {
        var wrap = status.Icon == 0 ? null : textures.GetFromGameIcon(status.Icon).GetWrapOrDefault();
        if (wrap != null) ImGui.Image(wrap.Handle, new Vector2(size, size));
        else ImGui.Dummy(new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, 5);
        ImGui.TextUnformatted(status.Category == 0 ? status.Name + "（分類不明）" : DefenseRules.StatusLabel(status, hit));
        return hovered || ImGui.IsItemHovered();
    }
    private static void ActionCell(string text, byte type)
    {
        if (!ImGui.TableNextColumn() || text.Length == 0) return;
        // Code-drawn sword / staff avoid missing font glyphs and undocumented game icon IDs.
        var size = ImGui.GetTextLineHeight();
        var p = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var physical = AttackKind.Physical(type);
        if (physical || AttackKind.Magical(type))
        {
            var color = ImGui.GetColorU32(physical ? new Vector4(1,.63f,.43f,1) : new Vector4(.5f,.75f,1,1));
            Vector2 P(float x, float y) => p + new Vector2(x * size, y * size);
            if (physical)
            {
                draw.AddLine(P(.25f,.8f), P(.83f,.16f), color, 3);
                draw.AddLine(P(.16f,.57f), P(.48f,.87f), color, 2);
                draw.AddTriangleFilled(P(.83f,.16f), P(.64f,.22f), P(.77f,.36f), color);
            }
            else
            {
                draw.AddLine(P(.28f,.9f), P(.65f,.32f), color, 3);
                draw.AddCircle(P(.67f,.24f), size * .16f, color, 12, 2);
                draw.AddCircleFilled(P(.67f,.24f), size * .06f, color);
            }
            ImGui.Dummy(new Vector2(size, size));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(AttackKind.Name(type) + "ダメージ");
            ImGui.SameLine(0, 5);
        }
        ImGui.TextUnformatted(text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(AttackKind.Name(type) + " / " + text);
    }
    private static Vector4 JobColor(string job) => job switch
    {
        "PLD" or "WAR" or "DRK" or "GNB" => new(.45f,.68f,1,1),
        "WHM" or "SCH" or "AST" or "SGE" => new(.45f,.87f,.6f,1),
        _ => new(.98f,.62f,.57f,1),
    };
    private void DrawVisibilityHeader()
    {
        ImGui.TextColored(Muted, "表示する列（チェックで再表示できます）");
        if (!ImGui.BeginTable("columnVisibilityHeader", 4, ImGuiTableFlags.SizingStretchSame)) return;
        for (var i = 0; i < TimelineColumns.Names.Length; i++)
        {
            ImGui.TableNextColumn();
            var enabled = !plugin.Config.HiddenColumns.Contains(i);
            if (ImGui.Checkbox(TimelineColumns.Names[i] + "##visible" + i, ref enabled)) plugin.SetColumnVisible(i, enabled);
        }
        ImGui.EndTable();
    }
    private static void Cell(string text, Vector4? color = null, string? tooltip = null)
    {
        if (!ImGui.TableNextColumn()) return;
        if (color.HasValue) ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        ImGui.TextUnformatted(text);
        if (color.HasValue) ImGui.PopStyleColor();
        if (text.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip ?? text);
    }
}
