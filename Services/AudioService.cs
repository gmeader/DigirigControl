using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using DigiRigControlCenter.Models;

namespace DigiRigControlCenter.Services;

public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private WasapiCapture? capture;
    private WasapiOut? output;
    private BufferedWaveProvider? buffer;
    private MediaFoundationResampler? resampler;
    private WasapiLoopbackCapture? outputMonitor;
    private WaveOutEvent? testToneOutput;
    private TestToneWaveProvider? testToneProvider;

    public event Action<float>? InputLevelChanged;
    public event Action<float>? OutputLevelChanged;
    public event Action<float, float>? OutputStereoLevelChanged;
    public event Action<string>? MonitorError;

    public IReadOnlyList<AudioEndpointInfo> GetInputs() => GetEndpoints(DataFlow.Capture);
    public IReadOnlyList<AudioEndpointInfo> GetOutputs() => GetEndpoints(DataFlow.Render);

    public MMDevice? FindDigiRigInput()
        => FindDigiRig(DataFlow.Capture);

    public MMDevice? FindDigiRigOutput()
        => FindDigiRig(DataFlow.Render);

    public bool TrySetInputVolume(float level, out string message) => TrySetVolume(FindDigiRigInput(), level, out message);

    public bool TrySetOutputVolume(float level, out string message) => TrySetVolume(FindDigiRigOutput(), level, out message);

    private static bool TrySetVolume(MMDevice? device, float level, out string message)
    {
        if (device == null)
        {
            message = "DigiRig audio device was not found.";
            return false;
        }

        try
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            message = "Level set.";
            return true;
        }
        catch (Exception ex)
        {
            message = "Could not set audio level: " + ex.Message;
            return false;
        }
    }

    public float GetInputVolume() => GetVolume(FindDigiRigInput());
    public float GetOutputVolume() => GetVolume(FindDigiRigOutput());

    private static float GetVolume(MMDevice? device)
    {
        try { return device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
        catch { return 0f; }
    }

    private MMDevice? FindDigiRig(DataFlow flow)
    {
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        return devices.FirstOrDefault(d => IsDigiRig(d));
    }

    private IReadOnlyList<AudioEndpointInfo> GetEndpoints(DataFlow flow)
    {
        var defaults = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)?.ID;
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioEndpointInfo
            {
                Name = d.FriendlyName,
                Id = d.ID,
                IsInput = flow == DataFlow.Capture,
                IsDigiRigCandidate = IsDigiRig(d),
                IsDefault = d.ID == defaults,
                RoleText = d.ID == defaults ? "Default" : "",
                Volume = d.AudioEndpointVolume.MasterVolumeLevelScalar,
                Muted = d.AudioEndpointVolume.Mute,
                EnhancementsDisabled = ReadEnhancementsDisabled(d),
                AgcKnown = false,
                AgcDisabled = false,
                SampleFormat = TryGetFormat(d)
            }).ToList();
    }

    private static string TryGetFormat(MMDevice d)
    {
        try
        {
            var f = d.AudioClient.MixFormat;
            return $"{f.SampleRate:N0} Hz / {f.BitsPerSample} bit / {f.Channels} ch";
        }
        catch { return "Unknown"; }
    }

    private static bool IsDigiRig(MMDevice d)
    {
        var n = d.FriendlyName.ToLowerInvariant();
        var id = d.ID.ToLowerInvariant();
        return n.Contains("digirig") || n.Contains("usb audio device") || id.Contains("0d8c");
    }

    private static bool ReadEnhancementsDisabled(MMDevice d)
    {
        return WindowsAudioPropertyService.TryGetSystemEffectsDisabled(d.ID, out var disabled) && disabled;
    }

    public bool TryDisableEnhancements(MMDevice device, out string message)
    {
        return WindowsAudioPropertyService.TrySetSystemEffectsDisabled(device.ID, true, out message);
    }

    public bool TryRenameDigiRigDevices(out string message)
    {
        var messages = new List<string>();
        bool ok = true;

        var input = FindDigiRigInput();
        string inMessage;
        if (input != null)
            ok &= WindowsAudioPropertyService.TrySetFriendlyName(input.ID, "DigiRig In", out inMessage);
        else
            inMessage = "DigiRig RX / recording device was not found.";
        messages.Add(inMessage);

        var output = FindDigiRigOutput();
        string outMessage;
        if (output != null)
            ok &= WindowsAudioPropertyService.TrySetFriendlyName(output.ID, "DigiRig Out", out outMessage);
        else
            outMessage = "DigiRig TX / playback device was not found.";
        messages.Add(outMessage);

        message = string.Join(" ", messages);
        return ok;
    }

    public void OpenLegacySoundProperties()
    {
        Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true });
    }

    public void StartMonitor(MMDevice input, MMDevice speakerOutput, MMDevice? digirigOutput)
    {
        StopMonitor();

        // The DigiRig input is 16-bit PCM, mono, 48 kHz on this system.
        // We keep that format all the way through capture and only convert at
        // the Windows audio-engine boundary for the selected speaker device.
        capture = new WasapiCapture(input);
        buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };

        capture.DataAvailable += (_, e) =>
        {
            try
            {
                buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                InputLevelChanged?.Invoke(CalculateRmsPercent(e.Buffer, e.BytesRecorded, capture.WaveFormat));
            }
            catch (Exception ex)
            {
                MonitorError?.Invoke("RX capture error: " + ex.Message);
            }
        };

        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("RX capture stopped: " + e.Exception.Message);
        };

        // Windows speakers may expose a different bit depth/channel layout.
        // Convert explicitly to the speaker endpoint's actual mix format.
        // This avoids relying on Windows 'Listen to this device' and avoids
        // assuming that the DigiRig and speaker formats are identical.
        var speakerFormat = speakerOutput.AudioClient.MixFormat;
        resampler = new MediaFoundationResampler(buffer, speakerFormat)
        {
            ResamplerQuality = 60
        };

        output = new WasapiOut(speakerOutput, AudioClientShareMode.Shared, true, 100);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("Speaker playback stopped: " + e.Exception.Message);
        };

        output.Init(resampler);
        output.Play();

        // TX metering is intentionally independent of RX monitoring.
        // Refresh()/EnsureOutputMeter starts WASAPI loopback on DigiRig Out so
        // audio from ANY Windows application is reflected by the TX meters.

        capture.StartRecording();
    }

    private static (float left, float right) CalculateStereoRmsPercent(byte[] data, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
            return (0f, 0f);

        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = channels * bytesPerSample;
        if (bytesPerSample <= 0 || frameSize <= 0)
            return (0f, 0f);

        double leftSum = 0, rightSum = 0;
        int frames = bytesRecorded / frameSize;
        int rightSamples = channels > 1 ? frames : 0;

        for (int frame = 0; frame < frames; frame++)
        {
            double left = ReadNormalizedSample(data, frame * frameSize, format);
            leftSum += left * left;

            if (channels > 1)
            {
                double right = ReadNormalizedSample(data, frame * frameSize + bytesPerSample, format);
                rightSum += right * right;
            }
        }

        return (RmsToPercent(leftSum, frames), RmsToPercent(rightSum, rightSamples));
    }

    private static double ReadNormalizedSample(byte[] data, int pos, WaveFormat format)
    {
        if (format.BitsPerSample == 16)
            return BitConverter.ToInt16(data, pos) / 32768.0;

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            return Math.Clamp(BitConverter.ToSingle(data, pos), -1.0f, 1.0f);

        if (format.BitsPerSample == 24)
        {
            int value = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            return value / 8388608.0;
        }

        if (format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible))
            return BitConverter.ToInt32(data, pos) / 2147483648.0;

        return 0.0;
    }

    private static float RmsToPercent(double sum, int samples)
    {
        if (samples <= 0) return 0f;
        double rms = Math.Sqrt(sum / samples);
        double db = rms > 0 ? 20.0 * Math.Log10(rms) : -60.0;
        return (float)Math.Clamp((db + 60.0) / 60.0 * 100.0, 0.0, 100.0);
    }

    private static float CalculateRmsPercent(byte[] data, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
            return 0f;

        double sum = 0;
        int samples = 0;
        if (format.BitsPerSample == 16)
        {
            samples = bytesRecorded / 2;
            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(data, i * 2);
                double normalized = sample / 32768.0;
                sum += normalized * normalized;
            }
        }
        else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            samples = bytesRecorded / 4;
            for (int i = 0; i < samples; i++)
            {
                double normalized = Math.Clamp(BitConverter.ToSingle(data, i * 4), -1.0f, 1.0f);
                sum += normalized * normalized;
            }
        }
        else
        {
            return 0f;
        }

        double rms = Math.Sqrt(sum / Math.Max(1, samples));
        // Match the useful visual range of a Windows-style level meter:
        // silence is 0%, full-scale is 100%, with -60 dBFS at the bottom.
        double db = rms > 0 ? 20.0 * Math.Log10(rms) : -60.0;
        return (float)Math.Clamp((db + 60.0) / 60.0 * 100.0, 0.0, 100.0);
    }

    public bool IsTestToneRunning => testToneOutput != null;

    public void StartTestTone(MMDevice digirigOutput)
    {
        StopTestTone();

        // Use NAudio's WaveOut device path for the test generator. This is the
        // same Windows legacy playback path that reliably reaches the C-Media
        // DigiRig endpoint, while the loopback meter below independently watches
        // the actual DigiRig render stream.
        int deviceNumber = FindWaveOutDeviceNumber(digirigOutput);
        if (deviceNumber < 0)
            throw new InvalidOperationException("DigiRig Out was found by Windows Core Audio, but its WaveOut playback device could not be located.");

        // DigiRig Lite uses the LEFT channel for payload audio and the RIGHT channel
        // for its optional internal VOX trigger.  A mono signal would be copied
        // to both channels by the Windows audio path and would therefore key
        // the radio.  Generate stereo with tone ONLY on the left channel so
        // TEST TONE can exercise the audio path without asserting PTT.
        var format = new WaveFormat(48000, 16, 2);
        testToneProvider = new TestToneWaveProvider(format, 1000.0, 0.20);

        testToneOutput = new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            DesiredLatency = 100
        };
        testToneOutput.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("Test tone stopped: " + e.Exception.Message);
        };

        // Start the real DigiRig output meter independently of RX monitoring.
        StartOutputMeterIfNeeded(digirigOutput);

        testToneOutput.Init(testToneProvider);
        testToneOutput.Play();
    }

    private static int FindWaveOutDeviceNumber(MMDevice digirigOutput)
    {
        string friendly = digirigOutput.FriendlyName;
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            try
            {
                var caps = WaveOut.GetCapabilities(i);
                if (string.Equals(caps.ProductName, friendly, StringComparison.OrdinalIgnoreCase) ||
                    caps.ProductName.Contains("DigiRig", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            catch { }
        }

        // The C-Media endpoint may retain its original product name after the
        // Core Audio friendly name is changed to "DigiRig Out".
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            try
            {
                var caps = WaveOut.GetCapabilities(i);
                if (caps.ProductName.Contains("USB Audio Device", StringComparison.OrdinalIgnoreCase) ||
                    caps.ProductName.Contains("C-Media", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            catch { }
        }
        return -1;
    }

    public void StopTestTone()
    {
        try { testToneOutput?.Stop(); } catch { }
        testToneOutput?.Dispose();
        testToneOutput = null;
        testToneProvider = null;
    }

    private void StartOutputMeterIfNeeded(MMDevice digirigOutput)
    {
        if (outputMonitor != null)
            return;

        outputMonitor = new WasapiLoopbackCapture(digirigOutput);
        outputMonitor.DataAvailable += (_, e) =>
        {
            try
            {
                var levels = CalculateStereoRmsPercent(e.Buffer, e.BytesRecorded, outputMonitor.WaveFormat);
                    OutputLevelChanged?.Invoke(Math.Max(levels.left, levels.right));
                    OutputStereoLevelChanged?.Invoke(levels.left, levels.right);
            }
            catch (Exception ex)
            {
                MonitorError?.Invoke("TX monitor error: " + ex.Message);
            }
        };
        outputMonitor.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                MonitorError?.Invoke("TX monitor stopped: " + e.Exception.Message);
        };
        outputMonitor.StartRecording();
    }

    public void StopMonitor()
    {
        // This method controls INPUT monitoring only. The DigiRig OUT loopback
        // meter is intentionally independent so TX activity remains visible
        // even when the input-monitor buttons are stopped.
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); capture = null;
        output?.Stop(); output?.Dispose(); output = null;
        resampler?.Dispose(); resampler = null;
        buffer = null;
        InputLevelChanged?.Invoke(0f);
    }

    public void StopOutputMeter()
    {
        try { outputMonitor?.StopRecording(); } catch { }
        outputMonitor?.Dispose(); outputMonitor = null;
        OutputLevelChanged?.Invoke(0f);
        OutputStereoLevelChanged?.Invoke(0f, 0f);
    }

    public void EnsureOutputMeter(MMDevice digirigOutput)
    {
        StartOutputMeterIfNeeded(digirigOutput);
    }

    public void Dispose()
    {
        StopMonitor();
        StopOutputMeter();
        StopTestTone();
        enumerator.Dispose();
    }

    private sealed class TestToneWaveProvider : IWaveProvider
    {
        private readonly WaveFormat format;
        private readonly double frequency;
        private readonly double amplitude;
        private double phase;

        public TestToneWaveProvider(WaveFormat format, double frequency, double amplitude)
        {
            this.format = format;
            this.frequency = frequency;
            this.amplitude = amplitude;
        }

        public WaveFormat WaveFormat => format;

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample <= 0 || format.Channels <= 0) return 0;

            int frameSize = bytesPerSample * format.Channels;
            int frames = count / frameSize;
            double phaseStep = 2.0 * Math.PI * frequency / format.SampleRate;

            for (int frame = 0; frame < frames; frame++)
            {
                double sample = Math.Sin(phase) * amplitude;
                phase += phaseStep;
                if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;

                for (int ch = 0; ch < format.Channels; ch++)
                {
                    int pos = offset + frame * frameSize + ch * bytesPerSample;
                    // Left channel (channel 0) carries the test tone.
                    // Right channel (channel 1) is intentionally silent because
                    // DigiRig Lite can use right-channel audio as its VOX PTT trigger.
                    WriteSample(buffer, pos, format, ch == 0 ? sample : 0.0);
                }
            }

            return frames * frameSize;
        }

        private static void WriteSample(byte[] buffer, int pos, WaveFormat format, double sample)
        {
            sample = Math.Clamp(sample, -1.0, 1.0);

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(pos, 4), (float)sample);
                return;
            }

            if ((format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible) && format.BitsPerSample == 16)
            {
                short value = (short)Math.Round(sample * short.MaxValue);
                BitConverter.TryWriteBytes(buffer.AsSpan(pos, 2), value);
                return;
            }

            if ((format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible) && format.BitsPerSample == 24)
            {
                int value = (int)Math.Round(sample * 8388607.0);
                buffer[pos] = (byte)(value & 0xff);
                buffer[pos + 1] = (byte)((value >> 8) & 0xff);
                buffer[pos + 2] = (byte)((value >> 16) & 0xff);
                return;
            }

            if ((format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible) && format.BitsPerSample == 32)
            {
                int value = (int)Math.Round(sample * 2147483647.0);
                BitConverter.TryWriteBytes(buffer.AsSpan(pos, 4), value);
            }
        }
    }

}
