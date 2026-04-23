using System;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;

namespace XivTreasureParty.Game;

/// <summary>
/// 透過遊戲的 ProcessChatBox 函數送出聊天訊息。
/// Signature 與 ChatPayload 結構為 goatcorp 社群眾多插件通用的慣例。
/// 可傳純文字或含 Payload 的 SeString (例如 MapLinkPayload)。
/// </summary>
public sealed unsafe class ChatSender
{
    private delegate void ProcessChatBoxDelegate(IntPtr uiModule, IntPtr message, IntPtr unused, byte a4);

    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9")]
    private readonly ProcessChatBoxDelegate? _processChatBox = null;

    public ChatSender(IGameInteropProvider interop)
    {
        interop.InitializeFromAttributes(this);
    }

    public void SendMessage(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendBytes(Encoding.UTF8.GetBytes(text));
    }

    public void SendSeString(SeString seString)
    {
        if (seString == null || seString.Payloads.Count == 0) return;
        SendBytes(seString.Encode());
    }

    private void SendBytes(byte[] bytes)
    {
        if (_processChatBox == null)
        {
            Plugin.Log.Warning("[ChatSender] 找不到 ProcessChatBox signature，無法送出聊天");
            return;
        }
        if (bytes.Length == 0 || bytes.Length >= 500)
        {
            Plugin.Log.Warning($"[ChatSender] 訊息長度不合法: {bytes.Length}");
            return;
        }

        var uiModule = (IntPtr)FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->GetUIModule();
        if (uiModule == IntPtr.Zero)
        {
            Plugin.Log.Warning("[ChatSender] UIModule == null");
            return;
        }

        var textPtr = Marshal.AllocHGlobal(bytes.Length + 30);
        Marshal.Copy(bytes, 0, textPtr, bytes.Length);
        Marshal.WriteByte(textPtr + bytes.Length, 0);

        var payloadPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ChatPayload>());
        var payload = new ChatPayload
        {
            TextPtr = textPtr,
            Unk1 = 64,
            TextLen = (ulong)(bytes.Length + 1),
            Unk2 = 0
        };
        Marshal.StructureToPtr(payload, payloadPtr, false);

        try
        {
            _processChatBox(uiModule, payloadPtr, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ChatSender] ProcessChatBox 呼叫失敗");
        }
        finally
        {
            Marshal.FreeHGlobal(payloadPtr);
            Marshal.FreeHGlobal(textPtr);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 400)]
    private struct ChatPayload
    {
        [FieldOffset(0)] public IntPtr TextPtr;
        [FieldOffset(8)] public ulong Unk1;
        [FieldOffset(16)] public ulong TextLen;
        [FieldOffset(24)] public ulong Unk2;
    }
}
