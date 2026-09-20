using System.Text.Json;

namespace AutoMakeTimeline;

public sealed record BarrierReading(long? Amount, string Source);
public sealed record BarrierSample(int Version, uint EntityId, int CurrentHp, int MaxHp, int ShieldPercent,
    int BarrierHp, string CalculationSource, long SampledAtMilliseconds, bool IsReplay = false);
public static class BarrierWire
{
    public static BarrierReading Read(string json, uint entityId, uint? maxHp, long now, bool isReplay = false)
    {
        try
        {
            var s = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BarrierSample>(json);
            if (s == null) return new(null, "devLibraに対象の最新データがありません（パーティリストを確認してください）");
            if (s.Version != 1 || s.IsReplay != isReplay || s.EntityId != entityId || s.EntityId is 0 or 0xE0000000 || s.BarrierHp < 0 ||
                s.MaxHp <= 0 || maxHp.HasValue && s.MaxHp != maxHp || now - s.SampledAtMilliseconds is < 0 or > 500)
                return new(null, "devLibraの対象・更新時刻・最大HPが一致しません");
            return new(s.BarrierHp, "devLibra / " + s.CalculationSource);
        }
        catch (JsonException) { return new(null, "devLibraからのデータ形式が不正です"); }
    }
}

public static class EnemyStatusRules
{
    public static bool Include(StatusInfo s) => s.Id != 2586 && s.Name != "デスデザイン" &&
        !string.Equals(s.Name, "Death's Design", StringComparison.OrdinalIgnoreCase) && !DotStatusIds.Values.Contains(s.Id);
    public static List<StatusInfo>? Display(IEnumerable<StatusInfo>? effects) => effects?.Where(Include).ToList();
}

public static class TimelineColumns
{
    public static readonly string[] Names = ["No.", "時間", "エネミー", "攻撃名称", "ジョブ", "ダメージ", "想定ダメージ",
        "残りHP", "バフ（軽減・バリア）", "デバフ", "敵バフ", "敵デバフ", "バリア合計", "軽減%"];
    public static readonly float[] Widths = [38, 58, 160, 200, 55, 100, 115, 100, 240, 230, 230, 230, 125, 95];
    public static void SetVisible(HashSet<int> hidden, int column, bool visible)
    {
        if (column < 0 || column >= Names.Length) return;
        if (visible) hidden.Remove(column); else hidden.Add(column);
    }
}

public static class AttackKind
{
    public static bool Physical(byte type) => type is 1 or 2 or 3 or 4 or 7;
    public static bool Magical(byte type) => type == 5;
    public static string Name(byte type) => Physical(type) ? "物理" : Magical(type) ? "魔法" : "種別不明";
}

public static class AdditiveRates
{
    public static decimal? Percent(StatusInfo status, byte type)
    {
        if (status.ReductionPhysical is { } physical && status.ReductionMagical is { } magical)
        {
            if (physical == magical) return physical;
            return AttackKind.Physical(type) ? physical : AttackKind.Magical(type) ? magical : null;
        }
        return DefenseRules.IsBarrier(status) ? 0 : null;
    }
}

public sealed record MitigationSummary(decimal? Percent, string Reason, bool Complete = true)
{
    public string Display => Percent is { } p ? $"{p:0.##}%" + (Complete ? "" : "＋未設定") : "取得不可";
    public static MitigationSummary For(TimelineRow row)
    {
        if (row.Hit.Effects == null) return new(null, "バフ情報が未取得", false);
        decimal total = 0;
        var details = new List<string>();
        var complete = true;
        // Exactly the statuses visible in the party buff column. Enemy effects and debuffs
        // intentionally have no influence on this user-defined sum.
        foreach (var status in row.Hit.Effects.Where(s => s.Category == 1 && DefenseRules.IsDefensive(s)))
        {
            var rate = AdditiveRates.Percent(status, row.Hit.DamageType);
            if (rate.HasValue)
            {
                total += rate.Value;
                details.Add($"{status.Name}: {rate:0.##}%" + (DefenseRules.IsBarrier(status) ? "（バリア合計へ計上）" : "") + " [" + status.ReductionSource + "]");
            }
            else { complete = false; details.Add($"{status.Name}: 説明未取得／付与元・攻撃種別不明"); }
        }
        return new(total, "バフ列の軽減率を単純加算（100%を超えても上限処理なし）。" +
            (details.Count == 0 ? "対象バフなし" : string.Join(" / ", details)), complete);
    }
}
