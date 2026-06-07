namespace NMKMTO.Models
{
  public sealed class NMKMTO_ModelDistributedReoResult
  {
    public int SheetCount { get; set; }
    public double DistributedTopAreaM2 { get; set; }
    public double DistributedBottomAreaM2 { get; set; }
    public string ExportPath { get; set; } = string.Empty;
    public string WarningPath { get; set; } = string.Empty;
    public List<string> Warnings { get; } = new();
    public string Message { get; set; } = string.Empty;
  }
}
