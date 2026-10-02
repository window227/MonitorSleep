using System.Runtime.InteropServices;

namespace MonitorSleep.Interop;

/// <summary>
/// 通过 Core Audio (WASAPI) 的默认放音设备峰值表，判断"现在是否真的有声音在播放"。
/// 只用默认设备，开销可忽略；任何失败都返回 null（未知），绝不抛异常拖垮主程序。
/// </summary>
internal static class AudioMeter
{
    private const int eRender = 0;   // EDataFlow.eRender
    private const int eConsole = 0;  // ERole.eConsole
    private const int CLSCTX_ALL = 0x17;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out int pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(int u32ChannelCount, [Out] float[] afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out int pdwHardwareSupportMask);
    }

    /// <summary>true = 正在播放；false = 静音/无播放；null = 无法判断（无声卡或调用失败）。</summary>
    public static bool? IsPlaying(float threshold = 0.0008f)
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? meterObj = null;
        try
        {
            enumeratorObj = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;

            if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device) != 0 || device is null)
                return null;

            Guid iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out meterObj) != 0 || meterObj is null)
                return null;

            var meter = (IAudioMeterInformation)meterObj;
            if (meter.GetPeakValue(out float peak) != 0)
                return null;

            return peak > threshold;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(meterObj);
            Release(device);
            Release(enumeratorObj);
        }
    }

    private static void Release(object? o)
    {
        try
        {
            if (o is not null && Marshal.IsComObject(o))
                Marshal.ReleaseComObject(o);
        }
        catch
        {
            // 释放失败无所谓
        }
    }
}
