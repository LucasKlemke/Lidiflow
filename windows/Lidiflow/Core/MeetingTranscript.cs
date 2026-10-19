using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lidiflow.Core;

/// A meeting track: 16 kHz mono 16-bit PCM WAV. Reads past a data size of 0 or a short one, so a file left open
/// by a crash still reads to its end.
public sealed class MeetingTrack
{
    public const int SampleRate = 16_000;

    private readonly string path;
    private readonly long dataOffset;

    public long SampleCount { get; }

    public MeetingTrack(string path)
    {
        this.path = path;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("não é um WAV");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("não é um WAV");
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            if (id == "data")
            {
                dataOffset = stream.Position;
                SampleCount = (stream.Length - dataOffset) / 2;
                return;
            }
            stream.Position += size + (size & 1);
        }
        throw new InvalidDataException("WAV sem dados");
    }

    public double Duration => (double)SampleCount / SampleRate;

    /// Samples from start to end, in seconds.
    public short[] Read(double start, double end)
    {
        long from = Math.Clamp((long)(start * SampleRate), 0, SampleCount);
        long to = Math.Clamp((long)(end * SampleRate), from, SampleCount);
        var samples = new short[to - from];
        using var stream = File.OpenRead(path);
        stream.Position = dataOffset + from * 2;
        var bytes = new byte[samples.Length * 2];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = stream.Read(bytes, read, bytes.Length - read);
            if (n == 0) break;
            read += n;
        }
        Buffer.BlockCopy(bytes, 0, samples, 0, read);
        return samples;
    }

    /// RMS (0…1) of each 30 ms window, read in one streaming pass.
    public float[] WindowLevels()
    {
        int window = MeetingTranscript.WindowSamples;
        var levels = new float[(int)(SampleCount / window)];
        using var stream = File.OpenRead(path);
        stream.Position = dataOffset;
        var bytes = new byte[window * 2 * 1024];
        int index = 0;
        while (index < levels.Length)
        {
            int read = stream.Read(bytes, 0, bytes.Length);
            if (read <= 0) break;
            int windows = read / (window * 2);
            for (int w = 0; w < windows && index < levels.Length; w++, index++)
            {
                double sum = 0;
                int baseByte = w * window * 2;
                for (int i = 0; i < window; i++)
                {
                    double value = BitConverter.ToInt16(bytes, baseByte + i * 2) / 32768.0;
                    sum += value * value;
                }
                levels[index] = (float)Math.Sqrt(sum / window);
            }
            if (read % (window * 2) != 0) stream.Position -= read % (window * 2);
        }
        return levels;
    }
}

/// The pure steps of turning two tracks into a transcript (docs/reunioes.md): where there is speech, which
/// microphone segments are only the echo of the others, and how a long transcript is split for the summary.
public static class MeetingTranscript
{
    public const double WindowSeconds = 0.03;
    public const int WindowSamples = 480; // 30 ms at 16 kHz
    private const double MergeGap = 0.8, MinimumSpeech = 0.4, MaximumSegment = 30, Margin = 0.2;

    /// Stretches of speech, in seconds, from the RMS of each 30 ms window.
    public static List<(double Start, double End)> DetectSpeech(float[] levels, double duration)
    {
        var result = new List<(double, double)>();
        if (levels.Length == 0) return result;
        // Threshold from the track's noise floor (10th percentile), capped at half the level of its loud part (90th
        // percentile). The contract's 3 × median fails on a track that is mostly speech: at 80% speech the median is
        // speech itself and nothing is detected; the cap keeps a track with no pauses at all from going silent too.
        var sorted = (float[])levels.Clone();
        Array.Sort(sorted);
        double floor = sorted[sorted.Length / 10], ceiling = sorted[sorted.Length * 9 / 10];
        double threshold = Math.Max(0.008, Math.Min(3 * floor, ceiling / 2));

        // Runs of loud windows, then gaps shorter than 0.8 s closed.
        var runs = new List<(int Start, int End)>();
        int? open = null;
        for (int i = 0; i < levels.Length; i++)
        {
            bool loud = levels[i] > threshold;
            if (loud && open == null) open = i;
            if (!loud && open is int start) { runs.Add((start, i)); open = null; }
        }
        if (open is int last) runs.Add((last, levels.Length));

        int gapWindows = (int)Math.Round(MergeGap / WindowSeconds);
        var merged = new List<(int Start, int End)>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && run.Start - merged[^1].End < gapWindows) merged[^1] = (merged[^1].Start, run.End);
            else merged.Add(run);
        }

        int minimumWindows = (int)Math.Round(MinimumSpeech / WindowSeconds);
        int maximumWindows = (int)Math.Round(MaximumSegment / WindowSeconds);
        int searchFrom = (int)Math.Round(20 / WindowSeconds);
        foreach (var run in merged.Where(r => r.End - r.Start >= minimumWindows))
        {
            // Longer than 30 s: cut at the quietest window between 20 and 30 s in, and go on from there.
            int start = run.Start;
            while (run.End - start > maximumWindows)
            {
                int cut = start + maximumWindows;
                float quietest = float.MaxValue;
                for (int i = start + searchFrom; i < start + maximumWindows; i++)
                {
                    if (levels[i] < quietest) { quietest = levels[i]; cut = i; }
                }
                // No margin at a cut: the neighbouring pieces would both transcribe the same words.
                result.Add(Padded(start, cut, duration, start == run.Start, false));
                start = cut;
            }
            result.Add(Padded(start, run.End, duration, start == run.Start, true));
        }
        return result;
    }

    private static (double, double) Padded(int start, int end, double duration, bool padStart, bool padEnd) =>
        (Math.Max(0, start * WindowSeconds - (padStart ? Margin : 0)),
         Math.Min(duration, end * WindowSeconds + (padEnd ? Margin : 0)));

    /// Drops "me" segments that overlap a "them" segment in time and share at least 60% of their words with it:
    /// without headphones the microphone hears the others through the speakers.
    public static List<MeetingSegment> RemoveEcho(List<MeetingSegment> segments)
    {
        var them = segments.Where(s => s.Source == "them").ToList();
        return segments.Where(segment =>
        {
            if (segment.Source != "me") return true;
            var words = Words(segment.Text);
            if (words.Count == 0) return true;
            foreach (var other in them)
            {
                if (other.End <= segment.Start || other.Start >= segment.End) continue;
                var shared = words.Count(Words(other.Text).Contains);
                if ((double)shared / words.Count >= 0.6) return false;
            }
            return true;
        }).ToList();
    }

    /// Lowercase words without accents, for comparing two transcriptions of the same speech.
    public static HashSet<string> Words(string text)
    {
        var folded = new string(text.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        return Regex.Split(folded, @"[^\p{L}\p{Nd}]+").Where(w => w.Length > 0).ToHashSet();
    }

    /// Parts of at most `limit` characters, split between lines (a single longer line is cut where it must).
    public static List<string> SplitForSummary(string transcript, int limit = 8000)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in transcript.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            for (int start = 0; start < Math.Max(line.Length, 1); start += limit)
            {
                var piece = line.Length == 0 ? "" : line.Substring(start, Math.Min(limit, line.Length - start));
                if (current.Length > 0 && current.Length + 1 + piece.Length > limit)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append('\n');
                current.Append(piece);
            }
        }
        if (current.ToString().Trim().Length > 0) parts.Add(current.ToString());
        return parts;
    }
}
