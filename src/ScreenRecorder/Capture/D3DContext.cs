using System;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Graphics.Direct3D11;

namespace ScreenRecorder.Capture;

/// <summary>持有一个 D3D11 设备及其 WinRT 包装，供 WGC 与回读共用。</summary>
internal sealed unsafe class D3DContext : IDisposable
{
    public ID3D11Device* Device;
    public ID3D11DeviceContext* Context;
    public IDirect3DDevice? WinrtDevice;

    private bool _disposed;

    public D3DContext()
    {
        Interop.Direct3D11Helper.CreateD3DDevice(out var dev, out var ctx);
        Device = dev;
        Context = ctx;
        WinrtDevice = Interop.Direct3D11Helper.CreateInteropDevice(dev);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Context != null)
        {
            Context->Release();
            Context = null;
        }
        if (Device != null)
        {
            Device->Release();
            Device = null;
        }
    }
}
