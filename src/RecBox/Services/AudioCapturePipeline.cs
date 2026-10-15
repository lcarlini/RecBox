using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RecBox.Models;

namespace RecBox.Services;

/// <summary>
/// Captures microphone and/or system audio (WASAPI loopback), mixes to 48 kHz stereo PCM16
/// and streams it to FFmpeg over a named pipe, paced to the real-time clock so audio stays
/// in sync with gdigrab's wall-clock video timestamps.
/// Capture keeps running across FFmpeg segment restarts; each segment gets its own pipe.
/// </summary>
public sealed class AudioCapturePipeline : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly List<CaptureSource> _sources = new();
    private readonly object _writerLock = new();
    private MMDeviceEnumerator? _enumerator;
    private DeviceNotifications? _notifications;
    private SegmentWriter? _writer;
    private bool _disposed;

    public bool HasSources => _sources.Count > 0;

    public string Stats => string.Join(", ", _sources.Select(s => $"{s.Label}: {s.Underruns} underruns, {s.Trims} trims"));

    private List<CaptureSource> SlowSources { get; } = new();

    /// <summary>Sources that are still not delivering audio (device slow to open); they join once it opens.</summary>
    public IReadOnlyList<string> PendingSources => SlowSources.Where(s => !s.IsOpen).Select(s => s.Label).ToList();

    /// <summary>
    /// Opens the selected devices. A device that fails outright is reported (so the user can pick another);
    /// a device that hangs (Windows audio service stall) never blocks the recording.
    /// </summary>
    public async Task StartAsync(AppSettings settings, TimeSpan openTimeout)
    {
        if (_sources.Count > 0)
            throw new InvalidOperationException("Audio pipeline already started.");

        var pending = new List<(CaptureSource Source, Task Open, Func<Exception, string> Describe)>();
        if (settings.RecordMicrophone)
        {
            var micId = settings.MicrophoneId;
            var source = new CaptureSource("Microphone", () => OpenMicrophone(micId), micId, DataFlow.Capture);
            pending.Add((source, Task.Run(source.Open),
                ex => $"Microphone capture failed ({ex.Message}). Try another microphone or the Default device."));
        }

        if (settings.RecordSystemAudio)
        {
            var source = new CaptureSource("System audio", OpenLoopback, AudioDeviceService.DefaultDeviceId, DataFlow.Render);
            pending.Add((source, Task.Run(source.Open),
                ex => $"System audio capture failed ({ex.Message}). Make sure a playback device is set as default in Windows Sound settings."));
        }

        // Devices open in parallel; the whole step never takes longer than the timeout.
        var deadline = Task.Delay(openTimeout);
        try
        {
            foreach (var (source, open, describe) in pending)
                await AddSourceAsync(source, open, deadline, describe);
        }
        catch
        {
            foreach (var (source, _, _) in pending)
                source.Dispose();
            throw;
        }

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = new DeviceNotifications(this);
            _enumerator.RegisterEndpointNotificationCallback(_notifications);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Audio device notifications unavailable: {ex.Message}");
        }
    }

    private async Task AddSourceAsync(CaptureSource source, Task open, Task deadline, Func<Exception, string> describe)
    {
        if (await Task.WhenAny(open, deadline) != open)
        {
            AppLog.Warn($"{source.Label} did not open in time; recording continues and it will join when ready.");
            SlowSources.Add(source);
            _ = open.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    source.RequestReopen($"initial open failed: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
            _sources.Add(source);
            return;
        }

        try
        {
            await open;
        }
        catch (Exception ex)
        {
            source.Dispose();
            Dispose();
            throw new InvalidOperationException(describe(ex), ex);
        }

        _sources.Add(source);
    }

    /// <summary>Approximate capture-to-pipe delay (WASAPI packet + jitter buffer); video is offset to match.</summary>
    public const double PipelineLatencySeconds = 0.040;

    /// <summary>
    /// Creates a fresh pipe for the next FFmpeg process and returns its path.
    /// Sample N on the pipe corresponds to wall-clock <paramref name="epochUnixMs"/> + N / SampleRate.
    /// </summary>
    public string BeginSegment(long epochUnixMs)
    {
        lock (_writerLock)
        {
            _writer?.Dispose();
            var name = $"RecBoxAudio_{Environment.ProcessId}_{Guid.NewGuid():N}";
            _writer = new SegmentWriter(name, this, epochUnixMs);
            _writer.Start();
            return $@"\\.\pipe\{name}";
        }
    }

    public void EndSegment()
    {
        lock (_writerLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// Writes all audio up to now (the writer may lag while FFmpeg back-pressures), then closes the pipe
    /// so FFmpeg sees a clean end of the audio stream.
    /// </summary>
    public async Task DrainSegmentAsync(TimeSpan timeout)
    {
        SegmentWriter? writer;
        lock (_writerLock)
        {
            writer = _writer;
            _writer = null;
        }

        if (writer is null)
            return;

        await Task.Run(() => writer.Drain(timeout));
        writer.Dispose();
    }

    internal void ResetSources()
    {
        foreach (var source in _sources)
            source.Reset();
    }

    internal void Mix(float[] mix, float[] scratch, int count, bool allowTrim)
    {
        Array.Clear(mix, 0, count);
        foreach (var source in _sources)
        {
            source.ReadInto(scratch, count, allowTrim);
            for (var i = 0; i < count; i++)
                mix[i] += scratch[i];
        }
    }

    private static IWaveIn OpenMicrophone(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (!string.IsNullOrWhiteSpace(deviceId) && deviceId != AudioDeviceService.DefaultDeviceId)
        {
            try
            {
                var candidate = enumerator.GetDevice(deviceId);
                if (candidate.State == DeviceState.Active)
                    device = candidate;
                else
                    AppLog.Warn($"Selected microphone is {candidate.State}; using the Windows default microphone.");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Selected microphone not found ({ex.Message}); using the Windows default microphone.");
            }
        }

        device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return new WasapiCapture(device);
    }

    private static IWaveIn OpenLoopback()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new WasapiLoopbackCapture(device);
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role)
    {
        if (_disposed)
            return;

        foreach (var source in _sources)
        {
            if (source.Flow == DataFlow.Render && flow == DataFlow.Render && role == Role.Multimedia)
                source.RequestReopen("default playback device changed");
            else if (source.Flow == DataFlow.Capture && flow == DataFlow.Capture && role == Role.Console)
                source.RequestReopen("default microphone changed", onlyIfFollowingDefault: true);
        }
    }

    private void OnDeviceBecameActive(string deviceId)
    {
        if (_disposed)
            return;

        foreach (var source in _sources)
        {
            if (string.Equals(source.PreferredDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                source.RequestReopen("selected microphone is available again");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        EndSegment();

        try
        {
            if (_enumerator is not null && _notifications is not null)
                _enumerator.UnregisterEndpointNotificationCallback(_notifications);
        }
        catch
        {
            // ignore
        }

        foreach (var source in _sources)
            source.Dispose();
        _sources.Clear();

        try { _enumerator?.Dispose(); } catch { /* ignore */ }
        _enumerator = null;
    }

    private sealed class DeviceNotifications(AudioCapturePipeline owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active)
                Task.Run(() => owner.OnDeviceBecameActive(deviceId));
        }

        public void OnDeviceAdded(string pwstrDeviceId) => Task.Run(() => owner.OnDeviceBecameActive(pwstrDeviceId));

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            => Task.Run(() => owner.OnDefaultDeviceChanged(flow, role));

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }

    /// <summary>One FFmpeg connection: writes mixed PCM at exactly real-time rate.</summary>
    private sealed class SegmentWriter : IDisposable
    {
        private const int ChunkFrames = SampleRate / 100; // 10 ms
        private const int MaxChunksPerWrite = 10;
        private static readonly TimeSpan DrainTail = TimeSpan.FromMilliseconds(200);

        private readonly string _name;
        private readonly AudioCapturePipeline _owner;
        private readonly long _epochUnixMs;
        private readonly CancellationTokenSource _cts = new();
        private readonly NamedPipeServerStream _pipe;
        private Thread? _thread;
        private long _drainTargetFrames = long.MaxValue;
        private volatile bool _connected;
        private long _framesWritten;
        private TimeSpan _sinceEpochAtConnect;
        private Stopwatch? _clock;

        /// <summary>
        /// Writes until shortly after "now" (sound played just before Stop is still in flight through
        /// WASAPI and the jitter buffer), then the loop exits.
        /// </summary>
        public void Drain(TimeSpan timeout)
        {
            if (!_connected || _clock is null)
                return;

            var target = (long)((_sinceEpochAtConnect + _clock.Elapsed + DrainTail).TotalSeconds * SampleRate);
            Interlocked.Exchange(ref _drainTargetFrames, target);
            var behindMs = (target - Interlocked.Read(ref _framesWritten)) * 1000 / SampleRate - (long)DrainTail.TotalMilliseconds;
            if (!(_thread?.Join(timeout) ?? true))
                AppLog.Warn($"Audio drain timed out ({behindMs} ms were pending).");
            else if (behindMs > 100)
                AppLog.Info($"Audio drain delivered {behindMs} ms of buffered audio.");
        }

        public SegmentWriter(string name, AudioCapturePipeline owner, long epochUnixMs)
        {
            _name = name;
            _owner = owner;
            _epochUnixMs = epochUnixMs;
            _pipe = new NamedPipeServerStream(
                name,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                SampleRate * Channels * 2 / 10);
        }

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RecBox audio writer",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        private void Run()
        {
            var token = _cts.Token;
            try
            {
                _pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
            }
            catch
            {
                return;
            }

            _owner.ResetSources();

            var maxSamples = ChunkFrames * MaxChunksPerWrite * Channels;
            var mix = new float[maxSamples];
            var scratch = new float[maxSamples];
            var bytes = new byte[maxSamples * 2];
            // The time between the epoch and FFmpeg connecting is written as silence right away,
            // so the sample count stays locked to the wall clock the video uses.
            var sinceEpoch = TimeSpan.FromMilliseconds(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _epochUnixMs));
            var clock = Stopwatch.StartNew();
            _sinceEpochAtConnect = sinceEpoch;
            _clock = clock;
            _connected = true;
            long framesWritten = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var drainTarget = Interlocked.Read(ref _drainTargetFrames);
                    var target = Math.Min((long)((sinceEpoch + clock.Elapsed).TotalSeconds * SampleRate), drainTarget);
                    var behind = target - framesWritten;

                    if (behind > SampleRate * 30L)
                    {
                        // Clock jump (e.g. system resumed from sleep): never flood FFmpeg with silence.
                        AppLog.Warn($"Audio writer {behind / (double)SampleRate:F1}s behind; resyncing.");
                        framesWritten = target - ChunkFrames;
                        behind = ChunkFrames;
                    }

                    if (behind < ChunkFrames)
                    {
                        if (drainTarget != long.MaxValue)
                        {
                            _pipe.Flush();                            break; // drained: closing the pipe gives FFmpeg a clean EOF
                        }

                        Thread.Sleep(2);
                        continue;
                    }

                    var chunks = (int)Math.Min(behind / ChunkFrames, MaxChunksPerWrite);
                    var frames = chunks * ChunkFrames;
                    var samples = frames * Channels;

                    _owner.Mix(mix, scratch, samples, allowTrim: behind < ChunkFrames * 3);

                    for (var i = 0; i < samples; i++)
                    {
                        var v = mix[i];
                        if (v > 1f) v = 1f;
                        else if (v < -1f) v = -1f;
                        var s = (short)(v * 32767f);
                        bytes[2 * i] = (byte)s;
                        bytes[2 * i + 1] = (byte)(s >> 8);
                    }

                    _pipe.WriteAsync(bytes.AsMemory(0, samples * 2), token).AsTask().GetAwaiter().GetResult();
                    framesWritten += frames;
                    Interlocked.Exchange(ref _framesWritten, framesWritten);                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break; // FFmpeg closed the pipe (segment ended)
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Audio writer error on {_name}", ex);
                    Thread.Sleep(10);
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _thread?.Join(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
            try { _pipe.Dispose(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}

/// <summary>
/// One WASAPI input converted to 48 kHz stereo float with a small jitter buffer.
/// Survives device loss: the source outputs silence and reconnects in the background.
/// </summary>
internal sealed class CaptureSource : IDisposable
{
    // Jitter buffer: WASAPI delivers ~10 ms packets; 30 ms absorbs scheduling jitter
    // while keeping audio latency (and thus A/V offset) low.
    private const double PrimeMs = 30;
    private const double TrimAboveMs = 300;
    private const double TrimToMs = 60;

    private readonly string _label;
    private readonly Func<IWaveIn> _factory;
    private readonly object _lock = new();
    private IWaveIn? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _chain;
    private byte[] _dropScratch = new byte[4096];
    private bool _priming = true;
    private bool _disposed;
    private System.Threading.Timer? _reconnectTimer;
    private int _reopening;

    public CaptureSource(string label, Func<IWaveIn> factory, string preferredDeviceId, DataFlow flow)
    {
        _label = label;
        _factory = factory;
        PreferredDeviceId = preferredDeviceId;
        Flow = flow;
    }

    public string PreferredDeviceId { get; }
    public DataFlow Flow { get; }
    public string Label => _label;

    public bool IsOpen
    {
        get
        {
            lock (_lock)
                return _chain is not null;
        }
    }

    /// <summary>Times the source ran dry mid-stream (silence inserted) — only meaningful while audio is flowing.</summary>
    public int Underruns { get; private set; }

    /// <summary>Times excess latency was trimmed (device clock faster than system clock).</summary>
    public int Trims { get; private set; }

    /// <summary>Test hook: simulates a device that is slow to open (e.g. NVIDIA Broadcast starting up).</summary>
    internal static TimeSpan TestOpenDelay { get; set; }

    public void Open()
    {
        if (TestOpenDelay > TimeSpan.Zero)
            Thread.Sleep(TestOpenDelay);
        var capture = _factory();
        try
        {
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
            var chain = BuildChain(buffer);

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                    buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            IWaveIn? old;
            lock (_lock)
            {
                if (_disposed)
                {
                    old = capture;
                }
                else
                {
                    old = _capture;
                    _capture = capture;
                    _buffer = buffer;
                    _chain = chain;
                    _priming = true;
                }
            }

            if (old is not null)
                DisposeCapture(old);

            AppLog.Info($"{_label} capture opened: {capture.WaveFormat}");
        }
        catch
        {
            DisposeCapture(capture);
            throw;
        }
    }

    private static ISampleProvider BuildChain(BufferedWaveProvider buffer)
    {
        ISampleProvider sample = buffer.ToSampleProvider();
        sample = sample.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(sample),
            2 => sample,
            _ => new DownmixToStereoSampleProvider(sample)
        };

        if (sample.WaveFormat.SampleRate != AudioCapturePipeline.SampleRate)
            sample = new WdlResamplingSampleProvider(sample, AudioCapturePipeline.SampleRate);

        return sample;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _buffer?.ClearBuffer();
            _priming = true;
        }
    }

    /// <summary>Fills exactly <paramref name="count"/> floats (48 kHz stereo); silence on underrun/device loss.</summary>
    public void ReadInto(float[] dest, int count, bool allowTrim)
    {
        lock (_lock)
        {
            if (_chain is null || _buffer is null)
            {
                Array.Clear(dest, 0, count);
                return;
            }

            var bufferedMs = _buffer.BufferedDuration.TotalMilliseconds;
            if (_priming)
            {
                if (bufferedMs < PrimeMs)
                {
                    Array.Clear(dest, 0, count);
                    return;
                }

                _priming = false;
            }

            if (allowTrim && bufferedMs > TrimAboveMs)
            {
                Drop(_buffer, bufferedMs - TrimToMs);
                Trims++;
            }

            var got = 0;
            try
            {
                while (got < count)
                {
                    var n = _chain.Read(dest, got, count - got);
                    if (n <= 0)
                        break;
                    got += n;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"{_label} read error: {ex.Message}");
            }

            if (got < count)
            {
                Array.Clear(dest, got, count - got);
                _priming = true;
                Underruns++;
            }
        }
    }

    private void Drop(BufferedWaveProvider buffer, double ms)
    {
        var format = buffer.WaveFormat;
        var bytes = (int)(ms / 1000.0 * format.AverageBytesPerSecond);
        bytes -= bytes % format.BlockAlign;
        if (_dropScratch.Length < bytes)
            _dropScratch = new byte[bytes];
        if (bytes > 0)
            buffer.Read(_dropScratch, 0, bytes);
    }

    public void RequestReopen(string reason, bool onlyIfFollowingDefault = false)
    {
        if (_disposed)
            return;
        if (onlyIfFollowingDefault && PreferredDeviceId != AudioDeviceService.DefaultDeviceId)
            return;

        AppLog.Info($"{_label}: reopening ({reason}).");
        TryReopen();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(sender, _capture))
                return;

            _capture = null;
            _buffer = null;
            _chain = null;
        }

        AppLog.Warn($"{_label} capture stopped unexpectedly ({e.Exception?.Message ?? "no error"}); recording silence and reconnecting.");
        if (sender is IWaveIn stopped)
            Task.Run(() => DisposeCapture(stopped));

        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _reconnectTimer ??= new System.Threading.Timer(_ => TryReopen(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }
    }

    private void TryReopen()
    {
        if (Interlocked.Exchange(ref _reopening, 1) != 0)
            return;

        try
        {
            if (_disposed)
                return;
            Open();
            lock (_lock)
            {
                _reconnectTimer?.Dispose();
                _reconnectTimer = null;
            }

            AppLog.Info($"{_label} capture reconnected.");
        }
        catch (Exception ex)
        {
            AppLog.Info($"{_label} reconnect pending: {ex.Message}");
            ScheduleReconnect();
        }
        finally
        {
            Interlocked.Exchange(ref _reopening, 0);
        }
    }

    private void DisposeCapture(IWaveIn capture)
    {
        try { capture.RecordingStopped -= OnRecordingStopped; } catch { /* ignore */ }
        try { capture.StopRecording(); } catch { /* ignore */ }
        try { capture.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        IWaveIn? capture;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            capture = _capture;
            _capture = null;
            _buffer = null;
            _chain = null;
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }

        if (capture is not null)
            DisposeCapture(capture);
    }
}

/// <summary>Folds multichannel (e.g. 5.1 / 7.1) audio into stereo.</summary>
internal sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = [];

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / 2;
        var needed = frames * _channels;
        if (_buffer.Length < needed)
            _buffer = new float[needed];

        var read = _source.Read(_buffer, 0, needed);
        var framesRead = read / _channels;

        for (var f = 0; f < framesRead; f++)
        {
            var b = f * _channels;
            float l = _buffer[b], r = _buffer[b + 1];
            if (_channels >= 3)
            {
                var c = _buffer[b + 2] * 0.707f;
                l += c;
                r += c;
            }

            // Skip LFE (index 3); fold surrounds alternately into left / right.
            for (var ch = 4; ch < _channels; ch++)
            {
                if ((ch & 1) == 0) l += _buffer[b + ch] * 0.707f;
                else r += _buffer[b + ch] * 0.707f;
            }

            buffer[offset + 2 * f] = l * 0.5f;
            buffer[offset + 2 * f + 1] = r * 0.5f;
        }

        return framesRead * 2;
    }
}
