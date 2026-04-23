using System;
using Dalamud.Configuration;

namespace XivTreasureParty;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string Nickname { get; set; } = string.Empty;

    public string? LastPartyCode { get; set; }

    public string? LastRefreshToken { get; set; }

    public string? LastIdToken { get; set; }

    public string? LastLocalId { get; set; }

    public DateTime? LastTokenRefreshedAtUtc { get; set; }

    public bool AutoRejoinOnStart { get; set; } = true;

    /// <summary>自動偵測玩家解碼藏寶圖時預選下方新增欄位（不會直接推送，需手動按加入清單）。</summary>
    public bool AutoCaptureOnDecode { get; set; } = false;

    /// <summary>讀取/自動偵測到新藏寶圖時，順便在遊戲內開啟地圖並打旗標。</summary>
    public bool AutoOpenMapOnCapture { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
