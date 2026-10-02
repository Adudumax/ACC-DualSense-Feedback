using System.Text.RegularExpressions;

namespace ACCDualSenseFeedback.Diagnostics;

internal static partial class DiagnosticPrivacy
{
    // Apply once when the user copies a report, never in the feedback loop.
    internal static string Redact(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3)
        {
            text = RedactProfile(text, profile);
            text = RedactProfile(text, profile.Replace('\\', '/'));
        }

        // Also cover paths belonging to another account, escaped paths and NT paths.
        text = UserDirectoryRegex().Replace(text, "${prefix}[user]");
        // Device PID/transport remain available in the separate HID probe fields.
        return HidDevicePathRegex().Replace(text, "[hid-device-path]");
    }

    private static string RedactProfile(string text, string profile)
        => Regex.Replace(text, Regex.Escape(profile) + @"(?=$|[\\/\s""'<>])",
            "[user-profile]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(?<prefix>[\\/]+Users[\\/]+)[^\\/\r\n""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserDirectoryRegex();

    [GeneratedRegex(@"\\\\\?\\(?:hid|usb)#[^\s""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HidDevicePathRegex();
}
