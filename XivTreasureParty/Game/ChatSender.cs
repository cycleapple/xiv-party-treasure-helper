using System;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace XivTreasureParty.Game;

/// <summary>
/// 送出聊天訊息到遊戲（送伺服器，不是本機 echo）。
/// 改用 FFXIVClientStructs 提供的 <c>UIModule::ProcessChatBoxEntry</c> wrapper，
/// 不再自己 signature scan —— 版本更新時不用修 sig。
/// 作法參考 ECommons/Automation/Chat.cs (NightmareXIV)。
/// </summary>
public sealed unsafe class ChatSender
{
    public void SendMessage(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        SendBytes(bytes);
    }

    public void SendSeString(SeString seString)
    {
        if (seString == null || seString.Payloads.Count == 0) return;
        SendBytes(seString.Encode());
    }

    private void SendBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length >= 500)
        {
            Plugin.Log.Warning($"[ChatSender] 訊息長度不合法: {bytes.Length}");
            return;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            Plugin.Log.Warning("[ChatSender] UIModule == null");
            return;
        }

        Utf8String* mes = null;
        try
        {
            mes = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(mes);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ChatSender] ProcessChatBoxEntry 呼叫失敗");
        }
        finally
        {
            if (mes != null) mes->Dtor(true);
        }
    }
}
