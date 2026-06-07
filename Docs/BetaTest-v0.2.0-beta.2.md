# OW Translator Lite v0.2.0-beta.2 测试说明

## 本版修复重点

- 修复新机器首次选择语言/模型时可能因为 overlay 坐标未初始化导致闪退的问题。
- overlay 拖动或调整大小后会自动保存位置和大小。
- 增加 beta 测试入口：打开数据目录、打开日志、打开去重日志、导出诊断、清除本机数据。
- 增加运行日志、崩溃日志和可选去重调试日志：`%AppData%\OWTranslatorLite\runtime.log`、`%AppData%\OWTranslatorLite\crash.log`、`%AppData%\OWTranslatorLite\dedupe.log`。
- 内置一组默认聊天区域和 overlay 位置；测试者可以直接开始，但不同分辨率或 UI 缩放下仍建议重新框选。
- 如果打开聊天历史但没有新消息，overlay 会临时显示最近翻译约 5 秒，方便回看历史。
- 保留 beta.1 的 OneOCR 彩色预处理、Quick Start、异步翻译队列和重复过滤逻辑。

## 推荐配置

- OW 显示模式：无边框窗口或窗口化无边框，不建议独占全屏。
- OCR 引擎：OneOCR。
- OCR 源语言：韩语测试优先选“韩语”，英语选“英语”，日语选“日语”；混合语言再尝试“自动”。
- 翻译：DeepSeek 或 OpenAI Compatible API。
- DeepSeek API URL：`https://api.deepseek.com`
- DeepSeek 模型：优先 `deepseek-v4-flash`。

## DeepSeek API

1. 打开 `https://platform.deepseek.com/api_keys`。
2. 登录并创建 API Key。
3. 在本程序选择 DeepSeek，填写 API URL 和 API Key。
4. 点击“获取模型”，选择 `deepseek-v4-flash`。

## 框选区域

- 点击“选择聊天区域”。
- 框选 OW 左侧聊天消息文本框部分。
- 需要完整包含 `[玩家名]：正文` 这一整行。
- 不要框太大，尽量避开头像、HUD、菜单和背景噪声。
- 当前版本带默认区域；如果翻译不动、漏字或位置不对，第一步先重新框选。

## Overlay

- 点击“显示 Overlay”后可以预览翻译框。
- 关闭“鼠标穿透”后可以拖动和滚动 overlay。
- 拖动或调整大小后会自动保存。
- 暂停会隐藏 overlay，但不清空历史。
- “清空”按钮会清空主日志和 overlay 历史。

## Beta 测试入口

- “打开数据目录”：打开 `%AppData%\OWTranslatorLite`，里面有设置、日志和诊断文件。
- “打开日志”：打开 `runtime.log`，记录程序内实时记录。
- “记录去重调试日志”：默认关闭；测试重复/漏翻问题时开启，会记录 OCR 聊天行、锚点判断、候选新消息和重复丢弃原因。
- “打开去重日志”：打开 `dedupe.log`。
- “导出诊断”：生成 `diagnostics-日期时间.txt`，包含脱敏配置、最近日志和崩溃日志尾部；API Key 不会明文导出。
- “清除本机数据”：清空设置、API Key、日志、诊断文件和 overlay 历史，并恢复默认配置。

## 如果出问题

- 如果程序闪退、卡住或翻译没反应，优先点击“导出诊断”，把生成的 `diagnostics-*.txt` 发给开发者。
- 如果程序直接打不开，可以手动发送 `%AppData%\OWTranslatorLite\crash.log` 和 `%AppData%\OWTranslatorLite\runtime.log`。
- 如果翻译没反应，先确认 API Key、模型、聊天区域和 OW 无边框模式。
- 如果 OCR 识别不稳定，优先固定源语言，例如韩语局选“韩语”。
