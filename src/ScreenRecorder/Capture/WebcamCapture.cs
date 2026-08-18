using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace ScreenRecorder.Capture;

public sealed record WebcamDeviceInfo(string Id, string Name);

/// <summary>
/// WinRT MediaFrameReader 摄像头采集：后台回调更新最新 BGRA 帧，录制线程 TryCopyLatestFrame 快照。
/// </summary>
public sealed class WebcamCapture : IDisposable
{
    private readonly object _frameLock = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[]? _latest;
    private int _latestW;
    private int _latestH;
    private bool _started;
    private bool _disposed;
    private string? _error;

    public string? LastError => _error;
    public bool IsRunning => _started && !_disposed;

    public static IReadOnlyList<WebcamDeviceInfo> EnumerateDevices()
    {
        try
        {
            var groups = MediaFrameSourceGroup.FindAllAsync().AsTask().GetAwaiter().GetResult();
            var list = new List<WebcamDeviceInfo>();
            foreach (var g in groups)
            {
                bool hasColor = g.SourceInfos.Any(s =>
                    s.SourceKind == MediaFrameSourceKind.Color
                    || s.MediaStreamType == MediaStreamType.VideoPreview
                    || s.MediaStreamType == MediaStreamType.VideoRecord);
                if (!hasColor || string.IsNullOrEmpty(g.Id))
                    continue;
                string name = string.IsNullOrWhiteSpace(g.DisplayName) ? "摄像头" : g.DisplayName;
                list.Add(new WebcamDeviceInfo(g.Id, name));
            }
            return list;
        }
        catch
        {
            return Array.Empty<WebcamDeviceInfo>();
        }
    }

    /// <summary>打开设备并开始拉流。deviceId 为空则用第一台。</summary>
    public void Start(string? deviceId = null, int preferredWidth = 640)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WebcamCapture));
        if (_started)
            return;

        try
        {
            StartAsync(deviceId, preferredWidth).GetAwaiter().GetResult();
            _started = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            CleanupMedia();
            throw new InvalidOperationException("无法打开摄像头：" + ex.Message, ex);
        }
    }

    private async Task StartAsync(string? deviceId, int preferredWidth)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        MediaFrameSourceGroup? group = null;
        if (!string.IsNullOrEmpty(deviceId))
            group = groups.FirstOrDefault(g => g.Id == deviceId);
        group ??= groups.FirstOrDefault(g =>
            g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color
                || s.MediaStreamType == MediaStreamType.VideoPreview
                || s.MediaStreamType == MediaStreamType.VideoRecord));
        if (group == null)
            throw new InvalidOperationException("未找到可用摄像头设备。");

        // ExclusiveControl 更稳拿满分辨率；失败再退 SharedReadOnly
        _capture = new MediaCapture();
        try
        {
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
        }
        catch
        {
            _capture.Dispose();
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
        }

        MediaFrameSource? source = null;
        foreach (var kv in _capture.FrameSources)
        {
            var s = kv.Value;
            if (s.Info.SourceKind == MediaFrameSourceKind.Color
                || s.Info.MediaStreamType == MediaStreamType.VideoPreview
                || s.Info.MediaStreamType == MediaStreamType.VideoRecord)
            {
                source = s;
                break;
            }
        }
        source ??= _capture.FrameSources.Values.FirstOrDefault();
        if (source == null)
            throw new InvalidOperationException("摄像头无可用视频源。");

        TrySelectFormat(source, preferredWidth);

        // 请求 BGRA8；部分设备会在 reader 内完成色彩转换
        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.FrameArrived += OnFrameArrived;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException("摄像头拉流失败：" + status);
    }

    private static void TrySelectFormat(MediaFrameSource source, int preferredWidth)
    {
        try
        {
            var formats = source.SupportedFormats;
            if (formats == null || formats.Count == 0)
                return;

            // 优先选 ≥480p 的格式，保证人像清晰度；其次才考虑接近 preferredWidth
            MediaFrameFormat? best = null;
            int bestScore = int.MaxValue;
            foreach (var f in formats)
            {
                int w = (int)(f.VideoFormat?.Width ?? 0);
                int h = (int)(f.VideoFormat?.Height ?? 0);
                // 最低 480p，确保人像细节
                if (w < 640 || h < 480)
                    continue;

                int score = Math.Abs(w - preferredWidth);
                double fps = 0;
                try
                {
                    var fr = f.FrameRate;
                    if (fr != null && fr.Denominator > 0)
                        fps = fr.Numerator / (double)fr.Denominator;
                }
                catch { /* ignore */ }
                if (fps is >= 24 and <= 30)
                    score -= 30;
                else if (fps > 30)
                    score += 10;

                string subtype = f.Subtype ?? "";
                if (subtype.Equals(MediaEncodingSubtypes.Bgra8, StringComparison.OrdinalIgnoreCase)
                    || subtype.Equals("BGRA8", StringComparison.OrdinalIgnoreCase)
                    || subtype.Equals(MediaEncodingSubtypes.Nv12, StringComparison.OrdinalIgnoreCase)
                    || subtype.Equals("NV12", StringComparison.OrdinalIgnoreCase)
                    || subtype.Equals(MediaEncodingSubtypes.Yuy2, StringComparison.OrdinalIgnoreCase))
                    score -= 5;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }

            // 找不到 ≥480p 则放宽到 ≥320
            if (best == null)
            {
                foreach (var f in formats)
                {
                    int w = (int)(f.VideoFormat?.Width ?? 0);
                    int h = (int)(f.VideoFormat?.Height ?? 0);
                    if (w >= 320 && h >= 240)
                    {
                        int score = Math.Abs(w - preferredWidth);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = f;
                        }
                    }
                }
            }

            if (best != null)
                source.SetFormatAsync(best).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // 格式协商失败则用默认
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var video = frame?.VideoMediaFrame;
            if (video == null)
                return;

            // 优先 SoftwareBitmap；部分路径只有 Direct3DSurface
            SoftwareBitmap? owned = null;
            SoftwareBitmap? bmp = video.SoftwareBitmap;
            if (bmp == null)
            {
                try
                {
                    var vf = video.GetVideoFrame();
                    if (vf?.SoftwareBitmap != null)
                    {
                        owned = SoftwareBitmap.Copy(vf.SoftwareBitmap);
                        bmp = owned;
                    }
                }
                catch { /* ignore */ }
            }

            if (bmp == null)
                return;

            // 关键：必须用 Ignore，不能 Premultiplied。
            // 摄像头帧 alpha 常为 0，Premultiplied 会把 RGB 乘成全黑，画中画「完全看不清」。
            SoftwareBitmap? converted = null;
            try
            {
                if (bmp.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                    || bmp.BitmapAlphaMode != BitmapAlphaMode.Ignore)
                {
                    converted = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    bmp = converted;
                }

                int w = bmp.PixelWidth;
                int h = bmp.PixelHeight;
                if (w < 1 || h < 1)
                    return;

                byte[] buffer = new byte[w * h * 4];
                if (!TryCopyBgra(bmp, buffer, w, h))
                    return;

                // 部分设备输出几乎全透明/全零通道的坏帧，直接丢弃
                if (IsNearlyBlank(buffer))
                    return;

                lock (_frameLock)
                {
                    _latest = buffer;
                    _latestW = w;
                    _latestH = h;
                }
            }
            finally
            {
                converted?.Dispose();
                owned?.Dispose();
            }
        }
        catch
        {
            // 单帧失败忽略
        }
    }

    /// <summary>
    /// 按 BitmapBuffer 真实 stride 拷到紧密 BGRA；失败时回退 CopyToBuffer。
    /// </summary>
    private static bool TryCopyBgra(SoftwareBitmap bmp, byte[] dest, int width, int height)
    {
        try
        {
            using var buffer = bmp.LockBuffer(BitmapBufferAccessMode.Read);
            using var refBox = buffer.CreateReference();
            var desc = buffer.GetPlaneDescription(0);
            // IMemoryBufferByteAccess
            if (refBox is not IMemoryBufferByteAccess access)
            {
                bmp.CopyToBuffer(dest.AsBuffer());
                return true;
            }

            access.GetBuffer(out IntPtr data, out uint capacity);
            int srcStride = desc.Stride;
            int dstStride = width * 4;
            int rows = Math.Min(height, desc.Height);
            // Width 为像素数；BGRA 每像素 4 字节，且不超过 stride
            int copyW = Math.Min(dstStride, Math.Min(desc.Width * 4, srcStride));
            if (copyW < 4 || rows < 1)
            {
                bmp.CopyToBuffer(dest.AsBuffer());
                return true;
            }

            if (capacity < (uint)(desc.StartIndex + srcStride * Math.Max(0, rows - 1) + copyW))
            {
                bmp.CopyToBuffer(dest.AsBuffer());
                return true;
            }

            unsafe
            {
                byte* srcBase = (byte*)data + desc.StartIndex;
                for (int y = 0; y < rows; y++)
                {
                    Marshal.Copy((IntPtr)(srcBase + y * srcStride), dest, y * dstStride, copyW);
                }
            }
            return true;
        }
        catch
        {
            try
            {
                bmp.CopyToBuffer(dest.AsBuffer());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool IsNearlyBlank(byte[] bgra)
    {
        // 抽样看是否几乎全 0（黑且无内容）
        int step = Math.Max(16, bgra.Length / 256);
        int nonZero = 0;
        for (int i = 0; i + 2 < bgra.Length; i += step)
        {
            if (bgra[i] > 8 || bgra[i + 1] > 8 || bgra[i + 2] > 8)
                nonZero++;
        }
        return nonZero < 3;
    }

    /// <summary>拷贝最新一帧引用；无帧时返回 false。调用方只读、勿修改数组。</summary>
    public bool TryCopyLatestFrame(out byte[] bgra, out int width, out int height)
    {
        lock (_frameLock)
        {
            if (_latest == null || _latestW < 1 || _latestH < 1)
            {
                bgra = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }
            bgra = _latest;
            width = _latestW;
            height = _latestH;
            return true;
        }
    }

    public void Stop()
    {
        CleanupMedia();
        _started = false;
        lock (_frameLock)
        {
            _latest = null;
            _latestW = 0;
            _latestH = 0;
        }
    }

    private void CleanupMedia()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                try { _reader.StopAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                _reader.Dispose();
                _reader = null;
            }
        }
        catch { }

        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}

/// <summary>WinRT IMemoryBufferByteAccess，用于按 stride 读 SoftwareBitmap。</summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out IntPtr buffer, out uint capacity);
}
