using System;
using System.Runtime.InteropServices;

namespace AudioVisualizer.Services;

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[Flags]
internal enum AUDCLNT_STREAMFLAGS : uint
{
    None = 0x00000000,
    AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000,
    AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000
}

[Flags]
internal enum AUDCLNT_BUFFERFLAGS : uint
{
    None = 0,
    AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1,
    AUDCLNT_BUFFERFLAGS_SILENT = 0x2,
    AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4
}

internal static class WasapiGuids
{
    public const uint CLSCTX_ALL = 23;
    public const uint DEVICE_STATE_ACTIVE = 0x00000001;

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IMMDevice = new("D666063F-1587-4E43-81F1-B948E807363F");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice? ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        int ShareMode,
        uint StreamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr pFormat,
        ref Guid AudioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
}

[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr ppData,
        out uint pNumFramesToRead,
        out AUDCLNT_BUFFERFLAGS pdwFlags,
        out ulong pu64DevicePosition,
        out ulong pu64QPCPosition);

    [PreserveSig]
    int ReleaseBuffer(uint NumFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}
