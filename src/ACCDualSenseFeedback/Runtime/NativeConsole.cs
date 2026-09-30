using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ACCDualSenseFeedback.Runtime;

internal static partial class NativeConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void TryAttachParent()
    {
        if (GetConsoleWindow() != IntPtr.Zero || !AttachConsole(AttachParentProcess))
            return;

        Console.SetOut(CreateWriter(Console.OpenStandardOutput()));
        Console.SetError(CreateWriter(Console.OpenStandardError()));
        Console.SetIn(new StreamReader(
            new FileStream(new SafeFileHandle(GetStdHandle(-10), ownsHandle: false), FileAccess.Read)));
    }

    private static StreamWriter CreateWriter(Stream stream) => new(stream)
    {
        AutoFlush = true
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int standardHandle);
}
