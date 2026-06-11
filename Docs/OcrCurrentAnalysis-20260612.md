# OCR Current Analysis - 2026-06-12

## Scope

- Input directory: `captured-screenshots/`
- Screenshot count: `149`
- OCR path: current production `ColorPreserving`
- Tool: `Tools/OcrPreprocessLab` in `--mode basic`
- Full raw report: `Docs/ocr-lab-output/20260612-current-basic/report.md`

Command used:

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode basic --input E:\rstgametranslation\ow-translate-lite\captured-screenshots --output E:\rstgametranslation\ow-translate-lite\Docs\ocr-lab-output\20260612-current-basic
```

## Headline Numbers

- Total screenshots: `149`
- Parsed chat lines > 0: `140`
- Parsed chat lines = 0: `9`
- Noise flagged: `36`
- OCR lines > 0 but parsed chat lines = 0: `8`

Interpretation:

- Most frames do produce at least one parsed player message.
- The main failure pattern is not "OCR saw nothing".
- The main failure pattern is "OCR saw text, but the text was not in a shape the parser accepted as player chat".

## Main Failure Shapes

### 1. Incomplete player message shell

Example: `cap_20260608-205638-349.png`

Raw OCR:

```text
[로헨]:
你受到了赞赏!
```

Processed OCR:

```text
[로헨]:
你受到了赞赏!
```

Result:

- OCR recognized a speaker prefix.
- The message body is missing.
- Parser drops it because it is not a complete `[speaker]: message`.

### 2. System/UI text dominates the frame, player message is absent or broken

Example: `cap_20260608-205811-669.png`

Processed OCR excerpt:

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

Result:

- OCR did recognize many lines.
- Most lines are Chinese UI/system text or non-supported message formats.
- The only bracketed player line is incomplete: `[로헨]:`
- Final parsed chat count is `0`.

### 3. Non-supported chat formats appear valid to humans but not to current parser

Example: `cap_20260609-104232-308.png`

Processed OCR excerpt:

```text
두부두루치기 (天使): 你好!
锻灵爱好者(秩序之光)对你说
你好!
锻灵爱好者(秩序之光)对你说:干得好
셍이공주123切换至朱诺
曾使用雾子
송파왕고추切换至末日铁拳
曾使用路霸)
[锻灵爱好者]:
0
```

Result:

- There are human-readable chat-like lines here.
- But current parser is heavily optimized for bracketed OW chat: `[speaker]: message`
- Formats like `玩家(英雄)对你说:` and split whisper lines are not recognized as player chat.
- Final parsed chat count is `0`.

### 4. Speaker/body boundary damage

Example: `cap_20260609-104235-906.png`

Processed OCR excerpt:

```text
锻灵爱好者(秩序之光)对你说 你如
锻灵爱好者(秩序之光)对你说:
干得好
셍이공주123切換至朱诺
曾使用雾子!
송파왕고추切换至末日铁拳
曾使用路霸
锻灵爱好者1:
0
```

Result:

- The intended message is split across lines.
- The parser does not currently reconstruct this format.
- The fallback shell becomes `锻灵爱好者1:` + `0`, which is unusable.
- Final parsed chat count is `0`.

### 5. Prefix noise before valid bracketed lines

Example: `cap_20260609-105132-045.png`

Raw OCR:

```text
[송파왕고추]:
◆ [셍이공주123]: ㅎ르츠ㅇ
[锻灵爱好者]:나이슷
```

Parsed chat:

```text
[셍이공주123]: ㅎ르츠ㅇ
[锻灵爱好者]: 나이슷
```

Result:

- Noise prefixes like `◆` can appear.
- Current parser can still recover later valid lines.
- This is a partial success case, but it shows why raw OCR and parsed output can differ a lot.

## Success Shape

Example: `cap_20260609-104812-844.png`

Processed OCR excerpt:

```text
[锻灵爱好者]: 돼지 원콤 어케 내는거임?
[锻灵爱好者]:잘 안되던데
锻灵爱好者切换至秩序之光
曾使用猎空
[셍이공주123]: 끌고
[셍이공주123]: 앞으로 한발짝 가서
[셍이공주123]: 쳐야됨
[송파왕고추]: 느낌대로
```

Parsed chat:

```text
[锻灵爱好者]: 돼지 원콤 어케 내는거임?
[锻灵爱好者]: 잘 안되던데
[셍이공주123]: 끌고
[셍이공주123]: 앞으로 한발짝 가서
[셍이공주123]: 쳐야됨
[송파왕고추]: 느낌대로
```

Result:

- Bracketed player chat survives well.
- Chinese system lines are correctly filtered.
- This is the path the current architecture is tuned for.

## What This Means

The current OCR problem is split into two different layers:

1. OCR recognition errors:
   - Noise prefixes like `◆`, `·`, `₩`
   - Wrong characters inside Hangul text
   - Incomplete lines such as `[speaker]:`
   - Split lines such as `对你说:` on one line and content on the next

2. Parser acceptance limits:
   - Strong bias toward `[speaker]: message`
   - Weak support for whisper/system-hybrid formats
   - Weak recovery when OCR splits a message shell from its content

The most important conclusion is:

- Many "not translated" cases are not because OCR returned nothing.
- They are because OCR returned text that the current post-process and parser chain could not confidently classify as a valid player message.

## Best Next Debug Step

For future diagnosis, compare these three stages on the same frame:

- `Raw OCR`
- `Processed OCR`
- `Parsed chat`

If a frame has:

- `OCR lines > 0`
- `Parsed chat lines = 0`

then the likely root cause is parser/post-process strictness, unsupported chat format, or broken speaker/body reconstruction rather than pure OCR failure.
