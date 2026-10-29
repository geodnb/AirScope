using System.Runtime.InteropServices;

namespace AirScope.Interop;

/// <summary>
/// Minimal P/Invoke surface for the Native Wi-Fi API (wlanapi.dll).
/// Everything here is read-only apart from WlanScan, which only asks the
/// driver to refresh its list of nearby networks.
/// Struct layouts are read by explicit byte offset (see WlanClient) rather than
/// marshalled, so a size mismatch can never write past a buffer.
/// </summary>
internal static class WlanNative
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_INVALID_STATE = 5023;
    public const uint ERROR_NDIS_DOT11_POWER_STATE_INVALID = 0x80342002;

    public const uint WLAN_CLIENT_VERSION_2 = 2;

    // Notification sources
    public const uint WLAN_NOTIFICATION_SOURCE_NONE = 0;
    public const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x08;
    public const uint WLAN_NOTIFICATION_SOURCE_MSM = 0x10;

    // ACM codes
    public const uint ACM_SCAN_COMPLETE = 7;
    public const uint ACM_SCAN_FAIL = 8;
    public const uint ACM_CONNECTION_COMPLETE = 10;
    public const uint ACM_INTERFACE_ARRIVAL = 13;
    public const uint ACM_INTERFACE_REMOVAL = 14;
    public const uint ACM_DISCONNECTED = 21;
    public const uint ACM_SCAN_LIST_REFRESH = 26;

    // MSM codes
    public const uint MSM_CONNECTED = 4;
    public const uint MSM_ROAMING_START = 5;
    public const uint MSM_ROAMING_END = 6;
    public const uint MSM_DISCONNECTED = 10;
    public const uint MSM_LINK_DEGRADED = 15;
    public const uint MSM_LINK_IMPROVED = 16;

    // WLAN_INTF_OPCODE
    public const int OPCODE_CURRENT_CONNECTION = 7;
    public const int OPCODE_CHANNEL_NUMBER = 8;
    public const int OPCODE_STATISTICS = 0x10000101;
    public const int OPCODE_RSSI = 0x10000102;

    public const int DOT11_BSS_TYPE_ANY = 3;

    // Fixed struct sizes (x86 and x64 identical for these – no pointers inside)
    public const int WLAN_INTERFACE_INFO_SIZE = 532;      // GUID + WCHAR[256] + state
    public const int WLAN_BSS_ENTRY_SIZE = 360;
    public const int WLAN_CONNECTION_ATTRIBUTES_SIZE = 604;

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_NOTIFICATION_DATA
    {
        public uint NotificationSource;
        public uint NotificationCode;
        public Guid InterfaceGuid;
        public uint dwDataSize;
        public IntPtr pData;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WlanNotificationCallback(IntPtr data, IntPtr context);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanScan(IntPtr hClientHandle, in Guid pInterfaceGuid,
        IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanGetNetworkBssList(IntPtr hClientHandle, in Guid pInterfaceGuid,
        IntPtr pDot11Ssid, int dot11BssType, [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled,
        IntPtr pReserved, out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanQueryInterface(IntPtr hClientHandle, in Guid pInterfaceGuid,
        int opCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, out int pWlanOpcodeValueType);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern uint WlanRegisterNotification(IntPtr hClientHandle, uint dwNotifSource,
        [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate, WlanNotificationCallback? funcCallback,
        IntPtr pCallbackContext, IntPtr pReserved, out uint pdwPrevNotifSource);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    public static extern void WlanFreeMemory(IntPtr pMemory);
}
