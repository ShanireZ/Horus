# Horus

[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg?style=flat-square)](LICENSE)
![Type: LAN proctoring](https://img.shields.io/badge/type-LAN%20proctoring-blue.svg?style=flat-square)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4.svg?style=flat-square&logo=dotnet&logoColor=white)

Horus 是一款**本機區域網路考試監考系統**。

> 設計哲學:**純監測 + 取證**(不做網路/主機層阻斷)、**中繼資料優先、圖像為輔**、**系統只初篩、人工裁決**。
> 全部資料在本機區域網路內流轉，在本機監考伺服器上儲存。唯一對外出網的是伺服器側的**視覺 LLM 識圖**(最小化上傳:送雲前降取樣 + 剝離中繼資料·**原圖永不出網**)。

## 特點

- 面向"本機 IDE 寫程式碼 + 網頁 OJ 判題"場景的監考工具：採集 OS 中繼資料信號(前台視窗 / 處理程序 / 瀏覽器 URL / 剪貼板 / USB)、事件觸發 + 隨機基線截圖,在伺服器側分析、留證,供監考員人工複核。
- **預防層為零**:不控考場網路、不做主機防火牆、不阻斷網路、不強制鎖屏。
- **向量化檢索**:CLIP embedding "按圖搜圖",**單向不可逆、不能還原原圖**。

## 元件

| 元件         | 說明                                                           | 技術                                           |
| ------------ | -------------------------------------------------------------- | ---------------------------------------------- |
| 採集端 Agent | 考試機各一;採信號 + 截圖,雜湊鏈 + HMAC 上報                    | C#/.NET 8(`agent/`)                            |
| 監考伺服器   | 筆記本;接收 + 分析(L1 中繼資料 / L2 視覺 LLM 識圖) + 寫入資料庫 + 看板 | ASP.NET Core + SQLite + 檔案 + 本機 ONNX CLIP  |
| 監考端       | 即時看板 + 可疑佇列複核 + 按圖搜圖                             | 已實作的原生單頁 Web 看板（`server/wwwroot/`） |

監考看板按工作區 Web Platform Baseline 策略聲明為受控 Web：Newly 是開發能力上限，實際執行合同為機構管理的 Chrome / Edge 當前及前一主版本。看板沒有前端建置鏈，相容性由 [`baseline.config.json`](baseline.config.json)、合同測試與開考前實機檢查共同保證。

## 合規與正當使用

本系統僅用於**獲得授權的考試監考，請勿用於未授權的監控**。部署前須告知被監考者採集範圍、取得知情同意，並遵守所在地隱私與資料保護法規。

## 授權 / License

本專案採用 [GNU General Public License v3.0](LICENSE)（`GPL-3.0`）發佈。
