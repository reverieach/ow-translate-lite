# Timeline 对齐重构执行计划（2026-06-12）

> 本文档为定稿执行计划，供执行 Agent 直接落地。方案已经过问题分析、代码验证与多轮讨论，
> 执行时不要重新设计方案、不要质疑已确定的架构决策。

# 一、最终目标

为 OW 专用实时聊天翻译器达成以下体验标准（按优先级排序）：

1. **不漏翻**：所有被 OCR 捕获的玩家消息（含多人陆续发言、打开聊天历史时一次出现的多条消息）都进入翻译流程，不被去重逻辑误吞、不被队列丢弃。
2. **不乱序**：overlay 显示顺序与游戏内消息出现顺序一致，不受翻译完成先后、OCR 帧序、失败重试影响。
3. **不重复**：同一条消息因 OCR 帧间字符抖动（OneOCR 对同一句在相邻帧识别出不同字符）不产生重复翻译条目。
4. **韩语优先优化**：相似度匹配、消息身份判定、送翻文本质量全部针对韩语特性（空格不可靠、jamo 级 OCR 错误）优化。
5. **延迟尽量短**：在保证 1–3 的前提下越短越好；可接受为换取文本质量和顺序稳定而引入的 1–2 个确认帧（数百 ms 级）延迟。
6. **聊天历史及时显示**：打开聊天历史时 overlay 立即展示已有译文，无需特殊触发条件。
7. **不引回通用功能**：不恢复 WinOCR、多翻译商 UI、TTS、Whisper、Local Rules；OCR 固定 OneOCR 自动识别。

# 二、当前已确定的问题与结论

以下问题已通过实际代码阅读确认（文件与行号已核实，执行 Agent 仍应在改动前重新确认行号，文件可能已变动）：

| # | 问题 | 位置 | 结论 |
|---|------|------|------|
| 1 | 短文本相似度死区：`TextSimilarityScore` 在任一文本长度 < 8 时直接返回 0，短消息（gg、ㄱㄱ 等）永远无法模糊匹配，导致帧间匹配失败、anchor 失效、重复与漏翻放大 | `Core/OcrDedupeNormalizer.cs:46` | 用 jamo 级距离替代，消灭死区 |
| 2 | `_previousVisibleMessages` 每帧整体替换，一帧 OCR 坏帧即污染状态，下一帧 anchor 必失败 → 走 tail-2 截断 → 漏翻 | `Core/TranslationCoordinator.cs:347-351` | 改为权威 Timeline + 序列对齐，坏帧不污染状态 |
| 3 | 无 anchor 时只取最后 2 条（`MaxTailMessagesWithoutAnchor = 2`），多人陆续发言或历史打开时漏掉中间消息 | `Core/TranslationCoordinator.cs:26, 223-251` | 对齐后尾部新增不限条数 |
| 4 | anchor 取"全局最高分单点匹配"而非序列对齐，多人发言时锚错位置导致整帧 diff 错位 | `Core/TranslationCoordinator.cs:253-279` | 改为全局序列对齐（Needleman-Wunsch 类） |
| 5 | 显示层第二套去重 `IsDisplayDuplicate`（90 秒窗口 + 模糊匹配）静默吞掉合法消息，且 dedupe.log 不可见 | `MainWindow.xaml.cs:843-852, 21` | 删除；overlay 按 Seq 排序回填后重复不可能产生 |
| 6 | 翻译成功才写入 recent 记忆，API 失败后消息被重新检测、重新排队到更晚位置 → 乱序 | `Core/TranslationCoordinator.cs:182-184` | 消息持有固定 Seq，失败显式重试，保住原位置 |
| 7 | history peek 依赖 `chatJustBecameVisible` 边沿检测 + `newLines.Count == 0` + `_records.Count > 0` 三重条件，脆弱 | `MainWindow.xaml.cs:590-605, 1253-1272` | 改为电平触发 + 迟滞；history peek 特殊路径整体删除 |
| 8 | 队列上限 30 超出直接丢旧消息 → 漏翻 | `MainWindow.xaml.cs:24, 668-671` | 改为合批翻译，不丢消息 |
| 9 | 延迟叠加：900ms 采样 + 120ms 盲等批窗口 + 串行 worker | `MainWindow.xaml.cs:20, 645, 792` | 等待挪到检测端（确认帧），删除盲等窗口；采样改为帧哈希门控 + 突发采样 |
| 10 | 韩语空格不可靠（玩家打字不规范 + OCR 断句错误），token 级相似度对韩语失效 | `Core/OcrDedupeNormalizer.cs`（TokenOverlapRatio 等依赖空格分词） | 身份匹配一律去空格；相似度在 jamo 层级计算 |
| 11 | OneOCR 帧间字符抖动（同句相邻帧识别出不同字符）在当前架构下导致重复翻译 | 架构性问题 | 身份由位置对齐决定，文本只是证据；多帧变体投票产出共识文本 |

**已确定的设计决策（不再讨论）：**

- D1：消息身份由**序列对齐的位置**决定，不由内容判重决定。聊天区视为 append-only 日志，每帧 OCR 是有噪声观测。
- D2：相似度算法只降级为"对齐打分函数"，现有 `OcrDedupeNormalizer` 的多算法融合保留复用，但补 jamo 级计算、去空格化。
- D3：判重策略"宁可放过"：同人同句重复在实际场景罕见，误放代价远小于漏翻代价。TTL 缓存群（`_seenInCurrentChatCycle`、`_recentDedupeCache`、`_recentMessageSnapshots`、pending 模糊匹配、display 层去重）全部删除。
- D4：新消息检测后等多帧共识再入队（自适应：连续两帧一致即发车，分歧才多等一帧），用确认窗口换送翻文本质量和顺序稳定。
- D5：采样改为"低频像素 diff 巡逻（约 300ms）+ 检测到变化后突发提频 OCR（250–300ms 间隔连续 2–3 帧）"，使确认帧成本约 300ms 而非 900ms。
- D6：jamo 混淆代价矩阵用公开数据起步（可后置），初版只需 NFD 分解 + 普通 jamo 编辑距离即可解决大部分问题；自有语料校准是长期项。
- D7：翻译侧维持单 worker + 批量请求（共识窗口天然汇齐批次），不引入并发。
- D8：overlay 可见性电平触发，连续 2 帧无聊天才隐藏。历史打开时旧消息对齐到 Timeline 已有记录 → 译文已存在则直接显示，零 API 调用。
- D9：回放测试床是一切重构的前置条件，没有它不允许动检测逻辑。
- D10（OW 聊天生命周期约束）：OW 聊天可见区永远是 append-only 日志的一个**连续后缀**——消息按到达顺序底部追加、从最老开始淡出、不存在"中间被抽掉一条"（OCR 漏识别属噪声层另算）。对齐器必须利用该不变量做**后缀对齐**而非通用任意编辑对齐，缩小搜索空间。淡化是正常生命周期，**不清 Timeline**；旧的"聊天周期"概念（`ChatHiddenReset`/`ResetChatCycle` 清状态）整体删除，Timeline 只按自身容量/时间策略淘汰。
- D11（空帧后防误吸收）：完全淡化（零可见行帧）之后，若新帧仅 1–2 条可见行 → **强制按尾部新增**（分配新 Seq），禁止被文本相似度对齐吸收到旧记录（防止"淡化后再发同样短句"被判旧消息而漏翻）；若新帧 ≥3 条可见行（阈值在 replay 上调）→ 走正常后缀对齐（即开历史路径）。误翻一条真重复可接受，误吸收导致漏翻不可接受（与 D3 方向一致）。
- D12（系统消息中文判据，用户确认的前提）：玩家不会用中文发言；系统消息除嵌入的玩家名外全为中文。`OwChatParser.ShouldSkipMessage` 的中文占比阈值升级为**主导文字系统分类**：消息体含韩文/假名占主导 → 玩家消息；中文占主导且不含任何韩文 → 排除；**含任何韩文字符即偏向玩家消息**（漏翻代价远高于误翻一条系统提示）。通过 `[名字]:` 结构门但消息体中文占主导的行，按 OCR 拼错假阳性直接排除。
- D13（冷启动与退化输入）：Timeline 为空的首帧**全收**所有可见行为新消息（与开历史行为一致，不做 tail 限制）；对齐器必须对 1 对 1、1 对 N、N 对 1 等退化输入有定义良好的行为并有单测覆盖（单行帧无邻居上下文，完全依赖 jamo 级相似度，是最脆弱路径）。

# 三、整体实施原则

1. **测试床先行**：任何检测/去重逻辑改动前，必须先有离线回放工具和 golden case，否则只能靠游戏内体感验证，禁止。
2. **小步提交**：每个 Git 提交只含一类修改，可独立 revert。
3. **不虚构接口**：本计划中标注"执行 Agent 需先检查确认"的项目，必须先用 Read/Grep 确认实际代码后再动手。
4. **复用优先**：`OcrDedupeNormalizer` 的相似度算法、`OwChatParser`、`OcrTextPostProcessor`、`OpenAICompatibleTranslationProvider` 原则上不重写，只做接口适配和韩语补强。
5. **删除而非兼容**：被替代的旧机制（TTL 缓存群、history peek 特例、display 去重）直接删除，不留开关、不留死代码。回滚靠 Git revert，不靠 feature flag。
6. **诊断不退化**：dedupe.log 在新模型下要继续输出（对齐结果、变体归并、发车决策），保证问题可追溯。
7. **遵守项目约束**：改动前阅读 `AGENTS.md` 与 `Docs/ARCHITECTURE.md`，完成后同步更新 `Docs/ARCHITECTURE.md`。
8. **韩语样本驱动**：所有匹配阈值（对齐分数、jamo 距离、确认帧数）的初值在 replay 测试床上用真实韩语样本调出，不拍脑袋定死。

# 四、分阶段执行路线图

```
阶段 0（P0）回放测试床            —— 必须最先，所有后续阶段的验证依据
   T0.1 帧序列录制能力
   T0.2 ReplayLab 离线回放工具
   T0.3 录制 golden cases
        │
阶段 1（P0）韩语匹配基建          —— 可与阶段 0 并行开发，验证依赖阶段 0
   T1.1 jamo 分解 + 去空格相似度
   T1.2 replay 回归验证
   T1.3 parser 主导文字系统分类（D12）
        │
阶段 2（P0/P1）Timeline 对齐重写   —— 依赖阶段 0 + 阶段 1
   T2.1 ChatTimeline 数据模型
   T2.2 序列对齐检测器
   T2.3 接入主循环、删除旧判重机制群
   T2.4 overlay 按 Seq 回填、删除 display 去重
        │
阶段 3（P1）采样与发车优化        —— 依赖阶段 2
   T3.1 帧哈希门控 + 突发采样
   T3.2 多帧共识 + 自适应发车
   T3.3 队列溢出合批 + 失败重试保 Seq
        │
阶段 4（P1）Overlay 行为简化      —— 依赖阶段 2，可与阶段 3 并行
   T4.1 电平触发可见性 + 删除 history peek 特例
        │
阶段 5（P2）韩语深化              —— 依赖阶段 1/2，时间上可灵活安排
   T5.1 韩语折行合并规则校验
   T5.2 公开数据 jamo 混淆代价矩阵
   T5.3 文档更新（ARCHITECTURE.md 等）
        │
阶段 6（P3）长期演进              —— 见第十节
```

# 五、逐项详细执行步骤

## 阶段 0：回放测试床（P0）

**阶段目标**：建立"录制游戏帧序列 → 离线重跑 OCR 后链路 → 自动断言不漏/不重/不乱序"的闭环。
**为什么**：检测逻辑的重写没有回归手段就等于盲改；现有 `captured-screenshots/` 只在翻译触发时存单张图，不能复现帧间时序问题（决策 D9）。

### T0.1 帧序列录制能力

- **做什么**：在主循环中增加一个录制模式：开启后按采样节奏把每帧的截图（PNG）+ 该帧 OCR 原始输出 + 时间戳落盘到一个会话目录（如 `captured-screenshots/sessions/<时间戳>/`），形成可回放的帧序列。
- **执行前检查**：
  - 确认现有截图保存路径的实现方式（`TranslationCoordinator.cs` 中 `ScreenshotSaveDirectory` 的写入点，约 139–151 行）；
  - 确认设置项 `SaveScreenshotsOnTranslation` 在 `Core/AppSettings.cs` 中的实际字段名（**执行 Agent 需先确认**，用 Grep 搜 `SaveScreenshot`）；
  - 确认 `OcrTextLine`（`Core/OcrTextLine.cs`）包含哪些字段（是否有坐标/包围盒），决定录制时序列化哪些内容（**执行 Agent 需先确认**）。
- **如何修改**：录制点放在 `DetectNewLinesAsync` 内拿到 `ocrLines`（后处理前）之后；每帧序列化为一个 JSON（含原始 OCR 行、后处理结果、解析结果、时间戳）+ 对应 PNG。新增独立设置项控制开关，默认关闭。注意磁盘占用：录制模式下给出会话大小提示。
- **如何测试**：开启录制跑 1–2 分钟游戏，检查会话目录里帧文件连续、JSON 可解析、PNG 可打开。
- **完成标准**：能产出一个包含 ≥50 帧的完整会话目录，每帧图像与 OCR 数据一一对应。

### T0.2 ReplayLab 离线回放工具

- **做什么**：在 `Tools/` 下新建 ReplayLab（形态参照 `Tools/OcrPreprocessLab`），输入 T0.1 的会话目录，离线重跑「OCR 后处理 → 解析 → 新消息判定」全链路，输出每帧判定 trace（接受/丢弃/原因），并支持断言脚本：给定期望消息清单，自动判定漏翻数 / 重复数 / 乱序数。
- **执行前检查**：
  - 阅读 `Tools/OcrPreprocessLab` 的项目结构与 csproj 引用方式（**执行 Agent 需先确认**它如何引用主工程代码——是项目引用还是源码链接）；
  - 确认 `TranslationCoordinator` 的检测逻辑能否在不截图、不调 OCR 的情况下被驱动（当前 `DetectNewLinesAsync` 把截图、OCR、检测耦合在一个方法里，约 87–154 行）。若耦合，需要先做最小拆分：把"从 `ParsedChatLine` 列表到新消息判定"抽成可独立调用的方法。这个拆分属于本任务，且是阶段 2 重写的天然接缝。
- **如何修改**：回放时跳过截图与 OCR（直接用录制的 OCR 原始行），从后处理开始重跑；翻译阶段用假 provider（直接回显原文），不调真实 API。
- **如何测试**：用 T0.1 录的会话跑一遍，trace 输出与当时 dedupe.log 行为一致。
- **完成标准**：对同一会话，回放结果可复现、可断言；修改检测代码后重跑能看出行为差异。

### T0.3 录制 golden cases

- **做什么**：用录制模式采集 7 类真实场景帧序列并人工标注期望输出（该场景应翻译哪些消息、什么顺序）：
  1. 韩语普通新消息（单条、短句为主，含"冷启动后第一条短消息"序列——D13 的最脆弱路径）；
  2. 多人陆续发言（≥3 人在数秒内连续发言）；
  3. 打开聊天历史（一次出现多条旧消息）；
  4. OCR 字符抖动（同一句在相邻帧识别不同——录长一点自然会出现）；
  5. 韩语空格抖动（同句帧间空格位置变化）；
  6. 系统提示与玩家消息交错（含间歇性误分类帧），断言玩家消息零漏、系统消息零误翻（或可接受的误翻上限）——验证 D12；
  7. 完全淡化 → 再次发言（含与 Timeline 旧记录文本相同/相似的短句），断言必须作为新消息重新上屏——验证 D11。
- **执行前检查**：T0.1、T0.2 已完成。
- **如何测试**：每个 case 在当前线上逻辑下跑回放，记录基线指标（漏/重/乱序数）——预期能直接看到 tail-2 截断和死区问题的实锤。
- **完成标准**：7 个 case 入库（建议 `Tools/ReplayLab/fixtures/` 或类似位置），含期望输出标注；基线指标记录在案，作为后续所有阶段的对比基准。
- **潜在风险**：录制涉及游戏对局，部分场景（开历史、多人发言）不易随时复现 → 可先入库已有场景，缺的场景标注为待补，不阻塞阶段 1/2 开发（开发期用合成帧序列顶替，发布前用真实 case 验收）。

## 阶段 1：韩语匹配基建（P0）

**阶段目标**：相似度计算去空格化 + jamo 级编辑距离，消灭 <8 死区。
**为什么**：这是对齐模型的打分地基（决策 D2、结论 10/11）；且独立于重写，可作为热修先行收益。

### T1.1 jamo 分解与去空格相似度

- **做什么**：
  1. 新增韩语文本工具（建议 `Core/` 下新文件，如 KoreanJamoNormalizer）：对含韩文音节的文本做 NFD 风格分解（初声/中声/终声拆为 jamo 序列；可用 .NET `string.Normalize(NormalizationForm.FormD)` 或自实现 Unicode 算法——**执行 Agent 需先验证** .NET 对 Hangul 音节的 NFD 分解行为是否符合预期，写个小验证程序确认 `각` → `ㄱ+ㅏ+ㄱ` 类输出）；
  2. 修改 `OcrDedupeNormalizer.TextSimilarityScore`：
     - 比较前一律去空格（现有 `RemoveSpaces` 已存在，扩大适用范围；依赖空格分词的 `TokenOverlapRatio` 对韩语文本降权或跳过）；
     - 含韩文时在 jamo 序列上计算编辑距离/LCS；
     - 删除 `left.Length < 8 || right.Length < 8 → return 0` 死区（`OcrDedupeNormalizer.cs:46`）：短文本改用 jamo 级归一化编辑距离，阈值收紧（初值建议 ≥0.85 判同，最终在 replay 上调）；
  3. `IsSpeakerMatch` 同样消除对短 ID 的硬性拒绝（当前 `limit < 5` 直接 false，韩文 ID 2–3 个音节很常见——jamo 分解后长度足够）。
- **执行前检查**：Grep 所有调用 `TextSimilarityScore` / `IsSimilarText` / `IsSpeakerMatch` 的位置（已知：TranslationCoordinator、MainWindow 的 IsDisplayDuplicate；**执行 Agent 需确认是否还有其他调用方**），评估阈值变化的波及面。
- **修改原则**：纯函数、无状态；非韩语文本走原逻辑，行为尽量不回归。
- **如何测试**：
  - 单元测试级：构造韩语对照组（同句不同空格、同句单 jamo 错、真正不同的两句、2–3 字短句抖动），断言相似度区分度拉开；
  - 回放级：golden case 1/4/5 重跑，重复判定数对比基线下降，无新增漏翻。
- **完成标准**：死区消除；韩语空格抖动、单字符抖动样本判同率显著提升；replay 基线不回归。
- **风险与回滚**：阈值放松可能在旧 anchor 机制下引入新的误判 → 本任务与阶段 2 间隔期间，仅作为独立提交存在，发现问题 revert 单提交即可。

### T1.2 replay 回归验证

- **做什么**：把 T1.1 的对照组固化为 ReplayLab 可自动跑的断言集，纳入每次提交后的固定回归流程。
- **完成标准**：一条命令跑完全部 golden case + 韩语对照组并输出指标报告。

### T1.3 parser 主导文字系统分类（D12）

- **做什么**：升级 `Core/OwChatParser.cs` 的 `ShouldSkipMessage`（当前约 61–80 行）：
  1. 把"中文字符数 ≥ max(2, 长度/3)"的占比阈值改为**主导文字系统分类**：统计消息体中韩文（가-힣）、假名、拉丁、中日韩统一表意（中文）四类字符数；
  2. 规则：含韩文/假名占主导 → 玩家消息；中文占主导且**不含任何韩文** → 系统消息，排除；**含任何韩文字符即偏向玩家消息**（误判代价不对称：漏翻 >> 误翻一条系统提示）；
  3. 通过 `[名字]:` 结构门但消息体中文占主导的行，按 OCR 拼错产生的假阳性直接排除（前提：玩家不会用中文发言，已由用户确认）。
- **为什么**：当前阈值对短韩语消息太敏感——OneOCR 偶尔把笔画密的 Hangul 错认成形近汉字，6 字韩语短句混入 2 个错认汉字即触发跳过 → **持续性漏翻**，这是对齐层无法挽救的 parser 层问题（间歇性误判对齐层可免疫，持续性不行）。
- **执行前检查**：读 `OwChatParser.cs` 确认 `ShouldSkipMessage` 当前实现与正则（**行号以实际代码为准**）；确认 speaker 已在结构门被剥离、不参与消息体判断。
- **修改原则**：纯函数改动，单测覆盖：韩语短句混错认汉字（应保留）、纯中文系统提示（应排除）、中文+玩家名拼错形态（应排除）、英文/日文消息（应保留）。
- **如何测试**：单测 + golden case 6 回放，断言玩家消息零漏。
- **完成标准**：韩语混错认汉字样本不再被跳过；系统提示排除率不回归。
- **回滚**：独立提交，revert 即回旧阈值。

## 阶段 2：Timeline 对齐重写（P0/P1）

**阶段目标**：用「权威 Timeline + 每帧序列对齐」替代「帧间 diff + anchor + TTL 判重」，一举解决漏翻、乱序、抖动重复三类问题。
**为什么**：结论 2/3/4/5/6/11，决策 D1/D3。

### T2.1 ChatTimeline 数据模型（P0）

- **做什么**：新建数据模型（建议 `Core/ChatTimeline.cs` 或类似命名）：
  - `ChatMessage`：`Seq`（单调递增 long，唯一显示顺序依据）、`Speaker`、`ConsensusText`、`Variants`（各帧 OCR 变体列表）、`SeenCount`、`LastSeenFrameId`、`State`（Detected / Confirming / Queued / Translating / Translated / Failed(retryCount)）、`Translation`；
  - `ChatTimeline`：保序消息列表，保留最近约 100 条，按时间/数量淘汰；提供"尾部窗口（最近 ~15 条）"访问接口供对齐使用。
- **修改原则**：纯数据结构 + 状态机，不含 OCR/翻译逻辑，可独立单测。
- **完成标准**：模型可被 ReplayLab 引用并单测覆盖状态转换。

### T2.2 序列对齐检测器（P0）

- **做什么**：实现核心算法：输入「本帧有序可见行列表」与「Timeline 尾部窗口」，做**后缀对齐**（利用 D10 不变量：OW 可见区永远是 append-only 日志的连续后缀，不需要通用任意编辑对齐——只需找"可见行对齐到 Timeline 尾部的哪一段后缀 + 尾部多出几条新消息"；动态规划打分：匹配分 = T1.1 后的 `TextSimilarityScore` + speaker 匹配加成，gap penalty 初值在 replay 上调）。输出三类结果：
  - **匹配对**：可见行 ↔ 已有消息 → 追加 Variant、`SeenCount++`；
  - **尾部新增**：对齐后缀上多出的可见行 → 新消息，分配 Seq，**不限条数**；
  - **中间插入/缺失**：判为 OCR 噪声，按相似度归并到邻近已有消息或忽略，不产生新消息、不修改 Timeline 结构。
  - 整帧对齐质量过低（匹配率低于阈值）时判为坏帧，丢弃本帧、Timeline 不动（替代旧的"整体替换"，解决结论 2）。
- **必须实现的特殊规则**：
  - **冷启动（D13）**：Timeline 为空的首帧全收所有可见行为新消息，不做 tail 限制；
  - **退化输入（D13）**：1 对 1、1 对 N、N 对 1 输入下行为定义良好并有单测——单行帧无邻居上下文、完全依赖 jamo 级相似度，是最脆弱路径；
  - **空帧后防误吸收（D11）**：上一有效帧为零可见行时，新帧仅 1–2 条可见行 → 强制按尾部新增（分配新 Seq），禁止被相似度对齐吸收到旧记录；新帧 ≥3 条可见行（阈值在 replay 上调）→ 走正常后缀对齐（开历史路径）。
- **执行前检查**：确认 `ParsedChatLine` 的字段结构（**执行 Agent 需读 `Core/OwChatParser.cs` 确认**），以及 OCR 行是否携带几何信息可作对齐辅助证据。
- **如何测试**：ReplayLab 上跑全部 golden case：case 2（多人陆续发言）零漏翻、顺序正确；case 4（字符抖动）零重复；case 3（开历史）多条消息全部进入新消息列表；case 7（淡化后同文本再发言）必须重新上屏。
- **完成标准**：7 个 golden case 全部达到期望输出；坏帧注入测试（人工在序列中插入乱码帧）后 Timeline 不被污染、后续帧恢复正常；退化输入单测全绿。
- **潜在风险**：对齐窗口（~15 条）与聊天区可见行数不匹配时对齐质量下降 → 窗口大小做成内部常量并在 replay 上验证不同值。

### T2.3 接入主循环，删除旧判重机制群（P0）

- **做什么**：
  1. `TranslationCoordinator` 的 `DetectNewLinesAsync` 内部改为调用 T2.2 检测器；
  2. 删除：`FindNewLinesByVisibleOrder`、`TakeUnanchoredTail`、`FindBestAnchorIndex`、`GetAnchorScore`、`GetNeighborSupportScore`、`_previousVisibleMessages`、`_seenInCurrentChatCycle`、`_recentDedupeCache`、`_recentMessageSnapshots`、`_pendingMessageKeys`、`_pendingMessageSnapshots`、`GetDuplicateReason`、TTL 相关常量与清理逻辑（即 `TranslationCoordinator.cs` 的大部分私有状态）；
  3. 保留并改写 dedupe.log 输出：每帧记录对齐摘要（匹配 n 条 / 新增 m 条 / 噪声 k 条 / 坏帧判定）、每条新消息的 Seq 分配、变体归并事件；
  4. **删除聊天周期重置语义（D10，不做等价保留）**：淡化是 OW 正常生命周期，`ChatHiddenReset` 超时清状态、`ResetChatCycle` 清 Timeline 的旧语义整体删除——Timeline 只按自身容量/时间策略淘汰，跨淡化存活（这是开历史"零 API 直接显示"成立的前提）。`ResetChatCycle` 仅在用户显式重启/换区域等场景保留"清 Timeline"行为（**执行 Agent 需 Grep 其全部调用点逐一判断语义**）；
  5. **并入最低 1 帧确认**：新消息须至少被连续 2 帧观测到才入队（完整自适应发车仍在 T3.2）——消除 T2.3 至 T3.2 窗口期内"系统消息单帧闪现在尾部被误翻"的假阳性风险，成本极小。
- **执行前检查**：Grep `TranslationCoordinator` 全部公开成员的外部调用点（已知 MainWindow 多处：`DetectNewLinesAsync`、`TranslateAsync`、`ReleasePendingTranslations`、`ClearPendingTranslations`、`ResetChatCycle`、`HasVisibleChat`、`LastVisibleChatLines`、`ChatCycleJustReset`——**执行 Agent 需确认完整清单**），确保接缝处签名变更全部跟改。
- **如何测试**：全量 replay 回归 + 真实游戏 30 分钟试运行，对照 dedupe.log 抽查无误吞。
- **完成标准**：replay 全绿；游戏内体验验证多人发言不漏、抖动不重。
- **回滚**：本任务是最大单点改动，独立成一个提交；出问题 revert 该提交回到 anchor 旧逻辑（阶段 1 的相似度改进仍保留）。

### T2.4 overlay 按 Seq 回填，删除 display 去重（P1）

- **做什么**：
  1. `TranslationRecord` 增加 Seq（或改为引用 ChatMessage）；`AddTranslationRecords` 按 Seq 插入排序而非追加（`MainWindow.xaml.cs:813` 附近）；
  2. 删除 `IsDisplayDuplicate` 与 `DisplayDuplicateWindow`（`MainWindow.xaml.cs:843-852, 21`）——结论 5；
  3. 翻译失败的消息 `State = Failed(n)`，由 worker 在下一批显式重试（≤2 次），重试成功后按原 Seq 插入——结论 6。
- **执行前检查**：确认 `_records` 的数据结构与 `OverlayController.UpdateRecords` 的渲染假设（是否依赖追加序——**执行 Agent 需读 `Overlay/OverlayController.cs` 与 `OverlayWindow.xaml.cs` 确认**）。
- **如何测试**：replay 中人工让假 provider 对指定消息先失败后成功，断言最终显示顺序按 Seq；游戏内验证翻译先后完成不影响显示顺序。
- **完成标准**：乱序在 replay 与实测中均不可复现；失败消息可见重试且位置正确。

## 阶段 3：采样与发车优化（P1）

**阶段目标**：延迟尽量短 + 送翻文本质量提升（决策 D4/D5/D7，结论 8/9）。

### T3.1 帧哈希门控 + 突发采样（P1）

- **做什么**：
  1. 主循环节奏改为两档：**巡逻档**约 300ms 间隔只做聊天区廉价像素 diff（缩小图哈希或逐块差分，不跑 OCR）；**突发档**检测到变化后连续 2–3 帧、250–300ms 间隔跑完整 OCR，然后回落巡逻档；
  2. `CaptureIntervalMs` 设置语义相应调整（**执行 Agent 需检查** `RunLoopAsync` 的 delay 逻辑 `MainWindow.xaml.cs:645` 与设置 UI 的关联，决定是复用该设置为巡逻间隔还是新增设置项，倾向复用 + 文档说明）。
- **执行前检查**：确认 `ScreenCaptureService.Capture` 的成本与调用方式（**执行 Agent 需读 `Core/ScreenCaptureService.cs`**），像素 diff 在同一次截图上做，避免双截图。
- **如何测试**：空闲场景 CPU 占用不高于现状；出消息到首次 OCR 命中的延迟 ≤400ms（日志埋点测量）。
- **完成标准**：延迟埋点显示检测延迟从最差 ~900ms 降到 ~300ms 档；空闲 CPU 不升。
- **风险**：击杀播报、UI 动画导致像素 diff 频繁误触发 → diff 区域限定聊天区 + 突发档自带回落，最坏退化为接近现状的固定采样，可接受。

### T3.2 多帧共识 + 自适应发车（P1，依赖 T3.1 效果更佳但不强依赖）

- **做什么**（T2.3 已并入"最低 2 帧观测"的简化版，本任务将其扩展为完整自适应发车）：
  1. 新消息进入 `Confirming` 状态，等后续帧：**连续两帧文本一致（或 jamo 距离 ≤1）→ 立即发车**；两帧分歧 → 再等一帧做变体投票（多数票或 jamo 级逐位投票取共识）；最多等 2 个确认帧，超时按现有最佳变体发车；
  2. 发车时把确认窗口内汇齐的所有消息按 Seq 排序一次性入队（自然成批，替代 120ms 盲等）；
  3. 删除 `TranslationBatchWindow` 120ms 盲等（`MainWindow.xaml.cs:20, 792`）：worker 取队即发，批量上限沿用 `MaxTranslationBatchSize`（可调大，**执行 Agent 在 replay/实测中确认 provider 的批量 prompt 对 8 条的质量无回退后再调**）。
- **如何测试**：replay 的抖动 case 断言送翻文本为共识修正后文本；延迟埋点：消息出现 → 入队 ≤700ms（突发采样 2 帧）。
- **完成标准**：送翻文本质量（抽查共识修正命中）可见改善；端到端延迟不高于改造前；顺序在多人发言 case 下稳定。

### T3.3 队列溢出合批 + 不丢消息（P1）

- **做什么**：删除"超 30 丢旧"逻辑（`MainWindow.xaml.cs:668-671`），队列超长时改为允许 worker 一次取更大的合并批（30 条 ≈ 数次 API 调用）；保留一个高位硬上限（如 100）作最后防线，触发时在 overlay/日志显式提示"跳过 N 条"，不静默。
- **执行前检查**：确认 `OpenAICompatibleTranslationProvider` 批量 prompt 的构造方式与 token 上限承受力（**执行 Agent 需读 `Translation/OpenAICompatibleTranslationProvider.cs`**）。
- **完成标准**：爆发 30+ 条消息的 replay 合成 case 零丢弃。

## 阶段 4：Overlay 行为简化（P1，可与阶段 3 并行）

### T4.1 电平触发可见性 + 删除 history peek 特例

- **做什么**：
  1. overlay 可见性规则改为：`HasVisibleChat == true` → 显示；连续 2 帧 false（迟滞）→ 进入空闲倒计时隐藏。删除边沿检测 `chatJustBecameVisible`、`_wasChatVisibleLastTick`、`ShowOverlayForHistoryPeek`、`_historyPeekOverlayUntil`、`_overlayVisibleForHistoryPeek` 整组状态（`MainWindow.xaml.cs:590-605, 1253-1272` 及相关字段）；
  2. 打开历史时出现的旧消息走正常对齐：对齐到 Timeline 已有记录且已有译文 → 直接显示（零 API）；无译文 → 正常入队。无需任何特殊路径。
  3. 历史里可能出现 Timeline 已淘汰的更早消息 → 自然按新消息翻译，符合"不漏翻"目标，不做特殊处理。
- **执行前检查**：梳理 `MaybeHideOverlayAfterIdle` 与 `_overlayHiddenByIdle`、`OverlayIdleHideDelay` 的完整状态机（**执行 Agent 需读 `MainWindow.xaml.cs:1200-1272` 附近完整逻辑**），确保删除后空闲隐藏行为仍正确。
- **如何测试**：游戏内开/关聊天历史各 10 次，overlay 每次及时出现/按时隐藏；replay case 3 断言历史消息全部得到展示。
- **完成标准**：开历史 100% 触发显示；特例代码全部删除。

## 阶段 5：韩语深化与文档（P2）

### T5.1 韩语折行合并规则校验

- **做什么**：用韩语 golden case 检查 `Core/OcrTextPostProcessor.cs` 的 wrapped-line merge 是否存在按西文习惯（依赖行尾空格/连字符等文本信号）的判定；如有，改为几何信号优先（缩进、是否带 `[speaker]:` 前缀）。**执行 Agent 需先读该文件确认现状，不预设其实现方式。**
- **完成标准**：韩语折行样本在 replay 中合并正确率不低于现状，且无"两条消息被误合并"回归。

### T5.2 公开数据 jamo 混淆代价矩阵

- **做什么**：网络调研韩语 OCR 视觉混淆对的公开资料（学术论文、开源 OCR 项目混淆表），整理成 jamo 替换代价表接入 T1.1 的编辑距离；同时在 ReplayLab 中加变体统计输出，为长期自有语料校准积累数据。
- **修改原则**：代价表为数据文件 + 默认等权回退，没有它系统照常工作。
- **完成标准**：接入后 replay 抖动 case 的判同分数提升、误判不升。

### T5.3 文档更新

- **做什么**：更新 `Docs/ARCHITECTURE.md`（运行链路、模块职责、删除项）；按 `AGENTS.md` 约束补充维护说明；ReplayLab 使用说明入 `Tools/` 文档。
- **完成标准**：新 Agent 仅凭文档能理解 Timeline 模型并跑通 replay 回归。

# 六、任务依赖关系

| 任务 | 优先级 | 前置依赖 | 可并行 | 验收标准（摘要） |
|------|--------|----------|--------|------------------|
| T0.1 帧序列录制 | P0 | 无 | 与 T1.1 并行 | 产出 ≥50 帧完整会话 |
| T0.2 ReplayLab | P0 | T0.1（格式定义） | 与 T1.1 并行 | 回放可复现、可断言 |
| T0.3 golden cases | P0 | T0.1, T0.2 | 部分场景可延后补录 | 7 case 入库 + 基线指标 |
| T1.1 jamo 相似度 | P0 | 无（验证依赖 T0.2） | 与 T0.* 并行 | 死区消除、对照组区分度达标 |
| T1.2 回归集固化 | P0 | T0.2, T1.1 | — | 一条命令全量回归 |
| T1.3 parser 主导文字分类 | P0 | 无（验证依赖 case 6） | 与 T1.1 并行 | 韩语混错认汉字零误跳、系统提示排除不回归 |
| T2.1 Timeline 模型 | P0 | 无 | 与 T0/T1 并行可先行 | 单测覆盖状态机 |
| T2.2 对齐检测器 | P0 | T1.1, T2.1；验证依赖 T0.3 | — | golden case 全绿 + 坏帧免疫 + 退化输入单测全绿 |
| T2.3 接入主循环 | P0 | T2.2 | — | replay 全绿 + 30min 实测 |
| T2.4 Seq 回填显示 | P1 | T2.3 | 与 T3.1 并行 | 乱序不可复现 |
| T3.1 帧门控突发采样 | P1 | T2.3 | 与 T2.4、T4.1 并行 | 检测延迟 ≤400ms、CPU 不升 |
| T3.2 共识发车 | P1 | T2.3（T3.1 强烈建议先做） | — | 共识文本生效、端到端延迟不升 |
| T3.3 溢出合批 | P1 | T2.3 | 与 T3.1/T3.2 并行 | 爆发 case 零丢弃 |
| T4.1 电平触发 overlay | P1 | T2.3 | 与阶段 3 并行 | 开历史 100% 显示 |
| T5.1 折行校验 | P2 | T0.3 | 随时 | 韩语折行无回归 |
| T5.2 混淆矩阵 | P2 | T1.1 | 随时 | 抖动判同提升 |
| T5.3 文档 | P2 | 阶段 2–4 完成 | — | 文档与实现一致 |

**关键路径**：T0.1 → T0.2 → (T0.3 / T1.1 汇合) → T2.2 → T2.3 → 其余 P1 并行展开。

# 七、测试与验收方案

1. **回放回归（每次提交必跑）**：ReplayLab 跑全部 golden case + 韩语对照组，输出三指标：漏翻数 / 重复数 / 乱序数。验收线：case 1–7 全部达到标注期望；任何提交不得使指标劣于上一提交。
2. **合成压力 case**：坏帧注入（乱码帧、空帧）、30+ 条爆发、provider 注入失败。验收：Timeline 不污染、零丢弃、失败重试保序。
3. **延迟埋点**：在日志中记录三段时延（消息首次被 OCR 捕获 → 确认发车 → 译文上屏）。验收：阶段 3 完成后端到端中位延迟不高于改造前基线，检测段 ≤700ms。
4. **真实游戏验收（阶段 2、3、4 各完成后）**：≥30 分钟韩服对局，人工对照聊天区与 overlay：无漏、无重、无乱序；开关聊天历史 10 次全部及时显示；对照 dedupe.log 抽查。
5. **资源验收**：空闲 CPU/内存不高于改造前（任务管理器对比即可）。

# 八、风险与回滚方案

| 风险 | 概率/影响 | 缓解 | 回滚 |
|------|-----------|------|------|
| .NET NFD 对 Hangul 分解行为与预期不符 | 低/高 | T1.1 先写验证程序确认；不符则自实现 Unicode Hangul 分解算法（算法公开且简单） | 不涉及回滚，属前置验证 |
| 阈值放松导致"宁可放过"策略放过过多（重复上屏） | 中/低 | 用户已确认重复罕见、误放可接受；replay 监控重复数 | 调高对齐判同阈值（单常量改动） |
| T2.3 大改动引入未知回归 | 中/高 | 独立大提交 + replay 全绿 + 30min 实测后才合入后续任务 | `git revert` 该提交，回到 anchor 旧逻辑（阶段 1 改进保留） |
| 像素 diff 被击杀播报/动画频繁误触发 | 中/低 | diff 限聊天区 + 突发档自动回落 | revert T3.1 提交，退回固定间隔采样 |
| 共识等待在低帧率下拖慢首翻 | 低/中 | 自适应发车有 2 帧超时上限；T3.1 先行保证帧便宜 | 把确认帧数常量降为 1（等效旧行为） |
| 批量 prompt 加大后翻译质量回退 | 低/中 | T3.3 验收含质量抽查；批量上限逐步调 | 调回批量常量 |
| golden case 录制不全阻塞验收 | 中/中 | 缺的场景用合成序列顶替开发期验证，发布前补录真实 case | 不涉及 |

**总回滚原则**：所有回滚通过 `git revert` 单提交完成，不设运行时开关；每阶段完成后打 tag（如 `v0.2.0-beta.4-stage2`），最坏情况整体回 tag。

# 九、推荐 Git 提交顺序

每个提交独立可 revert，共 20 个提交，建议顺序：

1. `feat(replay): 帧序列录制模式`（T0.1）
2. `refactor(coordinator): 拆分检测逻辑为可离线驱动的方法`（T0.2 前置最小拆分，行为不变）
3. `feat(tools): ReplayLab 离线回放与断言工具`（T0.2）
4. `test(replay): 录入 golden cases 与基线指标`（T0.3，纯数据）
5. `feat(korean): jamo 分解工具与验证`（T1.1 第 1 步，纯新增）
6. `fix(dedupe): 相似度去空格化 + jamo 级距离，消除短文本死区`（T1.1 第 2/3 步）
7. `test(replay): 韩语对照组回归集`（T1.2）
7a. `fix(parser): 主导文字系统分类替代中文占比阈值`（T1.3）
8. `feat(timeline): ChatTimeline 数据模型与状态机`（T2.1，纯新增）
9. `feat(timeline): 后缀对齐检测器（含冷启动/退化输入/空帧防误吸收规则）`（T2.2，纯新增 + 单测）
10. `refactor(coordinator)!: 接入 Timeline 对齐，移除 anchor/TTL 判重与聊天周期重置，并入最低 2 帧观测确认`（T2.3，**本计划最大提交，打 tag**）
11. `fix(overlay): 译文按 Seq 排序回填，移除显示层去重`（T2.4 第 1/2 步）
12. `feat(timeline): 翻译失败显式重试并保留原 Seq`（T2.4 第 3 步）
13. `feat(capture): 帧哈希门控与突发采样`（T3.1）
14. `feat(timeline): 多帧共识与自适应发车，移除批量盲等窗口`（T3.2）
15. `fix(queue): 队列溢出合批替代丢弃`（T3.3）
16. `refactor(overlay): 电平触发可见性，移除 history peek 特例`（T4.1，**打 tag**）
17. `fix(postprocessor): 韩语折行合并规则`（T5.1，如需）
18. `feat(korean): jamo 混淆代价矩阵（公开数据版）`（T5.2）
19. `docs: 更新 ARCHITECTURE 与 ReplayLab 文档`（T5.3）

# 十、长期演进路线（P3）

1. **自有语料校准混淆矩阵**：ReplayLab 持续输出变体统计，语料量足够后（建议 ≥数千行级变体对）用统计结果替换/校准公开数据代价表。
2. **对齐参数自动调优**：用积累的 golden case 库对（对齐窗口、gap penalty、判同阈值、确认帧数）做网格搜索，替代手调初值。
3. **WGC 截图后端**：`Docs/ARCHITECTURE.md` 已列的方向，解决 GDI 无法捕获独占全屏的场景；接口已被 `ScreenCaptureService` 隔离，属可插拔增强。
4. **Timeline 持久化的会话内历史**：Timeline 容量加大 + 译文缓存，使滚动翻看更早历史也能秒显（当前 100 条上限之外的消息会重翻，属可接受行为，量大后再优化）。
5. **回话助手语言判定增强**：`RecentChatLanguageTracker` 基于 Timeline 共识文本（而非原始 OCR 行）判语言，准确率自然提升，属顺手优化。

# 十一、可直接交给执行 Agent 的提示词

```
你将在仓库 ow-translate-lite（OW 专用实时聊天翻译器，C#/WPF，OCR=OneOCR，
翻译=OpenAI 兼容 API）中执行一份已经定稿的重构计划。方案已经过充分讨论与代码
验证，你的职责是执行，不要重新设计方案、不要质疑已确定的架构决策。

【开始前必读】
1. 仓库根目录 AGENTS.md（维护约束）与 Docs/ARCHITECTURE.md（当前架构）。
2. 本执行计划全文：Docs/RefactorPlan-Timeline-20260612.md。
3. 红线：不得引回 WinOCR、多翻译商 UI、TTS、Whisper、Local Rules；OCR 固定
   OneOCR 自动识别；不引入 feature flag，回滚一律依赖 git revert。

【核心架构决策（已定稿，直接执行）】
- 消息身份由"与权威 ChatTimeline 的序列对齐位置"决定，不由内容判重决定。
- OW 聊天可见区永远是 append-only 日志的连续后缀（底部追加、最老先淡出），
  对齐器实现为后缀对齐而非通用任意编辑对齐（D10）。
- 淡化是正常生命周期，不清 Timeline；删除 ChatHiddenReset/聊天周期重置语义，
  Timeline 只按自身容量/时间策略淘汰（D10）。
- 冷启动首帧全收；对齐器须处理 1对1/1对N/N对1 退化输入并有单测（D13）。
- 空帧后仅 1-2 条可见行 → 强制按新消息处理，禁止相似度吸收到旧记录；
  ≥3 条可见行 → 正常后缀对齐即开历史路径（D11）。
- 现有 OcrDedupeNormalizer 相似度算法保留，降级为对齐打分函数；需补：
  去空格比较、韩语 NFD jamo 级编辑距离、删除 length<8 返回 0 的死区。
- OwChatParser.ShouldSkipMessage 升级为主导文字系统分类：含任何韩文即偏向
  玩家消息；中文占主导且无韩文 → 排除（玩家不发中文、系统消息除玩家名外
  全中文，前提已由用户确认）（D12）。
- 删除全部旧判重机制：_seenInCurrentChatCycle、_recentDedupeCache、
  _recentMessageSnapshots、pending 模糊匹配、MainWindow.IsDisplayDuplicate、
  anchor + tail-2 截断逻辑。判重唯一规则="对齐上了就是旧消息"，策略宁可放过。
- T2.3 起新消息至少连续 2 帧观测才入队（防单帧假阳性）；T3.2 扩展为完整
  自适应发车：连续两帧一致(或 jamo 距离≤1)立即发车，分歧再等一帧投票，
  最多 2 个确认帧。送翻文本=多帧共识文本。
- 采样两档：~300ms 像素 diff 巡逻档（不跑 OCR），变化后突发档 250-300ms 连续
  2-3 帧完整 OCR，然后回落。
- overlay 显示按 Seq 排序回填；可见性电平触发（连续 2 帧无聊天才隐藏），删除
  history peek 边沿检测特例。翻译失败显式重试(≤2 次)且保留原 Seq。
- 队列溢出改合批不丢弃；翻译侧维持单 worker。

【执行顺序】
严格按计划第九节的 20 个提交顺序执行，关键路径：
回放测试床(T0.*) → jamo 基建(T1.*) → Timeline 对齐(T2.*) → 采样发车(T3.*)
→ overlay 简化(T4.1) → 韩语深化(T5.*)。
T0 未完成前禁止改动任何检测/去重逻辑。每个提交完成后必须跑 ReplayLab 全量
回归（漏翻/重复/乱序三指标不得劣化）再进入下一项。

【不确定项处理】
计划中标注"执行 Agent 需先检查确认"的内容（如 AppSettings 字段名、
OcrTextLine 是否含几何信息、OcrPreprocessLab 工程引用方式、OverlayController
渲染假设、.NET NFD 对 Hangul 的分解行为等），必须先用 Read/Grep 实际确认，
禁止凭空假设。文件行号以当前代码为准，计划中的行号仅供定位参考。

【验收标准】
- 7 个 golden case（韩语短消息含冷启动/多人陆续发言/开聊天历史/OCR 字符抖动/
  空格抖动/系统消息交错/淡化后同文本再发言）全部达到标注期望：
  零漏翻、零重复、零乱序。
- 端到端中位延迟不高于改造前；空闲 CPU 不升。
- 真实游戏 30 分钟验证（需用户配合时明确告知用户操作步骤）。
- 完成后更新 Docs/ARCHITECTURE.md 并在阶段节点打 tag。

【提交纪律】
一类修改一个提交，提交信息按计划第九节命名；第 10、16 号提交完成后打 tag。
任何提交导致 replay 指标劣化时，先 revert 再分析，不带病前进。
```
