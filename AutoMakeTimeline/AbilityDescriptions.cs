using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoMakeTimeline;

public sealed record AbilityText(uint Id, string Name, string Description);
public sealed record DescriptionRate(decimal Physical, decimal Magical, string Evidence);

public static class ReductionDescription
{
    public static string Clean(string text) => text.Normalize(NormalizationForm.FormKC).Replace("**", "").Replace("\r", "");
    public static string BaseName(string name) => Clean(name).Replace(":効果", "").Replace("[被]", "");
    public static DescriptionRate? Parse(string text)
    {
        var physical = new HashSet<decimal>();
        var magical = new HashSet<decimal>();
        var evidence = new List<string>();
        foreach (var sentence in Regex.Split(Clean(text), "[。\n]"))
        {
            if (!sentence.Contains("軽減")) continue;
            var matches = Regex.Matches(sentence, @"(?<kind>被物理ダメージと被?魔法ダメージ|被魔法ダメージと被?物理ダメージ|被物理ダメージ|被魔法ダメージ|被ダメージ|受ける物理ダメージ|受ける魔法ダメージ|受けるダメージ)を\s*(?<n>\d+(?:\.\d+)?)\s*%");
            foreach (Match m in matches)
            {
                // Reject a percentage followed by a different effect such as damage increase.
                var suffix = sentence[(m.Index + m.Length)..];
                if (!suffix.Contains("軽減") || Regex.IsMatch(suffix.Split('、')[0], "上昇|増加|回復")) continue;
                var n = decimal.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                if (n is < 0 or > 100) continue;
                var kind = m.Groups["kind"].Value;
                if (!kind.Contains("魔法") || kind.Contains("物理")) physical.Add(n);
                if (!kind.Contains("物理") || kind.Contains("魔法")) magical.Add(n);
                evidence.Add(m.Value + "軽減");
            }
        }
        if (physical.Count > 1 || magical.Count > 1 || physical.Count + magical.Count == 0) return null;
        return new(physical.SingleOrDefault(), magical.SingleOrDefault(), string.Join(" / ", evidence.Distinct()));
    }
    public static DescriptionRate? ForStatus(AbilityText action, string statusName)
    {
        var description = Clean(action.Description);
        var status = BaseName(statusName);
        var marker = Clean(statusName) + "効果:";
        var index = description.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) { marker = status + "効果:"; index = description.IndexOf(marker, StringComparison.Ordinal); }
        if (index >= 0)
        {
            var section = description[(index + marker.Length)..];
            var end = Regex.Match(section, @"(?:追加効果|[^。\n:]{1,45}効果):");
            if (end.Success) section = section[..end.Index];
            return Parse(section);
        }
        if (BaseName(action.Name) != status) return null;
        // Do not accidentally add a secondary granted buff to its parent ability.
        var additional = description.IndexOf("追加効果:", StringComparison.Ordinal);
        if (additional >= 0) description = description[..additional];
        return Parse(description);
    }
}

public sealed class AbilityRateCatalog
{
    private readonly Dictionary<uint, AbilityText> actions;
    private readonly Dictionary<uint, string> statusNames;
    private readonly Dictionary<string, List<AbilityText>> byEffect = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, StatusInfo> defaults = [];
    // Identity mappings only. No reduction percentage is hard-coded.
    private static readonly Dictionary<uint, uint> Canonical = new()
    {
        [71] = 10, [1191] = 7531, [1978] = 7531, [299] = 188, [2638] = 188,
        [1839] = 16160, [1894] = 16471, [1951] = 16889, [1873] = 16536,
        [2674] = 25746, [2678] = 25751, [2679] = 25751, [2683] = 25758,
        [2684] = 25758, [1174] = 7382, [317] = 16538,
    };
    public AbilityRateCatalog(IEnumerable<AbilityText> source, IDictionary<uint, string> names)
    {
        actions = source.ToDictionary(a => a.Id); statusNames = new(names);
        foreach (var action in actions.Values)
        {
            Add(ReductionDescription.BaseName(action.Name), action);
            foreach (Match match in Regex.Matches(ReductionDescription.Clean(action.Description), @"(?:^|[。\n])\s*(?<name>[^:\n。]{1,45})効果:"))
                Add(ReductionDescription.BaseName(match.Groups["name"].Value), action);
        }
    }
    private void Add(string name, AbilityText action)
    {
        if (!byEffect.TryGetValue(name, out var list)) byEffect[name] = list = [];
        if (!list.Any(a => a.Id == action.Id)) list.Add(action);
    }

    public StatusInfo Resolve(StatusInfo status, uint observedAction = 0)
    {
        if (observedAction == 0 && defaults.TryGetValue(status.Id, out var cached)) return CopyRate(status, cached);
        var name = statusNames.GetValueOrDefault(status.Id, status.Name);
        var candidates = new List<(AbilityText Action, DescriptionRate Rate)>();
        if (observedAction != 0 && actions.TryGetValue(observedAction, out var observed))
        {
            if (ReductionDescription.ForStatus(observed, name) is { } rate) candidates.Add((observed, rate));
            else return status with { ReductionPhysical = null, ReductionMagical = null,
                ReductionActionId = observedAction, ReductionSource = $"{observed.Name}: 対応する軽減文を抽出できません" };
        }
        else if (Canonical.TryGetValue(status.Id, out var id) && actions.TryGetValue(id, out var canonical))
        {
            if (ReductionDescription.ForStatus(canonical, name) is { } rate) candidates.Add((canonical, rate));
        }
        else
        {
            foreach (var action in byEffect.GetValueOrDefault(ReductionDescription.BaseName(name)) ?? [])
                if (ReductionDescription.ForStatus(action, name) is { } rate) candidates.Add((action, rate));
        }
        var distinct = candidates.Select(c => (c.Rate.Physical, c.Rate.Magical)).Distinct().ToList();
        var result = status with { ReductionPhysical = null, ReductionMagical = null, ReductionActionId = 0,
            ReductionSource = distinct.Count > 1 ? "付与元により説明の率が異なります（付与元未確認）" : "対応するアビリティの軽減説明が未取得" };
        if (distinct.Count == 1)
        {
            var choice = candidates[0];
            result = status with { ReductionPhysical = choice.Rate.Physical, ReductionMagical = choice.Rate.Magical,
                ReductionActionId = choice.Action.Id, ReductionSource = $"{choice.Action.Name} (Action#{choice.Action.Id}): {choice.Rate.Evidence}" };
        }
        if (observedAction == 0) defaults[status.Id] = result;
        return result;
    }
    private static StatusInfo CopyRate(StatusInfo status, StatusInfo source) => status with
    { ReductionPhysical = source.ReductionPhysical, ReductionMagical = source.ReductionMagical,
        ReductionSource = source.ReductionSource, ReductionActionId = source.ReductionActionId };
}

public sealed class BuffApplicationTracker
{
    private readonly Dictionary<(uint Target, uint Status, uint Source), (uint Action, long At)> entries = [];
    public void Observe(uint target, uint status, uint source, uint action, long now)
    {
        if (entries.Count > 4096) entries.Clear();
        entries[(target, status, source)] = (action, now);
    }
    public uint Find(uint target, uint status, uint source, long now) => entries.TryGetValue((target, status, source), out var entry) &&
        now - entry.At is >= 0 and < 120_000 ? entry.Action : 0;
    public void Clear() => entries.Clear();
}
