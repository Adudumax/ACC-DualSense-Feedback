# ACC DualSense Feedback 1.2.0

新增 USB 体感转向，改善 TC 介入、后轮失抓和刹车反馈。

> [!IMPORTANT]
> **体感转向需要 USB 连接，其他连接方式请自行测试。** 默认预设下关闭，请在“自定义 → 高级设置 → 体感转向”中开启。
>
> 首次使用完成静置校准。**每次启动工具或重新连接手柄后，都需要设为直行。** 静置校准会保留。

## ✨ 新增

- 支持 DualSense / DualSense Edge 的 USB 体感转向，可调整转向手感，选择持续启用、按住或按键切换，并支持摇杆接管。
- 后轮失抓时增加有起伏的握把拖拽感，帮助辨认后轴失稳与恢复。
- 更容易辨认的 R2 TC 脉冲，介入起始带短促握把辅助，并与车轮空转提示区分。
- 更渐进的 L2 刹车阻力，中后段支撑更扎实，ABS 反馈期间也能保留支撑。

## ✅ 修复

- 修正 TC 介入与车轮空转信号，使其与其他反馈同时开启时提示更可靠。
- 修正未进入驾驶时误导性的“恢复体感”提示，改为对应的等待或启用说明。
- 加强手柄输入中断保护，清空虚拟操作并停止反馈。

保留原有路肩／草地左右定位、升档提醒和实际换挡冲击。实际换挡冲击仍优先；TC 介入时优先于其他 R2 提醒。强烈后轮失抓可能盖过同时发生的路肩反馈。刹车输入映射不变。

默认预设使用平滑灯光并关闭体感，自定义模式保留个人设置。

## 📦 下载

- **English:** `ACCDualSenseFeedback-v1.2.0-win-x64-portable.zip`
- **简体中文:** `ACCDualSenseFeedback-v1.2.0-win-x64-zh-CN-portable.zip`

中英文版均为独立便携包。请完整解压后运行，并保留 EXE 旁的 DLL 和 licenses 文件夹。

## 🔐 SHA-256

```text
8084fd985f0d5c764512da3a992864e6ce48197275711d0c7a195ea70a0e1220  ACCDualSenseFeedback-v1.2.0-win-x64-portable.zip
c2fb096839cd36328d3a96291fca28b29e5e6e69e10165f94eb9a7440a8dd139  ACCDualSenseFeedback-v1.2.0-win-x64-zh-CN-portable.zip
```

安装、升级与故障排查请参阅 [使用说明](https://github.com/Adudumax/ACC-DualSense-Feedback/blob/main/README.zh-CN.md)。
