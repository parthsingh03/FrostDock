using System.Management;

namespace FrostDock;

/// <summary>
/// Laptop-panel brightness via WMI (root\WMI: WmiMonitorBrightness /
/// WmiMonitorBrightnessMethods), the only API that reaches internal laptop
/// panels (DDC/CI does not). If the machine has no such panel (desktop
/// monitors), IsAvailable is false and the Control Center brightness row
/// disables itself with an honest note — never a dead slider.
/// </summary>
internal static class BrightnessControl
{
    public static bool TryGet(out byte level)
    {
        level = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    var v = mo["CurrentBrightness"];
                    if (v != null) { level = Convert.ToByte(v); return true; }
                }
                catch { }
                finally { try { mo.Dispose(); } catch { } }
            }
            return false;
        }
        catch { return false; }
    }

    public static bool TrySet(byte level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    // WmiSetBrightness(UInt32 Timeout, UInt8 Brightness).
                    var result = mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, level });
                    return result == null || (uint)result == 0;
                }
                catch { return false; }
                finally { try { mo.Dispose(); } catch { } }
            }
            return false;
        }
        catch { return false; }
    }
}
