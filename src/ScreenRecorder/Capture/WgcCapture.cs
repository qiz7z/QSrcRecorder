using System;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace ScreenRecorder.Capture;

/// <summary>
/// 基于 Windows Graphics Capture 的帧源。
/// 每次调用 TryReadFrame 取“最新一帧”，经 GPU 拷贝到暂存纹理后回读为 BGRA 字节。
/// </summary>
internal sealed unsafe class WgcCapture : IDisposable
{
    private readonly D3DContext _d3d;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D* _staging;
    private int _stagingW;
    private int _stagingH;
    private bool _disposed;

    /// <summary>采集源被系统关闭（窗口关闭/显示器移除）。</summary>
    public event Action? SourceClosed;

    /// <summary>画面尺寸发生变化（窗口大小改变等），调用方应结束录制。</summary>
    public bool SizeChanged { get; private set; }

    /// <summary>帧缓冲区（BGRA，尺寸 = FrameSize），由本类管理，采集循环直接写入编码器。</summary>
    public byte[] FrameBuffer { get; private set; } = Array.Empty<byte>();

    /// <summary>帧池尺寸（可能经过缩放）。</summary>
    public (int Width, int Height) FrameSize { get; private set; }

    private Windows.Graphics.SizeInt32 _sourceSize;

    public WgcCapture(D3DContext d3d)
    {
        _d3d = d3d;
    }

    public (int Width, int Height) StartForMonitor(IntPtr hmonitor, double scale = 1.0)
        => Start(Interop.WgcInterop.CreateItemForMonitor(hmonitor), scale);

    public (int Width, int Height) StartForWindow(IntPtr hwnd, double scale = 1.0)
        => Start(Interop.WgcInterop.CreateItemForWindow(hwnd), scale);

    private (int, int) Start(GraphicsCaptureItem item, double scale)
    {
        _item = item;
        _sourceSize = item.Size;
        _item.Closed += (_, _) => SourceClosed?.Invoke();

        // 帧池尺寸可小于源尺寸，WGC 会在 GPU 侧完成缩放（用于降低管道与编码压力）
        var poolSize = new Windows.Graphics.SizeInt32
        {
            Width = Math.Max(2, (int)(item.Size.Width * scale) & ~1),
            Height = Math.Max(2, (int)(item.Size.Height * scale) & ~1),
        };

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3d.WinrtDevice!,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            poolSize);

        _session = _pool.CreateCaptureSession(item);
        try
        {
            _session.IsBorderRequired = false; // Win11 / Win10 21H2+ 可去掉系统黄框
        }
        catch
        {
            // 旧系统不支持该属性，保留系统绘制的边框
        }
        // 注意：不关闭光标捕获——录屏必须保留鼠标指针（用户操作位置）。
        // 指针与跟随圆叠加导致的"闪烁感"由死区抑制，而不是移除指针。
        _session.StartCapture();

        FrameSize = (poolSize.Width, poolSize.Height);
        return FrameSize;
    }

    /// <summary>
    /// 取走帧池里积累的最新一帧并回读到 FrameBuffer；没有新帧时返回 false（复用上一帧内容）。
    /// </summary>
    public bool TryReadFrame()
    {
        var pool = _pool;
        if (pool == null)
            return false;

        Direct3D11CaptureFrame? latest = null;
        Direct3D11CaptureFrame? frame;
        while ((frame = pool.TryGetNextFrame()) != null)
        {
            latest?.Dispose();
            latest = frame;
        }
        if (latest == null)
            return false;

        try
        {
            var content = latest.ContentSize;
            if (content.Width != _sourceSize.Width || content.Height != _sourceSize.Height)
                SizeChanged = true;

            var tex = Interop.Direct3D11Helper.CreateTextureFromSurface(latest.Surface);
            try
            {
                D3D11_TEXTURE2D_DESC desc;
                tex->GetDesc(&desc);
                EnsureStaging((int)desc.Width, (int)desc.Height);

                _d3d.Context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)tex);

                D3D11_MAPPED_SUBRESOURCE mapped;
                _d3d.Context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                try
                {
                    int width = Math.Min((int)desc.Width, _stagingW);
                    int height = Math.Min((int)desc.Height, _stagingH);
                    fixed (byte* dst = FrameBuffer)
                    {
                        nuint dstPitch = (nuint)(_stagingW * 4);
                        for (int y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(
                                (byte*)mapped.pData + (nuint)y * mapped.RowPitch,
                                dst + (nuint)y * dstPitch,
                                dstPitch,
                                dstPitch);
                        }
                    }
                }
                finally
                {
                    _d3d.Context->Unmap((ID3D11Resource*)_staging, 0);
                }
            }
            finally
            {
                tex->Release();
            }
            return true;
        }
        finally
        {
            latest.Dispose();
        }
    }

    private void EnsureStaging(int w, int h)
    {
        if (_staging != null && _stagingW == w && _stagingH == h)
            return;
        if (_staging != null)
        {
            _staging->Release();
            _staging = null;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        ID3D11Texture2D* tex = null;
        _d3d.Device->CreateTexture2D(in desc, null, &tex);
        _staging = tex;
        _stagingW = w;
        _stagingH = h;
        FrameBuffer = new byte[(long)w * h * 4];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        if (_staging != null)
        {
            _staging->Release();
            _staging = null;
        }
        _item = null;
    }
}
