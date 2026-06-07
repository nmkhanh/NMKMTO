using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_ConsBar
  {
    public static NMKMTO_ModelActionResult Execute(Document doc, IEnumerable<Autodesk.Revit.DB.View> views, string exportFolder)
    {
      var viewList = views.ToList();
      var warnings = new List<string>();
      var elements = viewList
        .SelectMany(view => new FilteredElementCollector(doc, view.Id)
          .OfClass(typeof(FamilyInstance))
          .Cast<FamilyInstance>()
          .Where(IsReo)
          .Where(element => HasBlueProjectionLineOverride(view, element)))
        .GroupBy(element => element.Id)
        .Select(group => group.First())
        .ToList();

      int updated = 0;
      using (var transaction = new Transaction(doc, "NMKMTO Cons Bar"))
      {
        transaction.Start();
        foreach (var element in elements)
        {
          Parameter parameter = element.LookupParameter("Cons Bar");
          if (parameter == null)
          {
            warnings.Add($"ElementId {element.Id.Value}: missing Cons Bar parameter.");
            continue;
          }

          if (parameter.IsReadOnly)
          {
            warnings.Add($"ElementId {element.Id.Value}: Cons Bar parameter is read-only.");
            continue;
          }

          parameter.Set(1);
          updated++;
        }
        transaction.Commit();
      }

      string warningPath = F_WarningExporter.ExportIfAny(exportFolder, "NMKMTO_CONS_BAR", warnings);
      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = updated,
        WarningPath = warningPath,
        Message = string.IsNullOrWhiteSpace(warningPath)
          ? $"Cons Bar completed\nViews: {viewList.Count}\nUpdated: {updated}"
          : $"Cons Bar completed\nViews: {viewList.Count}\nUpdated: {updated}\nWarning file: {warningPath}"
      };
      foreach (var warning in warnings)
        result.Warnings.Add(warning);
      return result;
    }

    private static bool IsReo(FamilyInstance element)
    {
      string familyName = element.Symbol?.Family?.Name ?? string.Empty;
      return familyName.StartsWith("Reo__", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBlueProjectionLineOverride(Autodesk.Revit.DB.View view, Element element)
    {
      OverrideGraphicSettings overrides = view.GetElementOverrides(element.Id);
      Autodesk.Revit.DB.Color color = overrides.ProjectionLineColor;
      return color.IsValid && color.Red == 0 && color.Green == 0 && color.Blue == 255;
    }
  }
}
