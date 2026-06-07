namespace NMKMTO.Models
{
  public sealed class NMKMTO_ModelActionResult
  {
    public int TotalCount { get; set; }
    public string WarningPath { get; set; } = string.Empty;
    public List<string> Warnings { get; } = new();
    public string Message { get; set; } = string.Empty;
  }
}
