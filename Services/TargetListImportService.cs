using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VariLab.Services;

/// <summary>
/// Reads a target-name column out of a CSV or XLSX file for the Batch Process window — an
/// alternative to typing/pasting names directly. Deliberately minimal: this is reading a single
/// column of star designations (no commas, no embedded newlines), not general-purpose tabular
/// data, so plain line/comma splitting for CSV is enough and doesn't need a dedicated CSV
/// library. XLSX reuses ClosedXML, already a project dependency via ExcelExportService.
/// </summary>
public static class TargetListImportService
{
    /// <summary>Reads the header row (first row) of a CSV or XLSX file as column names.</summary>
    public static string[] ReadHeaders(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ReadXlsxHeaders(path),
            ".csv"  => ReadCsvHeaders(path),
            _ => throw new NotSupportedException($"Unsupported file type: {ext}"),
        };
    }

    /// <summary>Reads every non-blank value under the named column, skipping the header row.</summary>
    public static List<string> ReadColumn(string path, string columnName)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ReadXlsxColumn(path, columnName),
            ".csv"  => ReadCsvColumn(path, columnName),
            _ => throw new NotSupportedException($"Unsupported file type: {ext}"),
        };
    }

    private static string[] ReadCsvHeaders(string path)
    {
        using var reader = new StreamReader(path);
        var line = reader.ReadLine();
        return line is null ? [] : SplitCsvLine(line);
    }

    private static List<string> ReadCsvColumn(string path, string columnName)
    {
        var result = new List<string>();
        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return result;

        var headers = SplitCsvLine(headerLine);
        var colIndex = Array.FindIndex(headers, h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (colIndex < 0) return result;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var fields = SplitCsvLine(line);
            if (colIndex < fields.Length && !string.IsNullOrWhiteSpace(fields[colIndex]))
                result.Add(fields[colIndex].Trim());
        }
        return result;
    }

    private static string[] SplitCsvLine(string line) =>
        line.Split(',').Select(f => f.Trim().Trim('"')).ToArray();

    private static string[] ReadXlsxHeaders(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var headerRow = ws.FirstRowUsed();
        if (headerRow is null) return [];
        return headerRow.CellsUsed().Select(c => c.GetString().Trim()).ToArray();
    }

    private static List<string> ReadXlsxColumn(string path, string columnName)
    {
        var result = new List<string>();
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var headerRow = ws.FirstRowUsed();
        if (headerRow is null) return result;

        var headerCell = headerRow.CellsUsed()
            .FirstOrDefault(c => c.GetString().Trim().Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (headerCell is null) return result;

        int col = headerCell.Address.ColumnNumber;
        var lastRow = ws.LastRowUsed();
        if (lastRow is null) return result;

        for (int row = headerRow.RowNumber() + 1; row <= lastRow.RowNumber(); row++)
        {
            var value = ws.Cell(row, col).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }
        return result;
    }
}
