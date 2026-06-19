using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_CombinedMtoExporter
  {
    private sealed class AreaRow
    {
      public string Sequence { get; set; } = string.Empty;
      public double DistributedTop { get; set; }
      public double DistributedBottom { get; set; }
      public double Earthing { get; set; }
      public double FloorArea { get; set; }
      public double FloorVolume { get; set; }
    }

    public static NMKMTO_ModelActionResult Execute(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      NMKMTO_ModelModelDataOptions options,
      bool exportReo,
      bool exportDistributedReo,
      bool exportEarthingReo,
      bool exportModel)
    {
      var sheets = selectedSheets?.ToList() ?? new List<NMKMTO_ModelSheetRow>();
      if (sheets.Count == 0)
        throw new InvalidOperationException("Please select at least one sheet.");

      Directory.CreateDirectory(options.ExportFolder);
      var areaRows = new Dictionary<string, AreaRow>(StringComparer.OrdinalIgnoreCase);
      var areaSequenceOrder = new List<string>();
      var warningPaths = new List<string>();
      var allWarnings = new List<string>();
      string reoCsvContent = string.Empty;
      int reoRowCount = 0;

      if (exportDistributedReo)
      {
        NMKMTO_ModelDistributedReoResult result = F_DistributedReoExtractor.Extract(
          doc,
          sheets,
          options,
          writeDataCsv: false);
        foreach (NMKMTO_ModelModelDataRow source in result.Rows)
        {
          string sequence = GetSequence(source);
          AreaRow target = GetOrCreateAreaRow(sequence, areaRows, areaSequenceOrder);
          target.DistributedTop = source.DistributedTopAreaM2;
          target.DistributedBottom = source.DistributedBottomAreaM2;
        }
        if (!string.IsNullOrWhiteSpace(result.WarningPath))
          warningPaths.Add(result.WarningPath);
        allWarnings.AddRange(result.Warnings);
      }

      if (exportEarthingReo)
      {
        NMKMTO_ModelEarthingReoResult result = F_EarthingReoExtractor.Extract(
          doc,
          sheets,
          options,
          writeDataCsv: false);
        foreach (NMKMTO_ModelModelDataRow source in result.Rows)
        {
          string sequence = GetSequence(source);
          AreaRow target = GetOrCreateAreaRow(sequence, areaRows, areaSequenceOrder);
          target.Earthing = source.N16_1000AreaM2;
        }
        if (!string.IsNullOrWhiteSpace(result.WarningPath))
          warningPaths.Add(result.WarningPath);
        allWarnings.AddRange(result.Warnings);
      }

      if (exportModel)
      {
        NMKMTO_ModelModelDataResult result = F_ModelDataExtractor.Extract(
          doc,
          sheets,
          options,
          writeDataCsv: false);
        foreach (NMKMTO_ModelModelDataRow source in result.Rows)
        {
          string sequence = GetSequence(source);
          AreaRow target = GetOrCreateAreaRow(sequence, areaRows, areaSequenceOrder);
          target.FloorArea = source.FloorAreaM2;
          target.FloorVolume = source.FloorVolumeM3;
        }
        if (!string.IsNullOrWhiteSpace(result.WarningPath))
          warningPaths.Add(result.WarningPath);
        allWarnings.AddRange(result.Warnings);
      }

      if (exportReo)
      {
        NMKMTO_ModelActionResult result = F_ReoExtractor.Execute(
          doc,
          sheets,
          options,
          writeDataCsv: false);
        reoCsvContent = result.DataCsvContent;
        reoRowCount = result.TotalCount;
        if (!string.IsNullOrWhiteSpace(result.WarningPath))
          warningPaths.Add(result.WarningPath);
        allWarnings.AddRange(result.Warnings);
      }

      string dateSuffix = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
      string buildingName = doc.ProjectInformation?.LookupParameter("Building Name")?.AsString()?.Trim()
        ?? doc.ProjectInformation?.Name
        ?? doc.Title;
      List<string> levels = sheets
        .Select(sheet => sheet.LevelName)
        .Where(level => !string.IsNullOrWhiteSpace(level))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      string levelName = levels.Count == 1 ? levels[0] : "MULTI LEVEL";
      string combinedName = $"MTO_{buildingName}_{levelName}_ALL_{dateSuffix}";
      foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        combinedName = combinedName.Replace(invalidCharacter, '_');
      string combinedPath = Path.Combine(options.ExportFolder, $"{combinedName}.csv");

      var builder = new StringBuilder();
      bool hasAreaOptions = exportDistributedReo || exportEarthingReo || exportModel;
      if (hasAreaOptions)
      {
        builder.AppendLine("AREA");
        builder.AppendLine("No,Sequence,Distributed Top Area (m2),Distributed Bottom Area (m2),N16-1000 Area (m2),Floor Area (m2),Floor Volume (m3)");
        int no = 1;
        foreach (string sequence in areaSequenceOrder)
        {
          AreaRow row = areaRows[sequence];
          builder.AppendLine(string.Join(",", new[]
          {
            no++.ToString(CultureInfo.InvariantCulture),
            EscapeCsv(row.Sequence),
            FormatNumber(row.DistributedTop),
            FormatNumber(row.DistributedBottom),
            FormatNumber(row.Earthing),
            FormatNumber(row.FloorArea),
            FormatNumber(row.FloorVolume)
          }));
        }
      }

      if (exportReo)
      {
        if (builder.Length > 0)
          builder.AppendLine();
        builder.AppendLine("REO");
        builder.Append(reoCsvContent.TrimStart('\uFEFF'));
        if (builder.Length > 0 && builder[builder.Length - 1] != '\n')
          builder.AppendLine();
      }

      File.WriteAllText(combinedPath, builder.ToString(), Encoding.UTF8);

      var finalResult = new NMKMTO_ModelActionResult
      {
        TotalCount = areaRows.Count + reoRowCount,
        ExportPath = combinedPath,
        Message = $"MTO exported: {combinedPath}"
          + (warningPaths.Count > 0
            ? $"\nWarning files:\n{string.Join(Environment.NewLine, warningPaths.Distinct(StringComparer.OrdinalIgnoreCase))}"
            : string.Empty)
      };
      foreach (string warning in allWarnings)
        finalResult.Warnings.Add(warning);
      return finalResult;
    }

    private static AreaRow GetOrCreateAreaRow(
      string sequence,
      Dictionary<string, AreaRow> rows,
      List<string> order)
    {
      if (rows.TryGetValue(sequence, out AreaRow row))
        return row;

      row = new AreaRow { Sequence = sequence };
      rows.Add(sequence, row);
      order.Add(sequence);
      return row;
    }

    private static string GetSequence(NMKMTO_ModelModelDataRow row)
    {
      if (!string.IsNullOrWhiteSpace(row.Pour))
        return $"{row.Zone} - {row.Pour}";
      if (!string.IsNullOrWhiteSpace(row.Zone))
        return row.Zone;
      return "0";
    }

    private static string FormatNumber(double value)
    {
      return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string value)
    {
      value ??= string.Empty;
      return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
    }
  }
}
