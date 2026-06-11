# OW OCR Parsing Problem Report - 2026-06-12

## Summary

This report summarizes the current "OCR has text but no translation" problem using the latest local OCR lab reports.

The key finding is:

- Many missed translations are not pure OCR failures.
- OneOCR often returns visible text, but the text is too noisy, incomplete, split, or formatted differently from the current parser's expected `[speaker]: message` shape.
- The weak link is currently the boundary between raw OCR output, OCR post-processing, and OW chat parsing.

## Source Reports

Main reports used:

- `Docs/ocr-lab-output/20260609-final/report.md`
- `Docs/ocr-lab-output/20260609-tightened/report.md`
- `Docs/ocr-lab-output/20260612-current-basic/report.md`
- `Docs/OcrCurrentAnalysis-20260612.md`

Important context:

- `20260609-final` and `20260609-tightened` compare multiple preprocessing variants on 94 images.
- `20260612-current-basic` reruns 149 captured screenshots using only the current production `ColorPreserving` path.
- `Docs/ocr-lab-output/` is local generated data and is intentionally ignored by Git.

## Current Production Pipeline

Runtime OCR path:

```text
screen capture
  -> OcrImagePreprocessor.ColorPreserving
  -> OneOCR
  -> OcrTextPostProcessor
  -> OwChatParser
  -> TranslationCoordinator dedupe/new-message detection
  -> API translation
  -> overlay
```

Relevant files:

- `Ocr/OcrImagePreprocessor.cs`
- `Ocr/OneOcrEngine.cs`
- `Core/OcrTextPostProcessor.cs`
- `Core/OwChatParser.cs`
- `Core/TranslationCoordinator.cs`
- `Core/OcrDedupeNormalizer.cs`

## Quantitative Findings

From `20260612-current-basic`:

- Screenshots: `149`
- Production mode: `ColorPreserving`
- Average OCR time: `173 ms`
- Average parsed chat lines: `1.8`
- Average effective OCR lines: `4.6`
- Noise flagged: `36 / 149`
- Frames with parsed chat lines > 0: `140`
- Frames with parsed chat lines = 0: `9`
- Frames with OCR lines > 0 but parsed chat lines = 0: `8`

Interpretation:

- OCR usually returns something.
- Total parser failure is uncommon but important because those frames directly become "not translated".
- Partial parser failure is more common: a frame may parse 1-2 lines while dropping other useful raw OCR lines.

From `20260609-final` and `20260609-tightened`:

- `GrayscaleBaseline` scored slightly above `ColorPreserving` in aggregate, but not by a large margin.
- `ColorPreserving` stayed close to the top while preserving the production path.
- Cyan-only mask variants substantially reduced noise but also missed too much non-cyan content.
- Multi-color mask variants were not clearly better overall and added complexity.

Conclusion:

- Preprocessing matters, but current missed translations are not solved by simply returning to a mask pipeline.
- Parser/post-processing robustness is now the higher-leverage target.

## Failure Categories

### 1. Empty Or Incomplete Player Message Shell

Example:

```text
[로헨]:
你受到了赞赏!
```

Observed behavior:

- Raw OCR recognizes a speaker prefix.
- The player message body is missing.
- Parser drops the line because `[speaker]:` has no message.

Likely user-facing effect:

- User sees text/chat area in game.
- App has OCR lines, but no valid parsed chat.
- No translation appears.

Potential mitigation:

- Track incomplete speaker shells across adjacent frames.
- If `[speaker]:` appears in frame N and message-like text appears near it in frame N or N+1, attempt reconstruction with geometry/time constraints.

### 2. System And UI Lines Dominate The Frame

Example excerpt:

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

Observed behavior:

- OCR recognized many lines.
- Most are Chinese OW UI/system callouts, not foreign player chat.
- The only bracketed player shell is incomplete.
- Final parsed chat count is `0`.

Likely user-facing effect:

- Overlay may not show because parser sees no valid player message.
- Dedupe log may show `ocrLines > 0` and `chatLines = 0`.

Potential mitigation:

- Improve capture-region guidance and optional diagnostics to show when region includes too much UI.
- Add parser-level classification output for dropped lines: `system-ui`, `incomplete-speaker`, `unsupported-format`, `short-noise`.

### 3. Unsupported OW Chat-Like Formats

Example:

```text
두부두루치기 (天使): 你好!
锻灵爱好者(秩序之光)对你说
你好!
锻灵爱好者(秩序之光)对你说:干得好
```

Observed behavior:

- These lines are meaningful to a human.
- Current parser is tuned for bracketed player chat: `[speaker]: message`.
- Formats like `player(hero): text` and `player(hero)对你说:` are not treated as player chat.

Likely user-facing effect:

- Some whisper/private/system-hybrid chat may be ignored.
- If the ignored content is Chinese, ignoring it is fine.
- If the ignored content contains EN/JA/KO player text in the future, it may become a real miss.

Potential mitigation:

- Add explicit format handlers, but only for formats that can produce foreign player text.
- Keep Chinese UI/system filtering strict to avoid translating Chinese OW UI hints.
- Prefer classify-and-ignore over silently dropping, so diagnostics remain readable.

### 4. Split Message Body

Example:

```text
锻灵爱好者(秩序之光)对你说:
干得好
```

Observed behavior:

- OCR splits a message shell and its content across two lines.
- Current post-processor mainly handles bracketed wrapped lines.
- Non-bracketed `对你说:` formats are not reconstructed.

Likely user-facing effect:

- Parsed chat count can be zero even when OCR text clearly contains a message.

Potential mitigation:

- Add a geometry-aware continuation model for supported non-bracketed shells.
- Avoid broad string concatenation; require line proximity, similar left edge, and supported shell patterns.

### 5. Prefix Noise Before Valid Chat

Example:

```text
◆ [셍이공주123]: ㅎ르츠ㅇ
[锻灵爱好者]:나이슷
```

Observed behavior:

- Prefix symbols such as `◆`, `·`, `₩`, `※`, and occasional random Latin/CJK fragments appear before real chat.
- Current parser can sometimes recover because it searches for bracketed messages inside a line.
- Noise still affects scoring, dedupe, and line stability.

Likely user-facing effect:

- Some lines parse successfully but may be unstable across frames.
- Dedupe may see slightly different text/speaker shapes between frames.

Potential mitigation:

- Normalize known noise prefixes before parser extraction.
- Keep the raw OCR text in diagnostics so normalization mistakes are auditable.

### 6. Hangul Recognition Errors

Examples observed across reports:

```text
트게이서
트케이서
트츠ㅇ
ㅎ르츠ㅇ
```

Observed behavior:

- OneOCR may distort short Korean words or hero names.
- This is especially visible on short chat lines where one wrong syllable changes most of the text.

Likely user-facing effect:

- Translation quality degrades.
- Dedupe similarity may fail for short lines.
- Some garbage short lines still pass because they contain Hangul.

Potential mitigation:

- Do not hardcode one-off ordinary Korean typos into the glossary.
- Consider short-line confidence heuristics: very short Hangul lines with many odd symbols may need lower priority or diagnostic flagging.
- For OW-specific terms and hero names, use glossary normalization only when aliases are stable.

## Why "OCR Lines > 0 But No Translation" Happens

The common path is:

```text
OneOCR returns text
  -> OcrTextPostProcessor keeps or lightly repairs it
  -> OwChatParser rejects it because:
       - no `[speaker]: message`
       - empty message body
       - Chinese/UI-heavy content
       - unsupported split format
  -> TranslationCoordinator receives no ParsedChatLine
  -> no translation request is made
```

This explains the runtime pattern seen in `dedupe.log`:

```text
ocrLines=19 chatLines=0 visible=[]
```

That log line means OCR recognized text, but the app accepted none of it as translatable player chat.

## Architectural Implications

The current architecture is directionally right:

- Capture, OCR, post-processing, parser, dedupe, translation, and overlay are separate enough to debug.
- The lab already provides `Raw OCR`, `Processed OCR`, and `Parsed chat` comparisons.
- Production should stay OW-specific and avoid reintroducing broad generic translation behavior.

The weak boundary is:

```text
OcrTextLine[] -> OcrTextPostProcessor -> OwChatParser -> ParsedChatLine[]
```

The parser currently acts as both:

- extractor of valid player chat
- filter for UI/system noise

This makes misses hard to explain because all dropped text disappears without structured reasons.

## Recommended Improvements

### 1. Add Structured Parser Diagnostics

Return or optionally log dropped-line reasons:

```text
accepted-player-chat
dropped-system-ui
dropped-chinese-ui
dropped-empty-message
dropped-unsupported-format
dropped-short-noise
dropped-no-chat-script
```

Expected benefit:

- Faster beta diagnosis.
- Clearer separation between OCR failure and parser decision.

Risk:

- Keep diagnostics optional so runtime logs do not become noisy during normal use.

### 2. Expand Post-Processing Carefully

Add conservative repair rules for:

- leading noise prefixes before `[speaker]:`
- `[speaker] message` missing colon
- `[speaker]:` followed by next-line message when geometry supports it
- supported `player(hero)对你说:` split lines, only if useful for foreign text

Expected benefit:

- Reduces `ocrLines > 0 / parsed=0` cases.

Risk:

- Over-repair can turn OW system lines into false player chat.

### 3. Separate UI/System Filtering From Player Extraction

Instead of one parser pass that silently drops everything, consider:

```text
raw OCR line
  -> classify line shape
  -> repair candidate
  -> extract player message candidates
  -> filter Chinese/system-only candidates
  -> emit accepted lines plus dropped reasons
```

Expected benefit:

- More explainable behavior.
- Better future support for additional OW chat formats.

### 4. Improve History/Overlay Behavior Independently

The OCR/parser issue is separate from overlay history display.

For "open chat history but overlay does not appear", the app should not require a newly parsed message. It can show existing translated history when:

- current frame has visible OCR text, or
- recent frames suggest chat area is visible, and
- overlay records already exist.

Expected benefit:

- Better perceived responsiveness even when no new translatable line appears.

Risk:

- Needs a small overlay state-machine cleanup to avoid fighting idle hide and reply mode.

### 5. Keep Preprocessing Experiments Lab-Only For Now

Based on current reports:

- `ColorPreserving` is not perfect, but it is competitive.
- Cyan masks reduce some noise but lose non-cyan content.
- Multi-color masks add complexity without a stable enough win.

Recommendation:

- Do not reintroduce selectable production OCR modes yet.
- Continue using `OcrPreprocessLab` for preprocessing experiments.
- Focus near-term implementation on parser/post-processor diagnostics and conservative repair.

## Concrete Next Step Plan

1. Add an optional parser analysis mode in local tools first, not the main UI.
2. Generate a compact CSV/Markdown table with raw line, processed line, parser decision, and reason.
3. Implement conservative repairs for the highest-confidence patterns.
4. Re-run `20260612-current-basic` and compare:
   - parsed-zero frames
   - false positive Chinese/system lines
   - average parsed chat count
   - duplicate/unstable line behavior
5. Only after parser accuracy improves, revisit overlay history timing and translation queue ordering.

## Bottom Line

The reports point to a clear priority:

- Do not treat the main issue as "OneOCR cannot read OW chat".
- Treat it as "OneOCR returns messy OW chat-adjacent text, and our parser is still too strict and too opaque".

The best next improvement is a diagnosable parser/post-processing layer that can recover obvious player messages while still aggressively rejecting Chinese UI/system noise.
