using System;
using System.Collections.Generic;
using System.Linq;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Party;

/// <summary>
/// 路線優化：地圖分組 + 最近鄰居法。Port 自網頁版 js/route-optimizer.js。
///   1. 把藏寶點按 mapId 分群
///   2. 從點數最多的地圖開始，用中心點距離找下一個地圖
///   3. 同一張地圖內用最近鄰居法 (考慮上一張地圖最後一點) 排序
/// 注意：這是 heuristic，不保證全域最優，但和網頁版一致才能讓兩端優化結果可預期。
/// </summary>
public static class RouteOptimizer
{
    public static List<Treasure> Optimize(IEnumerable<Treasure> treasures, bool useMapGrouping = true)
    {
        var list = treasures.ToList();
        if (list.Count <= 1) return list;

        if (!useMapGrouping)
            return SortWithinMap(list, null);

        var groups = list.GroupBy(t => t.MapId).ToDictionary(g => g.Key, g => g.ToList());
        var sortedMapIds = SortMaps(groups);

        var result = new List<Treasure>(list.Count);
        TreasureCoords? lastCoords = null;
        foreach (var mapId in sortedMapIds)
        {
            var sorted = SortWithinMap(groups[mapId], lastCoords);
            result.AddRange(sorted);
            if (sorted.Count > 0)
                lastCoords = sorted[^1].Coords;
        }
        return result;
    }

    private static List<Treasure> SortWithinMap(List<Treasure> treasures, TreasureCoords? startCoords)
    {
        if (treasures.Count <= 1) return new List<Treasure>(treasures);

        var remaining = new List<Treasure>(treasures);
        var result = new List<Treasure>(treasures.Count);

        var currentIdx = 0;
        if (startCoords != null)
        {
            var minDist = double.MaxValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var d = Distance(startCoords, remaining[i].Coords);
                if (d < minDist) { minDist = d; currentIdx = i; }
            }
        }

        while (remaining.Count > 0)
        {
            var current = remaining[currentIdx];
            remaining.RemoveAt(currentIdx);
            result.Add(current);
            if (remaining.Count == 0) break;

            var minDist = double.MaxValue;
            var nearestIdx = 0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var d = Distance(current.Coords, remaining[i].Coords);
                if (d < minDist) { minDist = d; nearestIdx = i; }
            }
            currentIdx = nearestIdx;
        }
        return result;
    }

    private static List<int> SortMaps(Dictionary<int, List<Treasure>> mapGroups)
    {
        var mapIds = mapGroups.Keys.ToList();
        if (mapIds.Count <= 1) return mapIds;

        // 起點：點數最多的地圖
        var current = mapIds[0];
        foreach (var id in mapIds)
            if (mapGroups[id].Count > mapGroups[current].Count) current = id;

        var remaining = new HashSet<int>(mapIds);
        var result = new List<int>(mapIds.Count);
        while (remaining.Count > 0)
        {
            result.Add(current);
            remaining.Remove(current);
            if (remaining.Count == 0) break;

            var currentCenter = Center(mapGroups[current]);
            var minDist = double.MaxValue;
            var next = current;
            foreach (var id in remaining)
            {
                var d = Distance(currentCenter, Center(mapGroups[id]));
                if (d < minDist) { minDist = d; next = id; }
            }
            current = next;
        }
        return result;
    }

    private static TreasureCoords Center(List<Treasure> treasures)
    {
        if (treasures.Count == 0) return new TreasureCoords { X = 20, Y = 20 };
        float sx = 0, sy = 0;
        foreach (var t in treasures) { sx += t.Coords.X; sy += t.Coords.Y; }
        return new TreasureCoords { X = sx / treasures.Count, Y = sy / treasures.Count };
    }

    private static double Distance(TreasureCoords a, TreasureCoords b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static (double TotalDistance, int MapCount, int MapJumps) Analyze(List<Treasure> route)
    {
        if (route.Count == 0) return (0, 0, 0);
        var maps = new HashSet<int>();
        var jumps = 0;
        double total = 0;
        for (var i = 0; i < route.Count; i++)
        {
            maps.Add(route[i].MapId);
            if (i > 0)
            {
                total += Distance(route[i - 1].Coords, route[i].Coords);
                if (route[i].MapId != route[i - 1].MapId) jumps++;
            }
        }
        return (total, maps.Count, jumps);
    }
}
