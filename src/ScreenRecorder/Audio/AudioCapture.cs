using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenRecorder.Audio;

/// <summary>
/// 录制麦克风 PCM 音频到临时 WAV 文件。
///
/// 用 WasapiCapture（而非 WaveInEvent）：实测本机 Realtek“麦克风阵列”在 WinMM
/// 立体声模式下输入电平几乎为 0（人声录不上），WASAPI 用设备原生格式可录到满电平。
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly string _tempFolder;
    private string _wavePath = "";
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private readonly object _lock = new();
    private volatile bool _recording;

    public string WavePath => _wavePath;
    public int SampleRate => 44100;
    public int Channels => 2;

    public AudioCapture(string tempFolder)
    {
        _tempFolder = tempFolder;
        Directory.CreateDirectory(tempFolder);
        _wavePath = Path.Combine(tempFolder, $"audio_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_recording) return;

            _recording = true;
            // WASAPI 默认捕获设备（Realtek 麦克风阵列），使用设备原生格式
            _capture = new WasapiCapture();
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
