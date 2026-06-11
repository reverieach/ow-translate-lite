# OW Translator Lite - AGENTS 维护指南

本文档写给后续接手本项目的 AI agent/维护者。目标是避免重复摸索、避免把项目改回通用翻译器，并保留当前 beta 测试阶段的关键约束。

## 项目定位

- 本项目是基于 `thanhkeke97/RSTGameTranslation` 思路重做的 OW 专用实时竞技翻译工具。
- 当前目标不是完整通用翻译器，而是面向《守望先锋》外服对局聊天的轻量 Windows OCR 翻译 overlay。
- 默认目标语言固定为简体中文。
- 当前重点语言只维护英语、日语、韩语；其他语言不要主动扩展，除非用户明确要求。
- 语音、TTS、漫画模式、多 OCR 服务、多翻译商、泛用剪贴板监听翻译等通用功能已裁剪，不要随手加回。
- `Local Rules` 已移除，不要再把离线规则翻译作为产品路径加回。

## 当前技术栈

- UI：WPF / .NET 9 / Windows x64。
- 项目文件：`OwTranslateLite.csproj`。
- OCR：当前固定 OneOCR，WinOCR 不可用，不要作为默认路径恢复。
- 当前 OneOCR 本地接口不支持强制指定 EN/JA/KO 识别语言；UI 固定为自动识别。
- 翻译：
  - `DeepSeek` 和 `OpenAI Compatible` 走 OpenAI-compatible chat completions。
- Overlay：独立 WPF 窗口，支持透明背景、鼠标穿透、拖动、调整大小、滚动历史。
- 回话助手：overlay 底部可输入中文，翻译为英语/日语/韩语并复制到剪贴板；不自动发送游戏聊天。
- 用户数据目录：`%AppData%\OWTranslatorLite`。

## 重要目录

- `Core/`：设置、术语表、消息解析、去重、翻译协调逻辑。
- `Ocr/`：截图、OCR 引擎和 OW 聊天图像预处理。
- `Overlay/`：翻译 overlay 窗口。
- `Translation/`：OpenAI-compatible API 请求、模型列表获取。
- `Resources/OwGlossary.zh-CN.json`：OW 术语表。
- `Docs/`：架构、测试说明、历史决策。
- `Tools/OcrPreprocessLab/`：本地 OCR 预处理对比实验工具。
- `Tools/GlossaryValidator/`：词库 JSON、重复 alias、短 alias 风险检查工具。
- `captured-screenshots/`：本地 OCR 样本采集输出，已在 `.gitignore`，不要提交。
- `dist/`：发布产物，已在 `.gitignore`，不要提交。
- `app/`：本地 build 输出，已在 `.gitignore`，不要提交。

## 术语表维护规则

- 术语表文件：`Resources/OwGlossary.zh-CN.json`。
- 当前词库版本：`2026.06.08-beta3-glossary-v3`。
- 当前覆盖规模：约 302 个 entries、1672 个 terms/aliases。
- 术语表主要服务 API prompt 的术语命中和中文锁定。
- 只维护 OW 专用内容：
  - 英雄名、英雄简称和玩家常用叫法。
  - 技能、大招、常见技能简称和“技能交了/没技能”类 callout。
  - 地图、模式、角色、阵容类型。
  - 英语、日语、韩语竞技聊天常见短句。
  - 稳定的 OW 玩家黑话，例如 C9、stagger、peel、poke、dive、brawl、nano blade、Pharmercy 等。
- 不要把普通自然语言词典塞进术语表。普通聊天翻译应交给 DeepSeek/OpenAI-compatible API。
- 不要根据单次 OCR 测试样例硬编码玩家名或错字。只有 OW 术语、技能名、地图名、稳定社区叫法适合进入词库。
- 新增 alias 时要避免过短、过泛的词污染翻译。例如 `one`、`push`、`tp`、`fade` 这类词必须接受上下文歧义，必要时宁可不加。
- 新增词库后必须至少做：
  - JSON 解析校验。
  - 重复 alias 检查。
  - `dotnet build OwTranslateLite.csproj -c Release`。
- 推荐来源优先级：
  1. Blizzard 官方 OW 英雄、地图、模式页面。
  2. Overwatch Wiki/Fandom 的英雄、技能、地图、术语页面。
  3. Reddit、OverwatchUniversity、论坛、玩家社区中的高频通用叫法。
  4. 用户 beta 测试日志中反复出现、且明显不是个例的稳定叫法。
- 维护词库时要同时考虑英语、日语、韩语。当前不主动扩展俄语、法语、德语、西语等语言。

## 当前核心逻辑

### OCR 与消息解析

- 框选区域应完整包含 OW 左侧聊天文本框里的 `[玩家名]：正文`。
- 系统提示通常没有 `[player]：` 格式，应和玩家消息分开处理。
- OW 聊天会自动消失，但打开聊天窗口可看到历史；overlay 历史不因 OCR 暂时无字而清空。
- 当前 OneOCR 使用自动识别；项目没有可调用的 EN/JA/KO 强制识别接口。
- 当前主线 OCR 预处理是单一路径：保留颜色、2x 放大、轻微对比/亮度/gamma 增强、轻锐化。
- 2026-06-09 对约 91 张实战截图采样与 lab 对比后，已移除 `OcrPreprocessingMode` 和青色/多色 mask 主线路径；不要在没有新实验报告支撑时把 mask variant 加回主流程。
- `Tools/OcrPreprocessLab` 仍可比较 `ColorPreserving`、灰度 baseline、无锐化和参数 sweep；实验报告输出到 `Docs/ocr-lab-output/`，不要提交。

### 去重策略

- 维护有顺序的聊天消息列表，严格以玩家单条消息为翻译单元。
- 以 `[玩家名]：text` 作为玩家消息边界，不把同一个玩家连续多句话合成一条。
- 通过有序锚点和相似文本判断新增消息，抵抗 OCR 把同一行切块或轻微识别错误。
- 翻译请求有异步队列和上限，网络高延迟时会丢弃过旧队列，避免无限堆积。
- Overlay 最多保留最近 50 条翻译记录，方便用户滚动查看，不永久保存。
- 去重应依赖通用相似度、玩家名模糊匹配和前后顺序锚点；不要把普通韩语/日语/英语聊天 OCR 错字按测试样例硬编码进纠错表。
- 只有 OW 英雄、地图、技能、模式、固定竞技短句等稳定术语适合做 alias/术语表归一化。

### Overlay 行为

- 点击“暂停”应隐藏 overlay，但不清空历史。
- OCR 没文字时，overlay 可在翻译完成后继续显示约 5-6 秒，然后隐藏；隐藏不等于清空。
- 用户拖动或调整 overlay 大小后，应自动保存位置和尺寸。
- 透明度控制的是黑色背景透明度，文字必须保持清晰。
- 鼠标穿透关闭后，用户应能拖动、调整大小和滚动 overlay。

## Beta 测试模块

当前 beta 版程序内有“Beta 测试”入口：

- 打开数据目录。
- 打开日志。
- 导出诊断。
- 清除本机数据。
- 翻译时保存原始截图（本地 OCR 样本采集）。

这些入口只为小范围测试排错服务，完整版可以移除。导出诊断必须脱敏 API Key，只能记录是否已配置。

相关文件：

- `settings.json`：用户设置；API Key 通过 Windows DPAPI 保存为 `apiKeyProtected`，不要记录或分发明文。
- `runtime.log`：程序内运行日志。
- `crash.log`：未捕获异常日志。
- `diagnostics-*.txt`：用户点击导出诊断生成的脱敏诊断文件。
- `captured-screenshots/`：开发环境中启用“翻译时保存原始截图”后生成的原始截图样本；当前路径通过向上查找 `OwTranslateLite.csproj` 定位仓库根目录，发布包若要收集测试者样本，需要先把保存位置改到用户数据目录或可配置路径。

## API 与模型

- DeepSeek 默认 API URL：`https://api.deepseek.com`。
- 当前默认模型：`deepseek-v4-flash`。
- 模型下拉框应优先通过“获取模型”向 `/models` 获取，降低用户手填错误。
- 如果 DeepSeek 文档或模型名可能变化，必须联网查最新官方文档后再更新默认值。
- API Key 不应写入诊断文件、日志或崩溃日志。

## 构建与发布

推荐使用仓库内的本地 SDK：

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe build OwTranslateLite.csproj -c Release
```

发布 beta 包：

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe publish OwTranslateLite.csproj -c Release -o E:\rstgametranslation\ow-translate-lite\dist\OWTranslatorLite-vX.Y.Z-portable-win-x64
```

发布后将对应 `Docs/BetaTest-*.md` 复制为包内 `README-BETA.md`，再压缩 `dist` 下的发布目录。

注意：

- 不要每次小修都打包，用户要求确认测试完成后再最终打包时再做。
- 如果是给测试者的新 beta，包名要带清晰版本号。
- 自包含发布会带很多 .NET runtime DLL，这是正常的；不要手删不认识的 DLL，也不要随意移动 .NET host 依赖。OneOCR、Resources、UI 候选资源保持分目录。
- 词库、文档、日志类小改默认只 build 验证，不自动 publish/zip，除非用户明确要求发布 beta 包。

## Git 维护约定

- 修改前先 `git status --short`。
- 工作树若已有用户改动，不要回滚。
- 每组完成的改动可做本地 commit，作为恢复点。
- 不要 push、pull、加 remote，除非用户明确要求。
- `dist/`、`app/`、`obj/` 不应提交。
- `captured-screenshots/`、`ow-screenshot/`、`Docs/ocr-lab-output/` 是本地 OCR 实验数据，不应提交。
- 文档和词库变更也要纳入 git 管理；完成后确认 `git status --short` 干净。

## 常见风险

- 新机器选择语言或模型闪退：优先看 `crash.log` 和 overlay 坐标是否为 NaN/Infinity。
- 翻译重复：优先检查 OCR 是否把同一行切块、玩家名是否被识别变化、锚点匹配是否失效。
- 韩语/日语识别差：优先检查框选区域、当前 `ColorPreserving` 预处理和截图样本，不要承诺 OneOCR 可强制指定识别语言，也不要按单个样例硬编码普通聊天纠错。
- 翻译不动：检查 API URL、API Key、模型、请求超时、网络延迟和队列是否积压。
- Overlay 位置不保存：检查 `OverlayLeft/Top/Width/Height` 是否写入 `settings.json`，以及应用设置时是否触发了错误保存。

## 后续优先级

1. 收集 beta 诊断日志和 `captured-screenshots/` 样本，优先解决测试者机器上的闪退、无响应、重复/漏翻和 OCR 识别不稳。
2. 继续增强 OCR 切块合并和有序锚点去重，优先使用通用相似度与实测样本验证。
3. 继续验证 API Key DPAPI 迁移和异常恢复路径。
4. 维护英语、日语、韩语 OW 术语和常见聊天表达。
5. 暂不引入大体积本地翻译模型；竞技实时体验优先使用 DeepSeek/API + 术语表 + 缓存。
6. beta 稳定后裁掉或隐藏测试入口，整理发布包结构，做正式版本说明。
