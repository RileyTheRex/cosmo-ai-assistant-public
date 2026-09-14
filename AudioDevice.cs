using System;
using System.Runtime.InteropServices;

namespace OliverAssistant
{
    // Minimal Core Audio API (WASAPI) interop, just enough to read the friendly
    // name of the current default microphone. No external libraries required.
    internal enum EDataFlow { eRender, eCapture, eAll }
    internal enum ERole { eConsole, eMultimedia, eCommunications }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        int GetCount(out int cProps);
        int GetAt(int iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    public static class AudioDevice
    {
        private static readonly Guid PKEY_Device_FriendlyName_Fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        private const int PKEY_Device_FriendlyName_Pid = 14;

        public static string GetDefaultMicrophoneName()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice device;
                var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
                if (hr != 0 || device == null) return "(default device — name unavailable)";

                IPropertyStore store;
                device.OpenPropertyStore(0, out store);

                var key = new PropertyKey { fmtid = PKEY_Device_FriendlyName_Fmtid, pid = PKEY_Device_FriendlyName_Pid };
                PropVariant value;
                store.GetValue(ref key, out value);

                if (value.pointerValue == IntPtr.Zero) return "(default device — name unavailable)";
                return Marshal.PtrToStringUni(value.pointerValue);
            }
            catch
            {
                return "(default device — name unavailable)";
            }
        }
    }
}
