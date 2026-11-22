using System.Runtime.InteropServices;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Queries Windows Battery Saver status via GetSystemPowerStatus so decorative
/// backdrop animation freezes automatically when Battery Saver is active.
/// </summary>
internal static class PowerStatusHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag; // 1 == Battery Saver ON
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    internal static bool? ForceBatterySaverOverrideForTest { get; set; }

    public static bool IsBatterySaverActive
    {
        get
        {
            if (ForceBatterySaverOverrideForTest.HasValue)
            {
                return ForceBatterySaverOverrideForTest.Value;
            }

            try
            {
                if (GetSystemPowerStatus(out var status))
                {
                    return status.SystemStatusFlag == 1;
                }
            }
            catch
            {
                // Ignore query failure on non-standard hosts; default to not active
            }

            return false;
        }
    }
}
