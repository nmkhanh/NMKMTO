using System.IO;

namespace NMKMTO.Functions
{
  public static class F_WarningExporter
  {
    public static string ExportIfAny(string exportFolder, string filePrefix, List<string> warnings)
    {
      if (warnings.Count == 0 || string.IsNullOrWhiteSpace(exportFolder))
        return string.Empty;

      Directory.CreateDirectory(exportFolder);
      string path = Path.Combine(exportFolder, $"{filePrefix}_WARNING_{DateTime.Now:yyMMdd_HHmmss}.csv");
      var lines = new List<string> { "No,Warning" };
      for (int i = 0; i < warnings.Count; i++)
        lines.Add($"{i + 1},{EscapeCsv(warnings[i])}");

      File.WriteAllLines(path, lines);
      return path;
    }

    private static string EscapeCsv(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        return value;

      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
