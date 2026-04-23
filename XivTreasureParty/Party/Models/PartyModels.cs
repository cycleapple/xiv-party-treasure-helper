using System.Collections.Generic;
using System.Text.Json.Serialization;
using XivTreasureParty.Firebase;

namespace XivTreasureParty.Party.Models;

public class PartyMeta
{
    [JsonPropertyName("createdAt")] public object? CreatedAt { get; set; }
    [JsonPropertyName("createdBy")] public string? CreatedBy { get; set; }
    [JsonPropertyName("expiresAt")] public long? ExpiresAt { get; set; }
    [JsonPropertyName("orderLocked")] public bool? OrderLocked { get; set; }
}

public class PartyMember
{
    [JsonPropertyName("joinedAt")] public object? JoinedAt { get; set; }
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("isLeader")] public bool? IsLeader { get; set; }
    [JsonPropertyName("lastSeen")] public object? LastSeen { get; set; }
}

public class TreasureCoords
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
}

public class Treasure
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("coords")] public TreasureCoords Coords { get; set; } = new();
    [JsonPropertyName("mapId")] public int MapId { get; set; }
    [JsonPropertyName("gradeItemId")] public int GradeItemId { get; set; }
    [JsonPropertyName("partySize")] public int PartySize { get; set; }
    [JsonPropertyName("addedBy")] public string? AddedBy { get; set; }
    [JsonPropertyName("addedByNickname")] public string? AddedByNickname { get; set; }
    [JsonPropertyName("addedAt")] public object? AddedAt { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("player")] public string? Player { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }

    [JsonIgnore] public string FirebaseKey { get; set; } = "";
}

public class PartyRoot
{
    [JsonPropertyName("meta")] public PartyMeta? Meta { get; set; }
    [JsonPropertyName("members")] public Dictionary<string, PartyMember>? Members { get; set; }
    [JsonPropertyName("treasures")] public Dictionary<string, Treasure>? Treasures { get; set; }
}

public static class TreasureFactory
{
    public static Dictionary<string, object?> BuildNewTreasurePayload(
        Treasure treasure, int order, string userId, string nickname)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = treasure.Id,
            ["coords"] = new Dictionary<string, object?>
            {
                ["x"] = treasure.Coords.X,
                ["y"] = treasure.Coords.Y
            },
            ["mapId"] = treasure.MapId,
            ["gradeItemId"] = treasure.GradeItemId,
            ["partySize"] = treasure.PartySize,
            ["addedBy"] = userId,
            ["addedByNickname"] = nickname,
            ["addedAt"] = ServerTimestamp.Instance,
            ["order"] = order,
            ["completed"] = false,
            ["player"] = nickname
        };
    }
}
