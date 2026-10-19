using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidiflow.Platform;

namespace Lidiflow.Core;

/// Turns a recorded meeting into a transcript and a summary (docs/reunioes.md): speech segments of each track,
/// transcribed in time order with the dictation engine, echo removed, dictionary corrections, the voices of the
/// "them" track separated (phase 3), then the summary.
/// Runs one meeting at a time; every step saves meeting.json, so a restart resumes where it was.
public sealed class MeetingProcessor
{
    private readonly Func<byte[], Task<string>> transcribe;
    private readonly Func<string, string> correct;
    private readonly System.Threading.SemaphoreSlim queue = new(1, 1);

    /// `transcribe` takes a 16 kHz WAV and returns its text (the same path as a dictation, with the dictionary
    /// hint); `correct` applies the dictionary replacements.
    public MeetingProcessor(Func<byte[], Task<string>> transcribe, Func<string, string> correct)
    {
        this.transcribe = transcribe;
        this.correct = correct;
    }

    public async Task ProcessAsync(Meeting meeting)
    {
        await queue.WaitAsync();
        try
        {
            if (meeting.Discarded) return;
            // Resumes from the step it stopped at: a transcript already made is not made again.
            bool transcribed = meeting.Segments.Count > 0 && meeting.Status is Meeting.Separating or Meeting.Summarizing;
            if (!transcribed) await TranscribeAsync(meeting);
            if (meeting.Status != Meeting.Summarizing || !transcribed) await SeparateAsync(meeting);
            await SummarizeAsync(meeting);
        }
        catch (OperationCanceledException) when (meeting.Discarded)
        {
        }
        catch (Exception e)
        {
            meeting.Status = Meeting.Failed;
            meeting.Error = e is UserFacingException ? e.Message : "não foi possível transcrever";
            Log.Write($"Meeting {meeting.Id}: {e}");
        }
        finally
        {
            // Deleted while processing: a file that was open when the folder was deleted kept it there.
            if (meeting.Discarded) MeetingStore.Delete(meeting);
            else MeetingStore.Save(meeting);
            queue.Release();
        }
    }

    /// Stops a processing whose meeting was deleted ("Começou sem querer?" → Descartar, or the meetings screen).
    private static void ThrowIfDiscarded(Meeting meeting)
    {
        if (meeting.Discarded) throw new OperationCanceledException();
    }

    /// Writes the summary again from the transcript, e.g. after configuring the AI.
    public async Task ResummarizeAsync(Meeting meeting)
    {
        await queue.WaitAsync();
        try
        {
            await SummarizeAsync(meeting);
        }
        catch (OperationCanceledException) when (meeting.Discarded)
        {
        }
        finally
        {
            MeetingStore.Save(meeting);
            queue.Release();
        }
    }

    private async Task TranscribeAsync(Meeting meeting)
    {
        meeting.Status = Meeting.Transcribing;
        meeting.Progress = 0;
        meeting.Error = null;
        MeetingStore.Save(meeting);

        var tracks = new List<(string Source, MeetingTrack Track)>();
        if (File.Exists(meeting.MicPath)) tracks.Add(("me", new MeetingTrack(meeting.MicPath)));
        if (meeting.SystemAudio && File.Exists(meeting.SystemPath)) tracks.Add(("them", new MeetingTrack(meeting.SystemPath)));
        if (tracks.Count == 0) throw new UserFacingException("a gravação não tem áudio");

        var pending = new List<(string Source, MeetingTrack Track, double Start, double End)>();
        foreach (var (source, track) in tracks)
        {
            var levels = await Task.Run(track.WindowLevels);
            foreach (var (start, end) in MeetingTranscript.DetectSpeech(levels, track.Duration))
                pending.Add((source, track, start, end));
        }
        pending = pending.OrderBy(p => p.Start).ToList();

        var segments = new List<MeetingSegment>();
        for (int i = 0; i < pending.Count; i++)
        {
            ThrowIfDiscarded(meeting);
            var (source, track, start, end) = pending[i];
            var samples = await Task.Run(() => track.Read(start, end));
            var text = (await transcribe(AudioRecorder.Wav(samples))).Trim();
            if (text.Length > 0) segments.Add(new MeetingSegment { Source = source, Start = start, End = end, Text = text });
            meeting.Progress = (double)(i + 1) / pending.Count;
            if (i % 10 == 9) MeetingStore.Save(meeting);
        }

        meeting.Segments = MeetingTranscript.RemoveEcho(segments)
            .Select(s => { s.Text = correct(s.Text).Trim(); return s; })
            .Where(s => s.Text.Length > 0)
            .OrderBy(s => s.Start)
            .ToList();
        meeting.Progress = 1;
    }

    /// Names the voices of the "them" track. A failure only leaves the others as "Eles".
    private async Task SeparateAsync(Meeting meeting)
    {
        ThrowIfDiscarded(meeting);
        meeting.Speakers = new List<string>();
        foreach (var segment in meeting.Segments) segment.Speaker = null;
        if (!meeting.SystemAudio || !File.Exists(meeting.SystemPath) || meeting.Segments.All(s => s.Source != "them")) return;

        meeting.Status = Meeting.Separating;
        MeetingStore.Save(meeting);
        try
        {
            if (!Diarizer.IsInstalled)
            {
                await Diarizer.InstallAsync(p => meeting.SeparatingDetail = $"Baixando o separador de vozes {(int)Math.Round(p * 100)}%");
                meeting.SeparatingDetail = null;
            }
            var turns = await Diarizer.SeparateAsync(meeting.SystemPath);
            int voices = Diarizer.Assign(meeting.Segments, turns);
            meeting.Speakers = Enumerable.Repeat("", voices).ToList();
        }
        catch (Exception e)
        {
            foreach (var segment in meeting.Segments) segment.Speaker = null;
            meeting.Speakers = new List<string>();
            Log.Write($"Meeting {meeting.Id} voices: {e.Message}");
        }
        finally
        {
            meeting.SeparatingDetail = null;
        }
        meeting.NotifySegmentsChanged();
    }

    private async Task SummarizeAsync(Meeting meeting)
    {
        ThrowIfDiscarded(meeting);
        meeting.Status = Meeting.Summarizing;
        MeetingStore.Save(meeting);
        var transcript = meeting.TranscriptText();
        if (meeting.Segments.Count == 0)
        {
            meeting.Summary = "## Resumo\nNada foi dito nesta gravação.";
        }
        else
        {
            try
            {
                meeting.Summary = await Llm.SummarizeMeetingAsync(transcript);
            }
            catch (Exception e)
            {
                // Without an AI the meeting is still useful: the transcript stays, the summary says what to do.
                var reason = e is UserFacingException ? e.Message : "a IA não respondeu";
                meeting.Summary = $"## Resumo\nO resumo não foi gerado ({reason}). Configure a IA em Modelos e use Refazer resumo.";
                Log.Write($"Meeting {meeting.Id} summary: {e.Message}");
            }
        }
        ThrowIfDiscarded(meeting);
        meeting.Status = Meeting.Ready;
    }
}
