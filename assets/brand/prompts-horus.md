# Horus（horus）— v2 重構提示詞

## 平台檔案

| 項            | 值                                                 |
| ------------- | -------------------------------------------------- |
| code / 顯示名 | `horus` / Horus                                    |
| 定位          | 區域網路考試監考：檢測與取證，系統初篩、人工裁決     |
| 最終文字      | `HORUS`（定案無中文名，單語）                      |
| 字體          | Cinzel，大寫，字距 0.16–0.18em                     |
| 色板          | 青金藍 `#2E5EAA` · 鎏金 `#C9A227` · 暖白 `#F2EDE4` |

## 視覺重構

核心是**莊重的守望與證據儀器**。荷魯斯之眼仍是身分，但要做成一枚真實有層次的監察徽記：
鷹眼輪廓、經典淚線與卷尾、相機光圈瞳孔、表圈刻度和未閉合狀態環。背景表達區域網路考場的
記錄與取證秩序，不走陰森監視、純色科技底或 CCTV 直白圖標。

## 1. Logo 原生生成（隨後摳圖）

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

摳圖後入庫 `horus-logo.png`。

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

入庫 `horus-badge-bg.webp`。

## 3. 登入背景原生生成（4:5）

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

入庫 `horus-stage-bg.webp`。

## 4. 後期合成

- 徽章左側 1/4 疊 `horus-logo.png`；中間只排 `HORUS`，文字在中段安全區內略向左放置，
  Cinzel 全大寫並與 Logo 垂直居中；右側只保留背景光圈 HERO。
- 登入宣傳圖中央疊 `horus-logo.png`，下方排 `HORUS`；組合在中央安全區內整體略向下放置，
  文字在組內略向上收緊與 Logo 的間距。
- 不排副標、不創造中文名。

## 5. 自檢

- Logo 必須保留淚線與卷尾，否則只是普通眼睛。
- 背景要有石材、金屬、刻度、光圈與空間縱深，不能再是純藍色塊。
- `HORUS` 五個字母正確，且視覺組的中心不是文字基線而是整組包圍盒中心。
