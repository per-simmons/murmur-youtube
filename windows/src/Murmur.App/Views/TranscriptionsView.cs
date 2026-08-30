using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Past transcriptions: searchable, each copyable and deletable.
/// </summary>
/// <remarks>
/// Rows show which dictionary corrections fired. Without that the dictionary is invisible and
/// there is no way to tell a rule that works from one that never matches.
/// </remarks>
public sealed class TranscriptionsView : UserControl
{
    private readonly TranscriptStore _store;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly Silkscreen _count;

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    public TranscriptionsView(TranscriptStore store)
    {
        _store = store;

        _search = Panels.SearchBox("Search transcriptions");
        _search.TextChanged += (_, _) => Refresh();

        _list = new StackPanel { Spacing = Tokens.Space.Snug, Margin = new Thickness(Tokens.Space.Base) };
        _count = new Silkscreen { Foreground = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.5) };

        var clear = Panels.DeckButton("DELETE ALL");
        clear.Click += (_, _) => { _store.Clear(); Refresh(); };

        Content = new DockPanel
        {
            Children =
            {
                Panels.Docked(Panels.SearchRow(_search), Dock.Top),
                Panels.Docked(Panels.Footer(_count, clear), Dock.Bottom),
                new ScrollViewer { Content = _list },
            },
        };

        // Marshalled: Add() is raised from the dictation engine's background thread, and a
        // straight Refresh() there makes Avalonia throw "Call from invalid thread" — which
        // travels back through Completed and kills the dictation before injection runs.
        _store.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var records = _store.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();
        _count.Text = $"{_store.Records.Count} RECORDING{(_store.Records.Count == 1 ? "" : "S")}";

        if (records.Count == 0)
        {
            _list.Children.Add(Panels.EmptyState(
                _store.Records.Count == 0 ? "NO RECORDINGS" : "NO MATCHES",
                _store.Records.Count == 0 ? "Hold the push-to-talk key and speak." : "Try a different search."));
            return;
        }

        foreach (var record in records) _list.Children.Add(BuildRow(record));
    }

    private Border BuildRow(TranscriptRecord record)
    {
        var copy = Panels.DeckButton("COPY");
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(record.Text).ConfigureAwait(true);

            copy.Content = "COPIED";
            await Task.Delay(TimeSpan.FromSeconds(1.4)).ConfigureAwait(true);
            copy.Content = "COPY";
        };

        var delete = Panels.DeckButton("DELETE");
        delete.Click += (_, _) => _store.Remove(record.Id);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Silkscreen
                {
                    Text = record.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                    Foreground = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.55),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = record.ProcessingSeconds.ToString("0.00", CultureInfo.CurrentCulture) + "s",
                    FontFamily = Tokens.Fonts.Mono,
                    FontSize = Tokens.Fonts.Caption,
                    Foreground = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.45),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { copy, delete },
        };

        var body = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Grid { Children = { header, actions } },
                new TextBlock
                {
                    Text = record.Text,
                    FontFamily = Tokens.Fonts.Grotesque,
                    FontSize = Tokens.Fonts.Body,
                    Foreground = Tokens.Brushes.InkOnDeck,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        if (record.Corrections is { Count: > 0 } corrections)
        {
            body.Children.Add(BuildCorrectionBadges(corrections));
        }

        return Panels.DeckCard(body);
    }

    /// <summary>Shows that the dictionary fired, and on what.</summary>
    private static WrapPanel BuildCorrectionBadges(IReadOnlyList<Dictionary.AppliedCorrection> corrections)
    {
        var row = new WrapPanel { ItemSpacing = Tokens.Space.Snug, LineSpacing = Tokens.Space.Tight };

        row.Children.Add(new Silkscreen
        {
            Text = "CORRECTED",
            Foreground = new SolidColorBrush(Tokens.Colors.MeterAmber),
            VerticalAlignment = VerticalAlignment.Center,
        });

        foreach (var correction in corrections)
        {
            var label = correction.Count > 1
                ? $"{correction.From} → {correction.To} ×{correction.Count}"
                : $"{correction.From} → {correction.To}";

            row.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Tokens.Colors.MeterAmber, 0.35),
                BorderThickness = new Thickness(Tokens.Border.Hairline),
                CornerRadius = new CornerRadius(Tokens.Radius.Chip),
                Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Hair),
                Child = new TextBlock
                {
                    Text = label,
                    FontFamily = Tokens.Fonts.Grotesque,
                    FontSize = Tokens.Fonts.Caption,
                    Foreground = Tokens.Brushes.InkOnDeck,
                },
            });
        }

        return row;
    }
}
