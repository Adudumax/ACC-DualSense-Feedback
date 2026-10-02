# 简体中文便携构建

保留原版 B 的窗口结构、品牌图标与配色，按已确认的中文 HTML 预览重新定义字号、真实字重、行高和参数行布局。设计规范集中在 `Ui/UiTypography.cs`；翻译和无障碍文案集中在 `Ui/UiText.cs`。

## 构建

```powershell
./tools/Build-Release.ps1 -Language zh-CN
```

默认 `-Language en`，不会将中文字体资源加入英文构建。输出分别为 `dist/release-v1.0.1-zh-CN` 与 `dist/release-v1.0.1`；已存在的发布目录不会覆盖。

中文包仅包含 EXE、使用说明和四份必要许可/说明：项目 MIT 许可、第三方说明、ViGEm.NET MIT 许可与字体 OFL 许可。字体内嵌在 EXE 中，用户无需安装字体。ZIP 使用标准 ZIP 的最高压缩档，不改变可执行文件内容。

## 字体

- 来源：Google Fonts 官方 Noto Sans SC，SIL OFL 1.1。
- 三个静态字重：400 / 500 / 600；修改后族名 ACC Noto UI SC。
- 保留本地化界面所用字符与 ASCII/标点；非内嵌字符仍由 WPF 正常回退。
- 三个裁剪 TTF 合计 260,716 字节；源字体、fontTools 与检查程序不发布。
- 字体字重在原生 GlyphTypeface 检查中逐一确认，没有系统字体回退或合成加粗。
- 数值/品牌标识使用 Segoe UI，中文强度描述使用内嵌字体。

更新中文文案后，需要重新运行 `tools/Subset-ChineseFonts.py` 并核对字符覆盖。构建期依赖 `fonttools==4.60.1`；源文件来自 https://github.com/google/fonts/tree/main/ofl/notosanssc 。完整 OFL 许可保存在 `licenses/NOTO-SANS-SC-OFL.txt`。

WPF 原生 TextBlock 不使用 CSS 字距，也不通过逐字位移模拟字距。字体保持原生字形度量与文本塑形，通过真实字重、字号、行高和留白落实中文排版，避免破坏原生文本边缘。

## 保持不变

反馈算法、遥测读取、虚拟手柄与 HID 通信、参数序列化格式、默认强度、退出清理均未变更。诊断报告的技术字段及异常原文保留英文，显示层的状态和近期事件为中文。英文发布包没有覆盖。

## 本轮构建检查

本地临时检查程序存放在 Git 忽略的 `artifacts/zh-cn-build`，不属于产品源码或发布包。没有恢复 GitHub Actions 工作流或给正式 EXE 添加验证入口。

已对最终发布用程序集检查三个页面的 96/144 DPI 原生离屏快照、异常状态顶部文字宽度、静态译文覆盖、滑块 0 关闭、单项右键重置、全部重置与默认值。此检查未加载窗口、未启动反馈或访问硬件；它不代替实际 Windows 缩放环境与 ACC 驾驶手感测试。
