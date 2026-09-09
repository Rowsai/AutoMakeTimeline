using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace AutoMakeTimeline;

internal sealed class MainWindow : IDisposable
{
    private readonly Plugin plugin;
    private bool open;
    private string folder;
    private bool follow = true;
    public MainWindow(Plugin plugin, IDalamudPluginInterface pi)
    {
        this.plugin = plugin;
        folder = plugin.Config.OutputFolder;
    }
    public void Open() => open = true;
    public void Dispose() { }
    public void Draw()
    {
        if (!open) return;
        ImGui.SetNextWindowSize(new(1200, 800), ImGuiCond.FirstUseEver);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0, .58f, .29f, 1));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0, .35f, .18f, 1));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.015f, .025f, .03f, .98f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(.025f, .31f, .40f, 1));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, new Vector4(.025f, .31f, .40f, 1));
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, new Vector4(.025f, .23f, .30f, 1));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.025f, .31f, .40f, 1));
        if (ImGui.Begin("Auto Make Timeline v1.0.0.2###AMT", ref open))
        {
            DrawContents();
        }
        ImGui.End();
        ImGui.PopStyleColor(7);
    }
    private void DrawContents()
    {
        var enabled = plugin.Config.Enabled;
        if (ImGui.Checkbox("タイムライン自動作成", ref enabled)) plugin.SetEnabled(enabled);
        ImGui.SameLine();
        ImGui.TextUnformatted(!plugin.HookReady ? "監視エラー" : plugin.Current != null ? $"● 記録中  {Csv.Time(plugin.Elapsed)}" : enabled ? "戦闘開始待機中" : "停止中");
        ImGui.SameLine();
        if (ImGui.Button("現在の記録")) plugin.Selected = plugin.Current ?? plugin.History.Items.FirstOrDefault();
        var selected = plugin.Selected;
        ImGui.TextUnformatted("コンテンツ情報: " + (selected?.Content ?? plugin.ContentName));
        if (selected != null)
            ImGui.TextUnformatted($"開始: {selected.Start:yyyy/MM/dd HH:mm:ss}   終了: {selected.End:HH:mm:ss}   {selected.EndReason}   {selected.Rows.Count:N0}行");
        ImGui.Checkbox("記録中は末尾へスクロール", ref follow);
        var height = Math.Max(150, ImGui.GetContentRegionAvail().Y - 245);
        if (ImGui.BeginTable("timeline", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("No.", ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn("時間", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("エネミー名称", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("攻撃名称", ImGuiTableColumnFlags.WidthStretch, 1.7f);
            ImGui.TableSetupColumn("ジョブ", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("ダメージ", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("バフ・デバフ情報", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableHeadersRow();
            if (selected != null)
            {
                // Virtualize rows: large encounters must not redraw every hit every frame.
                var clipper = ImGui.ImGuiListClipper();
                clipper.Begin(selected.Rows.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var row = selected.Rows[i];
                        var first = i == clipper.DisplayStart || selected.Rows[i - 1].No != row.No;
                        ImGui.TableNextRow();
                        Cell(first ? row.No.ToString() : ""); Cell(first ? Csv.Time(row.Seconds) : "");
                        Cell(first ? row.Enemy : ""); Cell(first ? row.Action : "");
                        Cell(row.Hit.Job); Cell(row.Hit.Damage?.ToString("N0") ?? "—"); Cell(row.Hit.Statuses);
                    }
                }
                clipper.End(); clipper.Destroy();
                if (follow && selected == plugin.Current) ImGui.SetScrollY(ImGui.GetScrollMaxY());
            }
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ImGui.TextUnformatted($"履歴 ({plugin.History.Items.Count}/100)");
        if (ImGui.BeginChild("history", new Vector2(0, 115), true))
        {
            foreach (var e in plugin.History.Items)
            {
                if (ImGui.Selectable(Csv.FileName(e) + "###" + e.Id, selected?.Id == e.Id)) plugin.Selected = e;
            }
        }
        ImGui.EndChild();
        ImGui.BeginDisabled(plugin.Selected?.End == null);
        if (ImGui.Button("結果をCSVで出力", new Vector2(210, 32))) plugin.Export();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("出力先設定")) ImGui.OpenPopup("出力先設定");
        if (ImGui.BeginPopup("出力先設定"))
        {
            ImGui.SetNextItemWidth(550);
            ImGui.InputText("フォルダ", ref folder, 1024);
            if (ImGui.Button("保存")) { plugin.SaveFolder(folder); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("標準ダウンロード")) folder = DownloadsFolder.Get();
            ImGui.EndPopup();
        }
        ImGui.TextWrapped(plugin.Message);
        if (!string.IsNullOrEmpty(plugin.Error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .45f, .35f, 1));
            ImGui.TextWrapped(plugin.Error);
            ImGui.PopStyleColor();
        }
    }
    private static void Cell(string text)
    {
        ImGui.TableNextColumn(); ImGui.TextUnformatted(text);
        if (text.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
}
