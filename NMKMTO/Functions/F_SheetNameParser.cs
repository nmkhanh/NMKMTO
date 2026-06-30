using NMKMTO.Models;
using System.Text.RegularExpressions;

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

      string scopePart = parts.Length > 0 ? parts[0] : sheetName;
      var levelMatch = Regex.Match(scopePart, @"\bLEVEL\s+[A-Z0-9.]+\b", RegexOptions.IgnoreCase);
      if (levelMatch.Success)
        info.LevelName = levelMatch.Value.Trim();
      else if (parts.Length > 0)
        info.LevelName = parts[0];

      var zoneMatch = Regex.Match(scopePart, @"\bZONE\s+[^-]+", RegexOptions.IgnoreCase);
      if (zoneMatch.Success)
        info.ZoneName = zoneMatch.Value.Trim();
      else if (parts.Length > 1 && parts[1].StartsWith(F_MtoNames.Keywords.Zone, StringComparison.OrdinalIgnoreCase))
        info.ZoneName = parts[1];

      var locationPart = parts.Length > 0 ? parts[parts.Length - 1] : string.Empty;
      info.Location = ParseLocation(locationPart);

      return info;
    }

    private static MtoSheetReinforcementLocation ParseLocation(string value)
    {
      string text = value.ToUpperInvariant();
      if (text.Contains(F_MtoNames.Keywords.Top))
        return MtoSheetReinforcementLocation.TopRein;
      if (text.Contains(F_MtoNames.Keywords.Bottom))
        return MtoSheetReinforcementLocation.BottomRein;
      if (text.Contains(F_MtoNames.Keywords.Shear))
        return MtoSheetReinforcementLocation.ShearRein;

      return MtoSheetReinforcementLocation.Unknown;
    }
  }
}
