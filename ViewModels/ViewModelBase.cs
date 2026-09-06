using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VariLab.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private DispatcherTimer? _elapsedTimer;
    private DateTime _elapsedStartedAt;
    private string _elapsedText = "";

    /// <summary>Live "count up" elapsed-time text (e.g. "00:03:27") while a long-running
    /// operation is in progress (issue #7) — shared here by CompStarsViewModel,
    /// PhotometryViewModel, and BatchViewModel rather than tripling the same
    /// DispatcherTimer logic in each. Empty when nothing has run yet.</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    /// <summary>Starts the elapsed-time counter, ticking once a second. Call right before
    /// starting the actual work (i.e. alongside setting IsRunning = true).</summary>
    protected void StartElapsedTimer()
    {
        _elapsedStartedAt = DateTime.UtcNow;
        ElapsedText = "00:00:00";
        if (_elapsedTimer is null)
        {
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += OnElapsedTimerTick;
        }
        _elapsedTimer.Start();
    }

    /// <summary>Stops the counter but leaves the last value showing (e.g. "finished in
    /// 00:04:12") — call from the same place IsRunning gets set back to false.</summary>
    protected void StopElapsedTimer() => _elapsedTimer?.Stop();

    private void OnElapsedTimerTick(object? sender, EventArgs e) =>
        ElapsedText = (DateTime.UtcNow - _elapsedStartedAt).ToString(@"hh\:mm\:ss");
}
