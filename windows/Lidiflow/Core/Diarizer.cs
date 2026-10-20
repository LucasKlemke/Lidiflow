using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lidiflow.Core;

/// One stretch of the system track attributed to a voice by the separator.
public readonly record struct SpeakerTurn(double Start, double End, int Speaker);

/// Separates the voices of the "them" track with sherpa-onnx (docs/reunioes.md, phase 3). The program and its two
/// models are downloaded to %LOCALAPPDATA%\Lidiflow\diarization the first time a meeting needs them.
public static class Diarizer
{
    private const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    private const string ProgramFolder = "sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts";

    private sealed record Piece(string Url, string Sha256, string File, bool Archive);

    private static readonly Piece[] Pieces =
    {
        new(Release + "v1.13.8/" + ProgramFolder + ".tar.bz2",
            "4b0a94f7b5c606b1b64a19a831c2127559e4b3d34e195465ebc7be73d9ed4783", ProgramFolder, true),
        new(Release + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
            "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488", "sherpa-onnx-pyannote-segmentation-3-0", true),
        new(Release + "speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
            "357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b", "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx", false),
    };

    private static string Folder => Path.Combine(Paths.LocalSupport, "diarization");
    private static string Program => Path.Combine(Folder, ProgramFolder, "bin", "sherpa-onnx-offline-speaker-diarization.exe");
    private static string Segmentation => Path.Combine(Folder, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
    private static string Embedding => Path.Combine(Folder, Pieces[2].File);

    public static bool IsInstalled => File.Exists(Program) && File.Exists(Segmentation) && File.Exists(Embedding);

    /// Downloads what is missing, checking each file's SHA-256; `progress` goes 0…1 over all of it.
    public static async Task InstallAsync(Action<double> progress)
    {
        Directory.CreateDirectory(Folder);
        var missing = Pieces.Where(p => !Path.Exists(Path.Combine(Folder, p.File))).ToList();
        for (int i = 0; i < missing.Count; i++)
        {
            var piece = missing[i];
            int index = i;
            var download = Path.Combine(Folder, Path.GetFileName(new Uri(piece.Url).LocalPath));
            await Downloader.DownloadAsync(piece.Url, download, p => progress((index + p) / missing.Count), CancellationToken.None);
            try
            {
                await using (var stream = File.OpenRead(download))
                {
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(piece.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new UserFacingException("o separador de vozes baixado não é o esperado e foi descartado");
                }
                if (!piece.Archive)
                {
                    File.Move(download, Path.Combine(Folder, piece.File), overwrite: true);
                    continue;
                }
                // Unpack beside the final folder and move it in only when complete.
                var staging = Path.Combine(Folder, ".unpack");
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);
                await RunAsync(Path.Combine(Environment.SystemDirectory, "tar.exe"), $"-xf \"{download}\" -C \"{staging}\"");
                var unpacked = Path.Combine(staging, piece.File);
                if (!Directory.Exists(unpacked)) throw new UserFacingException("o pacote do separador de vozes veio diferente do esperado");
                Directory.Move(unpacked, Path.Combine(Folder, piece.File));
                Directory.Delete(staging, recursive: true);
            }
            finally
            {
                try { File.Delete(download); } catch { }
            }
        }
        progress(1);
    }

    /// The voices of a 16 kHz WAV, as the separator prints them.
    public static async Task<List<SpeakerTurn>> SeparateAsync(string wav)
    {
        var output = await RunAsync(Program,
            "--clustering.cluster-threshold=0.4 " +
            $"--segmentation.pyannote-model=\"{Segmentation}\" --embedding.model=\"{Embedding}\" \"{wav}\"");
        return ParseTurns(output);
    }

    private static readonly Regex TurnLine = new(@"^\s*([\d.]+)\s*--\s*([\d.]+)\s+speaker_(\d+)\s*$", RegexOptions.Multiline);

    /// Lines like "0.031 -- 5.043 speaker_00".
    public static List<SpeakerTurn> ParseTurns(string output) => TurnLine.Matches(output)
        .Select(m => new SpeakerTurn(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                     double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                                     int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
        .ToList();

    /// Gives each "them" segment the voice that overlaps it most, renumbered by first appearance (0, 1, 2…);
    /// returns how many voices there are. Segments that no voice overlaps keep no voice.
    public static int Assign(List<MeetingSegment> segments, List<SpeakerTurn> turns)
    {
        var numbers = new Dictionary<int, int>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            segment.Speaker = null;
            if (segment.Source != "them") continue;
            var best = turns
                .GroupBy(t => t.Speaker)
                .Select(g => (Speaker: g.Key, Overlap: g.Sum(t => Math.Max(0, Math.Min(t.End, segment.End) - Math.Max(t.Start, segment.Start)))))
                .Where(v => v.Overlap > 0)
                .OrderByDescending(v => v.Overlap)
                .FirstOrDefault();
            if (best.Overlap <= 0) continue;
            if (!numbers.TryGetValue(best.Speaker, out var number)) numbers[best.Speaker] = number = numbers.Count;
            segment.Speaker = number;
        }
        return numbers.Count;
    }

    /// Moves a voice's segments to another and removes it, renumbering the voices after it.
    public static void Merge(Meeting meeting, int from, int into)
    {
        if (from == into || from < 0 || into < 0 || from >= meeting.Speakers.Count || into >= meeting.Speakers.Count) return;
        foreach (var segment in meeting.Segments)
        {
            if (segment.Speaker == from) segment.Speaker = into;
            if (segment.Speaker > from) segment.Speaker--;
        }
        // The merged voice keeps a name given to either one.
        if (meeting.Speakers[into].Length == 0 && meeting.Speakers[from].Length > 0) meeting.Speakers[into] = meeting.Speakers[from];
        meeting.Speakers.RemoveAt(from);
    }

    /// Removes voices left with no segments (after moving lines one by one), renumbering the ones after them.
    public static void DropEmptyVoices(Meeting meeting)
    {
        for (int voice = meeting.Speakers.Count - 1; voice >= 0; voice--)
        {
            if (meeting.Segments.Any(s => s.Speaker == voice)) continue;
            foreach (var segment in meeting.Segments)
                if (segment.Speaker > voice) segment.Speaker--;
            meeting.Speakers.RemoveAt(voice);
        }
    }

    private static async Task<string> RunAsync(string program, string arguments)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(program) ?? Folder,
        };
        using var process = Process.Start(start) ?? throw new UserFacingException("não foi possível iniciar o separador de vozes");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(program)} saiu com {process.ExitCode}: {(await stderr).Trim()}");
        // The separator prints its result on stdout and its log on stderr; keep both, the parser picks the lines.
        return await stdout + "\n" + await stderr;
    }
}
