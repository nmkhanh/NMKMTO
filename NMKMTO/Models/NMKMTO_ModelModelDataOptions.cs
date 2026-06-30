namespace NMKMTO.Models
{
  public sealed class NMKMTO_ModelModelDataOptions
  {
    public string ExportFolder { get; set; } = string.Empty;
    public string FilledRegionViewName { get; set; } = NMKMTO.Functions.F_MtoNames.Views.FilledRegionAreaTemplate;
    public double TopOffsetMm { get; set; } = 1000;
    public double BottomOffsetMm { get; set; } = 1000;
    public bool Create3d { get; set; } = true;
    public List<string> CheckedExportTypes { get; } = new();
    public int TotalExportTypeCount { get; set; } = 5;
  }
}
