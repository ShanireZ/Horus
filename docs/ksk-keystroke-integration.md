---
type: reference
title: KSK 擊鍵旁路對接文件
---

# KSK 擊鍵旁路對接文件

> 適用範圍：外部判題系統（含判題後端 + 判題網頁）向 Horus 伺服器上報「擊鍵節奏」樣本。
> 通道：`POST /ingest/keystroke`（**旁路**，不經採集 Agent）。
> 權威實作：`server/Ingest/KeystrokeIngest.cs`、`contracts/Crypto.cs`（鑑權）、`Config/ServerConfig.cs`（設定）。
> API 總覽見 [`api-contract-m1.md` §2.2](api-contract-m1.md)。

---

## 1. 為什麼需要 KSK 旁路

採集 Agent 跑在學員機上，受持 PSK 的學員機控制；但「擊鍵節奏」來自**判題網頁**（學員在網頁裡寫程式碼）——
它不經 Agent，直接由判題後端（可信側）發給 Horus 伺服器。

風險：同網段任意學員機都可建構 `POST /ingest/keystroke` 把他人 `seatId` 的擊鍵栽贓給同學，或給自己刷「清白」樣本。
對策：

- **工作階段鑑權（KSK）**：瀏覽器 JS 無法安全持金鑰，故由**判題後端**持 `KSK` 對整條提交體簽名，放在 `X-Horus-KSig` 頭。
- **域分隔前綴** `"keystroke\n"`：防止把圖片/事件通道的簽名拿來當擊鍵簽名重複使用。
- **冪等 `submissionId`**：防同網嗅探到明文後原樣重放合法簽名體（簽名驗得過但灌佇列/DoS）。
- KSK 與採集 PSK、管理權杖**相互獨立**：判題側失陷不等於採集側失陷。

---

## 2. 請求格式

```
POST http://<server>:<port>/ingest/keystroke
Content-Type: application/json
X-Horus-KSig: <hex hmac>              // 配了 KSK 时必带；未配 KSK 通道放行（仅联调）
```

請求體（JSON）：

| 欄位 | 類型 | 必填 | 說明 |
|---|---|---|---|
| `examId` | string | 是 | 考試 ID（須與 Horus 中已建立考試一致）。 |
| `seatId` | string | 是 | 座位/學員標識（栽贓防護的關鍵綁定欄位）。 |
| `submissionId` | string | 否 | 冪等鍵。**同一 `(examId, seatId, ts, submissionId)` 已寫入資料庫則判重放**，回傳 `{stored:false, duplicate:true}`，不重複入隊。強烈建議傳（如 `sub_<题号>_<尝试次>`）。 |
| `ts` | number | 否 | 提交 Unix 秒（浮點，可帶毫秒）。預設取伺服器當前 UTC 秒。 |
| `timeline` | array\<number\> | 否 | keydown 相對毫秒陣列（可降取樣）。**僅存檔，不參與打分**。 |
| `features` | object | 否 | 判題前端算好的特徵，**唯一決定風險分**。見 §4。 |

範例：

```json
{
  "examId": "E1",
  "seatId": "A07",
  "submissionId": "sub_42",
  "ts": 1750000000.0,
  "timeline": [12, 98, 210, 305, 401],
  "features": { "pasteCount": 1, "maxBurstCharsPerSec": 140, "idleThenBlock": true }
}
```

---

## 3. 簽名建構（判題後端）

偽碼：

```
body_bytes   = 请求体原始 UTF-8 字节（务必是**最终发送**的那串 JSON 字节）
body_sha256  = 小写 hex( SHA256(body_bytes) )
ksig         = 小写 hex( HMAC-SHA256(KSK_bytes, "keystroke\n" + body_sha256) )
```

- `KSK_bytes` = `keystrokeSecretBase64` 經 base64 解碼得到的原始位元組（與 `server.config.json` 中的 `keystrokeSecretBase64` 一致）。
- 分隔串是 `"keystroke\n"`（`keystroke` 後跟**一個換行符 `\n`** UTF-8）。
- 大小寫：**hex 全小寫**（與 `Crypto.HmacHex` 輸出一致）。
- 注意：`sha256(body)` 是對**位元組**取雜湊，不是對字串；JSON 字串化必須穩定（與傳送位元組完全一致），否則驗簽失敗。

<a id="31-最小示例"></a>

### 3.1 最小範例

**Python：**

```python
import hashlib, hmac, base64, json, urllib.request

KSK = base64.b64decode("<keystrokeSecretBase64>")      # 來自伺服器部署設定
body = json.dumps({
    "examId": "E1", "seatId": "A07", "submissionId": "sub_42",
    "features": {"pasteCount": 1, "maxBurstCharsPerSec": 140, "idleThenBlock": True}
}, separators=(",", ":")).encode("utf-8")              # 緊湊序列化,與傳送位元組一致

body_sha = hashlib.sha256(body).hexdigest()
ksig = hmac.new(KSK, b"keystroke\n" + body_sha.encode("utf-8"), hashlib.sha256).hexdigest()

req = urllib.request.Request(
    "http://<server>:8080/ingest/keystroke",
    data=body, headers={"Content-Type": "application/json", "X-Horus-KSig": ksig})
print(urllib.request.urlopen(req).read().decode())
```

**Node.js（fetch）：**

```js
import crypto from "node:crypto";
const KSK = Buffer.from("<keystrokeSecretBase64>", "base64");
const body = JSON.stringify({ examId:"E1", seatId:"A07", submissionId:"sub_42",
  features:{ pasteCount:1, maxBurstCharsPerSec:140, idleThenBlock:true } });
const bodySha = crypto.createHash("sha256").update(body).digest("hex");
const ksig = crypto.createHmac("sha256", KSK)
  .update("keystroke\n" + bodySha).digest("hex");
const r = await fetch("http://<server>:8080/ingest/keystroke", {
  method:"POST", headers:{"Content-Type":"application/json","X-Horus-KSig":ksig}, body });
console.log(await r.json());
```

**curl（整合測試用，先算好 ksig）：**

```bash
KSIG=$(printf 'keystroke\n%s' "$(sha256sum body.json | cut -d' ' -f1)" \
        | openssl dgst -sha256 -mac HMAC -macopt key:<hex-or-binary-KSK> -hex | cut -d' ' -f2)
curl -X POST http://<server>:8080/ingest/keystroke \
  -H "Content-Type: application/json" -H "X-Horus-KSig: $KSIG" \
  --data-binary @body.json
```

---

## 4. 伺服器側打分與入隊

`KeystrokeIngest.RiskFrom(features)` 僅看 `features`：

| 特徵 | 條件 | `risk` |
|---|---|---|
| `idleThenBlock == true` | 空窗後突現整段程式碼 | **70** |
| `pasteCount > 0` | 粘貼 | **60** |
| `maxBurstCharsPerSec > 120` | 超人輸入速度 | **55** |
| 以上皆否 | — | 0 |

- 寫入資料庫：`keystroke_samples(exam_id, seat_id, submission_id, ts, timeline, features, risk)`。
- `risk >= RiskThreshold`（預設 **50**，見 `ServerConfig.RiskThreshold`）→ 入 `suspicious_queue`：
  - `idleThenBlock` 真 → `kind = "ide_plugin_suspect"`
  - 否則 → `kind = "large_paste"`
  - `refs = ["keystroke:<newId>"]`，`status = "pending"`。
- 寫入資料庫與入隊在**同一寫鎖交易**內（避免歸檔視窗卡在中間產生孤兒 pending）。

---

## 5. 回應

| 情形 | HTTP | body |
|---|---|---|
| 驗簽失敗（配 KSK 卻 `ksig` 錯/缺） | 401 | `{"error":"bad_ksig"}` |
| 體過大（> 512 KB） | 413 | `{"error":"too_large"}` |
| JSON 非法 | 400 | `{"error":"bad_json"}` |
| 首存成功 | 200 | `{"stored":true,"risk":<n>}` |
| 冪等重放（已存在同鍵） | 200 | `{"stored":false,"duplicate":true,"risk":<n>}` |

> 重放回應仍是 200——判題後端把它當「成功但跳過」處理即可，不要據此重試。

---

## 6. 伺服器設定（部署側）

`server.config.json`：

```json
{
  "keystrokeSecretBase64": "<base64 of KSK raw bytes>",
  "riskThreshold": 50
}
```

- `keystrokeSecretBase64` **留空** → `KeystrokeAuthEnabled=false` → 該通道放行（僅本機整合測試，生產必配 KSK）。
- 變更 KSK 後，舊簽名立即失效（401）——判題後端與伺服器須同步同一把 KSK。
- KSK 與 `pskBase64`、管理權杖相互獨立、可分別輪換。

---

## 7. 安全要點 checklist

- [ ] 判題**後端**持有 KSK，絕不下發到瀏覽器 JS。
- [ ] 簽名用「最終傳送位元組」的 SHA256，序列化方式與傳送嚴格一致。
- [ ] 每次提交帶穩定 `submissionId`（冪等 + 防重放）。
- [ ] `seatId` 由可信後端填入，不被前端任意覆蓋（栽贓面）。
- [ ] 生產環境 `keystrokeSecretBase64` 非空（fail-closed）。
- [ ] 遷移/輪換 KSK 時，伺服器與判題後端同窗切換。
