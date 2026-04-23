using System;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Game;

/// <summary>
/// 藏寶點在遊戲內的定位：建立 MapLinkPayload、在遊戲中開啟地圖並打旗標。
/// 參考 Globetrotter/TreasureMaps.cs OpenMapLocation 的作法，但我們不 hook 遊戲，
/// 只利用 Firebase 已同步到的座標 + Lumina Map sheet 查到 TerritoryType。
/// </summary>
public static class TreasureLocator
{
    /// <summary>
    /// 根據藏寶圖的 mapId + 座標建立 MapLinkPayload。
    /// MapLinkPayload 內部會把可見座標 (如 22.1) 轉回 raw world position。
    /// </summary>
    public static MapLinkPayload? TryBuildMapLink(Treasure t)
    {
        var territoryId = LookupTerritoryId(t.MapId);
        if (territoryId == 0) return null;
        return new MapLinkPayload((uint)territoryId, (uint)t.MapId, t.Coords.X, t.Coords.Y);
    }

    /// <summary>
    /// 在遊戲內打開地圖並將旗標設在藏寶點（同 &lt;flag&gt; 行為）。
    /// 底層呼叫 IGameGui.OpenMapWithMapLink，這也是 Globetrotter 的核心呼叫。
    /// </summary>
    public static bool OpenInGame(Treasure t)
    {
        var payload = TryBuildMapLink(t);
        if (payload == null)
        {
            Plugin.Log.Warning($"[TreasureLocator] Map {t.MapId} 缺 TerritoryType，無法開地圖");
            return false;
        }
        try
        {
            Plugin.GameGui.OpenMapWithMapLink(payload);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[TreasureLocator] OpenMapWithMapLink 失敗");
            return false;
        }
    }

    public static uint LookupTerritoryId(int mapId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            if (sheet == null) return 0;
            var row = sheet.GetRow((uint)mapId);
            return row.TerritoryType.RowId;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TreasureLocator] 查 Map/TerritoryType 失敗 mapId={mapId}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 建立聊天可送出的 PreMapLinkPayload（AutoTranslateText 類型）。
    /// 失敗 (查不到 Map row / territory) 回 null。
    /// </summary>
    public static PreMapLinkPayload? TryBuildAutoTranslateMapLink(Treasure t)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            if (sheet == null) return null;
            var map = sheet.GetRow((uint)t.MapId);
            var territoryId = map.TerritoryType.RowId;
            if (territoryId == 0) return null;

            var rawX = PreMapLinkPayload.GenerateRawPosition(t.Coords.X, map.OffsetX, map.SizeFactor);
            var rawY = PreMapLinkPayload.GenerateRawPosition(t.Coords.Y, map.OffsetY, map.SizeFactor);
            return new PreMapLinkPayload(territoryId, (uint)t.MapId, rawX, rawY);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TreasureLocator] 建立 PreMapLinkPayload 失敗 mapId={t.MapId}: {ex.Message}");
            return null;
        }
    }
}
