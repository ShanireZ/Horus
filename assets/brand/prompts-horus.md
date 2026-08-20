# Horus（horus）— v2 重构提示词

## 平台档案

| 项            | 值                                                 |
| ------------- | -------------------------------------------------- |
| code / 显示名 | `horus` / Horus                                    |
| 定位          | 局域网考试监考：检测与取证，系统初筛、人工裁决     |
| 最终文字      | `HORUS`（定案无中文名，单语）                      |
| 字体          | Cinzel，大写，字距 0.16–0.18em                     |
| 色板          | 青金蓝 `#2E5EAA` · 鎏金 `#C9A227` · 暖白 `#F2EDE4` |

## 视觉重构

核心是**庄重的守望与证据仪器**。荷鲁斯之眼仍是身份，但要做成一枚真实有层次的监察徽记：
鹰眼轮廓、经典泪线与卷尾、相机光圈瞳孔、表圈刻度和未闭合状态环。背景表达局域网考场的
记录与取证秩序，不走阴森监视、纯色科技底或 CCTV 直白图标。

## 1. Logo 原生生成（随后抠图）

```text
Use case: logo-brand
Asset type: transparent platform logo source
Primary request: an original Eye of Horus emblem reinterpreted as a dignified evidence-and-exam-integrity instrument
Subject: a recognizable falcon Eye of Horus with the classic descending teardrop stroke and curled tail; the iris is a precision camera aperture surrounded by engraved bezel ticks; an incomplete status ring and two subtle evidence-registration marks frame the eye
Style/medium: premium institutional insignia, crisp illustrated metal-and-enamel finish, slightly dimensional, symmetric and authoritative, not a flat placeholder icon
Composition/framing: one centered emblem, 14% padding, strong silhouette, readable at 208px, all components coherent
Color palette: lapis blue #2E5EAA, antique gold #C9A227, tiny warm-ivory highlights; no magenta in the subject
Scene/backdrop: perfectly flat solid #FF00FF chroma-key background for local removal
Constraints: one uniform backdrop, no shadow, no gradient, no texture, no floor, no reflection; no text; no letters; no watermark; no frame
Avoid: realistic human eye, eyelashes, wet iris, blood, tears of sadness, horror, CCTV camera, pyramid scene, pharaoh portrait, generic eye icon
```

抠图后入库 `horus-logo.png`。

## 2. 徽章背景原生生成（3:1）

```text
Use case: stylized-concept
Asset type: platform badge background, 3:1 full-bleed
Primary request: a ceremonial evidence-control environment inspired by Egyptian lapis and modern precision instruments
Scene/backdrop: deep lapis stone and dark blue-black metal layers, engraved concentric aperture rings, fine gold measurement ticks, faint evidence waveform traces and restrained network-node constellations
Subject: on the right 40%, a large circular observation aperture with layered gold bezel rings and subtle falcon-wing geometry; left 55% remains dark, calm and low-detail for later typography
Style/medium: premium cinematic institutional design, tactile stone-and-metal texture, precise, solemn, controlled
Composition/framing: wide 3:1, full bleed, right focal point, left text-safe zone
Lighting/mood: quiet gold grazing light, vigilant but not threatening
Color palette: #16294d, #2E5EAA, #C9A227, restrained #F2EDE4
Constraints: no text, no letters, no logo, no watermark, no frame, no human eye
Avoid: plain blue gradient, empty solid background, neon cyberpunk, CCTV camera, horror, pyramid postcard, bright left side
```

入库 `horus-badge-bg.webp`。

## 3. 登录背景原生生成（4:5）

```text
Use case: stylized-concept
Asset type: full-height login-stage background, 4:5
Primary request: a vertical Hall of Vigilance blending ancient Egyptian lapis architecture with modern evidence instrumentation
Scene/backdrop: monumental dark lapis walls, gold inlay measurement rings, a recessed circular aperture chamber, quiet columns and engraved falcon geometry, faint calibrated data traces embedded into stone rather than floating UI
Subject: a luminous observation aperture in the upper-middle distance; central and lower-middle area stays controlled for a logo and the word HORUS to be added later
Style/medium: premium cinematic environment illustration, tactile stone, enamel and metal, sophisticated institutional atmosphere
Composition/framing: 4:5 portrait, full bleed; central 64% safe area; top and bottom 12% crop-safe; symmetrical architectural depth
Lighting/mood: calm vigilant gold light, evidence archive solemnity, no menace
Color palette: deep lapis #16294d / #2E5EAA, antique gold #C9A227, small warm-ivory highlights
Constraints: no text, no letters, no logo, no watermark, no person, no realistic eye
Avoid: pure-color background, cyberpunk screens, CCTV, horror, blood, tourist Egypt scene, green palette
```

入库 `horus-stage-bg.webp`。

## 4. 后期合成

- 徽章左侧 1/4 叠 `horus-logo.png`；中间只排 `HORUS`，文字在中段安全区内略向左放置，
  Cinzel 全大写并与 Logo 垂直居中；右侧只保留背景光圈 HERO。
- 登录宣传图中央叠 `horus-logo.png`，下方排 `HORUS`；组合在中央安全区内整体略向下放置，
  文字在组内略向上收紧与 Logo 的间距。
- 不排副标、不创造中文名。

## 5. 自检

- Logo 必须保留泪线与卷尾，否则只是普通眼睛。
- 背景要有石材、金属、刻度、光圈与空间纵深，不能再是纯蓝色块。
- `HORUS` 五个字母正确，且视觉组的中心不是文字基线而是整组包围盒中心。
