using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace AutoMakeTimeline;

internal sealed class DevLibraLink(IDalamudPluginInterface pi)
{
    private readonly ICallGateSubscriber<int> version = pi.GetIpcSubscriber<int>("devLibra.BarrierHP.Version");
    private readonly ICallGateSubscriber<uint, string> sample = pi.GetIpcSubscriber<uint, string>("devLibra.BarrierHP.SnapshotV1");
    public bool Available
    {
        get { try { return version.HasFunction && sample.HasFunction && version.InvokeFunc() == 1; } catch { return false; } }
    }
    public string State => Available ? "devLibra連携：接続済み" : "devLibra連携：未接続（連携対応版の起動が必要）";
    public BarrierReading Read(uint entity, uint? maxHp, bool isReplay)
    {
        if (!Available) return new(null, "devLibra連携対応版が未起動です");
        try { return BarrierWire.Read(sample.InvokeFunc(entity), entity, maxHp, Environment.TickCount64, isReplay); }
        catch { return new(null, "devLibraのバリア情報を取得できません"); }
    }
}
