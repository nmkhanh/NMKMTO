namespace NMKMTO.Models
{
  public sealed class NMKMTO_ModelModelDataResult
  {
    public int SheetCount { get; set; }
    public int OverViewCount { get; set; }
    public double FloorSurfaceArea { get; set; }
    public double FloorVolume { get; set; }
    public string ExportPath { get; set; } = string.Empty;
    public string WarningPath { get; set; } = string.Empty;
    public List<NMKMTO_ModelModelDataRow> Rows { get; } = new();
    public List<string> Warnings { get; } = new();
    public string Message { get; set; } = string.Empty;
  }
}
