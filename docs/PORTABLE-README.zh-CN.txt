ACC DualSense Feedback 1.0.1 · 简体中文版
========================================

为 Assetto Corsa Competizione 增强 DualSense 震动与自适应扳机反馈的小工具。
解压后直接运行 EXE，无需安装本工具、.NET 运行时或中文字体。

使用条件

- Windows 10 / Windows 11，64 位。
- PC 版 Assetto Corsa Competizione（ACC）。
- 通过 USB 连接的 DualSense / DualSense Edge。
- 已安装 ViGEmBus 虚拟手柄驱动。
- 关闭 ACC 的 Steam Input；本工具运行时完全退出 DS4Windows。

ViGEmBus 官方下载：
https://github.com/nefarius/ViGEmBus/releases

用户需要安装的是 ViGEmBus，不是 ViGEm.NET；后者已集成在本工具中。
HidHide 不是必需安装项。如果电脑已启用 HidHide，请在 Applications
列表中允许当前路径下的 ACCDualSenseFeedback.exe 访问实体手柄。
如果出现实体与虚拟手柄重复输入，再根据需要使用 HidHide。

首次使用

1. 将 ZIP 完整解压到可写目录，不要直接在压缩包中运行。
2. 如未安装 ViGEmBus，请先安装驱动。
3. 用 USB 连接手柄，退出 DS4Windows，关闭 ACC 的 Steam Input。
4. 打开 ACCDualSenseFeedback.exe，再启动 ACC 并进入驾驶。

条件齐全时反馈自动运行。关闭本工具会安全停止反馈与虚拟手柄。
仅关闭窗口才是退出；最小化时工具仍在运行。

反馈调校

默认预设使用经过调校的参数。进入“高级设置”后，可独立调整各项反馈。
强度参数为 0–100，调至 0 即关闭；右键点击参数行可恢复该项默认值。
“红线脉冲起点”是最高转速比例，范围 94.0%–99.0%，不是强度开关。
修改即时生效并切换到自定义预设。参数保存到 EXE 同目录的
ACCDualSenseFeedback.settings.json；该文件首次保存时自动生成。
中文与英文版本使用相同的参数格式，反馈算法及默认强度保持一致。

遇到问题

- 未检测到手柄：检查 USB 数据线与 HidHide 应用访问权限。
- 反馈不可用：检查 ViGEmBus、USB 与 HidHide。
- 需要处理：退出 DS4Windows，再点击“重新检查”。
- 等待驾驶：ACC 已启动，但尚未收到有效驾驶遥测，请进入驾驶。

打开“诊断与日志”，点击“复制诊断报告”，将完整报告发给开发者。
报告保留详细的驱动与运行信息；技术字段和异常原文保留英文，便于诊断。
运行日志只保存在内存中，不持续写入磁盘。导出的报告会脱敏用户目录与
私密 HID 设备路径，包括异常原文中出现的路径。

此 EXE 尚未数字签名，Windows SmartScreen 可能提示未知发布者。
本包不包含遥测录制脚本、自动验证程序、个人设置或测试数据。

支持与发布

https://github.com/Adudumax/ACC-DualSense-Feedback

本项目代码采用 MIT 许可证。项目许可与第三方许可位于 licenses 文件夹；
请与 EXE 一同保留。
简体中文字体为 Noto Sans SC 的裁剪静态版本，采用 SIL OFL 1.1，
修改后的内嵌字体名称为 ACC Noto UI SC，无需安装系统字体。
