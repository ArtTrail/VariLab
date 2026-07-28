namespace VariLab.Services;

/// <summary>
/// Pre-flight check: scans every FITS file in a directory for a valid WCS solution before
/// the pipeline runs. Catches datasets that were never plate-solved (or barely were — e.g. a
/// batch where only 1 of 89 frames actually solved) before burning time on Gaia/APASS queries
/// and photometry that can only reject almost everything.
/// </summary>
public static class PlateSolveCheckService
{
    public record Result(int Total, int WithWcs);

    public static Result CheckDirectory(string directory)
    {
        var files = FitsHeaderService.FindAllFits(directory);
        int withWcs = 0;
        foreach (var f in files)
        {
            try
            {
                var hdr = FitsHeaderService.Read(f);
                if (WcsService.ReadWcs(hdr) is not null) withWcs++;
            }
            catch
            {
                // Unreadable header counts as "not plate-solved" for this check.
            }
        }
        return new Result(files.Length, withWcs);
    }
}
