using System.IO;
using System.Text;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_NumberShearExporter
  {
    public static NMKMTO_ModelActionResult Export(IEnumerable<NMKMTO_ModelSheetRow> sheets, string exportFolder)
    {
      if (string.IsNullOrWhiteSpace(exportFolder))
        throw new InvalidOperationException("Export folder is empty.");

      Directory.CreateDirectory(exportFolder);
      var shearSheets = sheets
        .Where(sheet => sheet.SheetName.IndexOf("SHEAR", StringComparison.OrdinalIgnoreCase) >= 0)
        .OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
        .ToList();

      string path = Path.Combine(exportFolder, $"NMKMTO_NUMBER_SHEAR_{DateTime.Now:yyMMdd_HHmmss}.csv");
      var builder = new StringBuilder();
      builder.AppendLine("No,Sheet Number,Sheet Name");
      for (int i = 0; i < shearSheets.Count; i++)
        builder.AppendLine($"{i + 1},{EscapeCsv(shearSheets[i].SheetNumber)},{EscapeCsv(shearSheets[i].SheetName)}");

      File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
      return new NMKMTO_ModelActionResult
      {
        TotalCount = shearSheets.Count,
        Message = $"Number Shear exported\nSheets: {shearSheets.Count}\nFile: {path}"
      };
    }

    private static string EscapeCsv(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        return value;

      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
