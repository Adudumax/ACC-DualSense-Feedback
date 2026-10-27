# CSP 手柄灯光：源码分析与 ACC 移植边界

对应历史版本：`1.1.0-preview.3`。该版以当时确认的“严格按 CSP 已有灯光规则，不额外加入 ABS 效果”为准。

`1.1.0-preview.4` 已按后续讨论改为 ACC 专用灯光，新增运行底光、怠速呼吸和制动闪黄，
并使用明确的通用转速显示区间；不再声称生产效果严格等同 CSP。
以下内容保留为 preview.3 的源码研究记录。
当前实现见 [ACC 灯光方案与验证](ACC-LIGHTING.zh-CN.md)。

## 找到的真实实现

CSP 作者公开仓库 `ac-custom-shaders-patch/acc-lua-internal`，固定提交
`2031bd2d7c913e0aad3ff18a3bab86ffccad1326`：

- [led_colors.lua](https://github.com/ac-custom-shaders-patch/acc-lua-internal/blob/2031bd2d7c913e0aad3ff18a3bab86ffccad1326/lua-module/src/led_colors.lua)：`GetCarInfo()` 和 `ComputeHighlightColor()`，决定转速区间、RGB 颜色和覆盖顺序。
- [dualsense_lights.lua](https://github.com/ac-custom-shaders-patch/acc-lua-internal/blob/2031bd2d7c913e0aad3ff18a3bab86ffccad1326/lua-module/src/dualsense_lights.lua)：`updateDualSense()`，设置五颗白灯及其硬件亮度，然后调用公共 RGB 函数。
- `lua-shared/dualsense/accent.lua`：给其他 Lua 脚本覆写颜色的接口，不是默认效果的实现。

本机安装的 `dwrite.dll` 文件版本为 `0.3.0.595`。本机 `extension/config/gamepad_fx.ini`
确认使用 `SHIFTING_COLORS=SMOOTH`，并开启 `EXTRA_COLORS`、`SESSION_START`、`BACKGROUND_COLORS`。
没有解包核对 DLL 内置脚本是否逐字等同于上述公开提交，因此复刻基准明确限定为这份已固定的公开实现。
其 MIT 许可和版权说明已保留在 `licenses/CSP-LUA-INTERNAL-LICENSE.txt`。

## 转速不是直接除以红线

原代码读取 AC 每辆车的 `ai.ini`：

```text
up   = GEARS.UP，缺省 9000
down = max(GEARS.DOWN（缺省 6000）, up / 2)
x    = (当前转速 - down) / (up - down)
```

`x=0` 是降挡参考点，`x=1` 是升挡参考点。低于 down 时 x 为负；超过 up 时 x 大于 1。
上一版使用最高转速百分比、额外时间平滑、回差和最高挡限制，与 CSP 并不一致。

### RGB 平滑模式

```text
mix = clamp(2*x + 1, 0, 1)
hue = 90° * clamp(1-x, 0, 1)
颜色 = lerp(大灯底色或黑色, HSV(hue, 1, 1), mix)
```

这是 HSV 色相变化，并非普通的绿色与红色线性插值。默认无大灯底色时，
x≤-0.5 为黑色；随后逐渐亮起，到 x=0 时是黄绿色，再向黄色、橙色、红色移动。
源码没有额外的转速时间平滑，也没有闪烁阈值回差。“平滑”描述连续颜色变化。

### RGB 分段模式

只要 mix>0：x>0.6 为红色，x>0.2 为黄色，否则为绿色。
比较均为严格大于，边界本身属于前一档；分段模式不会使用 mix 作为亮度渐变。

### 换挡提醒

当 x>0.9，RGB 灯带在一部分相位熄灭：`(flashing*3)%1 > 0.5`。
这是约 3 Hz、50% 占空比的 RGB 闪烁。白灯仍然显示转速，不参与闪烁。
计时器第一次用到时才创建；此后持续运行，暂停时停止推进，不因升挡或退出提醒区重置。
源码没有空挡或最高挡禁闪规则；倒挡白色覆盖的优先级高于转速闪烁。

### 五颗白灯

对 i=0…4，分别判断 `x > i/4`，对应 0、0.25、0.5、0.75、1 五个阈值。
所以恰好 x=1 时只有前四颗亮，超过 1 才亮第五颗。回放时用于白灯的 x 强制设为 0。

硬件亮度也随区间变化：x>0.5 用 0（最高），x>0.25 用 1（中），其余用 2（低）。
关闭 LED 淡入淡出。上一版恒定低亮度和白灯同步闪烁已移除。

## RGB 覆盖优先级

以下顺序是原函数中的 `if/elseif`，并非把颜色叠加混合：

| 顺序 | CSP 条件 | 颜色 |
| --- | --- | --- |
| 1 | 低电量警告 | 约 1 Hz 红/黑 |
| 2 | 回放 | 场景环境色；背景颜色关闭时为黑色 |
| 3 | 起步倒计时/刚起步 | 6.7 秒以内红色，起步后一秒内绿色闪烁 |
| 4 | 倒挡 | 白色 |
| 5 | 发动机损坏或双闪 | 随转向灯实际相位显示橙色/透明 |
| 6 | 低于车型油量警戒值 | RGB(1, 0.15, 0) |
| 7 | 排气火焰生效 | 黄色 |
| 8 | 转速与大灯底色 | 上述平滑或分段规则 |

这里没有 ABS、TC 介入、比赛旗语或处罚分支。相关扩展未添加。
其他 CSP Lua 应用可以用更高优先级改写颜色，不属于此默认函数。

## ACC 可以和不可以等价提供什么

| 输入/效果 | 本版实际处理 |
| --- | --- |
| 当前 RPM、挡位 | 使用现有只读快照；ACC 挡位减 1，转换为 CSP 倒挡=-1、空挡=0 的编码 |
| 车型最大转速 | 灯光独立读取 static 页 maxRpm；不采用 ACC 标注未使用的 physics.currentMaxRpm |
| ai.ini 的升降挡转速 | ACC 不提供。将 CSP 缺省的 6000:9000 区间比例缩放到 static.maxRpm；不能声称等同于 AC 每辆车的 ai.ini |
| 平滑/分段色相、RGB 闪烁、白灯与亮度 | 依照固定源码实现，不添加自行设计的回差、时间平滑、最高挡抑制或白灯闪烁 |
| 倒挡白色 | 已连接实际挡位数据 |
| 回放/暂停 | 灯光专用读取 graphics 页 packetId 与 ACC_STATUS；回放关闭白灯，暂停冻结闪烁计时 |
| 回放场景颜色 | ACC 没有 CSP 的 skyColor/lightColor，采用 CSP 关闭背景颜色时的黑色分支 |
| 起步精确倒计时 | ACC 共享内存未提供对应值，不把 sessionTimeLeft 或绿旗冒充倒计时 |
| 双闪的精确亮灭相位 | 共享内存未明确提供与 CSP turningLightsActivePhase 等价的相位；未自行制造闪烁频率 |
| 车型低油阈值、大灯 RGB、排气火焰、发动机生命值、原生电池状态 | 没有已确认的等价输入；颜色核心保留原规则，但实际接入不伪造这些信号 |

因此本版是**有明确输入边界的源码移植**，不能宣称 ACC 上全部场景、全部车型都与 AC+CSP 一模一样。
核心函数在同样输入下的一致性，与车辆实际参考转速和缺失状态的数据适配，是两个不同的验证范围。

## 与稳定震动的边界

- 未修改 `Haptics/`、原有 physics 字段读取、触觉快照、ViGEm 桥接或反馈设置格式。
- 保留触觉计算调用和原有 HID 发送条件。灯光只在原本会发送的合并报告上计算，不增加输出线程唤醒或硬件写入。
- USB 灯光字段仅涉及 42（初始化）、43（白灯亮度）、44（白灯掩码）、45–47（RGB）；电机、两组扳机和 Accurate rumble 模式不变。
- 设置页不再运行灯光演示定时器；仅有用户改动后的短延迟保存。
- 本轮未继续调整 preview.2 的进程检测和旧帧防护。这些依然属于预览版变更，不等于已经通过正式版实机验收。

## 设置与验证

高级设置只保留灯光总开关、平滑/分段选择。使用项目现有配色和分段按钮设计；
开关滑块及底色、模式选中块有 160 ms 过渡，尊重 Windows 客户区动画设置。
灯光独立保存，仅含 Enabled、Mode；旧配置的 Enabled=false 或 Brightness=0 保持关闭状态。

`tests/Reference/Csp` 保存原始脚本、生成器和 862 组对照结果。生成器实际执行原始
`ComputeHighlightColor()` 及 `updateDualSense()` 函数体，仅模拟 CSP 宿主 API。
覆盖颜色边界、3 Hz 相位、白灯比较、亮度、优先级及回放。C# 输出 RGB 与原始浮点颜色的误差
不超过半个 8 位量化级；白灯掩码和亮度完全一致。宿主 HSV/颜色容器为测试替身，未覆盖 CSP 原生 HID 编码和光学显示。

另有旧配置迁移、独立共享内存、开关动效、中英文 100%/150% 离屏渲染，以及原有触觉字节固定样本对照。
这些检查不连接手柄或创建虚拟设备，不能替代实机亮度、固件行为和游戏内体验测试。

```powershell
# 可选：重新生成 Lua 对照数据；开发依赖不会进入便携包。
$store = Join-Path (Get-Location) 'artifacts/csp-reference/pnpm-store'
pnpm --dir artifacts/csp-reference add fengari-node-cli@0.1.0 --ignore-scripts --store-dir $store
node tools/Generate-CspLightFixtures.cjs > tests/Reference/Csp/fixtures.json
dotnet run --project tests/ACCDualSenseFeedback.Tests.csproj -c Release -- --verify-no-game
dotnet run --project tests/ACCDualSenseFeedback.Tests.csproj -c Release -p:UiLanguage=zh-CN -- --render-ui artifacts/csp-lights-v3/ui-zh-CN
```
