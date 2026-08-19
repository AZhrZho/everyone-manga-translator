# EMT Logo Assets

这组资源以 C12「whole-letter lighting」版本为正式基准，供 Everyone Manga Translator（EMT）在桌面端、安装包、文档中使用。

## 目录

- `svg/`：四套可缩放原图，适合产品内置、网页和高分辨率印刷。
- `png/dark-background/`：深色主题字母 + 深紫双色漫画牌面。
- `png/dark-transparent/`：适合深色 UI 背景的透明版本，仅保留字母与字母内的气泡光影。
- `png/light-background/`：浅色主题字母 + 浅色双色漫画牌面。
- `png/light-transparent/`：适合浅色 UI 背景的透明版本，仅保留字母与字母内的气泡光影。
- `preview/emt-logo-variants-2x2.png`：四种正式变体的对比总览。

有背景版本包含漫画风格的 squircle 牌面；四套 PNG 的牌面外画布均保持透明，便于放在任意 UI 背景上。透明版本不绘制牌面，只保留 EMT 字母造型。

## PNG 尺寸

每套变体均提供：`16`、`24`、`32`、`48`、`64`、`96`、`128`、`256`、`512`、`1024` px。

文件名统一为 `emt-logo-{size}.png`。

## 推荐使用

- Windows 应用图标、任务栏和小型工具栏：优先使用对应主题的透明 PNG；16–48 px 建议使用 32 或 48 px 源文件进行系统缩放。
- 启动页、项目欢迎页、文档封面：使用 `*-background` 版本，推荐 256–1024 px。
- Web/SaaS 或需要无损缩放的场景：直接使用 SVG；根据容器主题选择 dark 或 light 变体。
- 深色背景使用 `dark-*`，浅色背景使用 `light-*`。透明版本不要再额外叠加牌面背景。

## 主要品牌色

- EMT 紫：`#916CDD` → `#6B49B3`
- 深墨紫牌面：`#463B59` → `#352E44`
- 浅色牌面：`#EEE9F4` → `#E2DCEC`
- 珍珠白字母：`#F7F6FA` → `#D7D3E2`
- 浅色主题墨紫字母：`#3B334D` → `#24202F`

所有 SVG 都保留 `title` 与 `desc`，并使用透明画布；可直接作为可访问的 `<img>` 或内联 SVG 使用。
