using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lidiflow.Platform;

namespace Lidiflow.Core;

/// One line of the live transcript: who ("me" or "them"), when (seconds from the start) and what was said.
public sealed record LiveUtterance(string Source, double Start, string Text);

/// The live transcript of a meeting being recorded (docs/reunioes.md, "Transcrição ao vivo"): both tracks also go to
/// memory; every 8 s, the speech segments that have already ended are transcribed by the dictation engine and leave
/// memory. Same speech detection as the final transcript, no echo removal and no voices: the final one is still
/// made from the files when the recording stops.
public sealed class LiveTranscriber
{
    /// A segment counts as ended when it ends more than this before the end of what was recorded.
    private const double Settle = 1;

    private sealed class Track
    {
        public readonly List<short> Samples = new();
        /// Seconds from the start of the recording to the first sample in memory.
        public double Offset;
    }

    private readonly Func<byte[], Task<string>> transcribe;
    private readonly Dictionary<string, Track> tracks = new();
    private readonly object gate = new();
    private readonly List<LiveUtterance> utterances = new();
    private bool busy, stopped;

    /// Raised on the caller's context of TickAsync with every utterance so far, in time order.
    public event Action<IReadOnlyList<LiveUtterance>>? Changed;

    public LiveTranscriber(Func<byte[], Task<string>> transcribe)
    {
        this.transcribe = transcribe;
    }

    public IReadOnlyList<LiveUtterance> Utterances => utterances;

    /// Words heard so far, for "Começou sem querer?".
    public int WordCount => utterances.Sum(u => u.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    /// 16 kHz mono samples of a track, from the recorder's audio threads.
    public void Append(string source, short[] samples)
    {
        lock (gate)
        {
            if (stopped) return;
            if (!tracks.TryGetValue(source, out var track)) tracks[source] = track = new Track();
            track.Samples.AddRange(samples);
        }
    }

    /// Stops listening; a tick still running drops what it was transcribing.
    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            tracks.Clear();
        }
    }

    /// Transcribes the segments that ended since the last tick. Ticks never overlap.
    public async Task TickAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            List<(string Source, short[] Samples, double Offset)> snapshot;
            lock (gate)
            {
                if (stopped) return;
                snapshot = tracks.Select(t => (t.Key, t.Value.Samples.ToArray(), t.Value.Offset)).ToList();
            }

            var heard = new List<LiveUtterance>();
            foreach (var (source, samples, offset) in snapshot)
            {
                double duration = (double)samples.Length / MeetingTrack.SampleRate;
                var segments = await Task.Run(() => MeetingTranscript.DetectSpeech(WindowLevels(samples), duration));
                var ended = segments.Where(s => s.End < duration - Settle).ToList();
                foreach (var (start, end) in ended)
                {
                    var piece = samples[(int)(start * MeetingTrack.SampleRate)..(int)Math.Min(samples.Length, end * MeetingTrack.SampleRate)];
                    string text;
                    try { text = (await transcribe(AudioRecorder.Wav(piece))).Trim(); }
                    catch (Exception e) { Log.Write("Live transcript: " + e.Message); continue; }
                    if (text.Length > 0) heard.Add(new LiveUtterance(source, offset + start, text));
                }

                // What stays in memory: from the segment still going on, or the last second or so of silence.
                double keepFrom = ended.Count > 0 ? ended[^1].End : 0;
                var ongoing = segments.Where(s => s.End >= duration - Settle).ToList();
                keepFrom = ongoing.Count > 0 ? Math.Max(keepFrom, ongoing[0].Start) : Math.Max(keepFrom, duration - Settle - 0.5);
                int drop = Math.Clamp((int)(keepFrom * MeetingTrack.SampleRate), 0, samples.Length);
                lock (gate)
                {
                    if (stopped) return;
                    if (tracks.TryGetValue(source, out var track))
                    {
                        track.Samples.RemoveRange(0, Math.Min(drop, track.Samples.Count));
                        track.Offset += (double)drop / MeetingTrack.SampleRate;
                    }
                }
            }

            lock (gate) if (stopped) return;
            if (heard.Count == 0) return;
            utterances.AddRange(heard);
            utterances.Sort((a, b) => a.Start.CompareTo(b.Start));
            Changed?.Invoke(utterances.ToList());
        }
        finally
        {
            busy = false;
        }
    }

    /// RMS (0…1) of each 30 ms window.
    private static float[] WindowLevels(short[] samples)
    {
        int window = MeetingTranscript.WindowSamples;
        var levels = new float[samples.Length / window];
        for (int w = 0; w < levels.Length; w++)
        {
            double sum = 0;
            for (int i = w * window; i < (w + 1) * window; i++)
            {
                double value = samples[i] / 32768.0;
                sum += value * value;
            }
            levels[w] = (float)Math.Sqrt(sum / window);
        }
        return levels;
    }
}
