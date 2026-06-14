# OW Translator Lite

<p align="center">
  <img src="Resources/UI/ow-translator-lite-icon.png" alt="OW Translator Lite" width="96" height="96">
</p>

<p align="center">
  <strong>面向守望先锋外服聊天的实时 OCR 翻译 overlay</strong><br>
  <span>A real-time OCR translation overlay for Overwatch 2 chat</span>
</p>

<p align="center">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square">
  <img alt="Platform" src="https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square">
  <img alt="OCR" src="https://img.shields.io/badge/OCR-OneOCR-00A8E8?style=flat-square">
  <img alt="Status" src="https://img.shields.io/badge/status-0.2.0--beta.5-34C759?style=flat-square">
</p>

<p align="center">
  <a href="#中文">中文</a> · <a href="#english">English</a>
</p>

---

## 中文

OW Translator Lite 是一个 Windows 桌面工具，用于在《守望先锋 2》外服对局中实时翻译聊天内容。它通过本地 OneOCR 识别你框选的聊天区域，把英语、日语、韩语玩家消息发送到 DeepSeek 或 OpenAI-compatible API 翻译为简体中文，并按原聊天顺序显示在置顶翻译框中。

项目目标很明确：在不打断游戏视野和操作节奏的前提下，让中文玩家能快速理解队友和对手的文字沟通。当前版本为 `0.2.0-beta.5`，已经完成 Timeline 顺序对齐、多帧 OCR 共识、韩语 jamo 容错和 Apple Dark Console UI 改造。

### 演示

**主程序配置**

![主程序配置](Resources/QuickStart/app-api-config.png)

**游戏内实机演示**

![游戏内实机演示](Resources/QuickStart/game-live-demo.png)

**中文回话翻译复制**

![中文回话翻译复制](Resources/QuickStart/reply-overlay-input.png)

### 功能亮点

- **OW 专用聊天识别**：以 `[玩家名]: 正文` 这类玩家消息为翻译单元，过滤系统提示和中文 UI 噪声。
- **本地 OneOCR**：OCR 在本机完成，固定自动识别，不依赖云 OCR。
- **OpenAI-compatible 翻译**：支持 DeepSeek 和 OpenAI-compatible chat completions API。
- **稳定顺序显示**：用 Timeline `Seq` 对齐消息身份，降低开聊天历史、OCR 抖动、多人连续发言时的重复和乱序。
- **韩语友好**：支持去空格比较、Hangul jamo 级相似度、短韩语消息评分和 OCR 混淆容错。
- **低存在感 overlay**：置顶透明翻译框，支持常态显示翻译框、鼠标穿透、拖动缩放和历史滚动。
- **中文回话助手**：在 overlay 底部输入中文，翻译为英语、日语或韩语并复制到剪贴板，由用户自行粘贴发送。
- **本地隐私保护**：API Key 使用 Windows DPAPI 存储，诊断导出会脱敏。

### 快速使用

1. 从 GitHub Releases 下载最新的 `OWTranslatorLite-v*-portable-win-x64.zip`。
2. 解压整个压缩包，运行外层 `OWTranslatorLite.exe`。
3. 在翻译 API 中选择 `DeepSeek` 或 `OpenAI Compatible`。
4. 填写 API URL 和 API Key；DeepSeek 默认 URL 为 `https://api.deepseek.com`。
5. DeepSeek API 需要充值余额并按量计费，聊天翻译用量通常很小，实际费用很低。
6. 点击“获取模型”，选择模型，例如 `deepseek-v4-flash`。
7. 点击“选择翻译区域”，框选 OW 左侧聊天区域，尽量完整包含玩家名和正文。
8. 点击“开始”，译文会显示在翻译框中。
9. 如需固定显示译文，打开“常态显示翻译框”；如需拖动、缩放或滚动历史，关闭“鼠标穿透”。

### 回话输入

打开“显示回话输入条”后，overlay 底部会出现输入栏。输入中文，选择自动、英语、日语或韩语，按 Enter 后程序会把译文复制到剪贴板。

程序不会自动发送游戏聊天。翻译完成后，请在 OW 聊天框中粘贴，再由你自己按 Enter 发送。

### 实现逻辑

```text
selected chat region
  -> pixel diff patrol
  -> burst OCR
  -> OW chat parser
  -> Timeline alignment
  -> multi-frame consensus
  -> translation queue
  -> Seq-sorted overlay
```

核心思路是把“消息身份”交给有序 Timeline，而不是只靠文本内容判重。OCR 看到的当前聊天区域会被当作权威 Timeline 的可见后缀来对齐；对齐上的就是旧消息，对齐不上的才进入新消息确认流程。

新消息不会立刻发送翻译，而是经过多帧共识：连续两帧一致，或韩语 jamo 距离足够近时才入队。这样可以吸收 OneOCR 对短韩语、空格和相近字符的轻微抖动。

采样侧采用像素 diff 巡逻：稳定画面只做低成本截图签名，不跑 OCR；检测到聊天区域变化后再进行短突发 OCR。翻译完成后，overlay 按 Timeline `Seq` 排序回填，因此网络延迟或重试不会改变聊天显示顺序。

### 开发者命令

普通用户不需要运行这些命令。下面的命令用于开发、验证或准备测试包，路径均使用仓库相对路径，适合在其他机器或 CI 环境中复用。

```powershell
dotnet build OwTranslateLite.csproj -c Release
```

发布测试包：

```powershell
powershell -ExecutionPolicy Bypass -File Tools/PackageRelease.ps1
```

回归测试：

```powershell
dotnet run --project Tools/ReplayLab/ReplayLab.csproj -c Release -- --timeline-smoke
dotnet run --project Tools/ReplayLab/ReplayLab.csproj -c Release -- --similarity Tools/ReplayLab/similarity/korean-jamo-regression.json
dotnet run --project Tools/ReplayLab/ReplayLab.csproj -c Release -- Tools/ReplayLab/fixtures/smoke-korean-short Tools/ReplayLab/fixtures/smoke-korean-short/expected.json
dotnet build OwTranslateLite.csproj -c Release
```

期望 fixture 指标：

```text
missing=0, duplicates=0, outOfOrder=0, extra=0
```

### 项目结构

```text
Core/                  设置、Timeline、解析、诊断、翻译协调
Ocr/                   OneOCR 封装与 OW 图像预处理
Overlay/               置顶翻译框
Translation/           OpenAI-compatible / DeepSeek 请求
Resources/             OW 术语表、主题资源、UI 图标、教程截图
Tools/ReplayLab/       离线回放与回归断言
Tools/OcrPreprocessLab/ OCR 预处理实验工具
Docs/                  架构、测试指南、重构记录
```

### 维护文档

- [Architecture](Docs/ARCHITECTURE.md)
- [Timeline Refactor Update](Docs/TimelineRefactor-Update-20260613.md)
- [Replay Case Recording Guide](Docs/ReplayCaseRecordingGuide-20260612.md)
- [Publish Layout](Docs/PublishLayout.md)

---

## English

OW Translator Lite is a Windows desktop app for real-time Overwatch 2 chat translation. It captures a user-selected chat region, recognizes text locally with OneOCR, sends English/Japanese/Korean player messages to DeepSeek or any OpenAI-compatible chat completions API, and renders Simplified Chinese translations in a topmost overlay.

The goal is practical and narrow: help Chinese players understand match chat without interrupting visibility or input flow. The current version is `0.2.0-beta.5`, with Timeline-based ordering, multi-frame OCR consensus, Korean jamo tolerance, and an Apple Dark Console UI.

### Demo

**Main App Configuration**

![Main app configuration](Resources/QuickStart/app-api-config.png)

**In-Game Live Demo**

![In-game live demo](Resources/QuickStart/game-live-demo.png)

**Chinese Reply Translation**

![Chinese reply translation](Resources/QuickStart/reply-overlay-input.png)

### Highlights

- **OW-specific chat parsing**: player chat lines such as `[player]: message` are treated as translation units, while system/UI noise is filtered.
- **Local OneOCR**: OCR runs locally with automatic recognition and no cloud OCR dependency.
- **OpenAI-compatible translation**: DeepSeek and OpenAI-compatible chat completions APIs are supported.
- **Stable ordering**: Timeline `Seq` identity reduces duplicates and out-of-order display when chat history is opened or OCR jitters.
- **Korean-friendly matching**: whitespace-insensitive comparison, Hangul jamo similarity, short-message scoring, and OCR confusion tolerance.
- **Low-profile overlay**: topmost transparent translation box with always-show mode, click-through, dragging, resizing, and history scrolling.
- **Reply helper**: type Chinese in the overlay input bar, translate it to English/Japanese/Korean, and copy the result to the clipboard.
- **Local secret protection**: API keys are protected with Windows DPAPI, and diagnostics redact secrets.

### Quick Start

1. Download the latest `OWTranslatorLite-v*-portable-win-x64.zip` from GitHub Releases.
2. Extract the whole archive and run the outer `OWTranslatorLite.exe`.
3. Choose `DeepSeek` or `OpenAI Compatible`.
4. Enter the API URL and API key. DeepSeek defaults to `https://api.deepseek.com`.
5. DeepSeek API usage requires account balance and is billed by usage; chat translation usually costs very little.
6. Click Fetch Models and choose a model such as `deepseek-v4-flash`.
7. Select the OW chat region. Include complete player names and message text.
8. Click Start. Translations will appear in the overlay.
9. Enable always-show translation box if needed. Disable click-through when you want to drag, resize, or scroll the overlay.

### Reply Input

When the reply input bar is enabled, type Chinese at the bottom of the overlay, choose Auto/English/Japanese/Korean, and press Enter. The translated reply is copied to the clipboard.

The app does not auto-send game chat. Paste the copied text into OW chat and send it yourself.

### How It Works

```text
selected chat region
  -> pixel diff patrol
  -> burst OCR
  -> OW chat parser
  -> Timeline alignment
  -> multi-frame consensus
  -> translation queue
  -> Seq-sorted overlay
```

The central idea is that message identity comes from an ordered Timeline, not from content equality alone. The currently visible chat region is aligned as a visible suffix of the authoritative Timeline. Lines that align are old messages; lines that do not align become new candidates.

New candidates are not translated immediately. They wait for multi-frame consensus: two consistent observations, or near-equivalent Korean jamo observations, are enough to queue translation. This absorbs light OneOCR jitter around short Korean text, spacing, and visually similar characters.

The capture loop uses pixel-diff patrol: stable frames only compute a cheap screenshot signature and do not run OCR. When the chat region changes, the app runs a short OCR burst. Completed translations are rendered by Timeline `Seq`, so retries and network latency do not reorder the overlay.

### Developer Commands

End users do not need to run these commands. They are for development, validation, and tester package preparation. All paths are repository-relative so they can be reused on other machines or in CI.

```powershell
dotnet build OwTranslateLite.csproj -c Release
```

Publish a tester package:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/PackageRelease.ps1
```

Regression tests:

```powershell
dotnet run --project Tools/ReplayLab/ReplayLab.csproj -c Release -- --timeline-smoke
dotnet run --project Tools/ReplayLab/ReplayLab.csproj -c Release -- --similarity Tools/ReplayLab/similarity/korean-jamo-regression.json
dotnet run --project Tools/ReplayLab/ReplayLab.csproj -c Release -- Tools/ReplayLab/fixtures/smoke-korean-short Tools/ReplayLab/fixtures/smoke-korean-short/expected.json
dotnet build OwTranslateLite.csproj -c Release
```

Expected fixture metrics:

```text
missing=0, duplicates=0, outOfOrder=0, extra=0
```

### Project Layout

```text
Core/                  settings, Timeline, parsing, diagnostics, coordination
Ocr/                   OneOCR wrapper and OW image preprocessing
Overlay/               topmost translation overlay
Translation/           OpenAI-compatible / DeepSeek requests
Resources/             OW glossary, theme resources, UI icons, guide screenshots
Tools/ReplayLab/       offline replay and regression assertions
Tools/OcrPreprocessLab/ OCR preprocessing lab
Docs/                  architecture, test guides, refactor notes
```

### Maintainer Notes

- [Architecture](Docs/ARCHITECTURE.md)
- [Timeline Refactor Update](Docs/TimelineRefactor-Update-20260613.md)
- [Replay Case Recording Guide](Docs/ReplayCaseRecordingGuide-20260612.md)
- [Publish Layout](Docs/PublishLayout.md)
