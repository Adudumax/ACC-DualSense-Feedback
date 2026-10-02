using System.Globalization;
using System.Windows.Markup;

namespace ACCDualSenseFeedback.Ui;

// Translation happens only on the presentation path. Diagnostic identifiers,
// profile keys and the feedback engine remain language-independent.
public static class UiText
{
#if ZH_CN
    public const bool IsChinese = true;
    private static readonly Dictionary<string, string> Translations = new(StringComparer.Ordinal)
    {
        ["Minimize"] = "最小化",
        ["Close"] = "关闭",
        ["Starting"] = "正在启动",
        ["Preparing the controller path."] = "正在建立手柄连接。",
        ["Check again"] = "重新检查",
        ["Active preset"] = "当前预设",
        ["Default"] = "默认预设",
        ["Custom"] = "自定义预设",
        ["AUTOMATIC"] = "已校准",
        ["PERSONAL"] = "个人设置",
        ["Balanced vibration and trigger response. Feedback runs automatically while the app is open."] = "均衡的震动与扳机反馈。\n应用开启时自动运行，关闭后即停止。",
        ["Your saved feel. Feedback still runs automatically while the app is open."] = "按你的习惯保存震动与扳机设置。\n应用开启时自动运行，关闭后即停止。",
        ["Default preset"] = "默认预设",
        ["Custom preset"] = "自定义预设",
        ["Checking USB"] = "检查 USB",
        ["Waiting"] = "等待连接",
        ["USB"] = "USB 已连接",
        ["Telemetry live"] = "遥测正常",
        ["Game detected"] = "已检测到游戏",
        ["‹  Back"] = "‹  返回",
        ["Back to home"] = "返回首页",
        ["Feedback modules"] = "反馈调校",
        ["Tune each cue independently. Vehicle detection stays automatic."] = "各项反馈独立调节，车辆识别与判定保持自动。",
        ["Pedal resistance"] = "踏板阻力",
        ["Resting L2 and R2 force · event pulses stay independent"] = "L2 / R2 的基础阻力，独立于事件脉冲。",
        ["Brake resistance"] = "刹车扳机阻力",
        ["L2 · base force"] = "L2 · 基础阻力",
        ["Brake trigger base resistance"] = "刹车扳机阻力",
        ["Throttle resistance"] = "油门扳机阻力",
        ["R2 · base force"] = "R2 · 基础阻力",
        ["Throttle trigger base resistance"] = "油门扳机阻力",
        ["Set to zero to turn off. Right-click to reset to 50."] = "调至 0 关闭，右键重置为 50。",
        ["Set to zero to turn off. Right-click to reset to 20."] = "调至 0 关闭，右键重置为 20。",
        ["Set to zero to turn off. Right-click to reset to 60."] = "调至 0 关闭，右键重置为 60。",
        ["Braking cues"] = "制动反馈",
        ["Fast ABS rhythm and heavier wheel-lock warning"] = "区分 ABS 介入与车轮抱死。",
        ["ABS pulse"] = "ABS 脉冲",
        ["L2 + grips · intervention"] = "L2 与握把 · ABS 介入",
        ["ABS pulse strength"] = "ABS 脉冲强度",
        ["Wheel lock"] = "刹车抱死提示",
        ["L2 + grips · ABS off"] = "L2 与握把 · ABS 关闭时",
        ["Wheel lock pulse strength"] = "刹车抱死提示强度",
        ["Powertrain"] = "动力反馈",
        ["Subtle engine texture, shift impact and maximum RPM"] = "发动机底噪、升挡冲击与高转速提示。",
        ["Engine texture"] = "发动机底噪",
        ["Grips · quiet RPM character"] = "握把 · 随转速变化",
        ["Engine texture strength"] = "发动机底噪强度",
        ["Upshift kick"] = "升挡冲击",
        ["R2 + grips · short impact"] = "R2 与握把 · 短促冲击",
        ["Upshift impact strength"] = "升挡冲击强度",
        ["Redline pulse"] = "红线脉冲",
        ["R2 · high-RPM warning"] = "R2 · 高转速提示",
        ["Redline pulse strength"] = "红线脉冲强度",
        ["Pulse begins"] = "红线脉冲起点",
        ["Share of the car's max RPM"] = "占车辆最高转速的比例",
        ["Redline pulse start RPM percentage"] = "红线脉冲起始转速比例",
        ["Right-click to reset to 96.2 percent."] = "右键重置为 96.2%。",
        ["Traction"] = "牵引力反馈",
        ["Separate assisted intervention from raw wheelspin"] = "区分 TC 介入与未经辅助的车轮空转。",
        ["TC pulse"] = "TC 脉冲",
        ["R2 · intervention"] = "R2 · 牵引力控制介入",
        ["Traction control pulse strength"] = "TC 脉冲强度",
        ["Wheelspin warning"] = "车轮空转提示",
        ["R2 · TC off"] = "R2 · TC 关闭时",
        ["Wheelspin warning strength"] = "车轮空转提示强度",
        ["Surface & grip"] = "路面与抓地力",
        ["Directional tyre information and ACC's original texture"] = "保留方向信息与 ACC 原生震动细节。",
        ["Road & kerbs"] = "路面与路肩",
        ["Road and kerbs"] = "路面与路肩",
        ["Grips · directional impacts"] = "握把 · 方向性路面反馈",
        ["Road and kerb strength"] = "路面与路肩强度",
        ["Grip loss"] = "失去抓地力",
        ["Grips · tyre slip"] = "握把 · 轮胎打滑",
        ["Tyre grip loss strength"] = "失去抓地力强度",
        ["ACC original vibration"] = "ACC 原生震动",
        ["Original ACC"] = "ACC 原生震动",
        ["Grips · native game texture"] = "握把 · 游戏原有反馈",
        ["Original ACC vibration strength"] = "ACC 原生震动强度",
        ["Collision impact"] = "碰撞冲击",
        ["Grips · short directional hit"] = "握把 · 短促方向性冲击",
        ["Collision impact strength"] = "碰撞冲击强度",
        ["50 · Standard"] = "50 · 标准",
        ["60 · Standard"] = "60 · 标准",
        ["20 · Light"] = "20 · 轻微",
        ["0 · Off"] = "0 · 关闭",
        ["Light"] = "轻微",
        ["Soft"] = "柔和",
        ["Standard"] = "标准",
        ["Strong"] = "强烈",
        ["Intense"] = "很强",
        ["0 = Off · Right-click a parameter to reset it."] = "调至 0 关闭，右键可重置单项。",
        ["Reset all"] = "全部重置",
        ["Reset all feedback modules"] = "重置全部反馈参数",
        ["Diagnostics & logs"] = "诊断与日志",
        ["Diagnostics and logs"] = "诊断与日志",
        ["Advanced settings"] = "高级设置",
        ["A clear view of the signal path. Nothing is written to disk."] = "查看连接与反馈状态，运行日志仅保留在内存中。",
        ["Virtual controller"] = "虚拟手柄",
        ["ACC telemetry"] = "ACC 遥测",
        ["Feedback"] = "反馈输出",
        ["Idle"] = "待机",
        ["Active"] = "运行中",
        ["Connected · USB"] = "已连接 / USB",
        ["Session log"] = "本次运行",
        ["Copy diagnostic report"] = "复制诊断报告",
        ["No state changes yet."] = "暂无状态变化。",
        ["Thanks to HamzaYslmn for the DualSense technical reference · "] = "感谢 HamzaYslmn 提供 DualSense 技术参考 · ",
        ["Source"] = "参考项目",
        ["App ready"] = "应用已就绪。",
        ["Settings reset"] = "已重置设置",
        ["The saved choices could not be read. Balanced defaults are active."] = "无法读取保存的设置，已启用默认预设。",
        ["Action needed"] = "需要处理",
        ["Close DS4Windows before starting."] = "请先关闭 DS4Windows。",
        ["Waiting for DualSense"] = "等待手柄",
        ["Connect the controller by USB."] = "请通过 USB 连接 DualSense。",
        ["Ready for ACC"] = "已就绪",
        ["DualSense connected. Start ACC when ready."] = "手柄已连接，可以启动 ACC。",
        ["Waiting for session"] = "等待驾驶",
        ["ACC detected. Enter a driving session."] = "已检测到 ACC，请进入驾驶。",
        ["Feedback active"] = "反馈运行中",
        ["DualSense connected. ACC telemetry live."] = "手柄已连接，ACC 遥测正常。",
        ["DualSense unavailable"] = "未检测到手柄",
        ["Feedback unavailable"] = "反馈不可用",
        ["Connect by USB. Check HidHide access."] = "请连接 USB，检查 HidHide 访问权限。",
        ["Check ViGEmBus, USB and HidHide."] = "请检查 ViGEmBus、USB 与 HidHide。",
        ["Balanced response is already active."] = "当前已使用默认反馈设置。",
        ["Balanced response is active automatically."] = "已应用默认反馈设置。",
        ["Your saved feel is already active."] = "当前已使用你保存的反馈设置。",
        ["Your saved vibration and trigger feel is active."] = "已应用你保存的震动与扳机设置。",
        ["Already at default"] = "当前已是默认值",
        ["{0} is already {1}."] = "{0}当前已是 {1}。",
        ["Parameter reset"] = "已重置参数",
        ["{0} is back to {1}."] = "{0}已恢复为 {1}。",
        ["Modules reset"] = "已重置全部参数",
        ["Every feedback module is back at the calibrated standard."] = "全部反馈参数已恢复为默认值。",
        ["Change active"] = "设置已生效",
        ["This choice works now, but the app folder did not allow it to be saved."] = "设置已生效，但应用目录无法写入，未能保存。",
        ["The feel works now, but the app folder did not allow it to be saved."] = "设置已生效，但应用目录无法写入，未能保存。",
        ["Diagnostic report copied"] = "已复制诊断报告",
        ["Driver checks, runtime state and recent session events are on the clipboard."] = "驱动检查、运行状态与近期事件已复制到剪贴板。",
        ["Could not copy"] = "复制失败",
        ["The clipboard is busy. Try again in a moment."] = "剪贴板正忙，请稍后重试。",
        ["Could not open the link"] = "无法打开链接",
        ["See licenses\\THIRD_PARTY_NOTICES.md instead."] = "请查看 licenses\\THIRD_PARTY_NOTICES.md。",
        ["ACC DualSense Feedback is already running."] = "ACC DualSense 已在运行，请使用已打开的窗口。"
    };
#else
    public const bool IsChinese = false;
#endif

    public static string Get(string text)
    {
#if ZH_CN
        return Translations.TryGetValue(text, out string? translated) ? translated : text;
#else
        return text;
#endif
    }

    public static string Format(string format, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(format), args);

    // Short segment labels are intentionally distinct from the display title.
    public static string DefaultSegment => IsChinese ? "默认" : "Default";
    public static string CustomSegment => IsChinese ? "自定义" : "Custom";
    public static string WaitingForAcc => IsChinese ? "等待游戏" : "Waiting";
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    public string Text { get; set; } = string.Empty;
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(Text);
}
