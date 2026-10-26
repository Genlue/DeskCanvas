using System;
using System.Runtime.InteropServices;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// The raw Windows.Graphics.Capture ABI this layer needs — activation factories, capture items,
/// frame pools, sessions and the D3D11 texture behind a frame.
/// <para>
/// Everything here is a direct vtable call on an <see cref="IntPtr"/>. The alternative — the
/// CsWinRT projections — would force the whole application (and every widget and check project that
/// references it) onto a Windows-specific target framework for the sake of one capture path. The
/// ABI is small and stable, so the cost of talking to it directly is a handful of slot indices,
/// and the benefit is that the sampling layer stays an implementation detail of the host.
/// </para>
/// <para>
/// All of these interfaces are <c>IInspectable</c>-derived, so their own methods start at vtable
/// slot 6 (0–2 IUnknown, 3–5 IInspectable). The slots below were taken from the Windows SDK
/// 10.0.26100 metadata and each is annotated with the method it addresses — that mapping is the one
/// thing to re-check if this ever stops working on a future Windows build.
/// </para>
/// </summary>
internal static unsafe class WinRtInterop
{
    // ---- IIDs (Windows SDK 10.0.26100 metadata) ----
    private static readonly Guid CaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid FramePoolStatics2Iid = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid Session2Iid = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid Session3Iid = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid Session5Iid = new("67C0EA62-1F85-5061-925A-239BE0AC09CB");
    private static readonly Guid ClosableIid = new("30D168F3-0D18-4C90-8FAE-3D82F9D2CAF4");

    /// <summary><c>IDirect3DDxgiInterfaceAccess</c> — reaches the D3D11 texture behind a surface.</summary>
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary><c>ID3D11Texture2D</c>.</summary>
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary><c>DirectXPixelFormat.B8G8R8A8UIntNormalized</c>.</summary>
    private const int PixelFormatBgra8 = 87;

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>
    /// Put the calling thread into the multithreaded apartment, which is what a free-threaded frame
    /// pool expects. Threads that are already initialised (the render side, the CLR's own COM setup)
    /// report <c>RPC_E_CHANGED_MODE</c>, which is not an error here.
    /// </summary>
    public static void EnsureApartment()
    {
        try { RoInitialize(1 /* RO_INIT_MULTITHREADED */); } catch { }
    }

    /// <summary>The <c>IGraphicsCaptureItemInterop</c> activation factory, or <see cref="IntPtr.Zero"/>.</summary>
    public static IntPtr CaptureItemInteropFactory() => ActivationFactory(
        "Windows.Graphics.Capture.GraphicsCaptureItem", CaptureItemInteropIid);

    /// <summary><c>IGraphicsCaptureItemInterop::CreateForWindow</c> — slot 3.</summary>
    public static IntPtr CreateForWindow(IntPtr factory, IntPtr window) => CreateItem(factory, 3, window);

    /// <summary><c>IGraphicsCaptureItemInterop::CreateForMonitor</c> — slot 4.</summary>
    public static IntPtr CreateForMonitor(IntPtr factory, IntPtr monitor) => CreateItem(factory, 4, monitor);

    private static IntPtr CreateItem(IntPtr factory, int slot, IntPtr target)
    {
        if (factory == IntPtr.Zero || target == IntPtr.Zero) return IntPtr.Zero;
        var vtbl = *(IntPtr**)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot];
        IntPtr result;
        var iid = CaptureItemIid;
        return fn(factory, target, &iid, &result) < 0 ? IntPtr.Zero : result;
    }

    /// <summary><c>IGraphicsCaptureItem::get_Size</c> — slot 7 (<c>SizeInt32</c> = two int32).</summary>
    public static bool TryGetItemSize(IntPtr item, out int width, out int height)
    {
        width = height = 0;
        if (item == IntPtr.Zero) return false;
        var vtbl = *(IntPtr**)item;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)vtbl[7];
        long size;
        if (fn(item, &size) < 0) return false;
        width = (int)(size & 0xFFFFFFFF);
        height = (int)((size >> 32) & 0xFFFFFFFF);
        return width > 0 && height > 0;
    }

    /// <summary>
    /// <c>IDirect3D11CaptureFramePoolStatics2::CreateFreeThreaded</c> — slot 6 on the class's
    /// activation factory. <paramref name="d3dDevice"/> is the <c>IDirect3DDevice</c> produced by
    /// <c>CreateDirect3D11DeviceFromDXGIDevice</c>.
    /// </summary>
    public static IntPtr CreateFramePool(IntPtr d3dDevice, int width, int height, int buffers)
    {
        if (d3dDevice == IntPtr.Zero) return IntPtr.Zero;
        var factory = ActivationFactory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", FramePoolStatics2Iid);
        if (factory == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var vtbl = *(IntPtr**)factory;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, long, IntPtr*, int>)vtbl[6];
            IntPtr result;
            var size = ((long)height << 32) | (uint)width;
            return fn(factory, d3dDevice, PixelFormatBgra8, buffers, size, &result) < 0
                ? IntPtr.Zero
                : result;
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>
    /// <c>IDirect3D11CaptureFramePool::CreateCaptureSession</c> — slot 10.
    /// <para>
    /// The ABI order is <c>Recreate</c>, <c>TryGetNextFrame</c>, <c>add_FrameArrived</c>,
    /// <c>remove_FrameArrived</c>, <c>CreateCaptureSession</c>, <c>get_DispatcherQueue</c> — the
    /// events come <i>before</i> the last two methods, which is not the order the CsWinRT projection
    /// lists them in. Slot numbers here come from <c>Windows.winmd</c> (SDK 10.0.26100), not from the
    /// projection.
    /// </para>
    /// </summary>
    public static IntPtr CreateSession(IntPtr pool, IntPtr item)
    {
        if (pool == IntPtr.Zero || item == IntPtr.Zero) return IntPtr.Zero;
        var vtbl = *(IntPtr**)pool;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[10];
        IntPtr result;
        return fn(pool, item, &result) < 0 ? IntPtr.Zero : result;
    }

    /// <summary>
    /// <c>IDirect3D11CaptureFramePool::TryGetNextFrame</c> — slot 7. Null when the pool has no new
    /// frame, which is the normal case on a still desktop.
    /// </summary>
    public static IntPtr TryGetNextFrame(IntPtr pool)
    {
        if (pool == IntPtr.Zero) return IntPtr.Zero;
        var vtbl = *(IntPtr**)pool;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[7];
        IntPtr result;
        return fn(pool, &result) < 0 ? IntPtr.Zero : result;
    }

    /// <summary><c>IGraphicsCaptureSession::StartCapture</c> — slot 6.</summary>
    public static int StartCapture(IntPtr session)
    {
        if (session == IntPtr.Zero) return -1;
        var vtbl = *(IntPtr**)session;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[6];
        return fn(session);
    }

    /// <summary><c>IGraphicsCaptureSession2::set_IsCursorCaptureEnabled</c> — slot 7.</summary>
    public static bool SetCursorCaptureEnabled(IntPtr session, bool enabled) =>
        SetBoolean(session, Session2Iid, enabled);

    /// <summary>
    /// <c>IGraphicsCaptureSession3::set_IsBorderRequired</c> — slot 7.
    /// </summary>
    /// <returns>False on builds without the interface (Windows 10 and early Windows 11) — the
    /// caller then decides whether a capture border is acceptable.</returns>
    public static bool SetBorderRequired(IntPtr session, bool required) =>
        SetBoolean(session, Session3Iid, required);

    private static bool SetBoolean(IntPtr session, Guid iid, bool value)
    {
        var extended = QueryInterface(session, iid);
        if (extended == IntPtr.Zero) return false;
        try
        {
            var vtbl = *(IntPtr**)extended;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[7];
            return fn(extended, value ? 1 : 0) >= 0;
        }
        finally { Marshal.Release(extended); }
    }

    /// <summary>
    /// <c>IGraphicsCaptureSession5::set_MinUpdateInterval</c> — slot 7. The argument is a WinRT
    /// <c>TimeSpan</c>, i.e. 100 ns ticks.
    /// </summary>
    public static bool SetMinUpdateInterval(IntPtr session, int milliseconds)
    {
        var extended = QueryInterface(session, Session5Iid);
        if (extended == IntPtr.Zero) return false;
        try
        {
            var vtbl = *(IntPtr**)extended;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtbl[7];
            return fn(extended, milliseconds * 10_000L) >= 0;
        }
        finally { Marshal.Release(extended); }
    }

    /// <summary><c>IDirect3D11CaptureFrame::get_Surface</c> — slot 6.</summary>
    public static IntPtr GetFrameSurface(IntPtr frame)
    {
        if (frame == IntPtr.Zero) return IntPtr.Zero;
        var vtbl = *(IntPtr**)frame;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[6];
        IntPtr result;
        return fn(frame, &result) < 0 ? IntPtr.Zero : result;
    }

    /// <summary><c>IClosable::Close</c> — slot 6.</summary>
    public static void Close(IntPtr instance)
    {
        var closable = QueryInterface(instance, ClosableIid);
        if (closable == IntPtr.Zero) return;
        try
        {
            var vtbl = *(IntPtr**)closable;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[6];
            fn(closable);
        }
        finally { Marshal.Release(closable); }
    }

    /// <summary><c>IUnknown::QueryInterface</c> — slot 0.</summary>
    public static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var vtbl = *(IntPtr**)instance;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        IntPtr result;
        var local = iid;
        return fn(instance, &local, &result) < 0 ? IntPtr.Zero : result;
    }

    /// <summary><c>IDirect3DDxgiInterfaceAccess::GetInterface</c> — slot 3.</summary>
    public static IntPtr GetDxgiInterface(IntPtr access, Guid iid)
    {
        var vtbl = *(IntPtr**)access;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
        IntPtr result;
        var local = iid;
        return fn(access, &local, &result) < 0 ? IntPtr.Zero : result;
    }

    /// <summary>
    /// The D3D11 texture behind a captured surface: QI for <c>IDirect3DDxgiInterfaceAccess</c>, then
    /// ask it for <c>ID3D11Texture2D</c>.
    /// </summary>
    public static IntPtr GetTexture(IntPtr surface)
    {
        var access = QueryInterface(surface, DxgiInterfaceAccessIid);
        if (access == IntPtr.Zero) return IntPtr.Zero;
        try { return GetDxgiInterface(access, Texture2DIid); }
        finally { Marshal.Release(access); }
    }

    private static IntPtr ActivationFactory(string className, Guid iid)
    {
        var hr = WindowsCreateString(className, className.Length, out var hstring);
        if (hr < 0) return IntPtr.Zero;
        try
        {
            var local = iid;
            hr = RoGetActivationFactory(hstring, ref local, out var factory);
            return hr < 0 ? IntPtr.Zero : factory;
        }
        finally { WindowsDeleteString(hstring); }
    }
}
