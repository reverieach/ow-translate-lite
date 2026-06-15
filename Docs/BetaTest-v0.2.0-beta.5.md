# OW Translator Lite v0.2.0-beta.5

这是面向小范围测试的便携版发布包。

## 使用方式

1. 解压整个压缩包。
2. 解压路径不要包含中文字符，推荐 `C:\OWTranslatorLite\` 或 `D:\Tools\OWTranslatorLite\`。
3. 运行外层 `OWTranslatorLite.exe`。
4. 首次启动会显示快速上手指南，也可以在主窗口左侧点击“使用说明”再次打开。
5. 配置 DeepSeek 或 OpenAI Compatible API，选择模型后点击“开始”。

DeepSeek API 需要充值余额并按量计费，聊天翻译用量通常很小，实际费用很低。

如果启动时提示“当前解压路径包含中文字符”，请移动整个 `OWTranslatorLite` 文件夹到英文路径后再运行。

## 本版重点

- 新增外层启动器，依赖文件集中放在 `app/` 目录，发布包根目录更整洁。
- 重写快速上手为 Apple Dark 图文引导。
- 修复 overlay 译文区域滚轮滚动，并隐藏可见滚动条。
- 更新 O/T 应用图标，覆盖 README、窗口、任务栏和 exe 图标。
- 更新 GitHub README 为成熟的中英文项目首页。

## 发布包结构

```text
OWTranslatorLite/
  OWTranslatorLite.exe
  README-BETA.md
  app/
    OWTranslatorLite.exe
    *.dll
    OneOcr/
    Resources/
    ...
```

请不要只复制外层 `OWTranslatorLite.exe`；运行时需要整个目录结构。
