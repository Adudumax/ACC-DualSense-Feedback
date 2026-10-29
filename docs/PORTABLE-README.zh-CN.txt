ACC DualSense Feedback 1.2.1 · 简体中文版
========================================

为 Assetto Corsa Competizione 增强 DualSense 震动、自适应扳机反馈，并提供
可选 USB 体感转向的 Windows 工具。

请完整解压，保留 EXE 同目录的 ACCGyroFusion.dll 和 licenses 文件夹。
无需另行安装本工具、.NET 运行时或中文字体。

使用条件

- Windows 10 / Windows 11，64 位。
- PC 版 Assetto Corsa Competizione（ACC）。
- DualSense / DualSense Edge，使用 USB 连接。
- 已安装 ViGEmBus 虚拟手柄驱动。
- 为 ACC 禁用 Steam Input；运行本工具时完全退出 DS4Windows。

ViGEmBus 官方下载：
https://github.com/nefarius/ViGEmBus/releases

需要安装的是 ViGEmBus，不是 ViGEm.NET；后者已集成在本工具中。
如果电脑已有正常工作的 ViGEmBus，不必重复安装。

HidHide 是可选项。如果它已隐藏手柄，请在 Applications 列表中允许当前
ACCDualSenseFeedback.exe 路径访问手柄。升级或移动后需更新允许路径。

首次使用

1. 将 ZIP 完整解压到可写目录，不要直接在压缩包中运行。
2. 按需安装 ViGEmBus；安装程序要求重启时，请先重启 Windows。
3. 连接手柄，退出 DS4Windows，为 ACC 禁用 Steam Input。
4. 打开 ACCDualSenseFeedback.exe，再启动 ACC 并进入驾驶。

条件齐全时反馈自动运行。关闭窗口会停止反馈并释放虚拟手柄；
最小化时工具仍在运行。

ACC 尚未运行时，首次连接可能有短暂扳机阻力，用于确认手柄输出通道。
该提示不能证明游戏遥测已经生效。

默认与自定义

默认预设使用校准好的反馈参数，开启平滑灯光，关闭体感。
自定义模式可分别调整反馈、灯光和体感。强度为 0–100，调至 0 即关闭
该项反馈；右键参数行可恢复默认。修改即时生效，切回默认不会删除
已保存的自定义选择。

重要：“红线脉冲起点”保持默认 96.2%，已支持车型才会自动使用原生
仪表报警转速。修改后，R2 改用最高转速 94.0%–99.0% 的手动比例，
灯光恢复 97% 通用参考，二者可能不再匹配仪表。右键该行可恢复默认。
调整力度请使用独立强度控件。未知车型沿用通用转速提示。

体感转向（USB）

1. 选择“自定义 → 高级设置 → 07 体感转向 → 调整设置”。
2. 开启体感。首次使用时暂停 ACC，将手柄放稳，完成“静置校准”。
3. 自然握持手柄，点击“设为直行”，再返回 ACC 驾驶。

静置校准按手柄保存。每次启动工具或重新连接手柄后，都需要设为直行，
确认本次的自然握姿，无需因此重做静置校准。

可调整转向角度、中心死区、响应曲线和防抖，并选择持续启用、按住或
按键切换模式。持续启用在返回驾驶后自动生效；主动用绑定按钮暂停后，
仍需再次按键或点击“恢复体感”。按住与按键切换模式保留各自的按键
条件和恢复规则。可用实体摇杆接管转向，也可在工具内测试转向读数。

自定义且体感开启时，绑定的启停按钮仅用于体感，不转发给游戏。
体感仅支持 USB，请保持 ACCGyroFusion.dll 与 EXE 位于同一目录。

升级或移动应用

退出旧工具，将新 ZIP 解压到新目录，再从旧 EXE 目录复制以下已经存在
的文件：

- ACCDualSenseFeedback.settings.json
- ACCDualSenseFeedback.lights.json
- ACCDualSenseFeedback.gyro.json
- ACCDualSenseFeedback.gyro-calibration.json

中英文版使用相同的配置格式。保留新包的 EXE 与 DLL 配套使用，不要用
旧 DLL 替换新文件。使用 HidHide 时，请允许新 EXE 的路径。
启动新工具后，重新设为直行即可。

遇到问题

- 未检测到手柄：检查 USB 连接与 HidHide 应用访问权限。
- 反馈不可用：检查 ViGEmBus、手柄连接与 HidHide。
- 需要处理：退出 DS4Windows，再点击“重新检查”。
- 等待 ACC / 等待驾驶：进入驾驶；停留在菜单不属于驾驶状态。
- 重复输入：关闭 Steam Input 和其他手柄模拟工具。

关闭应用前，打开“诊断与日志 → 复制诊断报告”以便反馈问题。
会话日志只保存在内存中，退出即清空；复制的报告会脱敏 Windows 用户
目录与私密 HID 设备路径。

此 EXE 尚未数字签名，Windows SmartScreen 可能提示未知发布者。
请只从本仓库 Release 页面下载。

支持、发布与许可

https://github.com/Adudumax/ACC-DualSense-Feedback

本项目代码采用 MIT 许可证。项目及第三方许可位于 licenses 文件夹，
请与 EXE 一同保留。内嵌中文字体基于 Noto Sans SC，采用 SIL OFL 1.1，
修改后的名称为 ACC Noto UI SC，无需安装系统字体。
