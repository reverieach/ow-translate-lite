# OW Translator Lite v1.0.0

OW Translator Lite 是面向《守望先锋 2》外服文字聊天的实时 OCR 翻译工具。

## 快速开始

1. 解压整个压缩包。
2. 建议解压到英文路径，例如 `C:\OWTranslatorLite\` 或 `D:\Tools\OWTranslatorLite\`。
3. 运行外层 `OWTranslatorLite.exe`。
4. 首次启动会显示快速上手指南，也可以在主窗口左侧点击“使用说明”再次打开。
5. 配置 DeepSeek 或 OpenAI Compatible API，点击“获取模型”并选择模型。
6. 点击“选择翻译区域”，框选 OW 左侧聊天区域。
7. 点击“开始”，译文会显示在翻译框中。
8. 如需切换主窗口与快速上手的界面风格，可在左侧“外观”卡片中选择“深色”或“浅色”；游戏内 overlay 保持深色 HUD。

DeepSeek API 需要充值余额并按量计费，聊天翻译用量通常很小，实际费用很低。

如果启动时提示“当前解压路径包含中文字符”，程序仍会继续启动；只有在后续出现无法识别、OCR 初始化失败或启动异常时，才需要移动整个 `OWTranslatorLite` 文件夹到英文路径后再试。

## 更新方式

程序会检查 GitHub Releases 中的新版本，并在更新窗口展示版本号、发布时间、更新说明和包大小。你可以选择立即更新、打开发布页、稍后提醒，或对当前版本选择“此版本不再提醒”。

自动更新会下载最新 `OWTranslatorLite-v*-portable-win-x64.zip`，校验发布包提供的 sha256 后替换外层目录中的 `app/`。更新时会保留最近一次旧 `app/` 备份在 `.update-backup/`，并保留 `%AppData%\OWTranslatorLite` 中的设置、API Key、日志和诊断文件。

如果自动下载失败，可以手动从 GitHub Releases 下载最新 zip，放到外层 `OWTranslatorLite/` 文件夹，再运行 `OWTranslatorLiteUpdater.exe` 一键更新。

如需卸载，运行外层 `OWTranslatorLiteUninstall.exe`。卸载器会删除当前便携包目录和 `%AppData%\OWTranslatorLite`，包括 API Key、设置、日志、诊断包和 overlay 历史。

## 发布包结构

```text
OWTranslatorLite/
  OWTranslatorLite.exe
  OWTranslatorLiteUpdater.exe
  OWTranslatorLiteUninstall.exe
  README.md
  app/
    OWTranslatorLite.exe
    *.dll
    OneOcr/
    Resources/
    ...
```

请不要只复制外层 `OWTranslatorLite.exe`；运行时需要整个目录结构。

## 诊断与反馈

运行日志位于 `%AppData%\OWTranslatorLite\logs\`，反馈包导出位于 `%AppData%\OWTranslatorLite\diagnostics\`。

遇到无法启动、OCR 不识别、重复翻译、翻译框异常等问题时，请在主窗口左侧“诊断工具”中点击“导出反馈包”，并在 GitHub Issue 或邮件反馈中附上生成的 zip。API Key 不会明文写入反馈包；只有开启“诊断模式”时才会包含高级 debug 日志。
