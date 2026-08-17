using System;
using System.IO;
using NAudio.Wave;

namespace ScreenRecorder.Audio;

/// <summary>
/// 录制系统声音（WASAPI 回放设备回环）到临时 WAV 文件。
/// 采样率 44100Hz、立体声、16-bit。
/// </summary>
public sealed class SystemAudioCapture : IDisposable
{
    private readonly string _tempFolder;
    private string _wavePath = "";
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private readonly object _lock = new();
    private volatile bool _recording;

    public string WavePath => _wavePath;
    public int SampleRate => 44100;
    public int Channels => 2;

    public SystemAudioCapture(string tempFolder)
    {
        _tempFolder = tempFolder;
        Directory.CreateDirectory(tempFolder);
        _wavePath = Path.Combine(tempFolder, $"sysaudio_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_recording) return;

            _recording = true;
            // WASAPI 回环必须使用设备原生采样格式，不能手动指定 WaveFormat，
            // 否则捕获不到任何数据（WAV 文件几乎为空）。
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            _writer = new WaveFileWriter(_wavePath, _capture.WaveFormat);
        }
    }

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (!_recording || _writer == null) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _recording = false;
            try { _capture?.StopRecording(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Dispose() => Stop();
}
