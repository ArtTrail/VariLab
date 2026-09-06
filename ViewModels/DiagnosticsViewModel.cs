using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VariLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace VariLab.ViewModels;

/// <summary>
/// Diagnostics window — live view of the current session log (<see cref="SessionLogService"/>),
/// with the ability to pause/resume the feed, save it out, or load a previous session's log.
/// Ported from TransitLab's own DiagnosticsViewModel so error logging/troubleshooting works
/// the same way across both apps.
/// </summary>
public partial class DiagnosticsViewModel : ViewModelBase
{
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool _isLogFrozen;

    private readonly Queue<string> _pending = new();

    public Func<Task<string?>>? SaveFileFunc    { get; set; }
    public Func<Task<string?>>? OpenLogFileFunc { get; set; }
    public Action?              CloseCallback   { get; set; }

    [ObservableProperty] private string _logTitle = "Current Session";
    [ObservableProperty] private string _status = "";
    private bool _viewingPrevious;

    // ── Live feed ─────────────────────────────────────────────────────────────

    public void Connect()
    {
        // Seed with everything already in the log this session
        var existing = SessionLogService.ReadAll();
        foreach (var line in existing.Split('\n'))
            LogLines.Add(line.TrimEnd('\r'));

        SessionLogService.LineWritten += OnLineWritten;
    }

    public void Disconnect() => SessionLogService.LineWritten -= OnLineWritten;

    private void OnLineWritten(string line)
    {
        if (_viewingPrevious) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsLogFrozen)
                _pending.Enqueue(line);
            else
                LogLines.Add(line);
        });
    }

    partial void OnIsLogFrozenChanged(bool value)
    {
        if (value) return;
        // Flush any lines that arrived while paused
        while (_pending.TryDequeue(out var line))
            LogLines.Add(line);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveLog()
    {
        if (SaveFileFunc is null) return;
        var path = await SaveFileFunc();
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, LogLines));
            Status = $"✓  Saved to {path}";
        }
        catch (Exception ex)
        {
            Status = $"✗  Could not save log: {ex.Message}";
            SessionLogService.Write($"[Diagnostics] SaveLog to '{path}' failed: {ex}");
        }
    }

    [RelayCommand]
    private async Task LoadPreviousLog()
    {
        if (OpenLogFileFunc is null) return;
        var path = await OpenLogFileFunc();
        if (path is null) return;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            _viewingPrevious = true;
            _pending.Clear();
            LogLines.Clear();
            LogTitle = Path.GetFileName(path);
            foreach (var line in text.Split('\n'))
                LogLines.Add(line.TrimEnd('\r'));
            Status = $"✓  Loaded {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Status = $"✗  Could not load '{Path.GetFileName(path)}': {ex.Message}";
            SessionLogService.Write($"[Diagnostics] LoadPreviousLog from '{path}' failed: {ex}");
        }
    }

    [RelayCommand]
    private void Close() => CloseCallback?.Invoke();
}
