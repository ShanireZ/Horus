# web/ —— Horus 主页（`hr.betaoi.cn`）

两屏静态页：首屏定位，第二屏下载 + 页脚。**没有构建链** —— Caddy 直接 `file_server` 这几个文件。

| 文件 | 说明 |
| --- | --- |
| `index.html` | 两屏结构与全部文案 |
| `styles.css` | 全部样式（含窄屏取舍，见文件里那段注释） |
| `main.js` | 两件事：下载链接、Cloudflare Web Analytics 的 hostname 门控 |
| `assets/` | ★ **装配出来的，不入库**（见下） |

站点块在 [`../deploy/hr.caddy`](../deploy/hr.caddy)。

## ★★★ `assets/` 是装配出来的，不入库

权威在 `../assets/brand/`，而**那份本身已经是 `BetaPass/std/brand/horus/` 的逐字节镜像**
（见其 `PROVENANCE.md`）。再入库一份就是**第三份抄件** —— 上游换一次 logo，
三处一起过期而彼此自洽，★ **本仓内任何判据都看不见那个分歧**。

装配（本地预览与部署前都要先跑）：

```sh
mkdir -p web/assets
cp assets/brand/horus-logo.png assets/brand/horus-stage-bg.webp web/assets/
cp assets/brand/fonts/Cinzel.ttf web/assets/
cp assets/brand/fonts/OFL-1.1.txt web/assets/Cinzel-OFL-1.1.txt
```

★ `Cinzel-OFL-1.1.txt` 必须跟着字体一起走：Cinzel 按 SIL OFL 1.1 使用，
**分发字体文件时授权文本要在旁边**。

## 本地预览

```sh
python -m http.server 8150 --directory web
```

⚠ 直接双击 `index.html`（`file://`）看不出真样子：相对路径的 CSS 与资产不会按预期加载。

## 部署

```sh
rsync -a --delete web/ root@47.104.190.255:/opt/horus-web/
```

反代与验收命令见 [`../deploy/hr.caddy`](../deploy/hr.caddy) 的文件头。

## ★ 改文案之前先读这一条

首屏那三条与合规那一段**不是营销话术，是这个项目对自己的定义**：

- **纯检测 + 取证** —— `AGENTS.md` 设计铁律第 1 条「预防层为零」。
  ⛔ **任何时候都不得把它写成「防止 / 拦截 / 阻断作弊」。**
  预防层为零是定义，不是还没做的功能。
- **系统只初筛、人工裁决** —— 机器不下结论。
- **原图永不出网** —— 送云前降采样并剥离元数据（README「设计哲学」原话）。
- **仅用于获得授权的考试监考** —— README「合规与正当使用」原话，
  ★ 它把「未授权监控」明确划出去，**不是可以为排版让路的装饰**。

## ⏳ 待办

| # | 事 | 卡在哪 |
| --- | --- | --- |
| 1 | 下载按钮指向真实产物 | owner 2026-08-27 定：放 **Cloudflare R2**。落地只改 `main.js` 顶部 `DOWNLOADS` 一处；在那之前 `url` 保持 `null`，页面显式把按钮置灰并写「即将开放」 |
| 2 | Cloudflare Web Analytics 回传 | ⏳ **owner 控制台**：线上实测 beacon 加载得到但回传被 CORS 拦（`cdn-cgi/rum` 无 `Access-Control-Allow-Origin`），最可能是 `hr.betaoi.cn` 还没登记进那个 Web Analytics 站点。★ 代码有意不关掉 —— 登记做上那一刻它自己就好，**而在那之前控制台那两条报错就是这件事唯一活着的提示** |
