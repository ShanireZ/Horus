---
type: reference
title: KSK 击键旁路对接文档
---

# KSK 击键旁路对接文档

> 适用范围：外部判题系统（含判题后端 + 判题网页）向 Horus 服务器上报「击键节奏」样本。
> 通道：`POST /ingest/keystroke`（**旁路**，不经采集 Agent）。
> 权威实现：`server/Ingest/KeystrokeIngest.cs`、`contracts/Crypto.cs`（鉴权）、`Config/ServerConfig.cs`（配置）。
> API 总览见 [`api-contract-m1.md` §2.2](api-contract-m1.md)。

---

## 1. 为什么需要 KSK 旁路

采集 Agent 跑在学员机上，受持 PSK 的学员机控制；但「击键节奏」来自**判题网页**（学员在网页里写代码）——
它不经 Agent，直接由判题后端（可信侧）发给 Horus 服务器。

风险：同网段任意学员机都可构造 `POST /ingest/keystroke` 把他人 `seatId` 的击键栽赃给同学，或给自己刷「清白」样本。
对策：

- **会话鉴权（KSK）**：浏览器 JS 无法安全持密钥，故由**判题后端**持 `KSK` 对整条提交体签名，放在 `X-Horus-KSig` 头。
- **域分隔前缀** `"keystroke\n"`：防止把图片/事件通道的签名拿来当击键签名复用。
- **幂等 `submissionId`**：防同网嗅探到明文后原样重放合法签名体（签名验得过但灌队列/DoS）。
- KSK 与采集 PSK、管理令牌**相互独立**：判题侧失陷不等于采集侧失陷。

---

## 2. 请求格式

```
POST http://<server>:<port>/ingest/keystroke
Content-Type: application/json
X-Horus-KSig: <hex hmac>              // 配了 KSK 时必带；未配 KSK 通道放行（仅联调）
```

请求体（JSON）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `examId` | string | 是 | 考试 ID（须与 Horus 中已创建考试一致）。 |
| `seatId` | string | 是 | 座位/学员标识（栽赃防护的关键绑定字段）。 |
| `submissionId` | string | 否 | 幂等键。**同一 `(examId, seatId, ts, submissionId)` 已落库则判重放**，返回 `{stored:false, duplicate:true}`，不重复入队。强烈建议传（如 `sub_<题号>_<尝试次>`）。 |
| `ts` | number | 否 | 提交 Unix 秒（浮点，可带毫秒）。缺省取服务器当前 UTC 秒。 |
| `timeline` | array\<number\> | 否 | keydown 相对毫秒数组（可降采样）。**仅存档，不参与打分**。 |
| `features` | object | 否 | 判题前端算好的特征，**唯一决定风险分**。见 §4。 |

示例：

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

## 3. 签名构造（判题后端）

伪码：

```
body_bytes   = 请求体原始 UTF-8 字节（务必是**最终发送**的那串 JSON 字节）
body_sha256  = 小写 hex( SHA256(body_bytes) )
ksig         = 小写 hex( HMAC-SHA256(KSK_bytes, "keystroke\n" + body_sha256) )
```

- `KSK_bytes` = `keystrokeSecretBase64` 经 base64 解码得到的原始字节（与 `server.config.json` 中的 `keystrokeSecretBase64` 一致）。
- 分隔串是 `"keystroke\n"`（`keystroke` 后跟**一个换行符 `\n`** UTF-8）。
- 大小写：**hex 全小写**（与 `Crypto.HmacHex` 输出一致）。
- 注意：`sha256(body)` 是对**字节**取哈希，不是对字符串；JSON 字符串化必须稳定（与发送字节完全一致），否则验签失败。

### 3.1 最小示例

**Python：**

```python
import hashlib, hmac, base64, json, urllib.request

KSK = base64.b64decode("<keystrokeSecretBase64>")      # 来自服务器部署配置
body = json.dumps({
    "examId": "E1", "seatId": "A07", "submissionId": "sub_42",
    "features": {"pasteCount": 1, "maxBurstCharsPerSec": 140, "idleThenBlock": True}
}, separators=(",", ":")).encode("utf-8")              # 紧凑序列化,与发送字节一致

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

**curl（联调用，先算好 ksig）：**

```bash
KSIG=$(printf 'keystroke\n%s' "$(sha256sum body.json | cut -d' ' -f1)" \
        | openssl dgst -sha256 -mac HMAC -macopt key:<hex-or-binary-KSK> -hex | cut -d' ' -f2)
curl -X POST http://<server>:8080/ingest/keystroke \
  -H "Content-Type: application/json" -H "X-Horus-KSig: $KSIG" \
  --data-binary @body.json
```

---

## 4. 服务器侧打分与入队

`KeystrokeIngest.RiskFrom(features)` 仅看 `features`：

| 特征 | 条件 | `risk` |
|---|---|---|
| `idleThenBlock == true` | 空窗后突现整段代码 | **70** |
| `pasteCount > 0` | 粘贴 | **60** |
| `maxBurstCharsPerSec > 120` | 超人输入速度 | **55** |
| 以上皆否 | — | 0 |

- 落库：`keystroke_samples(exam_id, seat_id, submission_id, ts, timeline, features, risk)`。
- `risk >= RiskThreshold`（默认 **50**，见 `ServerConfig.RiskThreshold`）→ 入 `suspicious_queue`：
  - `idleThenBlock` 真 → `kind = "ide_plugin_suspect"`
  - 否则 → `kind = "large_paste"`
  - `refs = ["keystroke:<newId>"]`，`status = "pending"`。
- 落库与入队在**同一写锁事务**内（避免归档窗口卡在中间产生孤儿 pending）。

---

## 5. 响应

| 情形 | HTTP | body |
|---|---|---|
| 验签失败（配 KSK 却 `ksig` 错/缺） | 401 | `{"error":"bad_ksig"}` |
| 体过大（> 512 KB） | 413 | `{"error":"too_large"}` |
| JSON 非法 | 400 | `{"error":"bad_json"}` |
| 首存成功 | 200 | `{"stored":true,"risk":<n>}` |
| 幂等重放（已存在同键） | 200 | `{"stored":false,"duplicate":true,"risk":<n>}` |

> 重放响应仍是 200——判题后端把它当「成功但跳过」处理即可，不要据此重试。

---

## 6. 服务器配置（部署侧）

`server.config.json`：

```json
{
  "keystrokeSecretBase64": "<base64 of KSK raw bytes>",
  "riskThreshold": 50
}
```

- `keystrokeSecretBase64` **留空** → `KeystrokeAuthEnabled=false` → 该通道放行（仅本地联调，生产必配 KSK）。
- 变更 KSK 后，旧签名立即失效（401）——判题后端与服务器须同步同一把 KSK。
- KSK 与 `pskBase64`、管理令牌相互独立、可分别轮换。

---

## 7. 安全要点 checklist

- [ ] 判题**后端**持有 KSK，绝不下发到浏览器 JS。
- [ ] 签名用「最终发送字节」的 SHA256，序列化方式与发送严格一致。
- [ ] 每次提交带稳定 `submissionId`（幂等 + 防重放）。
- [ ] `seatId` 由可信后端填入，不被前端任意覆盖（栽赃面）。
- [ ] 生产环境 `keystrokeSecretBase64` 非空（fail-closed）。
- [ ] 迁移/轮换 KSK 时，服务器与判题后端同窗切换。
