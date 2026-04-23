using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Data;

public sealed class TreasureSpot
{
    public string Id { get; init; } = "";
    public float X { get; init; }
    public float Y { get; init; }
    public int MapId { get; init; }
    public int PartySize { get; init; }
    public int ItemId { get; init; }

    public string Label { get; init; } = "";

    public Treasure ToTreasure(string? nickname) => new()
    {
        Id = Id,
        Coords = new TreasureCoords { X = X, Y = Y },
        MapId = MapId,
        GradeItemId = ItemId,
        PartySize = PartySize,
        Player = nickname
    };
}

public static class TreasureData
{
    private static readonly IReadOnlyList<TreasureSpot> _all = Parse(TreasuresRaw.Data);

    public static IReadOnlyList<TreasureSpot> All => _all;

    public static IEnumerable<TreasureSpot> ByItemId(int itemId)
        => _all.Where(t => t.ItemId == itemId);

    public static IEnumerable<int> MapsForItem(int itemId)
        => _all.Where(t => t.ItemId == itemId).Select(t => t.MapId).Distinct();

    public static IEnumerable<TreasureSpot> ByItemAndMap(int itemId, int mapId)
        => _all.Where(t => t.ItemId == itemId && t.MapId == mapId);

    private static IReadOnlyList<TreasureSpot> Parse(string raw)
    {
        // 先解析所有欄位
        var records = new List<(string Id, float X, float Y, int MapId, int PartySize, int ItemId)>();
        foreach (var row in raw.Split('|'))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            var parts = row.Split(',');
            if (parts.Length < 6) continue;
            records.Add((
                parts[0],
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture),
                int.Parse(parts[3], CultureInfo.InvariantCulture),
                int.Parse(parts[4], CultureInfo.InvariantCulture),
                int.Parse(parts[5], CultureInfo.InvariantCulture)
            ));
        }

        // 為每個 (ItemId, MapId) 分組，先排序再給 A/B/C... 字母編號
        var labelByOriginalId = new Dictionary<string, string>();
        foreach (var group in records.GroupBy(r => (r.ItemId, r.MapId)))
        {
            var ordered = group.OrderBy(r => ExtractIndex(r.Id)).ToList();
            for (var i = 0; i < ordered.Count; i++)
                labelByOriginalId[ordered[i].Id] = IndexToLetter(i);
        }

        var list = new List<TreasureSpot>(records.Count);
        foreach (var r in records)
        {
            list.Add(new TreasureSpot
            {
                Id = r.Id,
                X = r.X,
                Y = r.Y,
                MapId = r.MapId,
                PartySize = r.PartySize,
                ItemId = r.ItemId,
                Label = labelByOriginalId.TryGetValue(r.Id, out var lbl) ? lbl : ""
            });
        }

        return list;
    }

    private static int ExtractIndex(string id)
    {
        // "27.13" → 13
        var dot = id.IndexOf('.');
        if (dot < 0) return 0;
        return int.TryParse(id.AsSpan(dot + 1), out var n) ? n : 0;
    }

    private static string IndexToLetter(int idx)
    {
        if (idx < 26) return ((char)('A' + idx)).ToString();
        var first = (char)('A' + (idx / 26) - 1);
        var second = (char)('A' + (idx % 26));
        return $"{first}{second}";
    }
}
