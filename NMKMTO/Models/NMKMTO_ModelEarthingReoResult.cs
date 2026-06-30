namespace NMKMTO.Models
{
  public sealed class NMKMTO_ModelEarthingReoResult
  {
    public int SheetCount { get; set; }
    public double EarthingAreaM2 { get; set; }
    public string EarthingHeader { get; set; } = string.Empty;
    public string ExportPath { get; set; } = string.Empty;
    public string WarningPath { get; set; } = string.Empty;
    public List<NMKMTO_ModelModelDataRow> Rows { get; } = new();
    public List<string> Warnings { get; } = new();
    public string Message { get; set; } = string.Empty;
  }
}
