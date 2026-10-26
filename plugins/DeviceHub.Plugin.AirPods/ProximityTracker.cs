namespace DeviceHub.Plugin.AirPods;

/// <summary>
/// 把左右耳各自发出的广播合成一个状态（思路同 AirPodsDesktop 的 StateManager）。
/// 两只耳机都会广播，但同一时刻往往只有一只在发，而且各自只确定知道“本耳”的情况；
/// 所以每侧保留最近一条，逐项取“有值且最新”的那一侧；某侧 10 秒没更新就丢掉，全部 30 秒没更新视为离开范围。
/// </summary>
public sealed class ProximityTracker
{
    private static readonly TimeSpan SideStale = TimeSpan.FromSeconds(10), Lost = TimeSpan.FromSeconds(30);
    private (ProximityStatus S, DateTimeOffset T)? _left, _right;

    /// <summary>收到一条广播。电量与上一条同侧广播相差超过 10% 时视为别人的耳机，丢弃。</summary>
    public bool Add(ProximityStatus s, DateTimeOffset now)
    {
        ref var slot = ref s.FromLeft ? ref _left : ref _right;
        if (slot is { } last && now - last.T < SideStale && (Jump(last.S.Left, s.Left) || Jump(last.S.Right, s.Right) || Jump(last.S.Case, s.Case)))
            return false;
        slot = (s, now);
        return true;
    }

    /// <summary>当前合成结果；没有任何新鲜广播时为 null。</summary>
    public ProximityStatus? Current(DateTimeOffset now)
    {
        if (_left is { } l && now - l.T > SideStale) _left = null;
        if (_right is { } r && now - r.T > SideStale) _right = null;
        var sides = new[] { _left, _right }.Where(x => x is not null).Select(x => x!.Value).OrderByDescending(x => x.T).ToList();
        if (sides.Count == 0) return null;

        ProximityStatus Pick(Func<ProximityStatus, bool> has) => sides.FirstOrDefault(x => has(x.S)).S ?? sides[0].S;
        var left = Pick(x => x.Left is not null);
        var right = Pick(x => x.Right is not null);
        var box = Pick(x => x.Case is not null);
        var newest = sides[0].S;
        return newest with
        {
            ModelId = sides.Select(x => x.S.ModelId).FirstOrDefault(id => id != 0),
            Left = left.Left, LeftCharging = left.LeftCharging, LeftInEar = left.LeftInEar,
            Right = right.Right, RightCharging = right.RightCharging, RightInEar = right.RightInEar,
            Case = box.Case, CaseCharging = box.CaseCharging,
        };
    }

    public DateTimeOffset? LastSeen => new[] { _left?.T, _right?.T }.Max();

    public bool IsLost(DateTimeOffset now) => LastSeen is not { } t || now - t > Lost;

    public void Reset() { _left = null; _right = null; }

    private static bool Jump(int? a, int? b) => a is { } x && b is { } y && Math.Abs(x - y) > 10;
}
