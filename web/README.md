# web/ —— Horus 主頁（`hr.betaoi.cc`）

兩屏靜態頁：首屏定位，第二屏下載 + 頁腳。**沒有建置鏈** —— Caddy 直接 `file_server` 這幾個檔案。

| 檔案 | 說明 |
| --- | --- |
| `index.html` | 兩屏結構與全部文案 |
| `styles.css` | 全部樣式（含窄螢幕取捨，見檔案裡那段註解） |
| `main.js` | 兩件事：下載連結、Cloudflare Web Analytics 的 hostname 門控 |
| `assets/` | ★ **裝配出來的，不入庫**（見下） |

P147/P149 當前僅 cc 主域，cn 休眠；共享主設定仍歸 betai，本站不掛演練門。現有下載指向歷史 Windows 建置，本輪未發佈新包；遷移上線前須核新產物/摘要與實際下載，見 architecture §0 D10。

站台塊在 [`../deploy/hr.caddy`](../deploy/hr.caddy)。

## ★★★ `assets/` 是裝配出來的，不入庫

權威在 `../assets/brand/`，而**那份本身已經是 `BetaPass/std/brand/horus/` 的逐位元組鏡像**
（見其 `PROVENANCE.md`）。再入庫一份就是**第三份抄件** —— 上游換一次 logo，
三處一起過期而彼此自洽，★ **本儲存庫內任何判據都看不見那個分歧**。

裝配（本機預覽與部署前都要先跑）：

```sh
mkdir -p web/assets
cp assets/brand/horus-logo.png assets/brand/horus-stage-bg.webp web/assets/
cp assets/brand/fonts/Cinzel.ttf web/assets/
cp assets/brand/fonts/OFL-1.1.txt web/assets/Cinzel-OFL-1.1.txt
```

★ `Cinzel-OFL-1.1.txt` 必須跟著字體一起走：Cinzel 按 SIL OFL 1.1 使用，
**散布字體檔案時授權文字要在旁邊**。

## 本機預覽

```sh
python -m http.server 8150 --directory web
```

⚠ 直接雙擊 `index.html`（`file://`）看不出真樣子：相對路徑的 CSS 與資產不會按預期載入。

## 部署

```sh
rsync -a --delete web/ root@47.104.190.255:/opt/horus-web/
```

反代與驗收命令見 [`../deploy/hr.caddy`](../deploy/hr.caddy) 的檔案頭。

## ★ 改文案之前先讀這一條

首屏那三條與合規那一段**不是營銷話術，是這個專案對自己的定義**：

- **純檢測 + 取證** —— `AGENTS.md` 設計鐵律第 1 條「預防層為零」。
  ⛔ **任何時候都不得把它寫成「防止 / 攔截 / 阻斷作弊」。**
  預防層為零是定義，不是還沒做的功能。
- **系統只初篩、人工裁決** —— 機器不下結論。
- **原圖永不出網** —— 送雲前降取樣並剝離中繼資料（README「設計哲學」原話）。
- **僅用於獲得授權的考試監考** —— README「合規與正當使用」原話，
  ★ 它把「未授權監控」明確劃出去，**不是可以為排版讓路的裝飾**。

## ⏳ 待辦

| # | 事 | 卡在哪 |
| --- | --- | --- |
| 1 | 下載按鈕指向真實產物 | owner 2026-08-27 定：放 **Cloudflare R2**。完成實作只改 `main.js` 頂部 `DOWNLOADS` 一處；在那之前 `url` 保持 `null`，頁面顯式把按鈕置灰並寫「即將開放」 |
| 2 | Cloudflare Web Analytics 回傳 | ⏳ **owner 控制檯**：2026-08-27 歷史 cn 實測 beacon 載入得到但回傳被 CORS 攔（`cdn-cgi/rum` 無 `Access-Control-Allow-Origin`），最可能是 `hr.betaoi.cn` 還沒登記進那個 Web Analytics 站台。★ 程式碼有意不關掉 —— 登記做上那一刻它自己就好，**而在那之前控制檯那兩條回報錯誤就是這件事唯一活著的提示** |
