using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.Text.SeStringHandling;

namespace XivTreasureParty.Game;

/// <summary>
/// 地圖連結的「auto-translate」形式 payload。
/// 送進 ProcessChatBoxEntry 不會被剝掉（遊戲把 AutoTranslateText chunk 當成玩家本來就能輸入的 token
/// ，如 &lt;flag&gt;），所以聊天出去之後會顯示為可點擊的地圖連結。
///
/// Port 自 DailyRoutines AutoConvertMapLink 的 PreMapLinkPayload
/// (原作者: KirisameVanilla / Asvel)。
///
/// Chunk 格式（SeString 規格）:
///   [0x02]              START
///   [0x2E]              SeStringChunkType.AutoTranslateKey
///   [chunkLen]          後續 bytes 長度 (不含 START/type/len 本身)
///   [0xC9] [0x04]       Completion sheet 的 auto-translate flag 固定 key
///   [territory MakeInt]
///   [map       MakeInt]
///   [rawX      MakeInt]
///   [rawY      MakeInt]
///   [rawZ      MakeInt]  固定 -30000
///   [0x01]              chunk 內部終止
///   [0x03]              END
/// </summary>
public sealed class PreMapLinkPayload : Payload
{
    private const byte START_BYTE = 0x02;
    private const byte END_BYTE = 0x03;
    private const byte CHUNK_TYPE_AUTO_TRANSLATE = 0x2E;
    private const int RAW_Z = -30000;

    private readonly uint _zoneId;
    private readonly uint _mapId;
    private readonly int _rawX;
    private readonly int _rawY;

    public PreMapLinkPayload(uint zoneId, uint mapId, int rawX, int rawY)
    {
        _zoneId = zoneId;
        _mapId = mapId;
        _rawX = rawX;
        _rawY = rawY;
    }

    public override PayloadType Type => PayloadType.AutoTranslateText;

    protected override byte[] EncodeImpl()
    {
        var territoryBytes = MakeInteger(_zoneId);
        var mapBytes = MakeInteger(_mapId);
        var xBytes = MakeInteger(unchecked((uint)_rawX));
        var yBytes = MakeInteger(unchecked((uint)_rawY));
        var zBytes = MakeInteger(unchecked((uint)RAW_Z));

        // chunk body: [0xC9][0x04][territory][map][x][y][z][0x01]
        var chunkLen = 4 + territoryBytes.Length + mapBytes.Length
                       + xBytes.Length + yBytes.Length + zBytes.Length;

        var bytes = new List<byte>(8 + chunkLen)
        {
            START_BYTE,
            CHUNK_TYPE_AUTO_TRANSLATE,
            (byte)chunkLen,
            0xC9,
            0x04
        };
        bytes.AddRange(territoryBytes);
        bytes.AddRange(mapBytes);
        bytes.AddRange(xBytes);
        bytes.AddRange(yBytes);
        bytes.AddRange(zBytes);
        bytes.Add(0x01);
        bytes.Add(END_BYTE);
        return bytes.ToArray();
    }

    protected override void DecodeImpl(BinaryReader reader, long endOfStream)
        => throw new NotImplementedException();

    /// <summary>
    /// 把顯示座標 (如 22.1) 算回遊戲內部的 raw world position。
    /// 公式與 DailyRoutines AutoConvertMapLink 一致。
    /// 加入小量隨機偏移以模擬玩家手貼座標的自然誤差（對 AutoConvertMapLink 攔截相容）。
    /// </summary>
    public static int GenerateRawPosition(float visibleCoordinate, short offset, ushort sizeFactor)
    {
        visibleCoordinate += (float)Random.Shared.NextDouble() * 0.07f;
        var scale = sizeFactor / 100.0f;
        var scaledPos = ((visibleCoordinate - 1.0f) * scale / 41.0f * 2048.0f - 1024.0f) / scale;
        return (int)Math.Ceiling(scaledPos - offset) * 1000;
    }
}
