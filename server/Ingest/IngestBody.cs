using System.IO;
using Microsoft.AspNetCore.Http;

namespace Horus.Server.Ingest;

/// 圖片 / 擊鍵等 HTTP 通道共用的 body 讀取 + 顯式大小上限。
///
/// 設計:在分配 MemoryStream 之前先按 <c>Content-Length</c> 拒絕超限(防禦:未鑑權者也能觸發的緩衝放大攻擊),
/// 再按實測長度備援(分塊無 Content-Length 時)。兩路 ingest 共用同一份,避免安全邏輯日後分叉
/// (品質#5 / BUG#1 同類分叉的體現)。
///
/// 回傳三元組:成功時 <c>(Body, null, null)</c>;超限回傳 <c>(null, 413, "too_large")</c>;
/// <paramref name="allowEmpty"/> 為 <c>false</c> 且 body 為空時回傳 <c>(null, 400, "empty_body")</c>。
internal static class IngestBody
{
    public static async Task<(byte[]? Body, int? StatusCode, string? Error)> ReadWithLimitAsync(
        HttpRequest req, int maxBytes, bool allowEmpty = false)
    {
        // 1) 聲明瞭超大 Content-Length 的先拒(不緩衝)——前置到最前,未鑑權者也觸發不了大緩衝。
        if (req.ContentLength is long declared && declared > maxBytes)
            return (null, 413, "too_large");

        // 2) 緩衝整條 body(既用於驗籤也用於解析);流讀一次即耗盡,故先整段讀入。
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);

        // 3) 無 Content-Length(分塊)時的備援上限(Kestrel 全域上限之內更早收斂)。
        if (ms.Length > maxBytes)
            return (null, 413, "too_large");

        // 4) 空 body(聲明 allowEmpty=false 時):不寫入資料庫壞圖。
        if (ms.Length == 0 && !allowEmpty)
            return (null, 400, "empty_body");

        return (ms.ToArray(), null, null);
    }
}
