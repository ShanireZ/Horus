# Horus

[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg?style=flat-square)](LICENSE)
![Type: LAN proctoring](https://img.shields.io/badge/type-LAN%20proctoring-blue.svg?style=flat-square)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4.svg?style=flat-square&logo=dotnet&logoColor=white)

Horus 是一款**本地局域网考试监考系统**。

> 设计哲学:**纯监测 + 取证**(不做网络/主机层阻断)、**元数据优先、图像为辅**、**系统只初筛、人工裁决**。
> 全部数据在本地局域网内流转，在本地监考服务器上存储。唯一对外出网的是服务器侧的**视觉 LLM 识图**(最小化上传:送云前降采样 + 剥离元数据·**原图永不出网**)。

## 特点

- 面向"本地 IDE 写代码 + 网页 OJ 判题"场景的监考工具：采集 OS 元数据信号(前台窗口 / 进程 / 浏览器 URL / 剪贴板 / USB)、事件触发 + 随机基线截图,在服务器侧分析、留证,供监考员人工复核。
- **预防层为零**:不控考场网络、不做主机防火墙、不阻断网络、不强制锁屏。
- **向量化检索**:CLIP embedding "按图搜图",**单向不可逆、不能还原原图**。

## 组件

| 组件         | 说明                                                           | 技术                                           |
| ------------ | -------------------------------------------------------------- | ---------------------------------------------- |
| 采集端 Agent | 考试机各一;采信号 + 截图,哈希链 + HMAC 上报                    | C#/.NET 8(`agent/`)                            |
| 监考服务器   | 笔记本;接收 + 分析(L1 元数据 / L2 视觉 LLM 识图) + 落库 + 看板 | ASP.NET Core + SQLite + 文件 + 本地 ONNX CLIP  |
| 监考端       | 实时看板 + 可疑队列复核 + 按图搜图                             | 已实现的原生单页 Web 看板（`server/wwwroot/`） |

## 合规与正当使用

本系统仅用于**获得授权的考试监考，请勿用于未授权的监控**。部署前须告知被监考者采集范围、取得知情同意，并遵守所在地隐私与数据保护法规。

## 许可 / License

本项目采用 [GNU General Public License v3.0](LICENSE)（`GPL-3.0`）发布。
