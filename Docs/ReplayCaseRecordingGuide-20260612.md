# Replay Case Recording Guide（3 账号专用版，2026-06-12）

本文档是 T0 golden cases 的固定录制脚本。你有 3 个测试账号：

- 主账号：`Reverieach`
- 辅助账号 A：`疯狂的鹿`
- 辅助账号 B：`天剑若叶`

请严格按每个 case 的消息顺序、账号和韩语文本发送。对应的 `expected.json`
已经预置在 `Tools/ReplayLab/expected-templates/<case-id>/expected.json`，录完后直接用，
不需要手工标注。

## 通用规则

1. 启动 OW Translator Lite，先“选择聊天区域”，完整框住 OW 左侧聊天文本框。
2. 三个账号进入同一个可打字环境，推荐自定义房间或训练场组队。
3. 所有测试消息都发到同一个聊天频道，建议使用队伍/小队聊天；不要混用频道。
4. 录制前确保聊天区没有大量旧消息干扰；需要“淡化”的 case 按脚本等待。
5. 每条消息都按文档逐字发送，不要加标点、表情或额外空格。
6. 每个 case 录完后，点击“停止录制”，程序会打开 session 目录。
7. 用对应 expected 模板跑 ReplayLab：

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\<case-id>\expected.json
```

ReplayLab 指标目标：

```text
missing=0, duplicates=0, outOfOrder=0, extra=0
```

如果真实 OCR 把中文账号名稳定识别错，先不要改 expected；把 ReplayLab 生成的
`trace.json` 和 `report.md` 保留下来。这类样本正好用于后续检查 speaker OCR 抖动。

## Case 1：韩语短消息/冷启动

入口选择：`Case 1 韩语短消息/冷启动`

模板：

```text
Tools\ReplayLab\expected-templates\case01-korean-short-cold-start\expected.json
```

目的：覆盖冷启动第一条短韩语消息、日常问候、短文本和常用竞技短句。

录制前：

1. 暂停识别或等待聊天完全淡出 5 秒以上。
2. 选择 Case 1，点击“录制 Case”。

发送顺序：

```text
Reverieach: 안녕
等待 2 秒
Reverieach: ㄱㄱ
等待 2 秒
Reverieach: 힐좀
等待 2 秒
Reverieach: 나이스
```

发送后继续录 10 秒再停止。

期望：4 条都出现一次；其中 `ㄱㄱ`、`힐좀` 是短文本重点样本。

## Case 2：多人陆续发言

入口选择：`Case 2 多人陆续发言`

模板：

```text
Tools\ReplayLab\expected-templates\case02-multi-speaker-burst\expected.json
```

目的：覆盖 3 个玩家在短时间内连续发言，暴露旧 tail-2 截断导致的中间消息漏翻。

录制前：

1. 保持聊天区可见。
2. 选择 Case 2，点击“录制 Case”。

发送顺序：每条间隔 1 秒左右，尽量在 8 秒内全部发完。

```text
Reverieach: 우리 왼쪽 가자
疯狂的鹿: 힐 줄게
天剑若叶: 겐지 뒤
Reverieach: 디바 매트릭스 없어
疯狂的鹿: 나노 있어
天剑若叶: 용검 준비
```

发送后继续录 10 秒再停止。

期望：6 条按上述顺序出现，不能漏掉中间任意一条。

## Case 3：打开聊天历史

入口选择：`Case 3 打开聊天历史`

模板：

```text
Tools\ReplayLab\expected-templates\case03-open-chat-history\expected.json
```

目的：覆盖打开聊天历史时一次出现多条旧消息。

关键点：这个 case 要先发送消息，再开始录制，然后通过打开聊天历史让旧消息进入截图区域。

录制前先发送，不开录制：

```text
Reverieach: 안녕하세요
等待 1 秒
疯狂的鹿: 오늘 첫 판이에요
等待 1 秒
天剑若叶: 위도우 조심
等待 1 秒
Reverieach: 처음엔 왼쪽으로 가자
等待 1 秒
疯狂的鹿: 궁 차면 말해줘
等待 1 秒
天剑若叶: 같이 들어가자
```

然后：

1. 等这些消息自然淡出，或至少等到可见区只剩少量消息。
2. 选择 Case 3，点击“录制 Case”。
3. 打开 OW 聊天历史，让 6 条历史消息一次性出现。
4. 关闭聊天框，等待 2 秒，再打开一次。
5. 重复开/关 2-3 次。
6. 停止录制。

期望：6 条历史消息都被 ReplayLab 看到，顺序保持一致。

## Case 4：OCR 字符抖动

入口选择：`Case 4 OCR 字符抖动`

模板：

```text
Tools\ReplayLab\expected-templates\case04-ocr-character-jitter\expected.json
```

目的：捕获同一句在相邻帧被 OneOCR 识别成不同字符的情况。

录制前：

1. 选择 Case 4，点击“录制 Case”。
2. 每发一条后都等待足够久，让同一句跨多帧停留。

发送顺序：

```text
Reverieach: 트레이서 뒤에 있어 조심해
等待 10 秒
疯狂的鹿: 키리코 스즈 빠졌어
等待 10 秒
天剑若叶: 라마트라 궁 조심해
```

发送后继续录 10 秒再停止。

期望：每句只算一条。raw OCR 内如果出现字符抖动，不应变成重复 accepted 消息。

## Case 5：韩语空格抖动

入口选择：`Case 5 韩语空格抖动`

模板：

```text
Tools\ReplayLab\expected-templates\case05-korean-spacing-jitter\expected.json
```

目的：覆盖韩语空格不稳定、OCR 断词位置变化、带空格竞技 callout。

录制前：选择 Case 5，点击“录制 Case”。

发送顺序：每条间隔 8 秒。

```text
Reverieach: 우리 같이 들어가자
Reverieach: 아나 힐 좀 줘
疯狂的鹿: 라인 방벽 없어
天剑若叶: 다음 한타 천천히
```

发送后继续录 10 秒再停止。

期望：空格位置的 OCR 变体不应制造重复消息。

## Case 6：系统提示与玩家消息交错

入口选择：`Case 6 系统提示交错`

模板：

```text
Tools\ReplayLab\expected-templates\case06-system-player-mixed\expected.json
```

目的：覆盖中文系统提示与韩语玩家消息交错。expected 只包含玩家消息。

录制前：选择 Case 6，点击“录制 Case”。

操作脚本：

1. 触发一条中文系统提示，例如让一个账号加入/离开语音、切换队伍状态、或打开会产生系统提示的聊天事件。
2. 发送：

```text
Reverieach: 힐 필요해
```

3. 再触发一条中文系统提示。
4. 发送：

```text
疯狂的鹿: 리퍼 뒤 조심
```

5. 再触发一条中文系统提示。
6. 发送：

```text
天剑若叶: 궁극기 있어
```

7. 最后发送：

```text
Reverieach: 이번 한타 기다려
```

发送后继续录 10 秒再停止。

期望：ReplayLab expected 中只有 4 条玩家韩语消息；中文系统提示不应进入 expected。

## Case 7：完全淡化后同文本再发

入口选择：`Case 7 淡化后同文本再发`

模板：

```text
Tools\ReplayLab\expected-templates\case07-fade-then-repeat\expected.json
```

目的：覆盖聊天完全淡化后，相同短句再次出现必须作为新消息处理。

录制前：选择 Case 7，点击“录制 Case”。

发送顺序：

```text
Reverieach: ㄱㄱ
```

然后等待聊天完全淡化，至少再多等 3 秒，确保录到空帧。

继续发送：

```text
Reverieach: ㄱㄱ
等待 2 秒
疯狂的鹿: 가자
等待 2 秒
天剑若叶: 가자
```

发送后继续录 10 秒再停止。

期望：两个 `Reverieach: ㄱㄱ` 都必须出现在 expected 中；后两个 `가자`
来自不同账号，也必须各算一条。

## 快速命令索引

把 `<session-directory>` 换成停止录制后打开的目录。

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case01-korean-short-cold-start\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case02-multi-speaker-burst\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case03-open-chat-history\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case04-ocr-character-jitter\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case05-korean-spacing-jitter\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case06-system-player-mixed\expected.json
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\ReplayLab\ReplayLab.csproj -c Release -- <session-directory> Tools\ReplayLab\expected-templates\case07-fade-then-repeat\expected.json
```
