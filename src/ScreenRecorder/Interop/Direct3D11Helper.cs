using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using WinRT;

namespace ScreenRecorder.Interop;

/// <summary>
/// D3D11 与 WinRT (IDirect3DDevice) 之间的互操作。
/// COM 指针类型由 CsWin32 从官方元数据生成，vtable 调用由编译器保证正确。
/// </summary>
internal static unsafe class Direct3D11Helper
{
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceFn(IntPtr This, ref Guid iid, out IntPtr ppv);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static void CreateD3DDevice(out ID3D11Device* device, out ID3D11DeviceContext* context)
    {
        const uint D3D11SdkVersion = 7;
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;

        ID3D11Device* dev = null;
        ID3D11DeviceContext* ctx = null;
        HRESULT hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
            flags, null, 0, D3D11SdkVersion, &dev, null, &ctx);
        if (hr.Failed)
        {
            hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, default,
                flags, null, 0, D3D11SdkVersion, &dev, null, &ctx);
        }
        Marshal.ThrowExceptionForHR(hr.Value);
        device = dev;
        context = ctx;
    }

    /// <summary>把 ID3D11Device 包装成 WGC 需要的 WinRT IDirect3DDevice。</summary>
    public static IDirect3DDevice CreateInteropDevice(ID3D11Device* device)
    {
        Guid iidDxgi = IID_IDXGIDevice;
        void* pDxgi = null;
        Marshal.ThrowExceptionForHR(device->QueryInterface(&iidDxgi, &pDxgi).Value);
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice((IntPtr)pDxgi, out IntPtr inspectable));
            try
            {
                return (IDirect3DDevice)MarshalInspectable<object>.FromAbi(inspectable)!;
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            ((IDXGIDevice*)pDxgi)->Release();
        }
    }

    /// <summary>取出 WGC 帧 surface 背后的 ID3D11Texture2D（调用方负责 Release）。</summary>
    public static ID3D11Texture2D* CreateTextureFromSurface(IDirect3DSurface surface)
    {
        // 按投影方式不同，surface 可能是 CsWinRT 包装对象（借 ThisPtr，不 Release），
        // 也可能是 COM RCW（GetIUnknownForObject，用完要 Release）
        bool isWinrt = surface is IWinRTObject;
        IntPtr abi;
        if (surface is IWinRTObject winrtObj)
        {
            abi = winrtObj.NativeObject.ThisPtr;
        }
        else
        {
            abi = Marshal.GetIUnknownForObject(surface);
        }
        try
        {
            Guid iidAccess = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, ref iidAccess, out IntPtr access));
            try
            {
                var vtable = *(IntPtr**)access;
                var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceFn>(vtable[3]);
                Guid iidTex = IID_ID3D11Texture2D;
                Marshal.ThrowExceptionForHR(getInterface(access, ref iidTex, out IntPtr tex));
                return (ID3D11Texture2D*)tex;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            if (!isWinrt)
                Marshal.Release(abi);
        }
    }
}
