# Timeline Refactor Update - 2026-06-13

This document summarizes the completed Timeline refactor for OW Translator Lite.
It is written as a handoff note for future maintenance and beta testing.

## Scope

The refactor keeps the product narrow: OW-specific real-time chat translation,
OneOCR auto recognition, and DeepSeek/OpenAI-compatible translation to Simplified
Chinese. It does not reintroduce WinOCR, multi-provider UI, TTS, Whisper, or
Local Rules.

## Main Method

The old system mixed several independent de-dupe layers: per-cycle seen state,
recent TTL caches, pending fuzzy matches, display duplicate filtering, and anchor
tail heuristics. The new system makes `ChatTimeline` the authority.

Every player message gets a monotonically increasing `Seq`. OCR content is not
the identity. OCR content is only used to align the current visible chat suffix
to the existing timeline. If a visible line aligns to a timeline message, it is
old. If it does not align, it becomes a new timeline message. This is the core
rule behind the refactor.

## New Runtime Flow

1. The main loop screenshots the selected chat region every 250-300 ms.
2. `FrameDiffGate` computes a cheap pixel signature. Stable frames do not run OCR.
3. When the region changes, the app enters a 3-frame OCR burst.
4. OneOCR output is post-processed and parsed into `[speaker]: message` lines.
5. `TimelineAlignmentDetector` aligns visible lines against the tail of `ChatTimeline`.
6. New messages collect multi-frame observations before being queued.
7. The translation worker sends confirmed messages to the API.
8. Overlay records carry `Seq` and are sorted by `Seq` before display.

## Implemented Features

- ReplayLab frame-sequence recording and offline replay.
- Three-account golden-case recording guide and expectation templates.
- Hangul/jamo normalization for Korean OCR jitter.
- Whitespace-insensitive Korean similarity.
- Removal of the short-text similarity dead zone for Korean.
- Dominant-script parser filtering so Korean with incidental CJK OCR noise is kept.
- `ChatTimeline` model with message state, variants, retry count, and `Seq`.
- Suffix alignment detector with cold-start and after-empty safeguards.
- Coordinator integration with Timeline alignment.
- Removal of old de-dupe mechanisms and display-layer fuzzy duplicate filtering.
- Overlay order based on `Seq`.
- Explicit translation retry up to two times, preserving original `Seq`.
- Pixel-diff patrol plus burst OCR sampling.
- Multi-frame consensus before translation.
- Queue overflow behavior changed from dropping at 30 to larger batches, with only a 100-item hard safety limit.
- Overlay visibility changed to level-triggered chat visibility with two no-chat OCR frames of hysteresis.
- Korean wrapped-line merge now tolerates incidental CJK OCR noise when Hangul is present.
- Weighted jamo confusion cost table at `Resources/KoreanJamoConfusionCosts.json`.
- ReplayLab variant summary for future OCR/jamo tuning.

## Korean Jamo Confusion Table

`KoreanJamoNormalizer` still exposes normal integer jamo edit distance for hard
rules such as "jamo distance <= 1". Similarity scoring now also uses weighted
jamo distance. The table lowers substitution cost for plausible visual OCR
confusions such as plain/aspirated consonants, plain/doubled consonants, and
orientation-adjacent vowels.

The table is copied to app output, but the same seed data is compiled as a
fallback so the app does not fail if the file is missing.

Public references used for the seed design:

- Unicode Hangul Jamo block chart: https://www.unicode.org/charts/PDF/U1100.pdf
- Wikimedia Commons Hangul stroke order category: https://commons.wikimedia.org/wiki/Category:Hangeul_stroke_order
- KORIE benchmark page describing small-font Hangul OCR difficulty, jamo fragmentation, and visually similar glyph confusion: https://huggingface.co/datasets/tabtoyou/KORIE
- Jamo-level OCR research direction: https://viplab.snu.ac.kr/viplab/courses/mlvu_2025_1/projects/01.pdf

## User-Visible Changes To Test

- Korean short messages such as `ㄱㄱ`, `힐좀`, `위도우 조심` should no longer be lost just because they are short.
- Korean spacing jitter such as `힐 줄게` vs `힐줄게` should align as the same message.
- Light OCR character jitter in Korean should wait for consensus instead of sending the first unstable frame.
- Chat history opening should show the overlay via normal chat visibility, not a special peek timer.
- Translation failures should retry visibly and keep the original display order.
- Busy chat bursts should batch instead of silently dropping messages at 30 queued items.
- Idle CPU should not rise because stable frames patrol by pixel diff instead of OCR.

## Current Verification Commands

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- --timeline-smoke
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- --similarity Tools\ReplayLab\similarity\korean-jamo-regression.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- Tools\ReplayLab\fixtures\smoke-korean-short Tools\ReplayLab\fixtures\smoke-korean-short\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe build OwTranslateLite.csproj -c Release
```

Expected fixture metrics:

```text
missing=0, duplicates=0, outOfOrder=0, extra=0
```

## Real-Game Validation Steps

Run this after installing or launching the built `app\OWTranslatorLite.exe`:

1. Use account `Reverieach` as the main player.
2. Select the OW chat region so it fully contains `[player]: message` lines.
3. Start recognition and keep dedupe debug log enabled.
4. Play or custom-test for at least 30 minutes.
5. Send Korean short messages, spacing variants, and multi-player bursts from the three prepared accounts.
6. Open and close chat history at least 10 times.
7. Check that overlay output has no missing translated player message, no duplicate translated line, and no out-of-order display.
8. Compare Task Manager idle CPU against the pre-refactor beta; stable no-chat frames should not be higher.
9. If a mismatch appears, export diagnostics and preserve the frame recording session for ReplayLab.

## Commits

```text
f6f7dfa feat(replay): 帧序列录制模式
b511833 feat(tools): ReplayLab 离线回放与断言工具
c94f6e9 docs(replay): 添加 golden case 录制指南
b2d38c3 docs(replay): 添加三账号 golden case 脚本
aaaa36c feat(korean): jamo 分解工具与验证
af7a427 fix(dedupe): 相似度去空格化 + jamo 级距离，消除短文本死区
c2b6bb9 test(replay): 韩语对照组回归集
6d3cdcd fix(parser): 主导文字系统分类替代中文占比阈值
8ee22e0 feat(timeline): ChatTimeline 数据模型与状态机
0f3e30c feat(timeline): 后缀对齐检测器（含冷启动/退化输入/空帧防误吸收规则）
7c74620 refactor(coordinator)!: 接入 Timeline 对齐，移除 anchor/TTL 判重与聊天周期重置，并入最低 2 帧观测确认
844d283 fix(overlay): 译文按 Seq 排序回填，移除显示层去重
11d6b6a feat(timeline): 翻译失败显式重试并保留原 Seq
d4dffa4 feat(capture): 帧哈希门控与突发采样
1ee87e3 feat(timeline): 多帧共识与自适应发车，移除批量盲等窗口
28f4fdd fix(queue): 队列溢出合批替代丢弃
353e285 refactor(overlay): 电平触发可见性，移除 history peek 特例
eebe043 fix(postprocessor): 韩语折行合并规则
24bd2f2 feat(korean): jamo 混淆代价矩阵（公开数据版）
```
