using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_SheetCollector
  {
    private sealed class MtoRegionInfo
    {
      public string ZoneName { get; set; } = string.Empty;
      public List<string> PourNames { get; } = new List<string>();
    }

    public static List<NMKMTO_ModelSheetRow> CollectSheets(Document doc, string filledRegionViewName)
    {
      var mtoRegionByZone = GetMtoRegionByZone(doc, filledRegionViewName);
      return new FilteredElementCollector(doc)
        .OfClass(typeof(ViewSheet))
        .Cast<ViewSheet>()
        .Where(x => !x.IsPlaceholder /*&& IsReinforcementSheetSeries(x)*/ && IsReinforcementSheetName(x))
        .OrderBy(x => x.SheetNumber)
        .ThenBy(x => x.Name)
        .Select(sheet => CreateRow(doc, sheet, mtoRegionByZone))
        .ToList();
    }

    private static NMKMTO_ModelSheetRow CreateRow(Document doc, ViewSheet sheet, Dictionary<string, MtoRegionInfo> mtoRegionByZone)
    {
      var sheetInfo = F_SheetNameParser.Parse(sheet.Name);
      string levelName = GetOverViewLevelName(doc, sheet);

      string zoneName = string.Empty;
      string pourName = string.Empty;
      if (!string.IsNullOrWhiteSpace(sheetInfo.ZoneName) && mtoRegionByZone.TryGetValue(NormalizeZoneKey(sheetInfo.ZoneName), out var mtoRegion))
      {
        zoneName = mtoRegion.ZoneName;
        pourName = string.Join(", ", mtoRegion.PourNames);
      }

      return new NMKMTO_ModelSheetRow
      {
        SheetId = sheet.Id,
        SheetNumber = sheet.SheetNumber,
        SheetName = sheet.Name,
        LevelName = levelName,
        ZoneName = zoneName,
        PourName = pourName ?? string.Empty,
        Location = sheetInfo.Location
      };
    }

    private static string GetOverViewLevelName(Document doc, ViewSheet sheet)
    {
      var overView = sheet.GetAllPlacedViews()
        .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
        .Where(view => view != null && view.Name.IndexOf(F_MtoViewNames.OverViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
        .Cast<Autodesk.Revit.DB.View>()
        .OrderBy(view => view.Name)
        .FirstOrDefault();

      if (overView == null)
        return string.Empty;

      if (overView is ViewPlan viewPlan && viewPlan.GenLevel != null)
        return viewPlan.GenLevel.Name.Trim();

      var levelId = overView.get_Parameter(BuiltInParameter.PLAN_VIEW_LEVEL)?.AsElementId();
      if (levelId == null || levelId == ElementId.InvalidElementId)
        return string.Empty;

      return doc.GetElement(levelId)?.Name?.Trim() ?? string.Empty;
    }

    private static Dictionary<string, MtoRegionInfo> GetMtoRegionByZone(Document doc, string filledRegionViewName)
    {
      var result = new Dictionary<string, MtoRegionInfo>(StringComparer.OrdinalIgnoreCase);
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
        string comments = GetStringParameter(region, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, F_MtoNames.Parameters.Comments);
        string zone = GetStringParameter(region, F_MtoNames.Parameters.RincoZone);
        if (string.IsNullOrWhiteSpace(zone))
          continue;

        string key = NormalizeZoneKey(zone);
        if (!result.ContainsKey(key))
          result.Add(key, new MtoRegionInfo { ZoneName = zone });

        if (!string.IsNullOrWhiteSpace(comments)
          && !result[key].PourNames.Any(x => string.Equals(x, comments, StringComparison.OrdinalIgnoreCase)))
          result[key].PourNames.Add(comments);
      }

      foreach (var item in result.Values)
        item.PourNames.Sort(StringComparer.OrdinalIgnoreCase);

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
      string sheetSeries = GetStringParameter(sheet, F_MtoNames.Parameters.SheetSeries);
      return string.Equals(sheetSeries, F_MtoNames.Values.ReinforcementSheetSeries, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReinforcementSheetName(ViewSheet sheet)
    {
      return sheet.Name.IndexOf(F_MtoNames.Keywords.ReinforcementSheetName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Normalize(string value)
    {
      return (value ?? string.Empty).Trim();
    }

    private static string NormalizeZoneKey(string value)
    {
      return string.Concat((value ?? string.Empty)
        .Trim()
        .Where(character => !char.IsWhiteSpace(character)))
        .ToUpperInvariant();
    }
  }
}
