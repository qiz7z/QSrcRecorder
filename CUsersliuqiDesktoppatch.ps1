$file = 'src/ScreenRecorder/UI/Wpf/MainView.xaml.cs'
$content = Get-Content $file -Raw -Encoding UTF8

# Add diag constant
$content = $content -replace 
    'private readonly ScreenRecorder.Overlays.ClickHighlightEngine _clickEngine = new\(\);',
    'private readonly ScreenRecorder.Overlays.ClickHighlightEngine _clickEngine = new();`n    private static readonly string _diagLog = @"C:\Users\liuqi\Desktop\qsrc_diag.txt";'

# Add Click diagnostic
$content = $content -replace
    'private void RecordButton_Click\(object sender, RoutedEventArgs e\) => ToggleRecord\(\);',
    'private void RecordButton_Click(object sender, RoutedEventArgs e) { System.IO.File.AppendAllText(_diagLog, $"[{DateTime.Now:HH:mm:ss.fff}] CLICK`n"); ToggleRecord(); }'

# Add TOGGLE diagnostic
$content = $content -replace
    'private async void ToggleRecord\(\)`n    {`n        if \(_starting\)',
    'private async void ToggleRecord()`n    {`n        System.IO.File.AppendAllText(_diagLog, $"[{DateTime.Now:HH:mm:ss.fff}] TOGGLE: starting={_starting}, session={_session != null}, isRecording={_session?.IsRecording}`n");`n        if (_starting)'

# Add SAR enter diagnostic
$content = $content -replace
    'private async Task StartRecordingAsync\(RecordingOptions\? preset = null\)`n    {`n        if \(_starting \|\| _session is \{ IsRecording: true }\)',
    'private async Task StartRecordingAsync(RecordingOptions? preset = null)`n    {`n        System.IO.File.AppendAllText(_diagLog, $"[{DateTime.Now:HH:mm:ss.fff}] SAR: enter`n");`n        if (_starting || _session is { IsRecording: true })'

Set-Content -Path $file -Value $content -Encoding UTF8 -NoNewline
Write-Host 'Patched'
