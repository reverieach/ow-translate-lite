# OW Translator Lite v0.2.0-beta.5

这是面向小范围测试的便携版发布包。

## 使用方式

1. 解压整个压缩包。
2. 建议解压到英文路径，例如 `C:\OWTranslatorLite\` 或 `D:\Tools\OWTranslatorLite\`；如果放在中文路径后 OCR/启动异常，请移动到英文路径再试。
3. 运行外层 `OWTranslatorLite.exe`。
4. 首次启动会显示快速上手指南，也可以在主窗口左侧点击“使用说明”再次打开。
5. 配置 DeepSeek 或 OpenAI Compatible API，选择模型后点击“开始”。
6. 后续更新可在主窗口点击“检查更新”；程序会先展示版本说明，再由你选择是否更新。

DeepSeek API 需要充值余额并按量计费，聊天翻译用量通常很小，实际费用很低。

如果启动时提示“当前解压路径包含中文字符”，程序仍会继续启动；只有在后续出现无法识别、OCR 初始化失败或启动异常时，才需要移动整个 `OWTranslatorLite` 文件夹到英文路径后再试。

## 本版重点

- 新增外层启动器，依赖文件集中放在 `app/` 目录，发布包根目录更整洁。
- 新增外层更新器，支持检查 GitHub Releases、查看更新说明、自动下载替换，网络失败时可手动放置 zip 后一键更新。
- 统一日志目录和反馈包导出：普通用户只需要发送“导出反馈包”生成的 zip。
- 新增“联系与反馈”入口，可打开 GitHub Bug 反馈、项目主页或复制联系邮箱。
- 主窗口和快速上手支持深色/浅色主题切换，游戏内 overlay 继续保持深色 HUD。
- 隐藏开发用 Case 录制入口，避免普通 beta 用户误触。
- 重写快速上手为 Apple Dark 图文引导。
- 修复 overlay 译文区域滚轮滚动，并隐藏可见滚动条。
- 更新 O/T 应用图标，覆盖 README、窗口、任务栏和 exe 图标。
- 更新 GitHub README 为成熟的中英文项目首页。

## 发布包结构

```text
OWTranslatorLite/
  OWTranslatorLite.exe
  OWTranslatorLiteUpdater.exe
  README-BETA.md
  app/
    OWTranslatorLite.exe
    *.dll
    OneOcr/
    Resources/
    ...
```

请不要只复制外层 `OWTranslatorLite.exe`；运行时需要整个目录结构。

## 日志与反馈

运行日志位于 `%AppData%\OWTranslatorLite\logs\`，诊断导出位于 `%AppData%\OWTranslatorLite\diagnostics\`。

遇到无法启动、OCR 不识别、重复翻译、翻译框异常等问题时，请点击“导出反馈包”，并在 GitHub Issue 或邮件反馈中附上生成的 zip。API Key 不会明文写入反馈包；只有开启“诊断模式”时才会包含高级 debug 日志。
