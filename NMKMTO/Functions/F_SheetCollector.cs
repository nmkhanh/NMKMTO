using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_SheetCollector
  {
    private const string SheetSeriesParameterName = "RINCO_TB_SHEET SERIES";
    private const string ReinforcementSheetSeries = "S50000 SERIES - PT&REO";

    public static List<NMKMTO_ModelSheetRow> CollectSheets(Document doc, string filledRegionViewName)
    {
      var pourNameByZone = GetPourNameByZone(doc, filledRegionViewName);
      return new FilteredElementCollector(doc)
        .OfClass(typeof(ViewSheet))
        .Cast<ViewSheet>()
        .Where(x => !x.IsPlaceholder && IsReinforcementSheetSeries(x) && IsReinforcementSheetName(x))
        .OrderBy(x => x.SheetNumber)
        .ThenBy(x => x.Name)
        .Select(sheet => CreateRow(sheet, pourNameByZone))
        .ToList();
    }

    private static NMKMTO_ModelSheetRow CreateRow(ViewSheet sheet, Dictionary<string, List<string>> pourNameByZone)
    {
      var sheetInfo = F_SheetNameParser.Parse(sheet.Name);
      string pourName = string.Empty;
      if (!string.IsNullOrWhiteSpace(sheetInfo.ZoneName) && pourNameByZone.TryGetValue(Normalize(sheetInfo.ZoneName), out var pours))
        pourName = string.Join(", ", pours);

      return new NMKMTO_ModelSheetRow
      {
        SheetId = sheet.Id,
        SheetNumber = sheet.SheetNumber,
        SheetName = sheet.Name,
        LevelName = sheetInfo.LevelName,
        ZoneName = sheetInfo.ZoneName,
        PourName = pourName ?? string.Empty,
        Location = sheetInfo.Location
      };
    }

    private static Dictionary<string, List<string>> GetPourNameByZone(Document doc, string filledRegionViewName)
    {
      var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
      var view = new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(filledRegionViewName, StringComparison.OrdinalIgnoreCase));

      if (view == null)
        return result;

      var regions = new FilteredElementCollector(doc, view.Id)
        .OfClass(typeof(FilledRegion))
        .Cast<FilledRegion>();

      foreach (var region in regions)
      {
        string comments = GetStringParameter(region, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments");
        if (!comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase))
          continue;

        string zone = GetStringParameter(region, "RINCO_ZONE");
        if (string.IsNullOrWhiteSpace(zone))
          continue;

        string key = Normalize(zone);
        if (!result.ContainsKey(key))
          result.Add(key, new List<string>());

        if (!result[key].Any(x => string.Equals(x, comments, StringComparison.OrdinalIgnoreCase)))
          result[key].Add(comments);
      }

      foreach (var item in result.Values)
        item.Sort(StringComparer.OrdinalIgnoreCase);

      return result;
    }

    private static string GetStringParameter(Element element, BuiltInParameter builtInParameter, string fallbackName)
    {
      string value = element.get_Parameter(builtInParameter)?.AsString() ?? string.Empty;
      return string.IsNullOrWhiteSpace(value) ? GetStringParameter(element, fallbackName) : value.Trim();
    }

    private static string GetStringParameter(Element element, string parameterName)
    {
      return element.LookupParameter(parameterName)?.AsString()?.Trim() ?? string.Empty;
    }

    private static bool IsReinforcementSheetSeries(ViewSheet sheet)
    {
      string sheetSeries = GetStringParameter(sheet, SheetSeriesParameterName);
      return string.Equals(sheetSeries, ReinforcementSheetSeries, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReinforcementSheetName(ViewSheet sheet)
    {
      return sheet.Name.IndexOf("REIN", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Normalize(string value)
    {
      return (value ?? string.Empty).Trim();
    }
  }
}
