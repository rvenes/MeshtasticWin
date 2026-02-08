using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshtasticWin.Services;

public sealed record TraceRouteContextMatch(uint TargetNodeNum);

public static class TraceRouteContext
{
    private static readonly object _gate = new();
    private static readonly List<PendingTraceRoute> _pending = new();
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(60);

    public static void RegisterActiveTraceRoute(uint targetNodeNum)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            CleanupLocked(now);
            _pending.Add(new PendingTraceRoute(targetNodeNum, now));
        }
    }

    public static bool TryMatchActiveTraceRoute(uint fromNodeNum, out TraceRouteContextMatch match)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            CleanupLocked(now);
            for (var i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].TargetNodeNum != fromNodeNum)
                    continue;

                var entry = _pending[i];
                _pending.RemoveAt(i);
                match = new TraceRouteContextMatch(entry.TargetNodeNum);
                return true;
            }
        }

        match = default!;
        return false;
    }

    public static bool TryMarkNoResponse(uint targetNodeNum)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            CleanupLocked(now);
            var entry = _pending.FirstOrDefault(item => item.TargetNodeNum == targetNodeNum);
            if (entry is null || entry.NoResponseLogged)
                return false;

            entry.NoResponseLogged = true;
            return true;
        }
    }

    private static void CleanupLocked(DateTime now)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (now - _pending[i].TimestampUtc > PendingWindow)
                _pending.RemoveAt(i);
        }
    }

    private sealed class PendingTraceRoute
    {
        public PendingTraceRoute(uint targetNodeNum, DateTime timestampUtc)
        {
            TargetNodeNum = targetNodeNum;
            TimestampUtc = timestampUtc;
        }

        public uint TargetNodeNum { get; }
        public DateTime TimestampUtc { get; }
        public bool NoResponseLogged { get; set; }
    }
}
