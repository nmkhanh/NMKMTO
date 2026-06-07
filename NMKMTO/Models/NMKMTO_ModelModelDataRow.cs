namespace NMKMTO.Models
{
  public sealed class NMKMTO_ModelModelDataRow
  {
    public int No { get; set; }
    public string Pour { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public double DistributedTopAreaM2 { get; set; }
    public double DistributedBottomAreaM2 { get; set; }
    public double N16_1000AreaM2 { get; set; }
    public double FloorAreaM2 { get; set; }
    public double FloorVolumeM3 { get; set; }
  }
}
