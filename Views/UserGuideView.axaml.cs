using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace VariLab.Views;

public partial class UserGuideView : UserControl
{
    private readonly List<TextBlock> _matches = new();
    private readonly Dictionary<TextBlock, string> _originalText = new();
    private int _matchIndex = -1;
    private string _lastQuery = "";
    private TextBlock? _highlightedBlock;

    public UserGuideView()
    {
        InitializeComponent();
    }

    /// <summary>Scrolls the Table of Contents entry's target section into view. Each
    /// HyperlinkButton's Tag names the section's x:Name (e.g. "3" -> Section3).</summary>
    private void OnTocClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;

        var name = tag == "Appendix" ? "SectionAppendix" : $"Section{tag}";
        if (this.FindControl<TextBlock>(name) is { } target)
            target.BringIntoView();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunFind();
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e) => RunFind();

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _matches.Clear();
        _matchIndex = -1;
        _lastQuery = "";
        SearchStatus.Text = "";
        ClearHighlight();
    }

    private void RunFind()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            SearchStatus.Text = "";
            return;
        }

        if (!query.Equals(_lastQuery, StringComparison.OrdinalIgnoreCase))
        {
            ClearHighlight();
            _matches.Clear();
            _matchIndex = -1;
            _lastQuery = query;
            CollectMatches(ContentPanel, query, _matches, _originalText);
        }

        if (_matches.Count == 0)
        {
            SearchStatus.Text = "No matches";
            ClearHighlight();
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        var match = _matches[_matchIndex];
        match.BringIntoView();
        ApplyHighlight(match, query);
        SearchStatus.Text = $"{_matchIndex + 1} / {_matches.Count}";
    }

    /// <summary>Highlights just the matched substring (the first occurrence within this
    /// block, case-insensitive) rather than the whole paragraph — swaps the block's plain
    /// Text for three Inlines/Run segments (before/match/after), since TextBlock.Text alone
    /// has no way to color part of itself. Restoring Text (see ClearHighlight) collapses
    /// Inlines back to a single plain Run.</summary>
    private void ApplyHighlight(TextBlock block, string query)
    {
        ClearHighlight();

        var original = _originalText.TryGetValue(block, out var saved) ? saved : block.Text ?? "";
        var index = original.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            _highlightedBlock = null;
            return;
        }

        var before = original[..index];
        var matched = original.Substring(index, query.Length);
        var after = original[(index + query.Length)..];

        var highlightBg = this.TryFindResource("BrushWarn", out var warn) ? warn as IBrush : Brushes.Yellow;
        var highlightFg = this.TryFindResource("BrushBg", out var bg) ? bg as IBrush : Brushes.Black;

        block.Inlines ??= new InlineCollection();
        block.Inlines.Clear();
        if (before.Length > 0) block.Inlines.Add(new Run(before));
        block.Inlines.Add(new Run(matched) { Background = highlightBg, Foreground = highlightFg });
        if (after.Length > 0) block.Inlines.Add(new Run(after));

        _highlightedBlock = block;
    }

    private void ClearHighlight()
    {
        if (_highlightedBlock is null) return;
        // Inlines takes rendering precedence over Text when non-empty, so it must be
        // explicitly cleared — just reassigning Text isn't guaranteed to revert it.
        _highlightedBlock.Inlines?.Clear();
        var original = _originalText.TryGetValue(_highlightedBlock, out var saved) ? saved : null;
        if (original is not null)
            _highlightedBlock.Text = original;
        _highlightedBlock = null;
    }

    private static void CollectMatches(
        ILogical parent, string query, List<TextBlock> results, Dictionary<TextBlock, string> originalText)
    {
        foreach (var child in parent.LogicalChildren)
        {
            if (child is TextBlock tb &&
                tb.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                results.Add(tb);
                originalText[tb] = tb.Text;
            }
            CollectMatches(child, query, results, originalText);
        }
    }

    private void OnBackToTopClick(object? sender, RoutedEventArgs e) =>
        MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, 0);
}
