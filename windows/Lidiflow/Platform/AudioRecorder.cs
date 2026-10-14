using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Lidiflow.Platform;

/// Captures the default microphone as 16 kHz mono 16-bit PCM, which is what Whisper expects.
/// Windows' audio engine does the resampling.
public sealed class AudioRecorder
{
    public const int SampleRate = 16_000;

    /// RMS level of the latest buffer, raised on a background thread.
    public event Action<float>? LevelChanged;

    private Capture? current;

    private void RaiseLevel(float level) => LevelChanged?.Invoke(level);

    public void Start()
    {
        var capture = new Capture(this);
        capture.Begin();
        current = capture;
    }

    /// Stops capture and returns the recorded samples.
    public async Task<short[]> StopAsync()
    {
        var capture = current;
        current = null;
        return capture == null ? Array.Empty<short>() : await capture.EndAsync();
    }

    /// One recording, so a new dictation can start while the previous one is still flushing.
    private sealed class Capture
    {
        private readonly AudioRecorder owner;
        private readonly WaveInEvent wave;
        private readonly List<short> samples = new();
        private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Capture(AudioRecorder owner)
        {
            this.owner = owner;
            // Device -1 is WAVE_MAPPER: the default recording device chosen in Windows.
            wave = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
            wave.DataAvailable += OnData;
            wave.RecordingStopped += (_, _) => stopped.TrySetResult(true);
        }

        public void Begin()
        {
            try
            {
                wave.StartRecording();
            }
            catch
            {
                wave.Dispose();
                throw;
            }
        }

        public async Task<short[]> EndAsync()
        {
            wave.StopRecording();
            await Task.WhenAny(stopped.Task, Task.Delay(500));
            wave.DataAvailable -= OnData;
            wave.Dispose();
            lock (samples) return samples.ToArray();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            int count = e.BytesRecorded / 2;
            var chunk = new short[count];
            double power = 0;
            for (int i = 0; i < count; i++)
            {
                var sample = BitConverter.ToInt16(e.Buffer, i * 2);
                chunk[i] = sample;
                double value = sample / 32768.0;
                power += value * value;
            }
            lock (samples) samples.AddRange(chunk);
            owner.RaiseLevel((float)Math.Sqrt(power / Math.Max(count, 1)));
        }
    }

    /// Encodes samples as a 16-bit PCM mono WAV file.
    public static byte[] Wav(short[] samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            int byteCount = samples.Length * 2;
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + byteCount);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(byteCount);
            foreach (var sample in samples) writer.Write(sample);
        }
        return stream.ToArray();
    }
}
