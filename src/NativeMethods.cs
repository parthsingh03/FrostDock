using System.Runtime.InteropServices;

namespace FrostDock;

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public const int SW_RESTORE = 9;

    // Taskbar auto-hide via SHAppBarMessage
    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABM_SETSTATE = 0x0000000A;
    public const int ABS_AUTOHIDE = 0x00000001;
    public const int ABS_ALWAYSONTOP = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    public static int GetTaskbarState()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return unchecked((int)SHAppBarMessage(ABM_GETSTATE, ref data));
    }

    public static void SetTaskbarAutoHide(bool autoHide)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            lParam = (IntPtr)(autoHide ? ABS_AUTOHIDE : ABS_ALWAYSONTOP)
        };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }
}
