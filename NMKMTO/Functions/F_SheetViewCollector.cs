using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_SheetViewCollector
  {
    public static List<Autodesk.Revit.DB.View> GetViewsByKeyword(Document doc, IEnumerable<NMKMTO_ModelSheetRow> sheets, string keyword)
    {
      return sheets
        .Select(sheet => doc.GetElement(sheet.SheetId) as ViewSheet)
        .Where(sheet => sheet != null)
        .Cast<ViewSheet>()
        .SelectMany(sheet => sheet.GetAllPlacedViews())
        .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
        .Where(view => view != null && view.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
        .Cast<Autodesk.Revit.DB.View>()
        .GroupBy(view => view.Id)
        .Select(group => group.First())
        .ToList();
    }
  }
}
