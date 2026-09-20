namespace AutoMakeTimeline;

public static class TimelineOrder
{
    public static List<TimelineRow> Sort(IEnumerable<TimelineRow> rows) => rows.OrderBy(r => r.No)
        .ThenByDescending(r => r.Hit.Damage).ToList(); // LINQ keeps equal-damage targets stable.
}

public static class DefenseRules
{
    public static bool IsBarrier(StatusInfo s) => s.Barrier || DefenseStatusIds.Barriers.Contains(s.Id);
    public static bool IsDefensive(StatusInfo s) => s.Defensive || IsBarrier(s) || DefenseStatusIds.Defensive.Contains(s.Id);
    public static bool AltersDamage(StatusInfo s) => s.AltersDamage || DefenseStatusIds.DamageModifiers.Contains(s.Id);
    public static StatusInfo Describe(uint id, string name, uint icon, byte category, bool company, string description) =>
        new(id, name, icon, category, company,
            DefenseStatusIds.Defensive.Contains(id), DefenseStatusIds.Barriers.Contains(id),
            DefenseStatusIds.DamageModifiers.Contains(id) || description.Contains("ダメージ") &&
            (description.Contains("軽減") || description.Contains("低下") || description.Contains("増加") || description.Contains("上昇") || description.Contains("無効")));
    public static string StatusLabel(StatusInfo s, Hit? hit = null)
    {
        return s.Name;
    }
    public static string EnemyText(TimelineRow r, bool debuff) => r.EnemyEffects == null ? "取得不可" :
        string.Join(",", r.EnemyEffects.Where(EnemyStatusRules.Include).Where(s => debuff ? s.Category == 2 : s.Category != 2).Select(s => StatusLabel(s)));
}

public sealed record DamageEstimate(long? Value, string Reason)
{
    public string Display => Value is { } v ? $"{v:N0}" : "算出不可";
    public static long Calculate(long damage, decimal percent, long barrier)
    {
        if (damage < 0 || percent < 0 || barrier < 0) throw new ArgumentOutOfRangeException(nameof(damage));
        return checked((long)decimal.Ceiling(damage * (1m + percent / 100m) + barrier));
    }
    public static DamageEstimate For(TimelineRow row)
    {
        if (row.Hit.Damage is not { } damage) return new(null, "ダメージなし");
        var mitigation = MitigationSummary.For(row);
        if (mitigation.Percent is not { } percent || !mitigation.Complete)
            return new(null, "軽減率に未取得・未設定のバフがあります。" + mitigation.Reason);
        if (row.Hit.BarrierTotal is not { } barrier)
            return new(null, "devLibraのバリア合計が未取得です。" + row.Hit.BarrierSource);
        return new(Calculate(damage, percent, barrier), $"指定式：{damage:N0} × (1 + {percent:0.##} / 100) + {barrier:N0}。小数切り上げ");
    }
}

public sealed record HpResult(uint Sequence, uint Target, uint Hp, byte? Shield, long Tick);
public static class HpPackets
{
    public static List<HpResult> Read(ReadOnlySpan<byte> packet, bool basic, long tick)
    {
        var result = new List<HpResult>();
        if (packet.Length < 4) return result;
        int count = packet[0], stride = basic ? 0x10 : 0x58;
        if (count > (basic ? 64 : 16) || packet.Length < 4 + count * stride) return result;
        for (var i = 0; i < count; i++)
        {
            var entry = packet.Slice(4 + i * stride, stride);
            result.Add(new(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), basic ? null : entry[20], tick));
        }
        return result;
    }
}
public static class HpCorrelation
{
    public static TimelineRow Apply(TimelineRow row, HpResult result, long frequency) =>
        row.Origin == "ActionEffect" && row.Sequence != 0 && row.Sequence == result.Sequence &&
        row.Hit.TargetId == result.Target && row.Hit.CaptureTick > 0 && result.Tick >= row.Hit.CaptureTick &&
        result.Tick - row.Hit.CaptureTick <= frequency * 15 ?
            row with { Hit = row.Hit with { RemainingHp = result.Hp, ShieldAfter = result.Shield } } : row;
}
