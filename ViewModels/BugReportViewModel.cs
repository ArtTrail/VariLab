using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VariLab.Services;

namespace VariLab.ViewModels;

public partial class BugReportViewModel : ViewModelBase
{
    [ObservableProperty] private string _reportType   = "Bug Report";
    [ObservableProperty] private string _summary      = "";
    [ObservableProperty] private string _description  = "";
    [ObservableProperty] private string _email        = "";
    [ObservableProperty] private string _statusText   = "";
    [ObservableProperty] private bool   _isSubmitting = false;
    [ObservableProperty] private bool   _isSubmitted  = false;

    public string   Version     => AppVersion.Version;
    public string   OsName      => DetectOs();
    public string[] ReportTypes { get; } = ["Bug Report", "Feature Request"];

    public Action? CloseCallback { get; set; }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        IsSubmitting = true;
        StatusText   = "Submitting…";
        try
        {
            await BugReportService.SubmitAsync(
                ReportType, Summary, Description, Email, Version, OsName);
            IsSubmitted = true;
            StatusText  = "Thank you — your report has been submitted.";
        }
        catch (Exception ex)
        {
            StatusText = $"Submission failed: {ex.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool CanSubmit() =>
        !string.IsNullOrWhiteSpace(Description) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !IsSubmitting && !IsSubmitted;

    partial void OnDescriptionChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnEmailChanged(string value)        => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnIsSubmittingChanged(bool value)   => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnIsSubmittedChanged(bool value)    => SubmitCommand.NotifyCanExecuteChanged();

    private static string DetectOs()
    {
        if (OperatingSystem.IsWindows())
        {
            var build = Environment.OSVersion.Version.Build;
            var major = build >= 22000 ? 11 : 10;
            return $"Windows {major}";
        }
        if (OperatingSystem.IsMacOS())
            return "macOS";
        if (OperatingSystem.IsLinux())
            return "Linux";
        return RuntimeInformation.OSDescription;
    }
}
