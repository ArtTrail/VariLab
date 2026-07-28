namespace VariLab.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public DataViewModel       Data        { get; }
    public CompStarsViewModel  CompStars   { get; }
    public PhotometryViewModel Photometry  { get; }
    public ResultsViewModel    Results     { get; }

    public MainWindowViewModel()
    {
        Data       = new DataViewModel();
        CompStars  = new CompStarsViewModel(Data);
        Photometry = new PhotometryViewModel(Data, CompStars);
        Results    = new ResultsViewModel(Data, CompStars, Photometry);

        // No button click needed: once a photometry pass finishes, immediately run the
        // Results tab's period search (and its auto-export) on the new light curve.
        Photometry.Completed += () => Results.RunCommand.Execute(null);

        // A new dataset — or just a new target within the same dataset (e.g. switching between
        // stars in the same cluster field without changing the Target Directory) — invalidates
        // every downstream tab's results. Clear them so stale comps/photometry/period-search
        // output is never left over from whatever was open before.
        void ClearDownstreamTabs()
        {
            CompStars.Clear();
            Photometry.Clear();
            Results.Clear();
        }
        Data.InputDirectoryChanged += ClearDownstreamTabs;
        Data.TargetNameChanged     += ClearDownstreamTabs;
    }
}
