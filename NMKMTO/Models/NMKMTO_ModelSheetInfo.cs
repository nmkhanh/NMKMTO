namespace NMKMTO.Models
{
  public enum MtoSheetReinforcementLocation
  {
    Unknown,
    TopRein,
    BottomRein,
    ShearRein
  }

  public sealed class NMKMTO_ModelSheetInfo
  {
    public string SheetName { get; set; } = string.Empty;
    public string LevelName { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;
    public MtoSheetReinforcementLocation Location { get; set; } = MtoSheetReinforcementLocation.Unknown;
  }
}
