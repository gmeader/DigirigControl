using System.ComponentModel;
using System.Runtime.InteropServices;
using HidSharp;

namespace DigiRigControlCenter.Services;

public sealed class Cm108PttService
{
    public const int DigiRigVendorId = 0x0D8C;
    public const int DigiRigLiteProductId = 0x0012;

    public HidDevice? Device { get; private set; }
    public string DeviceDescription => Device == null ? "Not detected" : $"C-Media {Device.VendorID:X4}:{Device.ProductID:X4}";

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        nint hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        nint lpOverlapped);


    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    private static readonly nint InvalidHandleValue = new(-1);

    public bool Refresh()
    {
        Device = DeviceList.Local.GetHidDevices(DigiRigVendorId, DigiRigLiteProductId).FirstOrDefault();
        return Device != null;
    }

    // CM108 GPIO3 uses bit 2.  The current Dire Wolf/hidapi Windows
    // implementation sends: report-id, 0, data, direction-mask, 0.
    // GPIO3 ON  = 00 00 04 04 00
    // GPIO3 OFF = 00 00 00 04 00
    private static byte[] MakeReport(bool on) => new byte[]
    {
        0x00,
        0x00,
        on ? (byte)0x04 : (byte)0x00,
        0x04,
        0x00
    };

    public bool SetPtt(bool on, out string error)
    {
        error = "";
        if (Device == null && !Refresh())
        {
            error = "DigiRig Lite C-Media CM108B HID interface was not found.";
            return false;
        }

        var path = Device!.DevicePath;
        var report = MakeReport(on);

        var handle = CreateFile(
            path,
            GenericWrite,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            0,
            0);

        if (handle == InvalidHandleValue)
        {
            var win32 = Marshal.GetLastWin32Error();
            error = "CreateFile failed: " + new Win32Exception(win32).Message;
            return false;
        }

        try
        {
            if (!WriteFile(handle, report, (uint)report.Length, out var written, 0))
            {
                var win32 = Marshal.GetLastWin32Error();
                error = "WriteFile failed: " + new Win32Exception(win32).Message;
                return false;
            }

            if (written != report.Length)
            {
                error = $"HID write was incomplete ({written}/{report.Length} bytes).";
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public bool TestPtt(TimeSpan duration, out string error)
    {
        error = "";
        duration = duration < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : duration;

        // One native HID write asserts GPIO3, then a second native HID write
        // releases it after the requested interval.
        if (!SetPtt(true, out error))
            return false;

        try
        {
            Thread.Sleep(duration);
        }
        finally
        {
            if (!SetPtt(false, out var releaseError))
            {
                error = string.IsNullOrWhiteSpace(releaseError)
                    ? "PTT release failed."
                    : "PTT release failed: " + releaseError;
            }
        }

        return string.IsNullOrWhiteSpace(error);
    }
}
