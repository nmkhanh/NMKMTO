using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_SheetNameParser
  {
    public static NMKMTO_ModelSheetInfo Parse(string sheetName)
    {
      var info = new NMKMTO_ModelSheetInfo { SheetName = sheetName };
      var parts = sheetName
        .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .ToArray();

      if (parts.Length > 0)
        info.LevelName = parts[0];

      if (parts.Length > 1 && parts[1].StartsWith("ZONE", StringComparison.OrdinalIgnoreCase))
        info.ZoneName = parts[1];

      var locationPart = parts.Length > 0 ? parts[parts.Length - 1] : string.Empty;
      info.Location = ParseLocation(locationPart);

      return info;
    }

    private static MtoSheetReinforcementLocation ParseLocation(string value)
    {
      return value.ToUpperInvariant() switch
      {
        "TOP REIN" => MtoSheetReinforcementLocation.TopRein,
        "BOTTOM REIN" => MtoSheetReinforcementLocation.BottomRein,
        "SHEAR REIN" => MtoSheetReinforcementLocation.ShearRein,
        _ => MtoSheetReinforcementLocation.Unknown
      };
    }
  }
}
