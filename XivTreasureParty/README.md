# XIV 藏寶圖工具小幫手 (Dalamud Plugin)

網頁版 [xiv-tc-treasure-finder](https://cycleapple.github.io/xiv-tc-treasure-finder/) 組隊模式的 Dalamud 插件實作。使用**相同的 Firebase 後端**，網頁版使用者與 Dalamud 使用者可以混合組同一個隊伍。

## 功能範圍 (v0.1)

- [x] 建立 / 加入 / 離開隊伍 (8 碼代碼)
- [x] 手動新增藏寶圖 (等級 → 地圖 → 字母編號位置)
- [x] 即時同步成員、藏寶圖、隊伍過期時間 (Firebase SSE)
- [x] 完成勾選、順序調整、備註、負責玩家
- [x] 房主順序鎖定
- [x] 清除已完成、一鍵複製 `<pos>` 指令
- [x] 心跳機制顯示成員線上 / 離線狀態

**未實作 (刻意)**：自動偵測開圖、從遊戲隊伍匯入成員。

## 目錄結構

```
XivTreasureParty/
├── Plugin.cs                    插件進入點與 service 注入
├── Configuration.cs             持久化設定 (暱稱 / 上次隊伍 / token)
├── Firebase/                    Firebase REST 客戶端
│   ├── FirebaseConfig.cs          API Key / DB URL
│   ├── FirebaseAuthClient.cs      匿名登入 + token 刷新
│   ├── FirebaseDatabaseClient.cs  REST CRUD
│   └── FirebaseStreamClient.cs    SSE 長連線監聽
├── Party/                       隊伍服務 (對應網頁版 party-service/sync-service)
│   ├── PartyService.cs
│   ├── SyncService.cs
│   ├── HeartbeatService.cs        (REST 無 onDisconnect, 用心跳模擬)
│   ├── PartyCodeGenerator.cs
│   └── Models/PartyModels.cs      DTO 與網頁版 Firebase schema 對齊
├── Data/                        靜態資料 (port 自 data.js)
│   ├── GradeData.cs               GRADE_DATA
│   ├── MapData.cs                 MAP_DATA + PLACE_NAMES
│   ├── TreasureData.cs            解析器
│   └── TreasuresRaw.cs            壓縮格式 965 筆藏寶點
└── UI/                          ImGui 介面
    ├── PluginWindow.cs            主視窗框架
    ├── PartyPanel.cs              暱稱 / 建立 / 加入 / 成員列表
    ├── AddTreasurePanel.cs        新增藏寶圖
    └── TreasureListPanel.cs       藏寶圖清單 + 順序 / 完成 / 備註
```

## Firebase Schema 相容性

path、欄位均與網頁版完全一致，兩邊互通：

```
parties/{CODE}/
├── meta/          createdAt, createdBy, expiresAt, orderLocked
├── members/{uid}/ joinedAt, nickname, isLeader, lastSeen (*新增)
└── treasures/{pushKey}/ id, coords{x,y}, mapId, gradeItemId,
                        partySize, addedBy, addedByNickname,
                        addedAt, order, completed, player, note
```

**`lastSeen` 欄位為本插件新增**，用於替代 Firebase REST 缺失的 `onDisconnect`。
每 30 秒寫入一次心跳；UI 判定 >90 秒無心跳即顯示「離線」。
網頁版會忽略此欄位，不影響互通。

## 編譯

需要 Dalamud.NET.Sdk 12.0.2 (API level 12) 及 .NET 8.0 SDK。

```bash
dotnet build -c Release
```

輸出會落在 `bin/x64/Release/XivTreasureParty/` 目錄，將該目錄複製到 Dalamud 的 `devPlugins` 下載入。

## 指令

- `/pth` — 切換主視窗開關
