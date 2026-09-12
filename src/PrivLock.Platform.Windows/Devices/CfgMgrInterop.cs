using System.Runtime.InteropServices;

namespace PrivLock.Platform.Windows.Devices;

/// <summary>
/// P/Invoke declarations for CfgMgr32.dll (Configuration Manager) on Windows.
/// Used to locate, disable, and enable PnP device nodes directly in-process.
/// </summary>
internal static class CfgMgrInterop
{
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    internal static extern uint CM_Disable_DevNode(
        uint dnDevInst,
        uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    internal static extern uint CM_Enable_DevNode(
        uint dnDevInst,
        uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    internal static extern uint CM_Get_DevNode_Status(
        out uint pulStatus,
        out uint pulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    internal const uint CR_SUCCESS = 0x00000000;
    internal const uint CR_NO_SUCH_DEVNODE = 0x0000000D;
    internal const uint CR_ACCESS_DENIED = 0x00000033;

    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    internal const uint CM_DISABLE_UI_NOT_OK = 0x00000004;

    internal const uint DN_STARTED = 0x00000008;
    internal const uint DN_DISABLEABLE = 0x00002000;
    internal const uint DN_HAS_PROBLEM = 0x00000400;

    internal const uint CM_PROB_DISABLED = 0x00000016; // 22 = Device is disabled
}
