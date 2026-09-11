using System.Runtime.InteropServices;

namespace CamMicBlocker.Infrastructure.Win32;

/// <summary>
/// P/Invoke declarations for CfgMgr32.dll (Configuration Manager).
/// Used by the elevated helper to enable/disable PnP devices.
/// </summary>
internal static class CfgMgrInterop
{
    /// <summary>Locate a device instance by its device instance ID.</summary>
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    /// <summary>Disable a device node.</summary>
    [DllImport("CfgMgr32.dll")]
    internal static extern uint CM_Disable_DevNode(
        uint dnDevInst,
        uint ulFlags);

    /// <summary>Enable a device node.</summary>
    [DllImport("CfgMgr32.dll")]
    internal static extern uint CM_Enable_DevNode(
        uint dnDevInst,
        uint ulFlags);

    /// <summary>Get the status of a device node.</summary>
    [DllImport("CfgMgr32.dll")]
    internal static extern uint CM_Get_DevNode_Status(
        out uint pulStatus,
        out uint pulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    // Return codes
    internal const uint CR_SUCCESS = 0x00000000;
    internal const uint CR_NO_SUCH_DEVNODE = 0x0000000D;
    internal const uint CR_ACCESS_DENIED = 0x00000033;

    // Flags for CM_Locate_DevNode
    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    internal const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;

    // Flags for CM_Disable_DevNode
    internal const uint CM_DISABLE_UI_NOT_OK = 0x00000004;

    // Device status flags (from CM_Get_DevNode_Status)
    internal const uint DN_STARTED = 0x00000008;
    internal const uint DN_DISABLEABLE = 0x00002000;
    internal const uint DN_HAS_PROBLEM = 0x00000400;

    // Problem codes
    internal const uint CM_PROB_DISABLED = 0x00000016; // 22 = Device is disabled
}
