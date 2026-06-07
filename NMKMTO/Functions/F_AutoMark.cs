using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_AutoMark
  {
    public static NMKMTO_ModelActionResult Execute(Document doc, IEnumerable<Autodesk.Revit.DB.View> views, string exportFolder)
    {
      var viewList = views.ToList();
      var warnings = new List<string>();
      int normalCount = 0;
      int cjCount = 0;
      int zbarCount = 0;
      int consBarUCount = 0;
      int consBarSCount = 0;

      using (var transaction = new Transaction(doc, "NMKMTO Auto Mark"))
      {
        transaction.Start();

        foreach (var view in viewList)
        {
          var normalElements = new List<FamilyInstance>();
          var cjElements = new List<FamilyInstance>();
          var zbarElements = new List<FamilyInstance>();
          var consBarUElements = new List<FamilyInstance>();
          var consBarSElements = new List<FamilyInstance>();

          var elements = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .ToList();

          foreach (var element in elements)
          {
            bool isBottom = GetYesNo(element, "Bottom");
            bool isTop = GetYesNo(element, "Top");
            bool isShear = GetYesNo(element, "Shear");
            bool isConsBar = GetYesNo(element, "Cons Bar");
            string comments = GetStringParameter(element, "Comments");
            string familyName = element.Symbol?.Family?.Name ?? string.Empty;

            if ((isBottom || isTop) && isConsBar)
            {
              if (Contains(comments, "C.J"))
                cjElements.Add(element);
              else if (Contains(comments, "U'BAR"))
                consBarUElements.Add(element);
              else
                consBarSElements.Add(element);

              continue;
            }

            if (!isBottom && !isTop && !isShear)
              continue;

            if (familyName.StartsWith("Reo__ZBar", StringComparison.OrdinalIgnoreCase) && HasLocationPoint(element))
            {
              zbarElements.Add(element);
            }
            else if (familyName.StartsWith("Reo__Reinforcement_DistributionAdjustable", StringComparison.OrdinalIgnoreCase) && HasLocationPoint(element))
            {
              if (Contains(comments, "C.J"))
                cjElements.Add(element);
              else
                normalElements.Add(element);
            }
          }

          SortByViewPosition(normalElements);
          SortByViewPosition(cjElements);
          SortByViewPosition(zbarElements);
          SortByViewPosition(consBarUElements);
          SortByViewPosition(consBarSElements);

          SetMarks(normalElements, string.Empty, view, warnings);
          SetMarks(cjElements, "C.J", view, warnings);
          SetMarks(zbarElements, "Z", view, warnings);
          SetMarks(consBarUElements, "U C.J", view, warnings);
          SetMarks(consBarSElements, "S C.J", view, warnings);

          normalCount += normalElements.Count;
          cjCount += cjElements.Count;
          zbarCount += zbarElements.Count;
          consBarUCount += consBarUElements.Count;
          consBarSCount += consBarSElements.Count;
        }

        transaction.Commit();
      }

      int total = normalCount + cjCount + zbarCount + consBarUCount + consBarSCount;
      string warningPath = F_WarningExporter.ExportIfAny(exportFolder, "NMKMTO_AUTO_MARK", warnings);
      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = total,
        WarningPath = warningPath,
        Message = string.IsNullOrWhiteSpace(warningPath)
          ? $"Auto Mark completed\nViews: {viewList.Count}\nNormal: {normalCount}\nC.J: {cjCount}\nZBar: {zbarCount}\nCONS BAR U C.J: {consBarUCount}\nCONS BAR S C.J: {consBarSCount}"
          : $"Auto Mark completed\nViews: {viewList.Count}\nNormal: {normalCount}\nC.J: {cjCount}\nZBar: {zbarCount}\nCONS BAR U C.J: {consBarUCount}\nCONS BAR S C.J: {consBarSCount}\nWarning file: {warningPath}"
      };
      foreach (var warning in warnings)
        result.Warnings.Add(warning);
      return result;
    }

    private static void SetMarks(List<FamilyInstance> elements, string suffix, Autodesk.Revit.DB.View view, List<string> warnings)
    {
      for (int i = 0; i < elements.Count; i++)
      {
        string mark = string.IsNullOrWhiteSpace(suffix) ? (i + 1).ToString() : $"{i + 1}{suffix}";
        Parameter parameter = elements[i].LookupParameter("Mark");
        if (parameter == null)
        {
          warnings.Add($"View '{view.Name}', ElementId {elements[i].Id.Value}: missing Mark parameter.");
          continue;
        }

        if (parameter.IsReadOnly)
        {
          warnings.Add($"View '{view.Name}', ElementId {elements[i].Id.Value}: Mark parameter is read-only.");
          continue;
        }

        parameter.Set(mark);
      }
    }

    private static void SortByViewPosition(List<FamilyInstance> elements)
    {
      elements.Sort((first, second) =>
      {
        XYZ firstPoint = ((LocationPoint)first.Location).Point;
        XYZ secondPoint = ((LocationPoint)second.Location).Point;
        int yCompare = secondPoint.Y.CompareTo(firstPoint.Y);
        return yCompare != 0 ? yCompare : firstPoint.X.CompareTo(secondPoint.X);
      });
    }

    private static bool GetYesNo(Element element, string parameterName)
    {
      Parameter parameter = element.LookupParameter(parameterName);
      return parameter != null && parameter.StorageType == StorageType.Integer && parameter.AsInteger() == 1;
    }

    private static string GetStringParameter(Element element, string parameterName)
    {
      return element.LookupParameter(parameterName)?.AsString() ?? string.Empty;
    }

    private static bool Contains(string source, string value)
    {
      return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasLocationPoint(Element element)
    {
      return element.Location is LocationPoint;
    }
  }
}
