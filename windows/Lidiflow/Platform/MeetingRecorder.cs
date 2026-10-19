using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lidiflow.Core;
using NAudio.Dsp;
using NAudio.Wave;

namespace Lidiflow.Platform;

/// Records a meeting as two 16 kHz mono 16-bit WAV tracks written as they come (an hour does not fit in memory):
/// mic.wav from the chosen microphone ("Você") and system.wav from the default output device through WASAPI
/// loopback ("Eles"). Loopback delivers nothing while nothing plays, so the system track is padded with silence
/// by wall-clock time and stays aligned with the microphone.
public sealed class MeetingRecorder
{
    private const int Rate = MeetingTrack.SampleRate;
    /// Delivery latency allowed before a silent stretch is padded.
    private const int Slack = Rate * 3 / 10;

    private readonly string micPath, systemPath;
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private WaveInEvent? mic;
    private WaveFileWriter? micWriter;
    private WasapiLoopbackCapture? loopback;
    private WaveFileWriter? systemWriter;
    private WdlResampler? resampler;
    private Timer? padTimer;
    private long systemWritten;
    private long lastSystemData;
    private readonly TaskCompletionSource<bool> micStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> systemStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// False when the system audio could not be captured: the meeting is recorded from the microphone only.
    public bool SystemAudio { get; private set; }

    /// Each track's samples as they are written ("me" or "them"), for the live transcript. Called on audio threads.
    public Action<string, short[]>? Captured { get; set; }

    public MeetingRecorder(string micPath, string systemPath)
    {
        this.micPath = micPath;
        this.systemPath = systemPath;
    }

    public void Start()
    {
        var format = new WaveFormat(Rate, 16, 1);
        micWriter = new WaveFileWriter(micPath, format);
        mic = new WaveInEvent
        {
            DeviceNumber = Microphones.Resolve(AppSettings.Current.Microphone),
            WaveFormat = format,
            BufferMilliseconds = 100,
        };
        mic.DataAvailable += (_, e) =>
        {
            lock (gate) micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            if (Captured is { } captured)
            {
                var samples = new short[e.BytesRecorded / 2];
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, samples.Length * 2);
                captured("me", samples);
            }
        };
        mic.RecordingStopped += (_, _) => micStopped.TrySetResult(true);

        try
        {
            loopback = new WasapiLoopbackCapture();
            var input = loopback.WaveFormat;
            resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(true);
            resampler.SetRates(input.SampleRate, Rate);
            systemWriter = new WaveFileWriter(systemPath, format);
            loopback.DataAvailable += (_, e) => OnSystemData(e.Buffer, e.BytesRecorded, input);
            loopback.RecordingStopped += (_, _) => systemStopped.TrySetResult(true);
            SystemAudio = true;
        }
        catch (Exception e)
        {
            Log.Write("Meeting system audio: " + e.Message);
            loopback?.Dispose();
            loopback = null;
            systemStopped.TrySetResult(true);
        }

        clock.Start();
        mic.StartRecording();
        if (loopback != null)
        {
            try
            {
                loopback.StartRecording();
                padTimer = new Timer(_ => PadSilence(), null, 200, 200);
            }
            catch (Exception e)
            {
                Log.Write("Meeting system audio start: " + e.Message);
                SystemAudio = false;
                systemStopped.TrySetResult(true);
            }
        }
    }

    public async Task StopAsync()
    {
        padTimer?.Dispose();
        try { mic?.StopRecording(); } catch { micStopped.TrySetResult(true); }
        try { loopback?.StopRecording(); } catch { systemStopped.TrySetResult(true); }
        await Task.WhenAny(Task.WhenAll(micStopped.Task, systemStopped.Task), Task.Delay(2000));
        lock (gate)
        {
            if (systemWriter != null) PadTo(Expected(), systemWriter);
            micWriter?.Dispose();
            micWriter = null;
            systemWriter?.Dispose();
            systemWriter = null;
        }
        mic?.Dispose();
        loopback?.Dispose();
    }

    private long Expected() => (long)(clock.Elapsed.TotalSeconds * Rate);

    /// Mono, resampled to 16 kHz, after padding any silence the loopback skipped before this buffer.
    private void OnSystemData(byte[] buffer, int bytes, WaveFormat input)
    {
        int channels = Math.Max(1, input.Channels);
        int bytesPerSample = input.BitsPerSample / 8;
        int frames = bytes / (bytesPerSample * channels);
        if (frames == 0 || resampler == null) return;

        int got = resampler.ResamplePrepare(frames, 1, out var inBuffer, out var inOffset);
        for (int f = 0; f < got; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += Sample(buffer, (f * channels + c) * bytesPerSample, input);
            inBuffer[inOffset + f] = sum / channels;
        }
        var output = new float[(int)((long)frames * Rate / input.SampleRate) + 64];
        int produced = resampler.ResampleOut(output, 0, got, output.Length, 1);

        lock (gate)
        {
            if (systemWriter == null) return;
            // A gap longer than the delivery slack is silence that loopback did not send.
            long expected = Expected();
            if (expected - systemWritten > produced + Slack) PadTo(expected - produced, systemWriter);
            var pcm = new byte[produced * 2];
            var samples = new short[produced];
            for (int i = 0; i < produced; i++)
            {
                short value = (short)Math.Clamp(output[i] * 32767f, short.MinValue, short.MaxValue);
                samples[i] = value;
                pcm[i * 2] = (byte)value;
                pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            systemWriter.Write(pcm, 0, pcm.Length);
            Captured?.Invoke("them", samples);
            systemWritten += produced;
            lastSystemData = clock.ElapsedMilliseconds;
        }
    }

    private static float Sample(byte[] buffer, int offset, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
        return format.BitsPerSample switch
        {
            32 when isFloat => BitConverter.ToSingle(buffer, offset),
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            24 => ((buffer[offset] | (buffer[offset + 1] << 8) | (sbyte)buffer[offset + 2] << 16)) / 8388608f,
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            _ => 0,
        };
    }

    /// Fills the system track with silence while nothing is playing.
    private void PadSilence()
    {
        lock (gate)
        {
            if (systemWriter == null || clock.ElapsedMilliseconds - lastSystemData < 300) return;
            PadTo(Expected() - Slack, systemWriter);
        }
    }

    private void PadTo(long samples, WaveFileWriter writer)
    {
        long missing = samples - systemWritten;
        if (missing <= 0) return;
        var zeros = new byte[Math.Min(missing, Rate) * 2];
        while (missing > 0)
        {
            int chunk = (int)Math.Min(missing, zeros.Length / 2);
            writer.Write(zeros, 0, chunk * 2);
            Captured?.Invoke("them", new short[chunk]);
            missing -= chunk;
            systemWritten += chunk;
        }
    }
}
