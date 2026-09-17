using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DigiRigControlCenter.Services;

/// <summary>
/// Small native Core Audio property-store helper.  This deliberately avoids
/// NAudio's version-specific PropertyStore/PropVariant API so the application
/// can build consistently with NAudio 2.2.1.
/// </summary>
internal static class WindowsAudioPropertyService
{
    private static readonly Guid PKEY_AudioEndpoint_Disable_SysFx_Fmtid =
        new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

    private const uint PKEY_AudioEndpoint_Disable_SysFx_Pid = 5;
    private const ushort VT_UI4 = 19;
    private const ushort VT_LPWSTR = 31;
    private const uint STGM_READ = 0;
    private const uint STGM_READWRITE = 2;

    public static bool TryGetSystemEffectsDisabled(string deviceId, out bool disabled)
    {
        disabled = false;
        try
        {
            var store = OpenStore(deviceId, STGM_READ);
            try
            {
                var key = new PROPERTYKEY(PKEY_AudioEndpoint_Disable_SysFx_Fmtid,
                    PKEY_AudioEndpoint_Disable_SysFx_Pid);
                var hr = store.GetValue(ref key, out var value);
                if (hr != 0)
                    return false;

                disabled = value.vt == VT_UI4 && value.uintVal != 0;
                return value.vt == VT_UI4;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return false;
        }
    }


    private static readonly Guid PKEY_Device_FriendlyName_Fmtid =
        new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint PKEY_Device_FriendlyName_Pid = 14;

    public static bool TrySetFriendlyName(string deviceId, string friendlyName, out string message)
    {
        IntPtr text = IntPtr.Zero;
        try
        {
            var store = OpenStore(deviceId, STGM_READWRITE);
            try
            {
                var key = new PROPERTYKEY(PKEY_Device_FriendlyName_Fmtid, PKEY_Device_FriendlyName_Pid);
                text = Marshal.StringToCoTaskMemUni(friendlyName);
                var value = new PROPVARIANT
                {
                    vt = VT_LPWSTR,
                    pointerVal = text
                };

                var hr = store.SetValue(ref key, ref value);
                if (hr != 0)
                {
                    message = $"Windows rejected the device-name change (HRESULT 0x{hr:X8}).";
                    return false;
                }

                hr = store.Commit();
                if (hr != 0)
                {
                    message = $"Windows could not commit the device-name change (HRESULT 0x{hr:X8}).";
                    return false;
                }

                message = $"Device renamed to {friendlyName}.";
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex)
        {
            message = "Unable to rename the Windows audio device: " + ex.Message;
            return false;
        }
        finally
        {
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
        }
    }

    public static bool TrySetSystemEffectsDisabled(string deviceId, bool disabled, out string message)
    {
        try
        {
            var store = OpenStore(deviceId, STGM_READWRITE);
            try
            {
                var key = new PROPERTYKEY(PKEY_AudioEndpoint_Disable_SysFx_Fmtid,
                    PKEY_AudioEndpoint_Disable_SysFx_Pid);
                var value = new PROPVARIANT
                {
                    vt = VT_UI4,
                    uintVal = disabled ? 1u : 0u
                };

                var hr = store.SetValue(ref key, ref value);
                if (hr != 0)
                {
                    message = $"Windows audio property store rejected the change (HRESULT 0x{hr:X8}).";
                    return false;
                }

                hr = store.Commit();
                if (hr != 0)
                {
                    message = $"Windows audio property store could not commit the change (HRESULT 0x{hr:X8}).";
                    return false;
                }

                message = disabled
                    ? "Windows audio system effects are disabled for this endpoint."
                    : "Windows audio system effects are enabled for this endpoint.";
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex)
        {
            message = "Unable to change the Windows audio effects setting: " + ex.Message;
            return false;
        }
    }

    private static IPropertyStore OpenStore(string deviceId, uint access)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            var hr = enumerator.GetDevice(deviceId, out var device);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);

            hr = device.OpenPropertyStore(access, out var store);
            Marshal.ReleaseComObject(device);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);

            return store;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort reserved1;
        [FieldOffset(4)] public ushort reserved2;
        [FieldOffset(6)] public ushort reserved3;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public IntPtr pointerVal;
    }
}
