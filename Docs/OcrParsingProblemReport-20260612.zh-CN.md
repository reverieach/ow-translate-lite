# OW OCR 解析问题报告 - 2026-06-12

## 摘要

这份文档基于最近几份本地 OCR lab 报告，专门总结当前“OCR 明明识别到了文字，但程序没有翻译”的问题。

核心结论：

- 很多漏翻不是纯 OCR 失败。
- OneOCR 经常能返回可见文字，但这些文字可能有噪声、缺失、断行，或者格式不符合当前 parser 期待的 `[玩家名]: 消息`。
- 当前最薄弱的环节是 `原始 OCR 输出 -> OCR 后处理 -> OW 聊天解析` 这段边界。

## 参考报告

主要依据：

- `Docs/ocr-lab-output/20260609-final/report.md`
- `Docs/ocr-lab-output/20260609-tightened/report.md`
- `Docs/ocr-lab-output/20260612-current-basic/report.md`
- `Docs/OcrCurrentAnalysis-20260612.md`

背景说明：

- `20260609-final` 和 `20260609-tightened` 在 94 张图上比较了多种预处理方案。
- `20260612-current-basic` 用当前生产路径 `ColorPreserving` 重新跑了 149 张 `captured-screenshots/` 样本。
- `Docs/ocr-lab-output/` 是本地生成数据，按约定不提交到 Git。

## 当前生产链路

运行时 OCR 主链路：

```text
屏幕截图
  -> OcrImagePreprocessor.ColorPreserving
  -> OneOCR
  -> OcrTextPostProcessor
  -> OwChatParser
  -> TranslationCoordinator 去重/新增消息检测
  -> API 翻译
  -> overlay 显示
```

相关文件：

- `Ocr/OcrImagePreprocessor.cs`
- `Ocr/OneOcrEngine.cs`
- `Core/OcrTextPostProcessor.cs`
- `Core/OwChatParser.cs`
- `Core/TranslationCoordinator.cs`
- `Core/OcrDedupeNormalizer.cs`

## 数据结论

来自 `20260612-current-basic`：

- 截图数量：`149`
- 生产预处理模式：`ColorPreserving`
- 平均 OCR 耗时：`173 ms`
- 平均解析出的聊天行：`1.8`
- 平均有效 OCR 行：`4.6`
- 标记为噪声的样本：`36 / 149`
- 解析出至少 1 条聊天的帧：`140`
- 完全没有解析出聊天的帧：`9`
- OCR 行数大于 0 但解析聊天数为 0 的帧：`8`

解释：

- OCR 通常不是空的。
- “完全解析失败”的比例不算高，但这些帧会直接表现为“不翻译”。
- 更常见的是“部分解析失败”：一帧里可能解析出 1-2 条，但其他有用的 raw OCR 行被丢掉。

来自 `20260609-final` 和 `20260609-tightened`：

- `GrayscaleBaseline` 总分略高于 `ColorPreserving`，但差距不大。
- `ColorPreserving` 总体仍然接近最优，同时也是当前生产路径。
- 纯青色 mask 可以降低一部分噪声，但会丢掉太多非青色聊天内容。
- 多色 mask 没有表现出足够稳定的整体优势，还会增加复杂度。

结论：

- 预处理当然重要，但当前漏翻问题不能靠“回到 mask 方案”直接解决。
- 现在收益更高的方向是增强 parser 和 OCR 后处理的鲁棒性。

## 失败类型

### 1. 只有玩家前缀，没有消息正文

例子：

```text
[로헨]:
你受到了赞赏!
```

现象：

- 原始 OCR 识别到了玩家名前缀。
- 但消息正文缺失。
- parser 会丢掉这行，因为 `[speaker]:` 后面没有消息。

用户体感：

- 用户看到聊天区域有文字。
- 程序也确实有 OCR 行。
- 但没有有效 `ParsedChatLine`，所以不会发起翻译。

可能改进：

- 跨相邻帧追踪这种不完整的玩家名前缀。
- 如果第 N 帧出现 `[speaker]:`，同一帧或第 N+1 帧附近出现像消息正文的文本，可以结合坐标和时间做保守重建。

### 2. 系统/UI 文本占据大部分 OCR 结果

例子：

```text
REVERIEACH (卡热迪)的终极辣能 (神射手
已就绪!
연뚜연도(点西奥)对문어숙희(狂鼠)说 : 我的终极
技能(音障)
准备好了!动手吧!
아서은지(安娜)): 敌方卡西迪睡在这里!
你赞赏了아시은지 !
[로헨]:
你受到了赞赏!
```

现象：

- OCR 识别到了很多行。
- 但大部分是中文 OW UI 或系统提示，不是外语玩家聊天。
- 唯一像玩家聊天的 bracket 行又是不完整的 `[로헨]:`。
- 最终解析聊天数为 `0`。

用户体感：

- overlay 可能不显示，因为 parser 没看到有效玩家消息。
- `dedupe.log` 里可能出现 `ocrLines > 0` 但 `chatLines = 0`。

可能改进：

- 改进框选区域提示和诊断，让用户知道是不是框进了太多 UI。
- 给 parser 增加可选的丢弃原因，例如 `system-ui`、`incomplete-speaker`、`unsupported-format`、`short-noise`。

### 3. 人能看懂，但当前 parser 不支持的 OW 聊天格式

例子：

```text
두부두루치기 (天使): 你好!
锻灵爱好者(秩序之光)对你说
你好!
锻灵爱好者(秩序之光)对你说:干得好
```

现象：

- 这些行对人来说是有意义的。
- 但当前 parser 主要识别 `[speaker]: message`。
- `玩家(英雄): 文本`、`玩家(英雄)对你说:` 这类格式目前不会被当作玩家聊天。

用户体感：

- 一些私聊、低优先级系统混合格式可能被忽略。
- 如果被忽略的内容本来就是中文，忽略是正确的。
- 但如果未来这类格式中包含 EN/JA/KO 玩家文本，就可能成为真实漏翻。

可能改进：

- 只为确实可能产生外语玩家文本的格式增加明确 handler。
- 继续严格过滤中文 UI/系统提示，避免把中文 OW 提示丢进翻译队列。
- 优先“分类后忽略”，而不是静默丢弃，这样诊断更清楚。

### 4. 消息正文被 OCR 拆到下一行

例子：

```text
锻灵爱好者(秩序之光)对你说:
干得好
```

现象：

- OCR 把消息壳和正文拆成了两行。
- 当前后处理主要处理 bracket 格式的折行。
- 非 bracket 的 `对你说:` 格式目前没有重建。

用户体感：

- 明明 raw OCR 看起来有一条消息，但最终 parsed chat 可能是 0。

可能改进：

- 为受支持的非 bracket 消息壳增加基于坐标的 continuation 规则。
- 不要做宽泛字符串拼接；必须要求行距、左边界、格式模式都比较可靠。

### 5. 有效聊天前面带噪声前缀

例子：

```text
◆ [셍이공주123]: ㅎ르츠ㅇ
[锻灵爱好者]:나이슷
```

现象：

- `◆`、`·`、`₩`、`※` 这类前缀噪声会出现在真实聊天前面。
- 当前 parser 有时能恢复，因为它会在一整行里搜索 bracket 消息。
- 但这些噪声仍然会影响评分、去重和跨帧稳定性。

用户体感：

- 有些行能解析成功，但在不同帧里可能形态不稳定。
- 去重可能看到略有差异的 speaker/text。

可能改进：

- 在 parser 提取前归一化已知噪声前缀。
- 保留 raw OCR 诊断，方便检查归一化是否误伤。

### 6. 韩语短句和英雄名识别错误

报告中出现过的例子：

```text
트게이서
트케이서
트츠ㅇ
ㅎ르츠ㅇ
```

现象：

- OneOCR 会把短韩语词、英雄名或玩家短句识别歪。
- 短句特别容易受影响，因为一个音节错了就占整句很大比例。

用户体感：

- 翻译质量变差。
- 短句去重相似度可能失效。
- 有些垃圾短韩语仍然会通过，因为它确实包含 Hangul。

可能改进：

- 不要把一次性普通韩语错字硬编码进术语表。
- 对非常短、符号多、形态异常的 Hangul 行增加低置信度标记。
- OW 专有英雄名和术语可以靠稳定 alias/术语表归一化，但普通自然语言不要硬修。

## 为什么会出现 “OCR 有行数但没有翻译”

常见路径是：

```text
OneOCR 返回文本
  -> OcrTextPostProcessor 保留或轻度修补
  -> OwChatParser 拒绝，因为：
       - 没有 `[speaker]: message`
       - 消息正文为空
       - 中文/UI 内容占比太高
       - 格式拆分或格式不支持
  -> TranslationCoordinator 收不到 ParsedChatLine
  -> 不发起翻译请求
```

这解释了运行时 `dedupe.log` 里的这类现象：

```text
ocrLines=19 chatLines=0 visible=[]
```

这行日志的意思不是 OCR 没识别到，而是程序没有把任何 OCR 文本接受为“可翻译玩家聊天”。

## 架构含义

当前架构方向是对的：

- 截图、OCR、后处理、parser、去重、翻译、overlay 已经有基本边界。
- OCR lab 已经能提供 `Raw OCR`、`Processed OCR`、`Parsed chat` 对照。
- 生产功能应该继续保持 OW 专用，不要重新变成泛用翻译器。

当前最薄弱的边界是：

```text
OcrTextLine[] -> OcrTextPostProcessor -> OwChatParser -> ParsedChatLine[]
```

现在 parser 同时承担两件事：

- 提取有效玩家聊天。
- 过滤 UI/系统噪声。

这会导致一个问题：被丢掉的内容没有结构化原因，所以漏翻很难解释。

## 建议改进方向

### 1. 增加结构化 parser 诊断

返回或可选记录每行被丢弃的原因：

```text
accepted-player-chat
dropped-system-ui
dropped-chinese-ui
dropped-empty-message
dropped-unsupported-format
dropped-short-noise
dropped-no-chat-script
```

收益：

- beta 排查更快。
- 能清楚区分 OCR 失败和 parser 决策。

风险：

- 诊断应保持可选，避免正常运行日志太吵。

### 2. 谨慎扩展 OCR 后处理

可以增加保守修复规则：

- `[speaker]:` 前面的噪声前缀。
- `[speaker] message` 缺冒号。
- `[speaker]:` 后面紧跟下一行正文，并且坐标关系可信。
- 受支持的 `player(hero)对你说:` 拆行格式，仅在对外语文本有价值时处理。

收益：

- 减少 `ocrLines > 0 / parsed=0`。

风险：

- 过度修复会把 OW 系统提示误当成玩家聊天。

### 3. 把 UI/系统过滤和玩家消息提取拆开

可以考虑把 parser 变成更明确的流水线：

```text
raw OCR line
  -> 判断行形态
  -> 生成修复候选
  -> 提取玩家消息候选
  -> 过滤中文/系统候选
  -> 输出 accepted lines 和 dropped reasons
```

收益：

- 行为更可解释。
- 后续支持更多 OW 聊天格式更安全。

### 4. Overlay 历史显示问题要独立处理

OCR/parser 问题和 overlay 历史显示问题不是同一个问题。

对于“打开聊天历史但 overlay 不出现”，程序不应该必须等到解析出新消息。只要满足以下条件，就可以显示已有翻译历史：

- 当前帧有可见 OCR 文本，或
- 最近几帧表明聊天区域可见，且
- overlay 已经有历史翻译记录。

收益：

- 即使没有新可翻译行，用户打开聊天历史时也能更快看到 overlay。

风险：

- 需要稍微整理 overlay 状态，避免和 idle hide、reply mode 互相打架。

### 5. 预处理实验先继续留在 lab

根据当前报告：

- `ColorPreserving` 不完美，但整体有竞争力。
- 青色 mask 降噪明显，但会漏掉非青色内容。
- 多色 mask 增加复杂度，却没有稳定到足够值得回主线。

建议：

- 暂时不要恢复生产环境可选 OCR 模式。
- 继续用 `OcrPreprocessLab` 做预处理实验。
- 近期优先做 parser/post-processor 的诊断和保守修复。

## 具体下一步计划

1. 先在本地工具里增加 parser analysis 模式，不直接塞进主 UI。
2. 生成紧凑的 CSV/Markdown 表，包含 raw line、processed line、parser decision 和 reason。
3. 实现最高置信度的保守修复规则。
4. 重新跑 `20260612-current-basic`，对比 parsed-zero 帧、中文/系统误入、平均 parsed chat、跨帧稳定性。
5. parser 准确性改善后，再处理 overlay 历史显示时机和翻译队列顺序问题。

## 最终判断

这些报告指向一个很清楚的优先级：

- 不要把问题简单理解成 “OneOCR 读不了 OW 聊天”。
- 更准确的描述是：OneOCR 返回了 messy 的 OW 聊天邻近文本，而我们的 parser 仍然太严格、太不透明。

最值得做的下一步，是建立一个可诊断的 parser/post-processing 层，让它能恢复明显的玩家消息，同时继续强力排除中文 UI 和系统噪声。
