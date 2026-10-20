using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lidiflow.Core;
using NAudio.Wave;

namespace Lidiflow.UI;

/// Meetings: record one, then read its summary and its transcript (Você / Eles). Mirrors the macOS
/// MeetingsViews.swift: a list of cards, and a detail with tabs, summary blocks, a conversation and skeletons.
public partial class MeetingsPage : UserControl, IOwnScrolling
{
    private static readonly CultureInfo PtBr = new("pt-BR");
    private const string Icons = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly AppModel model;
    private Meeting? selected;
    private string tab = "summary";
    private string? renderedStatus;
    private readonly List<TextBlock> timers = new();
    // Live parts of a skeleton view, updated in place so the pulsing does not restart on every progress tick.
    private TextBlock? progressText;
    private ProgressBar? progressBar;
    private readonly HashSet<Meeting> watched = new();
    /// Meetings whose voices were renamed or merged since the summary was written.
    private readonly HashSet<Meeting> namesChanged = new();
    private WaveOutEvent? player;
    // The question asked on this page (list or one meeting), kept across re-renders while it is answered.
    private string? askScope;
    private string askQuestion = "";
    private string? askAnswer;
    private bool askBusy;

    private static bool Animate => SystemParameters.ClientAreaAnimation;

    public MeetingsPage(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        model.Meetings.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (selected != null && !model.Meetings.Contains(selected)) selected = null;
            Render();
        });
        model.PropertyChanged += (_, e) => Dispatcher.InvokeAsync(() =>
        {
            if (e.PropertyName == nameof(AppModel.MeetingElapsed))
            {
                foreach (var timer in timers) timer.Text = Meeting.Clock(model.MeetingElapsed.TotalSeconds);
            }
            else if (e.PropertyName is nameof(AppModel.IsMeetingRecording) or nameof(AppModel.AutoRecordMeetings)
                     or nameof(AppModel.AutoRecordAsked))
            {
                Render();
            }
        });
        // The preview appears beside the list only when the page is wide enough.
        SizeChanged += (_, _) => { if (selected == null && (ActualWidth >= 900) != wide) Render(); };
        Render();
    }

    private bool ownedScrolling = true;

    private void Render()
    {
        foreach (var added in model.Meetings.Where(m => watched.Add(m))) added.PropertyChanged += Meeting_PropertyChanged;
        timers.Clear();
        progressText = null;
        progressBar = null;
        // Re-rendering the list keeps where it was scrolled.
        double offset = selected == null ? listScroll?.VerticalOffset ?? 0 : 0;
        listScroll = null;
        pastRows = null;
        Root.Children.Clear();
        Root.Children.Add(selected is { } meeting ? BuildDetail(meeting) : BuildList());
        if (listScroll is { } scroll && offset > 0)
            Dispatcher.InvokeAsync(() => scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        if (OwnsScrolling != ownedScrolling)
        {
            ownedScrolling = OwnsScrolling;
            OwnsScrollingChanged?.Invoke();
        }
    }

    private void Meeting_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Meeting meeting) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (selected == null)
            {
                if (e.PropertyName is nameof(Meeting.Status) or nameof(Meeting.Progress) or nameof(Meeting.Duration)
                    or nameof(Meeting.Segments)) Render();
                return;
            }
            if (meeting != selected) return;
            if (e.PropertyName is nameof(Meeting.Progress) or nameof(Meeting.SeparatingDetail)
                && meeting.Status == renderedStatus && progressText != null)
            {
                progressText.Text = ProgressLabel(meeting);
                if (progressBar != null) progressBar.Value = meeting.Progress;
                return;
            }
            if (e.PropertyName is nameof(Meeting.Status) or nameof(Meeting.Segments) or nameof(Meeting.Summary)
                or nameof(Meeting.Error) or nameof(Meeting.SystemAudio) or nameof(Meeting.Duration)) Render();
        });
    }

    private void Open(Meeting? meeting, string openTab = "summary")
    {
        selected = meeting;
        tab = openTab;
        renderedStatus = null;
        Render();
        if (Root.Children.Count > 0 && meeting != null) SlideIn(Root.Children[0], 24, 0);
        (Parent as ScrollViewer)?.ScrollToTop();
    }

    // MARK: - List (docs/reunioes.md, "Tela de Reuniões (versão 3)")

    /// The list scrolls its own middle column so the question stays pinned at the bottom and the preview at the side.
    public bool OwnsScrolling => selected == null;
    public event Action? OwnsScrollingChanged;

    private Meeting? previewed;
    private bool searchOpen;
    private string searchText = "";
    private bool wide;
    private ScrollViewer? listScroll;

    private UIElement BuildList()
    {
        wide = ActualWidth >= 900;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (wide && model.Meetings.Count > 0) root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var page = new StackPanel { Margin = new Thickness(32, 24, 32, 24), MaxWidth = 820 };
        page.Children.Add(ListHeader());
        if (model.IsMeetingRecording)
        {
            var banner = RecordingBanner();
            banner.Margin = new Thickness(0, 0, 0, 16);
            page.Children.Add(banner);
        }
        if (!model.AutoRecordMeetings && !model.AutoRecordAsked) page.Children.Add(AutoRecordBanner());
        page.Children.Add(PastNotes());

        listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };
        center.Children.Add(listScroll);

        if (model.Meetings.Any(m => m.Status == Meeting.Ready))
        {
            var footer = FooterAsk();
            Grid.SetRow(footer, 1);
            center.Children.Add(footer);
        }
        root.Children.Add(center);

        if (wide && model.Meetings.Count > 0)
        {
            var preview = Preview(PreviewedMeeting());
            Grid.SetColumn(preview, 1);
            root.Children.Add(preview);
        }
        return root;
    }

    private Meeting? PreviewedMeeting() =>
        previewed != null && model.Meetings.Contains(previewed) ? previewed : model.Meetings.OrderByDescending(m => m.StartedAt).FirstOrDefault();

    private UIElement ListHeader()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20), LastChildFill = true };
        var record = model.IsMeetingRecording
            ? PrimaryButton("", "Parar e resumir", () => model.ToggleMeeting?.Invoke())
            : PrimaryButton("", "Gravar reunião", () => model.ToggleMeeting?.Invoke());
        record.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(record, Dock.Right);
        header.Children.Add(record);
        var gear = IconButton("", "Ajustes", true, () => model.ShowSettings?.Invoke());
        gear.VerticalAlignment = VerticalAlignment.Center;
        gear.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(gear, Dock.Right);
        header.Children.Add(gear);
        var title = new TextBlock { Text = "Reuniões", Style = (Style)FindResource("PageTitle"), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        return header;
    }

    /// "Quer que o Lidiflow grave sozinho?", until answered either way.
    private UIElement AutoRecordBanner()
    {
        var row = new DockPanel { LastChildFill = true };

        var turnOn = SecondaryButton("Ativar", () =>
        {
            model.AutoRecordMeetings = true;
            model.DetectMeetings = true;
            model.AutoRecordAsked = true;
            Render();
        });
        turnOn.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(turnOn, Dock.Right);
        row.Children.Add(turnOn);

        var notNow = Text("Agora não", 13, FontWeights.Medium);
        notNow.VerticalAlignment = VerticalAlignment.Center;
        notNow.Margin = new Thickness(16, 0, 16, 0);
        notNow.Cursor = Cursors.Hand;
        notNow.MouseLeftButtonUp += (_, _) =>
        {
            model.AutoRecordAsked = true;
            Render();
        };
        AutomationLabel(notNow, "Agora não");
        DockPanel.SetDock(notNow, Dock.Right);
        row.Children.Add(notNow);

        var icon = Glyph("", 16, "AccentFillColorDefaultBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Child = icon,
                                  Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        circle.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel.SetDock(circle, Dock.Left);
        row.Children.Add(circle);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text("Quer que o Lidiflow grave sozinho?", 14, FontWeights.SemiBold));
        var body = Text("Começa a gravar assim que a chamada começa e para quando ela acaba.", 13, fg: "TextFillColorSecondaryBrush", wrap: true);
        body.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(body);
        row.Children.Add(texts);

        var card = Card(row, new Thickness(16, 14, 16, 14));
        card.Margin = new Thickness(0, 0, 0, 16);
        return card;
    }

    /// Anteriores: the recorded meetings by day, with a search behind the magnifier.
    private UIElement PastNotes()
    {
        var stack = new StackPanel();

        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 16) };
        var magnifier = IconButton(searchOpen ? "" : "", searchOpen ? "Fechar a busca" : "Buscar", true, () =>
        {
            searchOpen = !searchOpen;
            if (!searchOpen) searchText = "";
            Render();
        });
        magnifier.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(magnifier, Dock.Right);
        bar.Children.Add(magnifier);
        var tabLabel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        tabLabel.Children.Add(Text("Anteriores", 14, FontWeights.SemiBold));
        var underline = new Border { Height = 2, Margin = new Thickness(0, 7, 0, 0) };
        underline.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        tabLabel.Children.Add(underline);
        bar.Children.Add(tabLabel);
        stack.Children.Add(bar);

        if (searchOpen)
        {
            var input = new TextBox { Text = searchText, FontSize = 14, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 16) };
            input.SetResourceReference(Control.FontFamilyProperty, "BodyFont");
            AutomationLabel(input, "Buscar nas reuniões");
            input.TextChanged += (_, _) =>
            {
                searchText = input.Text;
                RenderPastRows();
            };
            input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                searchOpen = false;
                searchText = "";
                Render();
            };
            input.Loaded += (_, _) => { input.Focus(); input.CaretIndex = input.Text.Length; };
            stack.Children.Add(input);
        }

        pastRows = new StackPanel();
        stack.Children.Add(pastRows);
        RenderPastRows();
        return stack;
    }

    private StackPanel? pastRows;

    /// The day groups, rebuilt alone while typing in the search so the field keeps its focus.
    private void RenderPastRows()
    {
        if (pastRows == null) return;
        pastRows.Children.Clear();
        if (model.Meetings.Count == 0)
        {
            pastRows.Children.Add(EmptyState());
            return;
        }
        var query = searchText.Trim();
        var shown = model.Meetings.Where(m => query.Length == 0 || Matches(m, query)).OrderByDescending(m => m.StartedAt).ToList();
        if (shown.Count == 0)
        {
            pastRows.Children.Add(Text($"Nada encontrado para “{query}”.", 14, fg: "TextFillColorSecondaryBrush"));
            return;
        }
        var today = DateTime.Now.Date;
        foreach (var day in shown.GroupBy(m => m.StartedAt.LocalDateTime.Date))
        {
            var label = day.Key == today ? "HOJE"
                      : day.Key == today.AddDays(-1) ? "ONTEM"
                      : day.Key.ToString("ddd, d 'de' MMM", PtBr).Replace(".", "").ToUpper(PtBr);
            var heading = Text(label, 11, FontWeights.Medium, "TextFillColorSecondaryBrush", "MonoFont");
            heading.Margin = new Thickness(6, pastRows.Children.Count == 0 ? 0 : 18, 0, 8);
            pastRows.Children.Add(heading);
            foreach (var meeting in day) pastRows.Children.Add(PastRow(meeting));
        }
    }

    private static bool Matches(Meeting meeting, string query)
    {
        var compare = PtBr.CompareInfo;
        const CompareOptions loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return compare.IndexOf(meeting.Title, query, loose) >= 0
            || compare.IndexOf(meeting.Summary ?? "", query, loose) >= 0
            || compare.IndexOf(meeting.TranscriptText(), query, loose) >= 0;
    }

    private UIElement PastRow(Meeting meeting)
    {
        bool isSelected = meeting == PreviewedMeeting() && wide;
        var row = new DockPanel { LastChildFill = true };

        if (meeting.Status != Meeting.Ready)
        {
            var pill = StatusPill(meeting);
            pill.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pill, Dock.Right);
            row.Children.Add(pill);
        }

        var icon = Glyph("", 15, "TextFillColorSecondaryBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(4), Child = icon, Margin = new Thickness(0, 0, 14, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, isSelected ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorSecondaryBrush");
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text(meeting.Title, 14, FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(title);
        var meta = meeting.StartedAt.LocalDateTime.ToString("HH:mm", PtBr) + (meeting.Status == Meeting.Recording ? "" : "  ·  " + meeting.DurationLabel);
        var line = Text(meta, 12, fg: "TextFillColorSecondaryBrush");
        line.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(line);
        if (meeting.Status == Meeting.Transcribing)
        {
            var progress = ThinProgress(meeting.Progress);
            progress.MaxWidth = 220;
            progress.HorizontalAlignment = HorizontalAlignment.Left;
            progress.Margin = new Thickness(0, 6, 0, 0);
            info.Children.Add(progress);
        }
        row.Children.Add(info);

        var item = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 4), Child = row, Cursor = Cursors.Hand,
                                Background = Brushes.Transparent, ToolTip = "Clique para ver, dois cliques para abrir" };
        if (isSelected) item.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        item.MouseEnter += (_, _) => { if (!isSelected) item.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); };
        item.MouseLeave += (_, _) => { if (!isSelected) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2 || !wide)
            {
                Open(meeting);
                return;
            }
            previewed = meeting;
            Render();
        };
        AutomationLabel(item, meeting.Title);
        return item;
    }

    /// The question pinned at the bottom, with the answer in a card above it.
    private UIElement FooterAsk()
    {
        if (askScope != "*")
        {
            askScope = "*";
            askQuestion = "";
            askAnswer = null;
            askBusy = false;
        }
        var stack = new StackPanel { Margin = new Thickness(32, 4, 32, 16), MaxWidth = 820 };

        if (askBusy || askAnswer != null)
        {
            UIElement content;
            if (askBusy)
            {
                var waiting = new StackPanel();
                waiting.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
                waiting.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
                waiting.Children.Add(Skeleton(260, 12));
                content = waiting;
            }
            else
            {
                var text = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
                text.SetResourceReference(TextBlock.FontFamilyProperty, "BodyFont");
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                AddAnswer(text, askAnswer!);
                var close = Glyph("", 11, "TextFillColorSecondaryBrush");
                close.Cursor = Cursors.Hand;
                close.Margin = new Thickness(12, 2, 0, 0);
                close.VerticalAlignment = VerticalAlignment.Top;
                close.ToolTip = "Fechar";
                close.MouseLeftButtonUp += (_, _) =>
                {
                    askAnswer = null;
                    Render();
                };
                AutomationLabel(close, "Fechar a resposta");
                var dock = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(close, Dock.Right);
                dock.Children.Add(close);
                dock.Children.Add(new ScrollViewer { Content = text, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                content = dock;
            }
            var card = Card(content, new Thickness(16));
            card.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(card);
            if (!askBusy) SlideIn(card, 0, 8);
        }

        var input = new TextBox
        {
            Text = askQuestion, FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = !askBusy, Padding = new Thickness(2, 0, 2, 0),
        };
        input.SetResourceReference(Control.FontFamilyProperty, "BodyFont");
        input.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        const string placeholder = "Resuma as reuniões da semana…";
        AutomationLabel(input, "Pergunte sobre as suas reuniões");
        var hint = Text(placeholder, 14, fg: "TextFillColorSecondaryBrush");
        hint.Margin = new Thickness(4, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) =>
        {
            askQuestion = input.Text;
            hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || askBusy || input.Text.Trim().Length == 0) return;
            e.Handled = true;
            await AskAsync(input.Text.Trim(), null, "*");
        };
        var field = new Grid { MinHeight = 24 };
        field.Children.Add(input);
        field.Children.Add(hint);
        // The one rounded field on the page: a pill, like the question bar it is modeled on.
        var pill = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1), Padding = new Thickness(18, 10, 18, 10), Child = field };
        pill.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        pill.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        stack.Children.Add(pill);
        return stack;
    }

    /// The selected meeting at a glance: overview, people and tasks, and "Abrir reunião".
    private UIElement Preview(Meeting? meeting)
    {
        var pane = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        pane.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        if (meeting == null) return pane;

        var stack = new StackPanel { Margin = new Thickness(24, 28, 24, 24) };
        var title = Text(meeting.Title, 22, FontWeights.Medium, "TextFillColorPrimaryBrush", "DisplayFont", wrap: true);
        stack.Children.Add(title);
        var when = meeting.StartedAt.LocalDateTime;
        var date = Text($"{when.ToString("dddd", PtBr)} • {when.ToString("HH:mm", PtBr)}", 13, fg: "TextFillColorSecondaryBrush");
        date.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(date);
        var rule = new Border { Height = 1, Margin = new Thickness(0, 18, 0, 18) };
        rule.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");
        stack.Children.Add(rule);

        stack.Children.Add(PreviewLabel("VISÃO GERAL"));
        var summary = meeting.Summary is { } markdown ? MeetingSummary.Parse(markdown) : null;
        string overview = meeting.Status == Meeting.Ready
            ? (summary is { Overview.Count: > 0 } ? Plain(string.Join(" ", summary.Overview)) : "Sem resumo.")
            : StatusText(meeting) + "…";
        var paragraph = Text(overview, 14, wrap: true);
        paragraph.LineHeight = 22;
        stack.Children.Add(paragraph);

        var people = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var you = Text("Você", 13, FontWeights.SemiBold, "AccentFillColorDefaultBrush");
        you.Margin = new Thickness(0, 0, 12, 4);
        people.Children.Add(you);
        for (int voice = 0; voice < meeting.Speakers.Count; voice++)
        {
            var name = Text(meeting.VoiceName(voice), 13, FontWeights.SemiBold);
            name.Foreground = VoiceInk(voice);
            name.Margin = new Thickness(0, 0, 12, 4);
            people.Children.Add(name);
        }
        var peopleLabel = PreviewLabel("PARTICIPANTES");
        peopleLabel.Margin = new Thickness(0, 20, 0, 8);
        stack.Children.Add(peopleLabel);
        stack.Children.Add(people);

        if (summary is { Tasks.Count: > 0 })
        {
            var tasksLabel = PreviewLabel("TAREFAS");
            tasksLabel.Margin = new Thickness(0, 16, 0, 8);
            stack.Children.Add(tasksLabel);
            foreach (var task in summary.Tasks.Take(5))
            {
                var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
                if (task.Owner is { } owner)
                {
                    var chip = OwnerChip(owner);
                    chip.VerticalAlignment = VerticalAlignment.Top;
                    chip.Margin = new Thickness(0, 1, 8, 0);
                    DockPanel.SetDock(chip, Dock.Left);
                    row.Children.Add(chip);
                }
                row.Children.Add(Text(Plain(task.Text), 13, wrap: true));
                stack.Children.Add(row);
            }
        }

        var open = PrimaryButton(null, "Abrir reunião", () => Open(meeting));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 22, 0, 0);
        stack.Children.Add(open);

        pane.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return pane;
    }

    private TextBlock PreviewLabel(string text)
    {
        var label = Text(text, 10, FontWeights.Medium, "TextFillColorSecondaryBrush", "MonoFont");
        label.Margin = new Thickness(0, 0, 0, 8);
        return label;
    }

    private Border RecordingBanner()
    {
        var row = new DockPanel { LastChildFill = true };

        var stop = PrimaryButton("", "Parar e resumir", () => model.ToggleMeeting?.Invoke());
        stop.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(stop);

        var note = Text("Seu microfone e o áudio do computador", 12, fg: "TextFillColorSecondaryBrush");
        note.VerticalAlignment = VerticalAlignment.Center;
        note.Margin = new Thickness(16, 0, 16, 0);
        DockPanel.SetDock(note, Dock.Right);
        row.Children.Add(note);

        var dot = PulsingDot(12);
        dot.VerticalAlignment = VerticalAlignment.Center;
        dot.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text("GRAVANDO REUNIÃO", 10, FontWeights.Medium, "AccentFillColorDefaultBrush", "MonoFont"));
        var timer = Text(Meeting.Clock(model.MeetingElapsed.TotalSeconds), 28, FontWeights.Medium, "TextFillColorPrimaryBrush", "MonoFont");
        timers.Add(timer);
        labels.Children.Add(timer);
        row.Children.Add(labels);

        var banner = new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = row };
        banner.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        banner.BorderBrush = AccentAt(0.35);
        return banner;
    }

    private UIElement EmptyState()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Glyph("", 28, "AccentFillColorDefaultBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(icon);
        var title = Text("Nenhuma reunião gravada", 20, FontWeights.Medium, "TextFillColorPrimaryBrush", "DisplayFont");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 10, 0, 6);
        stack.Children.Add(title);
        var body = Text("Comece a gravar quando a chamada começar. Ao parar, o Lidiflow separa o que você disse do que os outros disseram e faz o resumo.",
                        14, fg: "TextFillColorSecondaryBrush", wrap: true);
        body.TextAlignment = TextAlignment.Center;
        body.MaxWidth = 440;
        stack.Children.Add(body);
        return Card(stack, new Thickness(16, 44, 16, 44));
    }

    private Border StatusPill(Meeting meeting)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement dot = meeting.Status == Meeting.Recording
            ? PulsingDot(7)
            : new Ellipse { Width = 7, Height = 7, Fill = StatusColor(meeting) };
        dot.VerticalAlignment = VerticalAlignment.Center;
        dot.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(dot);
        row.Children.Add(Text(StatusText(meeting).ToUpperInvariant(), 10, FontWeights.Medium, "TextFillColorPrimaryBrush", "MonoFont"));
        var pill = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), Child = row };
        pill.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return pill;
    }

    private static string StatusText(Meeting meeting) => meeting.Status switch
    {
        Meeting.Recording => "Gravando",
        Meeting.Transcribing => $"Transcrevendo {(int)Math.Round(meeting.Progress * 100)}%",
        Meeting.Separating => meeting.SeparatingDetail ?? "Separando as vozes",
        Meeting.Summarizing => "Resumindo",
        Meeting.Failed => "Falhou",
        _ => meeting.SystemAudio ? "Pronta" : "Só o seu microfone",
    };

    private Brush StatusColor(Meeting meeting)
    {
        return (Brush)FindResource(meeting.Status switch
        {
            Meeting.Ready when meeting.SystemAudio => "SystemFillColorSuccessBrush",
            Meeting.Failed => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorCautionBrush",
        });
    }

    // MARK: - Detail

    private UIElement BuildDetail(Meeting meeting)
    {
        var page = new StackPanel();
        bool statusChanged = renderedStatus != null && renderedStatus != meeting.Status;
        renderedStatus = meeting.Status;

        var back = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent,
                                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18) };
        var arrow = Glyph("", 11, "AccentFillColorDefaultBrush");
        arrow.VerticalAlignment = VerticalAlignment.Center;
        arrow.Margin = new Thickness(0, 0, 6, 0);
        back.Children.Add(arrow);
        back.Children.Add(Text("Reuniões", 13, FontWeights.Medium, "AccentFillColorDefaultBrush"));
        back.MouseLeftButtonUp += (_, _) => Open(null);
        page.Children.Add(back);

        // Header: title and state, then the facts.
        var titleRow = new DockPanel { LastChildFill = true };
        var pill = StatusPill(meeting);
        pill.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(pill, Dock.Right);
        titleRow.Children.Add(pill);
        var title = Text(meeting.Title, 30, FontWeights.Medium, "TextFillColorPrimaryBrush", "DisplayFont", wrap: true);
        title.Margin = new Thickness(0, 0, 12, 0);
        titleRow.Children.Add(title);
        page.Children.Add(titleRow);

        var facts = new WrapPanel { Margin = new Thickness(0, 10, 0, 18) };
        facts.Children.Add(Meta("", meeting.StartedAt.LocalDateTime.ToString("d 'de' MMMM, HH:mm", PtBr)));
        if (meeting.Status != Meeting.Recording) facts.Children.Add(Meta("", meeting.DurationLabel));
        if (meeting.Segments.Count > 0) facts.Children.Add(Meta("", $"{meeting.Segments.Count} falas"));
        facts.Children.Add(meeting.SystemAudio ? Meta("", "Você e os participantes") : Meta("", "Só o seu microfone"));
        page.Children.Add(facts);

        if (!string.IsNullOrEmpty(meeting.Error))
        {
            page.Children.Add(Notice("", meeting.Error!));
        }
        else if (meeting.Status == Meeting.Ready && !meeting.SystemAudio)
        {
            page.Children.Add(Notice("", "Só o seu microfone foi gravado. Para captar os outros, confira o dispositivo de saída de som do Windows e grave de novo."));
        }

        if (meeting == model.RecordingMeeting || meeting.Status == Meeting.Recording)
        {
            page.Children.Add(RecordingBanner());
            return page;
        }

        if (meeting.Status == Meeting.Ready && meeting.Segments.Count > 0) page.Children.Add(AskBox(meeting));

        // Tabs on the left, actions on the right.
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        var actions = Actions(meeting);
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        var tabs = Tabs();
        DockPanel.SetDock(tabs, Dock.Left);
        bar.Children.Add(tabs);
        page.Children.Add(bar);

        var content = tab == "summary" ? SummaryContent(meeting) : TranscriptContent(meeting);
        page.Children.Add(content);
        // New content arrives with a fade; the first view and tab switches too.
        if (statusChanged || renderedTab != tab) SlideIn(content, 0, 8);
        renderedTab = tab;
        return page;
    }

    private string? renderedTab;

    /// "Resumo" and "Transcrição", with an underline that slides to the selected one.
    private UIElement Tabs()
    {
        var labels = new StackPanel { Orientation = Orientation.Horizontal };
        var underline = new Rectangle { Height = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        underline.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        var slide = new TranslateTransform();
        underline.RenderTransform = slide;
        var grid = new Grid();
        grid.Children.Add(labels);
        grid.Children.Add(underline);

        var items = new List<(string Key, TextBlock Label)>();
        foreach (var (key, name) in new[] { ("summary", "Resumo"), ("transcript", "Transcrição") })
        {
            bool active = tab == key;
            var label = Text(name, 14, active ? FontWeights.SemiBold : FontWeights.Normal, active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 0, 0, 9), Margin = new Thickness(0, 0, 22, 0),
                                   Cursor = Cursors.Hand, Child = label };
            hit.MouseLeftButtonUp += (_, _) =>
            {
                if (tab == key) return;
                var from = PositionOf(items.First(i => i.Key == tab).Label, labels);
                tab = key;
                pendingUnderlineFrom = from;
                Render();
            };
            labels.Children.Add(hit);
            items.Add((key, label));
        }

        grid.Loaded += (_, _) =>
        {
            var target = items.First(i => i.Key == tab).Label;
            var to = PositionOf(target, labels);
            underline.Width = target.ActualWidth;
            if (pendingUnderlineFrom is { } from && Animate)
            {
                slide.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else
            {
                slide.X = to;
            }
            pendingUnderlineFrom = null;
        };
        return grid;
    }

    private double? pendingUnderlineFrom;

    private static double PositionOf(FrameworkElement element, Visual within) =>
        element.IsLoaded ? element.TranslatePoint(new Point(0, 0), (UIElement)within).X : 0;

    private UIElement Actions(Meeting meeting)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        bool done = meeting.Status is Meeting.Ready or Meeting.Failed;
        row.Children.Add(CopyButton("", "Copiar resumo", () => meeting.Summary, !string.IsNullOrWhiteSpace(meeting.Summary)));
        row.Children.Add(CopyButton("", "Copiar transcrição", meeting.TranscriptText, meeting.Segments.Count > 0));
        row.Children.Add(IconButton("", "Refazer resumo", done && meeting.Segments.Count > 0, () => model.ResummarizeMeeting?.Invoke(meeting)));

        var confirm = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var question = Text("Apagar a gravação e a transcrição?", 12, fg: "TextFillColorSecondaryBrush");
        question.VerticalAlignment = VerticalAlignment.Center;
        question.Margin = new Thickness(8, 0, 8, 0);
        confirm.Children.Add(question);
        confirm.Children.Add(PrimaryButton(null, "Apagar", () =>
        {
            model.DeleteMeeting(meeting);
            Open(null);
        }));
        var cancel = SecondaryButton("Cancelar", () => confirm.Visibility = Visibility.Collapsed);
        cancel.Margin = new Thickness(6, 0, 0, 0);
        confirm.Children.Add(cancel);

        row.Children.Add(IconButton("", "Apagar reunião", meeting != model.RecordingMeeting, () =>
            confirm.Visibility = confirm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        row.Children.Add(confirm);
        return row;
    }

    // MARK: - Summary

    private UIElement SummaryContent(Meeting meeting)
    {
        if (meeting.Status is Meeting.Ready or Meeting.Failed)
        {
            if (string.IsNullOrWhiteSpace(meeting.Summary)) return Text("Sem resumo.", 14, fg: "TextFillColorSecondaryBrush");
            var summary = MeetingSummary.Parse(meeting.Summary!);
            return summary.IsEmpty ? Card(Text(meeting.Summary!, 14, wrap: true)) : SummaryView(meeting, summary);
        }
        var stack = new StackPanel();
        stack.Children.Add(ProgressLine(meeting));
        stack.Children.Add(SummarySkeleton());
        return stack;
    }

    private UIElement SummaryView(Meeting meeting, MeetingSummary summary)
    {
        var stack = new StackPanel();
        if (summary.Overview.Count > 0)
        {
            var text = new StackPanel();
            foreach (var paragraph in summary.Overview)
            {
                var block = Text(Plain(paragraph), 15, wrap: true);
                block.LineHeight = 23;
                block.Margin = new Thickness(0, 0, 0, 8);
                text.Children.Add(block);
            }
            var card = Card(text, new Thickness(18, 18, 18, 10));
            card.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(card);
        }

        var tasks = new StackPanel();
        foreach (var task in summary.Tasks)
        {
            var key = TaskKey(task);
            bool done = meeting.DoneTasks.Contains(key);
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            // The box marks the task done or not, saved with the meeting.
            var box = new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.2),
                                   VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0), Cursor = Cursors.Hand };
            box.BorderBrush = done ? AccentAt(1) : SecondaryAt(0.6);
            box.Background = done ? AccentAt(1) : Brushes.Transparent;
            if (done) box.Child = Glyph("\uE73E", 9, "TextOnAccentFillColorPrimaryBrush");
            box.ToolTip = done ? "Marcar como não feita" : "Marcar como feita";
            AutomationLabel(box, (string)box.ToolTip);
            box.MouseLeftButtonUp += (_, _) =>
            {
                if (!meeting.DoneTasks.Remove(key)) meeting.DoneTasks.Add(key);
                MeetingStore.Save(meeting);
                Render();
            };
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            if (task.Owner is { } owner)
            {
                var chip = OwnerChip(owner);
                chip.VerticalAlignment = VerticalAlignment.Top;
                chip.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(chip, Dock.Left);
                row.Children.Add(chip);
            }
            var taskText = Text(Plain(task.Text), 14, fg: done ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush", wrap: true);
            if (done) taskText.TextDecorations = TextDecorations.Strikethrough;
            row.Children.Add(taskText);
            tasks.Children.Add(row);
        }
        var tasksBlock = Block("Tarefas", "", summary.Tasks.Count == 0, tasks,
                               summary.Tasks.Count == 0 ? null : TaskActions(meeting, summary));
        tasksBlock.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(tasksBlock);

        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        var decisions = Block("Decisões", "", summary.Decisions.Count == 0, Bullets(summary.Decisions));
        var open = Block("Pontos em aberto", "", summary.Open.Count == 0, Bullets(summary.Open));
        Grid.SetColumn(open, 2);
        pair.Children.Add(decisions);
        pair.Children.Add(open);
        stack.Children.Add(pair);
        return stack;
    }

    private UIElement Bullets(IEnumerable<string> items)
    {
        var stack = new StackPanel();
        foreach (var item in items)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            var dash = Text("–", 14, fg: "AccentFillColorDefaultBrush");
            dash.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(dash, Dock.Left);
            row.Children.Add(dash);
            row.Children.Add(Text(Plain(item), 14, wrap: true));
            stack.Children.Add(row);
        }
        return stack;
    }

    private Border Block(string title, string icon, bool empty, UIElement content, UIElement? actions = null)
    {
        var stack = new StackPanel();
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        if (actions != null)
        {
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
        }
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = Glyph(icon, 11, "AccentFillColorDefaultBrush");
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 6, 0);
        label.Children.Add(glyph);
        label.Children.Add(Text(title.ToUpperInvariant(), 10, FontWeights.Medium, "AccentFillColorDefaultBrush", "MonoFont"));
        head.Children.Add(label);
        stack.Children.Add(head);
        stack.Children.Add(empty ? Text("Nenhuma", 14, fg: "TextFillColorSecondaryBrush") : content);
        return Card(stack, new Thickness(16));
    }

    private Border OwnerChip(string owner)
    {
        bool you = owner.StartsWith("você", StringComparison.OrdinalIgnoreCase) || owner.StartsWith("voce", StringComparison.OrdinalIgnoreCase);
        var label = Text(owner, 11, FontWeights.SemiBold, you ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        var chip = new Border { Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(4), Child = label };
        chip.SetResourceReference(Border.BackgroundProperty, you ? "AccentFillColorDefaultBrush" : "SubtleFillColorSecondaryBrush");
        return chip;
    }

    /// How a task is remembered as done: "Dono: tarefa", or the task alone.
    private static string TaskKey(MeetingSummary.Task task) => task.Owner is { } owner ? $"{owner}: {task.Text}" : task.Text;

    /// The tasks as a checklist: "- [ ] Dono: tarefa" / "- [x] …".
    private static string TaskList(Meeting meeting, MeetingSummary summary) => string.Join("\n",
        summary.Tasks.Select(t => $"- [{(meeting.DoneTasks.Contains(TaskKey(t)) ? "x" : " ")}] {Plain(TaskKey(t))}"));

    /// Copy the checklist, or send it in a new e-mail.
    private UIElement TaskActions(Meeting meeting, MeetingSummary summary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(CopyButton("", "Copiar tarefas", () => TaskList(meeting, summary), true));
        var mail = IconButton("", "Enviar tarefas por e-mail", true, () =>
        {
            var subject = Uri.EscapeDataString($"Tarefas — {meeting.Title}");
            var body = Uri.EscapeDataString(TaskList(meeting, summary));
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"mailto:?subject={subject}&body={body}") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Log.Write("mailto: " + e.Message);
            }
        });
        mail.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(mail);
        return row;
    }

    // MARK: - Ask

    /// "Pergunte sobre as suas reuniões" (list) or "… sobre esta reunião" (one meeting), and the answer below.
    private UIElement AskBox(Meeting? only)
    {
        var scope = only?.Id ?? "*";
        if (askScope != scope)
        {
            askScope = scope;
            askQuestion = "";
            askAnswer = null;
            askBusy = false;
        }
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

        var placeholder = only == null ? "Pergunte sobre as suas reuniões" : "Pergunte sobre esta reunião";
        var input = new TextBox
        {
            Text = askQuestion, FontSize = 14, Padding = new Thickness(12, 9, 12, 9), VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = !askBusy,
        };
        input.SetResourceReference(Control.FontFamilyProperty, "BodyFont");
        AutomationLabel(input, placeholder);
        var hint = Text(placeholder + "  ·  Enter envia", 14, fg: "TextFillColorSecondaryBrush");
        hint.Margin = new Thickness(14, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) =>
        {
            askQuestion = input.Text;
            hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || askBusy || input.Text.Trim().Length == 0) return;
            e.Handled = true;
            await AskAsync(input.Text.Trim(), only, scope);
        };
        var field = new Grid();
        field.Children.Add(input);
        field.Children.Add(hint);
        stack.Children.Add(field);

        if (askBusy)
        {
            var waiting = new StackPanel();
            waiting.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
            waiting.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
            waiting.Children.Add(Skeleton(260, 12));
            var card = Card(waiting, new Thickness(16));
            card.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(card);
        }
        else if (askAnswer is { } answer)
        {
            var text = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "BodyFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            AddAnswer(text, answer);
            var card = Card(text, new Thickness(16));
            card.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(card);
            SlideIn(card, 0, 8);
        }
        return stack;
    }

    private async System.Threading.Tasks.Task AskAsync(string question, Meeting? only, string scope)
    {
        askQuestion = question;
        askBusy = true;
        askAnswer = null;
        Render();
        string answer;
        try
        {
            var meetings = model.Meetings.ToList();
            var context = await System.Threading.Tasks.Task.Run(() => MeetingSearch.Context(question, meetings, only));
            answer = context.Length == 0 ? "Ainda não há reuniões prontas para consultar." : await Llm.AskMeetingsAsync(question, context);
        }
        catch (Exception e)
        {
            answer = e is UserFacingException ? e.Message : "A IA não respondeu. Confira a IA em Modelos e tente de novo.";
            Log.Write("ask: " + e.Message);
        }
        if (askScope != scope) return;
        askBusy = false;
        askAnswer = answer.Trim();
        Render();
    }

    /// The answer with each "[título · mm:ss]" as a link that opens that meeting on its transcript.
    private void AddAnswer(TextBlock text, string answer)
    {
        int last = 0;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(answer, @"\[([^\[\]]+?)\s·\s(\d{1,2}(?::\d{2}){1,2})\]"))
        {
            text.Inlines.Add(new System.Windows.Documents.Run(answer[last..match.Index]));
            var title = match.Groups[1].Value.Trim();
            var target = model.Meetings.FirstOrDefault(m => m.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                      ?? model.Meetings.FirstOrDefault(m => m.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                                                         || title.StartsWith(m.Title, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                text.Inlines.Add(new System.Windows.Documents.Run(match.Value));
            }
            else
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(match.Value)) { TextDecorations = null };
                link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentFillColorDefaultBrush");
                link.Cursor = Cursors.Hand;
                link.ToolTip = "Abrir a transcrição";
                link.Click += (_, _) => Open(target, "transcript");
                text.Inlines.Add(link);
            }
            last = match.Index + match.Length;
        }
        text.Inlines.Add(new System.Windows.Documents.Run(answer[last..]));
    }

    /// Bold and italic markers out: the blocks are plain text.
    private static string Plain(string text) => text.Replace("**", "").Replace("__", "");

    // MARK: - Transcript

    private UIElement TranscriptContent(Meeting meeting)
    {
        if (meeting.Segments.Count > 0)
        {
            var stack = new StackPanel();
            if (meeting.Speakers.Count > 0) stack.Children.Add(VoicesPanel(meeting));
            stack.Children.Add(Conversation(meeting));
            return stack;
        }
        if (meeting.Status == Meeting.Transcribing)
        {
            var stack = new StackPanel();
            stack.Children.Add(ProgressLine(meeting));
            stack.Children.Add(TranscriptSkeleton());
            return stack;
        }
        return Text("Nenhuma fala foi captada.", 14, fg: "TextFillColorSecondaryBrush");
    }

    /// What you said on the right, the others on the left; name and time when the speaker changes.
    private UIElement Conversation(Meeting meeting)
    {
        var segments = meeting.Segments;
        var stack = new StackPanel();
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            bool mine = segment.Source != "them";
            bool first = i == 0 || meeting.LabelFor(segments[i - 1]) != meeting.LabelFor(segment);
            var align = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var item = new StackPanel { HorizontalAlignment = align, MaxWidth = 560, Margin = new Thickness(0, 0, 0, 6) };
            if (first)
            {
                var who = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align,
                                           Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 4) };
                var name = Text(meeting.LabelFor(segment), 12, FontWeights.SemiBold, mine ? "AccentFillColorDefaultBrush" : "TextFillColorPrimaryBrush");
                if (segment.Speaker is { } voice) name.Foreground = VoiceInk(voice);
                who.Children.Add(name);
                var time = Text(Meeting.Clock(segment.Start), 10, fg: "TextFillColorSecondaryBrush", font: "MonoFont");
                time.VerticalAlignment = VerticalAlignment.Center;
                time.Margin = new Thickness(6, 0, 0, 0);
                who.Children.Add(time);
                item.Children.Add(who);
            }
            var text = new TextBox
            {
                Text = segment.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), FontSize = 14, ToolTip = Meeting.Clock(segment.Start),
            };
            text.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            text.SetResourceReference(Control.FontFamilyProperty, "BodyFont");
            var bubble = new Border { Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(4), Child = text,
                                      HorizontalAlignment = align, BorderThickness = new Thickness(mine ? 0 : 1) };
            bubble.SetResourceReference(Border.BackgroundProperty, mine ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
            bubble.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            if (segment.Speaker is { } tone) bubble.Background = VoiceTint(tone);
            if (!mine) bubble.ContextMenu = SegmentVoiceMenu(meeting, segment);
            item.Children.Add(bubble);
            stack.Children.Add(item);
        }
        return stack;
    }

    // MARK: - Voices

    /// Five quiet tones that tell the voices apart; never the accent, which is "Você".
    private static readonly Color[] VoiceTones =
    {
        Color.FromRgb(0x4F, 0x6B, 0x8A), Color.FromRgb(0x5E, 0x7A, 0x5A), Color.FromRgb(0x8A, 0x6E, 0x4B),
        Color.FromRgb(0x6E, 0x5A, 0x8A), Color.FromRgb(0x4F, 0x7F, 0x80),
    };

    private static Brush VoiceInk(int voice)
    {
        var brush = new SolidColorBrush(VoiceTones[voice % VoiceTones.Length]);
        brush.Freeze();
        return brush;
    }

    private static Brush VoiceTint(int voice)
    {
        var color = ((SolidColorBrush)VoiceInk(voice)).Color;
        return new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));
    }

    /// One row per voice: play a sample, give it a name, or merge it into another voice.
    private UIElement VoicesPanel(Meeting meeting)
    {
        var stack = new StackPanel();
        var title = Text("VOZES", 10, FontWeights.Medium, "AccentFillColorDefaultBrush", "MonoFont");
        title.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(title);
        var hint = Text("Dê nome a cada voz; o resumo passa a usar os nomes.", 12, fg: "TextFillColorSecondaryBrush", wrap: true);
        hint.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(hint);

        for (int voice = 0; voice < meeting.Speakers.Count; voice++)
        {
            int index = voice;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };

            var dot = new Ellipse { Width = 10, Height = 10, Fill = VoiceInk(index), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            Border? playButton = null;
            playButton = IconButton("\uE768", "Ouvir esta voz", true, () => PlaySample(meeting, index, playButton!));
            playButton.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(playButton, Dock.Left);
            row.Children.Add(playButton);

            var others = Enumerable.Range(0, meeting.Speakers.Count).Where(v => v != index).ToList();
            if (others.Count > 0)
            {
                var merge = SecondaryButton("Juntar com…", () => { });
                merge.Margin = new Thickness(8, 0, 0, 0);
                var menu = new ContextMenu();
                foreach (var other in others)
                {
                    int target = other;
                    var item = new MenuItem { Header = meeting.VoiceName(target) };
                    item.Click += (_, _) =>
                    {
                        Diarizer.Merge(meeting, index, target);
                        VoicesChanged(meeting);
                    };
                    menu.Items.Add(item);
                }
                merge.ContextMenu = menu;
                merge.MouseLeftButtonUp += (_, _) => { menu.PlacementTarget = merge; menu.IsOpen = true; };
                DockPanel.SetDock(merge, Dock.Right);
                row.Children.Add(merge);
            }

            var name = new TextBox
            {
                Text = meeting.Speakers[index], FontSize = 13, Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = $"Nome de {meeting.VoiceName(index)}",
            };
            name.SetResourceReference(Control.FontFamilyProperty, "BodyFont");
            var placeholder = $"Pessoa {index + 1}";
            void Save()
            {
                var value = name.Text.Trim();
                if (index >= meeting.Speakers.Count || meeting.Speakers[index] == value) return;
                meeting.Speakers[index] = value;
                VoicesChanged(meeting);
            }
            name.LostFocus += (_, _) => Save();
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); Keyboard.ClearFocus(); } };
            // A hint for an unnamed voice, drawn under the empty box.
            var field = new Grid();
            var hintText = Text(placeholder, 13, fg: "TextFillColorSecondaryBrush");
            hintText.Margin = new Thickness(10, 0, 0, 0);
            hintText.VerticalAlignment = VerticalAlignment.Center;
            hintText.IsHitTestVisible = false;
            hintText.Visibility = name.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            name.TextChanged += (_, _) => hintText.Visibility = name.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            field.Children.Add(name);
            field.Children.Add(hintText);
            row.Children.Add(field);
            stack.Children.Add(row);
        }

        if (namesChanged.Contains(meeting))
        {
            var redo = PrimaryButton("\uE72C", "Refazer o resumo com os nomes", () =>
            {
                namesChanged.Remove(meeting);
                model.ResummarizeMeeting?.Invoke(meeting);
            });
            redo.HorizontalAlignment = HorizontalAlignment.Left;
            redo.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(redo);
        }

        var card = Card(stack);
        card.Margin = new Thickness(0, 0, 0, 16);
        return card;
    }

    /// Right click on someone else's line: "Esta fala é de…" each voice, or a new one. Changes only that line.
    private ContextMenu SegmentVoiceMenu(Meeting meeting, MeetingSegment segment)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Esta fala é de…", IsEnabled = false });
        menu.Items.Add(new Separator());
        for (int voice = 0; voice < meeting.Speakers.Count; voice++)
        {
            int target = voice;
            var item = new MenuItem { Header = meeting.VoiceName(target), IsCheckable = true, IsChecked = segment.Speaker == target };
            item.Click += (_, _) =>
            {
                segment.Speaker = target;
                Diarizer.DropEmptyVoices(meeting);
                VoicesChanged(meeting);
            };
            menu.Items.Add(item);
        }
        var fresh = new MenuItem { Header = "Nova voz" };
        fresh.Click += (_, _) =>
        {
            meeting.Speakers.Add("");
            segment.Speaker = meeting.Speakers.Count - 1;
            Diarizer.DropEmptyVoices(meeting);
            VoicesChanged(meeting);
        };
        menu.Items.Add(fresh);
        return menu;
    }

    private void VoicesChanged(Meeting meeting)
    {
        namesChanged.Add(meeting);
        MeetingStore.Save(meeting);
        meeting.NotifySegmentsChanged();
        Render();
    }

    /// Plays up to 6 s of the voice's longest stretch, from system.wav; a second click stops it.
    private void PlaySample(Meeting meeting, int voice, Border button)
    {
        if (player != null)
        {
            player.Stop();
            return;
        }
        var longest = meeting.Segments.Where(s => s.Speaker == voice).OrderByDescending(s => s.End - s.Start).FirstOrDefault();
        if (longest == null || !System.IO.File.Exists(meeting.SystemPath)) return;
        try
        {
            var samples = new MeetingTrack(meeting.SystemPath).Read(longest.Start, Math.Min(longest.End, longest.Start + 6));
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            var stream = new RawSourceWaveStream(new System.IO.MemoryStream(bytes), new WaveFormat(16000, 16, 1));
            var output = new WaveOutEvent();
            output.Init(stream);
            var glyph = (button.Child as TextBlock);
            if (glyph != null) glyph.Text = "\uE71A";
            output.PlaybackStopped += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                output.Dispose();
                stream.Dispose();
                player = null;
                if (glyph != null) glyph.Text = "\uE768";
            });
            player = output;
            output.Play();
        }
        catch (Exception e)
        {
            player = null;
            Log.Write($"Voice sample: {e.Message}");
        }
    }

    // MARK: - Loading

    private static string ProgressLabel(Meeting meeting) => meeting.Status switch
    {
        Meeting.Transcribing => $"Transcrevendo o que cada um disse… {(int)Math.Round(meeting.Progress * 100)}%",
        Meeting.Separating => (meeting.SeparatingDetail ?? "Separando as vozes") + "…",
        _ => "Escrevendo o resumo…",
    };

    private UIElement ProgressLine(Meeting meeting)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var spinner = Glyph("", 13, "AccentFillColorDefaultBrush");
        spinner.VerticalAlignment = VerticalAlignment.Center;
        spinner.Margin = new Thickness(0, 0, 8, 0);
        spinner.RenderTransformOrigin = new Point(0.5, 0.5);
        var turn = new RotateTransform();
        spinner.RenderTransform = turn;
        if (Animate) turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        row.Children.Add(spinner);
        progressText = Text(ProgressLabel(meeting), 13, FontWeights.Medium);
        row.Children.Add(progressText);
        stack.Children.Add(row);
        if (meeting.Status == Meeting.Transcribing)
        {
            progressBar = ThinProgress(meeting.Progress);
            progressBar.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(progressBar);
        }
        return stack;
    }

    private UIElement SummarySkeleton()
    {
        var stack = new StackPanel();
        var overview = new StackPanel();
        overview.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
        overview.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
        overview.Children.Add(Skeleton(320, 12));
        var overviewCard = Card(overview, new Thickness(18));
        overviewCard.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(overviewCard);

        var tasks = new StackPanel();
        tasks.Children.Add(Skeleton(90, 10, new Thickness(0, 0, 0, 12)));
        foreach (var width in new double[] { 260, 210, 300 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(Skeleton(13, 13, new Thickness(0, 0, 10, 0)));
            row.Children.Add(Skeleton(60, 16, new Thickness(0, 0, 10, 0)));
            row.Children.Add(Skeleton(width, 12));
            tasks.Children.Add(row);
        }
        var tasksCard = Card(tasks, new Thickness(16));
        tasksCard.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(tasksCard);

        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        for (int column = 0; column < 2; column++)
        {
            var block = new StackPanel();
            block.Children.Add(Skeleton(110, 10, new Thickness(0, 0, 0, 10)));
            block.Children.Add(Skeleton(null, 12, new Thickness(0, 0, 0, 10)));
            block.Children.Add(Skeleton(180, 12));
            var card = Card(block, new Thickness(16));
            Grid.SetColumn(card, column * 2);
            pair.Children.Add(card);
        }
        stack.Children.Add(pair);
        return stack;
    }

    private UIElement TranscriptSkeleton()
    {
        var stack = new StackPanel();
        double[] widths = { 340, 260, 300, 220, 360 };
        for (int i = 0; i < widths.Length; i++)
        {
            var align = i % 2 == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var item = new StackPanel { HorizontalAlignment = align, Margin = new Thickness(0, 0, 0, 14) };
            var name = Skeleton(70, 10, new Thickness(0, 0, 0, 6));
            name.HorizontalAlignment = align;
            item.Children.Add(name);
            item.Children.Add(Skeleton(widths[i], 34));
            stack.Children.Add(item);
        }
        return stack;
    }

    /// A placeholder bar that breathes (Secondary at 0.10 ↔ 0.22) while content is being made.
    private FrameworkElement Skeleton(double? width, double height, Thickness margin = default)
    {
        var bar = new Border { Height = height, CornerRadius = new CornerRadius(4), Margin = margin, HorizontalAlignment = HorizontalAlignment.Left };
        if (width is { } w) bar.Width = w; else bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        var color = ((SolidColorBrush)FindResource("TextFillColorSecondaryBrush")).Color;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(0.22 * 255), color.R, color.G, color.B));
        bar.Background = brush;
        if (Animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
                Color.FromArgb((byte)(0.10 * 255), color.R, color.G, color.B), TimeSpan.FromSeconds(0.9))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        return bar;
    }

    // MARK: - Pieces

    private TextBlock Text(string text, double size = 14, FontWeight? weight = null, string fg = "TextFillColorPrimaryBrush", string font = "BodyFont", bool wrap = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
                                    TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, fg);
        block.SetResourceReference(TextBlock.FontFamilyProperty, font);
        return block;
    }

    private TextBlock Glyph(string glyph, double size, string fg)
    {
        var block = new TextBlock { Text = glyph, FontSize = size, FontFamily = new FontFamily(Icons) };
        block.SetResourceReference(TextBlock.ForegroundProperty, fg);
        return block;
    }

    private UIElement Meta(string icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
        var glyph = Glyph(icon, 12, "TextFillColorSecondaryBrush");
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(glyph);
        row.Children.Add(Text(text, 12, fg: "TextFillColorSecondaryBrush"));
        return row;
    }

    private UIElement Notice(string icon, string text)
    {
        var row = new DockPanel { LastChildFill = true };
        var glyph = Glyph(icon, 13, "AccentFillColorDefaultBrush");
        glyph.Margin = new Thickness(0, 2, 10, 0);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(Text(text, 13, wrap: true));
        var border = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 16), Child = row };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return border;
    }

    private Border Card(UIElement child, Thickness? padding = null)
    {
        var card = new Border { Padding = padding ?? new Thickness(16), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    private ProgressBar ThinProgress(double value)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = value, Height = 3, BorderThickness = new Thickness(0) };
        bar.SetResourceReference(Control.ForegroundProperty, "AccentFillColorDefaultBrush");
        bar.SetResourceReference(Control.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return bar;
    }

    private Brush AccentAt(double opacity)
    {
        var color = ((SolidColorBrush)FindResource("AccentFillColorDefaultBrush")).Color;
        return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
    }

    private Brush SecondaryAt(double opacity)
    {
        var color = ((SolidColorBrush)FindResource("TextFillColorSecondaryBrush")).Color;
        return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
    }

    /// A dot with a ring that grows and fades, like a recording light.
    private FrameworkElement PulsingDot(double size)
    {
        var grid = new Grid { Width = size * 2, Height = size * 2 };
        var ring = new Ellipse { Width = size * 2, Height = size * 2, Fill = AccentAt(0.25), RenderTransformOrigin = new Point(0.5, 0.5) };
        var scale = new ScaleTransform(0.4, 0.4);
        ring.RenderTransform = scale;
        var dot = new Ellipse { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        grid.Children.Add(ring);
        grid.Children.Add(dot);
        if (Animate)
        {
            var grow = new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            ring.Visibility = Visibility.Collapsed;
        }
        return grid;
    }

    private Border PrimaryButton(string? icon, string label, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
        {
            var glyph = Glyph(icon, 12, "TextOnAccentFillColorPrimaryBrush");
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(glyph);
        }
        row.Children.Add(Text(label, 13, FontWeights.SemiBold, "TextOnAccentFillColorPrimaryBrush"));
        var button = new Border { Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(4), Child = row, Cursor = Cursors.Hand };
        button.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        button.MouseEnter += (_, _) => button.Opacity = 0.9;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, _) => action();
        AutomationLabel(button, label);
        return button;
    }

    private Border SecondaryButton(string label, Action action)
    {
        var button = new Border { Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                                  Child = Text(label, 13, FontWeights.Medium), Cursor = Cursors.Hand };
        button.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        button.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        button.MouseLeftButtonUp += (_, _) => action();
        AutomationLabel(button, label);
        return button;
    }

    private Border IconButton(string glyph, string help, bool enabled, Action action)
    {
        var icon = Glyph(glyph, 13, enabled ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border { Width = 32, Height = 30, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                                  Margin = new Thickness(6, 0, 0, 0), Child = icon, ToolTip = help, Opacity = enabled ? 1 : 0.5,
                                  Cursor = enabled ? Cursors.Hand : Cursors.Arrow };
        button.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        button.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        if (enabled)
        {
            button.MouseEnter += (_, _) => button.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            button.MouseLeave += (_, _) => button.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            button.MouseLeftButtonUp += (_, _) => action();
        }
        AutomationLabel(button, help);
        return button;
    }

    /// A copy button that confirms with a check for 1.4 s.
    private Border CopyButton(string glyph, string help, Func<string?> text, bool enabled)
    {
        Border? button = null;
        button = IconButton(glyph, help, enabled, () =>
        {
            var value = text();
            if (string.IsNullOrWhiteSpace(value)) return;
            try { Clipboard.SetText(value); } catch (Exception e) { Log.Write("Copy meeting: " + e.Message); return; }
            if (button?.Child is TextBlock icon)
            {
                icon.Text = "";
                button.ToolTip = "Copiado";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    icon.Text = glyph;
                    button.ToolTip = help;
                };
                timer.Start();
            }
        });
        return button;
    }

    private static void AutomationLabel(UIElement element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static void Move(TranslateTransform transform, double x)
    {
        if (!Animate)
        {
            transform.X = x;
            return;
        }
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(150)));
    }

    /// Fade in while moving from (x, y) to its place, in 300 ms.
    private static void SlideIn(UIElement element, double x, double y)
    {
        if (!Animate) return;
        var move = new TranslateTransform(x, y);
        element.RenderTransform = move;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (x != 0) move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        if (y != 0) move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }
}

/// The summary's four sections, read from the Markdown the model fills in (same rules as MeetingSummary in Swift).
public sealed class MeetingSummary
{
    public sealed record Task(string? Owner, string Text);

    public List<string> Overview { get; } = new();
    public List<string> Decisions { get; } = new();
    public List<Task> Tasks { get; } = new();
    public List<string> Open { get; } = new();

    public bool IsEmpty => Overview.Count == 0 && Decisions.Count == 0 && Tasks.Count == 0 && Open.Count == 0;

    public static MeetingSummary Parse(string markdown)
    {
        var summary = new MeetingSummary();
        string section = "other";
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var title = line.TrimStart('#').ToLowerInvariant();
                section = title.Contains("resumo") ? "overview"
                    : title.Contains("decis") ? "decisions"
                    : title.Contains("tarefa") ? "tasks"
                    : title.Contains("aberto") ? "open" : "other";
                continue;
            }
            bool item = line.StartsWith("- ") || line.StartsWith("* ");
            var text = item ? line[2..].Trim() : line;
            // "Nenhuma." is how the model says the section is empty.
            if (text.StartsWith("nenhum", StringComparison.OrdinalIgnoreCase) && text.Length < 12) continue;
            switch (section)
            {
                case "decisions": summary.Decisions.Add(text); break;
                case "open": summary.Open.Add(text); break;
                case "tasks":
                    int colon = text.IndexOf(':');
                    if (colon > 0 && colon <= 16) summary.Tasks.Add(new Task(text[..colon].Trim(), text[(colon + 1)..].Trim()));
                    else summary.Tasks.Add(new Task(null, text));
                    break;
                default: summary.Overview.Add(text); break;
            }
        }
        return summary;
    }
}
