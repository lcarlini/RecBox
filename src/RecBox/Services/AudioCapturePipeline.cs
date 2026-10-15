using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RecBox.Models;

namespace RecBox.Services;

/// <summary>
/// Captures microphone and/or WASAPI loopback via NAudio, mixes to 48 kHz stereo PCM16,
/// and streams samples to a named pipe for FFmpeg.
/// </summary>
public sealed class AudioCapturePipeline : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly string _pipeName;
    private NamedPipeServerStream? _pipe;
    private MMDevice? _micDevice;
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopBuffer;
    private IWaveProvider? _output;
    private CancellationTokenSource? _cts;
    private Task? _writeTask;
    private bool _started;

    public AudioCapturePipeline(string pipeName)
    {
        _pipeName = pipeName;
    }

    public string PipePath => $@"\\.\pipe\{_pipeName}";

    public void Start(AppSettings settings)
    {
        if (_started)
            throw new InvalidOperationException("Audio pipeline already started.");

        if (!settings.RecordMicrophone && !settings.RecordSystemAudio)
            throw new InvalidOperationException("No audio sources enabled.");

        var inputs = new List<ISampleProvider>();

        if (settings.RecordMicrophone)
        {
            try
            {
                _micDevice = ResolveCaptureDevice(settings.MicrophoneId);
                _micCapture = _micDevice is null
                    ? new WasapiCapture()
                    : new WasapiCapture(_micDevice);

                _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(3)
                };

                _micCapture.DataAvailable += (_, e) => _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                inputs.Add(CreateResampledProvider(_micBuffer));
                _micCapture.StartRecording();
            }
            catch (Exception ex)
            {
                DisposeCapture();
                throw new InvalidOperationException(
                    $"Microphone capture failed ({ex.Message}). Try another microphone or the Default device.",
                    ex);
            }
        }

        if (settings.RecordSystemAudio)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                // Ensure a render endpoint exists before opening loopback
                _ = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                _loopbackCapture = new WasapiLoopbackCapture();
                _loopBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(3)
                };

                _loopbackCapture.DataAvailable += (_, e) => _loopBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                inputs.Add(CreateResampledProvider(_loopBuffer));
                _loopbackCapture.StartRecording();
            }
            catch (COMException ex)
            {
                DisposeCapture();
                throw new InvalidOperationException(
                    "System audio loopback failed. Ensure a playback device is set as default in Windows Sound settings.",
                    ex);
            }
            catch (Exception ex)
            {
                DisposeCapture();
                throw new InvalidOperationException(
                    $"System audio capture failed ({ex.Message}). Ensure a default playback device is available.",
                    ex);
            }
        }

        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true
        };
        foreach (var input in inputs)
            mixer.AddMixerInput(input);

        _output = new SampleToWaveProvider16(mixer);

        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: SampleRate * Channels * 2);

        _cts = new CancellationTokenSource();
        _writeTask = Task.Run(() => WriteLoopAsync(_cts.Token));
        _started = true;
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        if (_pipe is null || _output is null)
            return;

        try
        {
            await _pipe.WaitForConnectionAsync(token);
        }
        catch
        {
            return;
        }

        var waveBuffer = new byte[SampleRate * Channels * 2 / 10]; // ~100 ms of PCM16

        try
        {
            while (!token.IsCancellationRequested && _pipe.IsConnected)
            {
                var read = _output.Read(waveBuffer, 0, waveBuffer.Length);
                if (read <= 0)
                {
                    await Task.Delay(5, token);
                    continue;
                }

                await _pipe.WriteAsync(waveBuffer.AsMemory(0, read), token);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (IOException)
        {
            // pipe closed by FFmpeg
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private static ISampleProvider CreateResampledProvider(BufferedWaveProvider buffer)
    {
        ISampleProvider sample = buffer.ToSampleProvider();

        if (sample.WaveFormat.Channels == 1)
            sample = new MonoToStereoSampleProvider(sample);

        if (sample.WaveFormat.SampleRate != SampleRate)
            sample = new WdlResamplingSampleProvider(sample, SampleRate);

        return sample;
    }

    private static MMDevice? ResolveCaptureDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == AudioDeviceService.DefaultDeviceId)
            return null;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        try { _micCapture?.StopRecording(); } catch { /* ignore */ }
        try { _loopbackCapture?.StopRecording(); } catch { /* ignore */ }

        try { _writeTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }

        DisposeCapture();
        _started = false;
    }

    private void DisposeCapture()
    {
        _micCapture?.Dispose();
        _micCapture = null;
        _loopbackCapture?.Dispose();
        _loopbackCapture = null;
        _micDevice?.Dispose();
        _micDevice = null;

        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;

        _cts?.Dispose();
        _cts = null;
        _writeTask = null;
        _micBuffer = null;
        _loopBuffer = null;
        _output = null;
    }

    public void Dispose() => Stop();
}
