using System;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using ImGuiNET;
using XivTreasureParty.Data;
using XivTreasureParty.Game;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.UI;

public sealed class TreasureListPanel
{
    private string? _editingKey;
    private string _editBuf = "";
    private int _editMode; // 0: none, 1: note, 2: player
    private bool _editJustStarted;

    public void Draw()
    {
        ImGui.TextUnformatted("藏寶圖清單");
        ImGui.Separator();

        if (!Plugin.PartyService.IsInParty)
        {
            ImGui.TextDisabled("加入隊伍後會在此顯示同步的藏寶圖");
            return;
        }

        var treasures = Plugin.SyncService.Treasures.Values
            .OrderBy(t => t.Completed)
            .ThenBy(t => t.Order)
            .ToList();

        if (treasures.Count == 0)
        {
            ImGui.TextDisabled("清單為空，從左側新增藏寶圖");
            return;
        }

        if (ImGui.BeginTable("##treasures", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("順序", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("等級", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("地點 / 座標", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("負責玩家", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("備註", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 260f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < treasures.Count; i++)
                DrawRow(treasures, i);

            ImGui.EndTable();
        }
    }

    private void DrawRow(System.Collections.Generic.List<Treasure> list, int index)
    {
        var t = list[index];
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(t.Order.ToString());

        ImGui.TableNextColumn();
        var grade = GradeData.GetByItemId(t.GradeItemId);
        if (t.Completed)
            ImGui.TextDisabled(grade?.Grade ?? $"#{t.GradeItemId}");
        else
            ImGui.TextUnformatted(grade?.Grade ?? $"#{t.GradeItemId}");

        ImGui.TableNextColumn();
        var line1 = MapData.GetMapName(t.MapId);
        var line2 = $"X: {t.Coords.X:0.0}  Y: {t.Coords.Y:0.0}";
        if (t.Completed)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted(line1);
            ImGui.TextUnformatted(line2);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextUnformatted(line1);
            ImGui.TextUnformatted(line2);
        }

        ImGui.TableNextColumn();
        if (_editingKey == t.FirebaseKey && _editMode == 2)
        {
            if (_editJustStarted) { ImGui.SetKeyboardFocusHere(); _editJustStarted = false; }
            ImGui.SetNextItemWidth(-1);
            var committed = ImGui.InputText($"##player-{t.FirebaseKey}", ref _editBuf, 32, ImGuiInputTextFlags.EnterReturnsTrue);
            if (committed || ImGui.IsItemDeactivatedAfterEdit())
                CommitPlayer(t);
            else if (ImGui.IsItemDeactivated())
            {
                _editingKey = null; _editMode = 0; _editBuf = "";
            }
        }
        else
        {
            var label = string.IsNullOrEmpty(t.Player) ? "(未指定)" : t.Player!;
            if (ImGui.Selectable($"{label}##p-{t.FirebaseKey}"))
            {
                _editingKey = t.FirebaseKey;
                _editMode = 2;
                _editBuf = t.Player ?? "";
                _editJustStarted = true;
            }
        }

        ImGui.TableNextColumn();
        if (_editingKey == t.FirebaseKey && _editMode == 1)
        {
            if (_editJustStarted) { ImGui.SetKeyboardFocusHere(); _editJustStarted = false; }
            ImGui.SetNextItemWidth(-1);
            var committed = ImGui.InputText($"##note-{t.FirebaseKey}", ref _editBuf, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            if (committed || ImGui.IsItemDeactivatedAfterEdit())
                CommitNote(t);
            else if (ImGui.IsItemDeactivated())
            {
                _editingKey = null; _editMode = 0; _editBuf = "";
            }
        }
        else
        {
            var label = string.IsNullOrEmpty(t.Note) ? "(點擊新增)" : t.Note!;
            if (ImGui.Selectable($"{label}##n-{t.FirebaseKey}"))
            {
                _editingKey = t.FirebaseKey;
                _editMode = 1;
                _editBuf = t.Note ?? "";
                _editJustStarted = true;
            }
        }

        ImGui.TableNextColumn();
        var canModifyOrder = Plugin.PartyService.CanModifyOrder();

        if (ImGui.SmallButton((t.Completed ? "取消" : "完成") + $"##c-{t.FirebaseKey}"))
            _ = Plugin.PartyService.ToggleTreasureCompleteAsync(t.FirebaseKey);
        ImGui.SameLine();

        if (ImGui.SmallButton($"↑##u-{t.FirebaseKey}") && canModifyOrder && index > 0)
            _ = Plugin.PartyService.SwapTreasureOrderAsync(t.FirebaseKey, list[index - 1].FirebaseKey);
        ImGui.SameLine();
        if (ImGui.SmallButton($"↓##d-{t.FirebaseKey}") && canModifyOrder && index < list.Count - 1)
            _ = Plugin.PartyService.SwapTreasureOrderAsync(t.FirebaseKey, list[index + 1].FirebaseKey);
        ImGui.SameLine();

        if (ImGui.SmallButton($"地圖##map-{t.FirebaseKey}"))
        {
            TreasureLocator.OpenInGame(t);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在遊戲內開啟地圖並將旗標設在藏寶點");
        ImGui.SameLine();

        if (ImGui.SmallButton($"發送##send-{t.FirebaseKey}"))
        {
            SendTreasureToChat(t);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("以 /p 發送『地圖 ( X , Y )』到小隊頻道，含可點擊的地圖連結");
        ImGui.SameLine();

        if (ImGui.SmallButton($"刪除##rm-{t.FirebaseKey}"))
            _ = Plugin.PartyService.RemoveTreasureAsync(t.FirebaseKey);
    }

    /// <summary>
    /// 對應網頁版 copyPlayerMessage 的行為，但不是複製到剪貼簿，而是直接以 /p 發送到遊戲小隊頻道。
    /// 利用 Dalamud 的 MapLinkPayload 讓坐標部分在聊天顯示為可點擊的地圖連結，
    /// 不需依賴 DailyRoutines AutoConvertMapLink 也能有一樣效果。
    /// </summary>
    private static void SendTreasureToChat(Treasure t)
    {
        try
        {
            var mapLink = TreasureLocator.TryBuildMapLink(t);
            var aetheryte = AetheryteData.FindNearestByMapId(t.MapId, t.Coords.X, t.Coords.Y);
            var player = string.IsNullOrWhiteSpace(t.Player) ? "" : t.Player + " ";

            var se = new SeString();
            se.Append("/p ");
            if (!string.IsNullOrEmpty(player)) se.Append(player);

            if (mapLink != null)
            {
                se.Append(mapLink);
            }
            else
            {
                var mapName = MapData.GetMapName(t.MapId);
                se.Append($"{mapName} ( {t.Coords.X:0.0}  , {t.Coords.Y:0.0} )");
            }

            if (aetheryte != null)
                se.Append($" | 最近傳送水晶:[{aetheryte.Name}]");

            Plugin.ChatSender.SendSeString(se);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[SendTreasure] 發送聊天失敗");
        }
    }

    private void CommitPlayer(Treasure t)
    {
        var newValue = _editBuf;
        var key = t.FirebaseKey;
        _editingKey = null; _editMode = 0; _editBuf = "";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await Plugin.PartyService.UpdateTreasurePlayerAsync(key, newValue); }
            catch (Exception ex) { Plugin.Log.Error(ex, "更新玩家失敗"); }
        });
    }

    private void CommitNote(Treasure t)
    {
        var newValue = _editBuf;
        var key = t.FirebaseKey;
        _editingKey = null; _editMode = 0; _editBuf = "";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await Plugin.PartyService.UpdateTreasureNoteAsync(key, newValue); }
            catch (Exception ex) { Plugin.Log.Error(ex, "更新備註失敗"); }
        });
    }
}
