using System.Runtime.InteropServices;
using System.Text;

namespace FrostDock;

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int VK_F4 = 0x73;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterWindowMessage(string lpString);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // v1.4 media-key fallback (Control Center when GSMTC interop is
    // unavailable): plain key events, no session, no track info.
    public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    public const byte VK_MEDIA_PREV_TRACK = 0xB1;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void SendMediaKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
        }
        catch { }
    }

    /// <summary>
    /// v1.4: true when a real File Explorer window exists. Explorer folder
    /// windows are top-level windows of class CabinetWClass; the shell
    /// itself (desktop/taskbar) never is, so this cleanly separates "a
    /// folder is open" from "explorer.exe is running" (which is always).
    /// </summary>
    public static bool ExplorerWindowOpen()
    {
        try
        {
            bool found = false;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "CabinetWClass") { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        catch { return false; }
    }

    public const int SW_RESTORE = 9;

    // ---- Taskbar auto-hide + AppBar (SHAppBarMessage) ----
    public const uint ABM_NEW = 0x00000000;
    public const uint ABM_REMOVE = 0x00000001;
    public const uint ABM_QUERYPOS = 0x00000002;
    public const uint ABM_SETPOS = 0x00000003;
    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABM_SETSTATE = 0x0000000A;
    public const uint ABE_TOP = 0x00000001;
    public const uint ABN_POSCHANGED = 0x00000001;
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

    // ---- Layered window (UpdateLayeredWindow) ----
    public const int WS_EX_LAYERED = 0x00080000;
    public const int ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect,
        int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    // ---- DWM blur-behind, clipped to a region ----
    public const uint DWM_BB_ENABLE = 0x00000001;
    public const uint DWM_BB_BLURREGION = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll", PreserveSig = false)]
    public static extern void DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    // ---- Acrylic / blur composition attribute (full-rect windows only) ----
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attrib;
        public IntPtr pvData;
        public int cbData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    public static void EnableAcrylic(IntPtr hWnd, int gradientColor)
    {
        try
        {
            var policy = new ACCENT_POLICY
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0,
                GradientColor = gradientColor,
                AnimationId = 0
            };
            int size = Marshal.SizeOf(policy);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attrib = WCA_ACCENT_POLICY,
                    pvData = ptr,
                    cbData = size
                };
                SetWindowCompositionAttribute(hWnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { /* acrylic is cosmetic - never crash the app over it */ }
    }

    // ---- Process / package helpers (UWP resolution) ----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetPackageFullName(IntPtr hProcess, ref uint packageFullNameLength, StringBuilder packageFullName);

    // ---- Shell item image factory (high-res icons) ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    // ---- DIB access (alpha-preserving HBITMAP conversion) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        IntPtr lpvBits, ref BITMAPINFO lpbmi, uint uUsage);

    // ---- Wallpaper path (SystemParametersInfo) ----
    public const uint SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    // ---- Recycle Bin state (SHQueryRecycleBin) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    public static bool TryGetRecycleBinCount(out long items)
    {
        items = 0;
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            int hr = SHQueryRecycleBin(string.Empty, ref info);
            if (hr != 0) return false;
            items = info.i64NumItems;
            return true;
        }
        catch { return false; }
    }

    // ---- Bluetooth radio presence (bthprops.cpl) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern bool BluetoothCloseHandle(IntPtr hRadio);

    public static bool BluetoothAdapterPresent()
    {
        try
        {
            var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref p, out IntPtr hRadio);
            if (hFind != IntPtr.Zero)
            {
                try { BluetoothFindRadioClose(hFind); } catch { }
            }
            if (hRadio != IntPtr.Zero)
            {
                try { BluetoothCloseHandle(hRadio); } catch { }
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ---- .lnk resolution (IShellLinkW + IPersistFile) ----
    [DllImport("ole32.dll", SetLastError = true)]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellLinkW ppv);
}

/// <summary>IShellItemImageFactory (shell32). Only GetImage is needed.</summary>
[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeMethods.SIZE size, ShellIcons.SIIGBF flags, out IntPtr phbm);
}

/// <summary>IShellLinkW: GetPath is the first vtable slot, so only it is declared.</summary>
[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    [PreserveSig]
    int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
}

/// <summary>IPersistFile: Load is the third vtable slot, so the first three are declared.</summary>
[ComImport]
[Guid("0000010B-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);
    [PreserveSig]
    int IsDirty();
    [PreserveSig]
    int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
}
