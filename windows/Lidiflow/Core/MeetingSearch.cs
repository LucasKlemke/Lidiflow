using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lidiflow.Core;

/// Picks the meetings and lines a question is about, before asking the AI (docs/reunioes.md, "Fase 4").
public static class MeetingSearch
{
    private const int Limit = 8_000;

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "que", "para", "com", "uma", "por", "mas", "foi", "ser", "tem", "como",
        "sobre", "isso", "essa", "esse", "quem", "qual", "quando", "onde", "reuniao", "reunioes",
    };

    /// hoje, ontem, semana (the last 7 days) or mês (the last 30), from the question's words.
    public static (DateTime From, DateTime To)? Period(IReadOnlyCollection<string> words, DateTime now)
    {
        var today = now.Date;
        if (words.Contains("hoje")) return (today, today.AddDays(1));
        if (words.Contains("ontem")) return (today.AddDays(-1), today);
        if (words.Contains("semana")) return (today.AddDays(-6), today.AddDays(1));
        if (words.Contains("mes")) return (today.AddDays(-29), today.AddDays(1));
        return null;
    }

    /// Lowercase, without accents, without words of up to 2 letters and the common ones.
    public static List<string> Words(string text)
    {
        var folded = new string(text.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        return Regex.Split(folded, @"[^\p{L}\p{Nd}]+").Where(w => w.Length > 2 && !Common.Contains(w)).Distinct().ToList();
    }

    /// The context for the question: the 3 meetings with most hits (or just `only`), each with its title, date,
    /// summary and the lines with a hit plus one before and one after; up to 8,000 characters. With no hit, the
    /// 3 most recent meetings, summary only.
    public static string Context(string question, IReadOnlyList<Meeting> meetings, Meeting? only = null)
    {
        var words = Words(question);
        var pool = only != null ? new List<Meeting> { only } : meetings.Where(m => m.Status == Meeting.Ready).ToList();

        // A period in the question ("hoje", "ontem", "semana", "mês") takes every meeting in it, summaries only.
        if (only == null && Period(words, DateTime.Now) is { } period)
        {
            var inPeriod = pool.Where(m => m.StartedAt.LocalDateTime >= period.From && m.StartedAt.LocalDateTime < period.To)
                               .OrderByDescending(m => m.StartedAt).ToList();
            var summaries = new StringBuilder();
            foreach (var meeting in inPeriod)
            {
                var part = $"### {meeting.Title} ({meeting.StartedAt:dd/MM/yyyy HH:mm})\n{meeting.Summary?.Trim()}\n\n";
                if (summaries.Length + part.Length > Limit)
                {
                    summaries.Append(part[..Math.Max(0, Limit - summaries.Length)]);
                    break;
                }
                summaries.Append(part);
            }
            return summaries.ToString().Trim();
        }

        int Hits(string text)
        {
            var folded = string.Join(' ', Words(text));
            return words.Sum(w => Regex.Matches(folded, $@"\b{Regex.Escape(w)}\b").Count);
        }

        var scored = pool.Select(m => (meeting: m, score: Hits(m.Title) + Hits(m.Summary ?? "") + Hits(m.TranscriptText())))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(3).Select(x => x.meeting).ToList();
        bool found = scored.Count > 0;
        if (!found) scored = pool.OrderByDescending(m => m.StartedAt).Take(3).ToList();

        var builder = new StringBuilder();
        foreach (var meeting in scored)
        {
            var part = new StringBuilder();
            part.AppendLine($"### {meeting.Title} ({meeting.StartedAt:dd/MM/yyyy HH:mm})");
            if (!string.IsNullOrWhiteSpace(meeting.Summary)) part.AppendLine(meeting.Summary.Trim());
            if (found)
            {
                var segments = meeting.Segments;
                var keep = new SortedSet<int>();
                for (int i = 0; i < segments.Count; i++)
                {
                    if (words.Count > 0 && Words(segments[i].Text).Intersect(words).Any())
                    {
                        if (i > 0) keep.Add(i - 1);
                        keep.Add(i);
                        if (i + 1 < segments.Count) keep.Add(i + 1);
                    }
                }
                if (keep.Count > 0) part.AppendLine("Falas:");
                foreach (int i in keep)
                    part.AppendLine($"[{Meeting.Clock(segments[i].Start)}] {meeting.LabelFor(segments[i])}: {segments[i].Text}");
            }
            if (builder.Length + part.Length > Limit)
            {
                builder.Append(part.ToString()[..Math.Max(0, Limit - builder.Length)]);
                break;
            }
            builder.AppendLine(part.ToString());
        }
        return builder.ToString().Trim();
    }
}
