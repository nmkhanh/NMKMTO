using Autodesk.Revit.DB;
using NMKMTO.Models;
using System.IO;

namespace NMKMTO.Functions
{
  public static class F_DistributedReoMtoFill
  {
    private const string ReoFamilyName = "Reo__Reinforcement_DistributionAdjustable[Rinco]";
    private const string ZBarFamilyName = "Reo__ZBar[Rinco]";
    private const string MtoPrefix = "MTO_";
    private const string MtoVerTypeName = "MTO VER";
    private const string MtoHorTypeName = "MTO HOR";
    private const string MtoNoneTypeName = "MTO NONE";

    public static NMKMTO_ModelActionResult CreateOrUpdate(Document doc, IEnumerable<NMKMTO_ModelSheetRow> selectedSheets, string exportFolder)
    {
      var warnings = new List<string>();
      var sheetViews = GetOverViewsBySheet(doc, selectedSheets)
        .Where(item => IsTopOrBottomSheet(item.Sheet.SheetName))
        .ToList();

      if (sheetViews.Count == 0)
        throw new InvalidOperationException("Selected TOP/BOTTOM sheets do not contain OVER views.");

      int fillCount = 0;
      using (var transactionGroup = new TransactionGroup(doc, "NMKMTO Distributed MTO Fill"))
      {
        transactionGroup.Start();
        var preparedViews = new List<PreparedViewData>();

        try
        {
          using (var readTransaction = new Transaction(doc, "NMKMTO Read Distributed Reo Geometry"))
          {
            readTransaction.Start();

            foreach (var sheetView in sheetViews)
            {
              var sourceElements = new FilteredElementCollector(doc, sheetView.View.Id)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(IsSupportedReoFamily)
                .ToList();

              UngroupAllInView(doc, sheetView.View);

              foreach (var elementId in sourceElements.Select(element => element.Id))
              {
                if (doc.GetElement(elementId) is FamilyInstance element)
                  TurnOnGeometryVisibility(element);
              }

              preparedViews.Add(new PreparedViewData
              {
                SourceView = sheetView.View,
                SourceElementIds = sourceElements.Select(element => element.Id).ToList()
              });
            }

            doc.Regenerate();

            foreach (var preparedView in preparedViews)
            {
              preparedView.ReoItems = CollectReoItems(doc, preparedView.SourceView, preparedView.SourceView, preparedView.SourceElementIds, warnings);
            }

            readTransaction.RollBack();
          }

          using (var createTransaction = new Transaction(doc, "NMKMTO Create Distributed MTO Fill"))
          {
            createTransaction.Start();
            var typeIds = EnsureFilledRegionTypes(doc);

            foreach (var preparedView in preparedViews)
            {
              var mtoView = RecreateMtoViewWithDetailing(doc, preparedView.SourceView);
              ClearFilledRegions(doc, mtoView);
              ClearViewTemplate(mtoView);
              SetStringParameter(mtoView, "RINCO_TB_Drawing Type", "MTO");

              foreach (var reoItem in preparedView.ReoItems)
              {
                if (reoItem.Loop == null)
                {
                  warnings.Add($"View '{preparedView.SourceView.Name}': skipped '{reoItem.Comment}' because no valid fill boundary was created.");
                  continue;
                }

                ElementId typeId = reoItem.Direction == ReoDirection.Vertical
                  ? typeIds.VerticalTypeId
                  : reoItem.Direction == ReoDirection.Horizontal
                    ? typeIds.HorizontalTypeId
                    : typeIds.NoneTypeId;

                var fill = FilledRegion.Create(doc, typeId, mtoView.Id, new List<CurveLoop> { reoItem.Loop });
                SetStringParameter(fill, "Comments", reoItem.Comment);
                fillCount++;
              }
            }

            createTransaction.Commit();
          }

          transactionGroup.Assimilate();
        }
        catch
        {
          transactionGroup.RollBack();
          throw;
        }
      }

      string warningPath = string.Empty;
      if (warnings.Count > 0 && !string.IsNullOrWhiteSpace(exportFolder))
      {
        Directory.CreateDirectory(exportFolder);
        warningPath = Path.Combine(exportFolder, $"NMKMTO_DISTRIBUTED_MTO_FILL_WARNING_{DateTime.Now:yyMMdd_HHmmss}.csv");
        ExportWarnings(warningPath, warnings);
      }

      return new NMKMTO_ModelActionResult
      {
        TotalCount = fillCount,
        Message = string.IsNullOrWhiteSpace(warningPath)
          ? $"Distributed MTO Fill completed\nViews: {sheetViews.Count}\nFilledRegions: {fillCount}"
          : $"Distributed MTO Fill completed\nViews: {sheetViews.Count}\nFilledRegions: {fillCount}\nWarning file: {warningPath}"
      };
    }

    private sealed class SheetViewData
    {
      public NMKMTO_ModelSheetRow Sheet { get; set; }
      public Autodesk.Revit.DB.View View { get; set; }
    }

    private sealed class PreparedViewData
    {
      public Autodesk.Revit.DB.View SourceView { get; set; }
      public Autodesk.Revit.DB.View MtoView { get; set; }
      public List<ElementId> SourceElementIds { get; set; } = new();
      public List<ElementId> MtoElementIds { get; set; } = new();
      public List<ReoItem> ReoItems { get; set; } = new();
    }

    private sealed class ChangedParameterData
    {
      public ElementId ElementId { get; set; }
      public string ParameterName { get; set; } = string.Empty;
      public int OldValue { get; set; }
    }

    private sealed class FilledRegionTypeIds
    {
      public ElementId VerticalTypeId { get; set; }
      public ElementId HorizontalTypeId { get; set; }
      public ElementId NoneTypeId { get; set; }
    }

    private sealed class ReoItem
    {
      public CurveLoop Loop { get; set; }
      public ReoDirection Direction { get; set; }
      public string Comment { get; set; } = string.Empty;
    }

    private enum ReoDirection
    {
      None,
      Vertical,
      Horizontal
    }

    private static FilledRegionTypeIds EnsureFilledRegionTypes(Document doc)
    {
      var baseType = new FilteredElementCollector(doc)
        .OfClass(typeof(FilledRegionType))
        .Cast<FilledRegionType>()
        .FirstOrDefault();

      if (baseType == null)
        throw new InvalidOperationException("No FilledRegionType found in this project.");

      return new FilledRegionTypeIds
      {
        VerticalTypeId = EnsureFilledRegionType(doc, baseType, MtoVerTypeName, new Autodesk.Revit.DB.Color(48, 209, 88)),
        HorizontalTypeId = EnsureFilledRegionType(doc, baseType, MtoHorTypeName, new Autodesk.Revit.DB.Color(0, 122, 255)),
        NoneTypeId = EnsureFilledRegionType(doc, baseType, MtoNoneTypeName, new Autodesk.Revit.DB.Color(142, 142, 147))
      };
    }

    private static ElementId EnsureFilledRegionType(Document doc, FilledRegionType baseType, string name, Autodesk.Revit.DB.Color color)
    {
      var existing = new FilteredElementCollector(doc)
        .OfClass(typeof(FilledRegionType))
        .Cast<FilledRegionType>()
        .FirstOrDefault(type => type.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

      if (existing != null)
        return existing.Id;

      var duplicated = baseType.Duplicate(name) as FilledRegionType;
      if (duplicated != null)
      {
        duplicated.ForegroundPatternColor = color;
        duplicated.BackgroundPatternColor = color;
        duplicated.IsMasking = false;
        return duplicated.Id;
      }

      return baseType.Id;
    }

    private static Autodesk.Revit.DB.View RecreateMtoViewWithDetailing(Document doc, Autodesk.Revit.DB.View sourceView)
    {
      string mtoViewName = MtoPrefix + sourceView.Name;
      var existing = new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(view => !view.IsTemplate && view.Name.Equals(mtoViewName, StringComparison.OrdinalIgnoreCase));

      if (existing != null)
        doc.Delete(existing.Id);

      ElementId duplicatedId = sourceView.Duplicate(ViewDuplicateOption.WithDetailing);
      var duplicatedView = doc.GetElement(duplicatedId) as Autodesk.Revit.DB.View;
      duplicatedView.Name = mtoViewName;
      return duplicatedView;
    }

    private static void UngroupAllInView(Document doc, Autodesk.Revit.DB.View view)
    {
      while (true)
      {
        var groups = new FilteredElementCollector(doc, view.Id)
          .OfClass(typeof(Group))
          .Cast<Group>()
          .ToList();

        if (groups.Count == 0)
          return;

        bool ungroupedAny = false;
        foreach (var group in groups)
        {
          if (!group.IsValidObject)
            continue;

          group.UngroupMembers();
          ungroupedAny = true;
        }

        if (!ungroupedAny)
          return;
      }
    }

    private static void ClearFilledRegions(Document doc, Autodesk.Revit.DB.View view)
    {
      var ids = new FilteredElementCollector(doc, view.Id)
        .OfClass(typeof(FilledRegion))
        .Select(element => element.Id)
        .ToList();

      if (ids.Count > 0)
        doc.Delete(ids);
    }

    private static List<ReoItem> CollectReoItems(Document doc, Autodesk.Revit.DB.View sourceView, Autodesk.Revit.DB.View mtoView, List<ElementId> elementIds, List<string> warnings)
    {
      var items = new List<ReoItem>();
      var elements = elementIds
        .Select(id => doc.GetElement(id) as FamilyInstance)
        .Where(element => element != null)
        .Cast<FamilyInstance>()
        .ToList();

      foreach (var element in elements)
      {
        var reoCurves = new List<Curve>();
        var arrowCurves = new List<Curve>();
        int totalCurves = 0;
        var seenStyleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectStyledCurves(doc, element, mtoView, reoCurves, arrowCurves, seenStyleNames, ref totalCurves);

        BoundingBoxXYZ boundingBox = element.get_BoundingBox(mtoView);
        ReoDirection direction = ClassifyByArrow(arrowCurves, boundingBox);
        CurveLoop loop = CreateExtentLoop(reoCurves, arrowCurves);
        string comment = BuildComment(element, direction);
        if (loop == null)
        {
          string styles = seenStyleNames.Count == 0 ? "none" : string.Join(" | ", seenStyleNames.OrderBy(x => x));
          string visibility = GetVisibilityParameterSummary(element);
          warnings.Add($"View '{sourceView.Name}', MTO View '{mtoView.Name}', ElementId {element.Id.Value}, {comment}: cannot create fill. Total curves: {totalCurves}, Reo curves: {reoCurves.Count}, Arrow curves: {arrowCurves.Count}. Visibility: {visibility}. Styles: {styles}.");
          continue;
        }

        items.Add(new ReoItem
        {
          Loop = loop,
          Direction = direction,
          Comment = comment
        });
      }

      return items;
    }

    private static bool IsSupportedReoFamily(FamilyInstance element)
    {
      string familyName = element.Symbol?.Family?.Name ?? string.Empty;
      return familyName.StartsWith(ReoFamilyName, StringComparison.OrdinalIgnoreCase)
        || familyName.StartsWith(ZBarFamilyName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ChangedParameterData> TurnOnGeometryVisibility(FamilyInstance element)
    {
      var changed = new List<ChangedParameterData>();
      foreach (string parameterName in new[] { "Arrow & Dot Visibility", "Arrow Visibility", "Definition: Arrow Visibility" })
      {
        changed.AddRange(TurnOnParameter(element, parameterName));
      }

      return changed;
    }

    private static List<ChangedParameterData> TurnOnParameter(Element element, string parameterName)
    {
      var changed = new List<ChangedParameterData>();
      Parameter parameter = element.LookupParameter(parameterName);
      if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Integer)
        return changed;

      int oldValue = parameter.AsInteger();
      if (oldValue != 1)
      {
        parameter.Set(1);
        changed.Add(new ChangedParameterData
        {
          ElementId = element.Id,
          ParameterName = parameterName,
          OldValue = oldValue
        });
      }

      return changed;
    }

    private static void RestoreParameters(Document doc, List<ChangedParameterData> changedParameters)
    {
      foreach (var item in changedParameters)
      {
        Element element = doc.GetElement(item.ElementId);
        Parameter parameter = element?.LookupParameter(item.ParameterName);
        if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Integer)
          parameter.Set(item.OldValue);
      }
    }

    private static void CollectStyledCurves(Document doc, Element element, Autodesk.Revit.DB.View view, List<Curve> reoCurves, List<Curve> arrowCurves, HashSet<string> seenStyleNames, ref int totalCurves)
    {
      var options = new Options
      {
        View = view,
        IncludeNonVisibleObjects = false
      };

      GeometryElement geometry = element.get_Geometry(options);
      if (geometry == null)
        return;

      CollectStyledCurves(doc, geometry, reoCurves, arrowCurves, seenStyleNames, ref totalCurves);
    }

    private static void CollectStyledCurves(Document doc, GeometryElement geometry, List<Curve> reoCurves, List<Curve> arrowCurves, HashSet<string> seenStyleNames, ref int totalCurves)
    {
      foreach (GeometryObject geometryObject in geometry)
        CollectStyledCurves(doc, geometryObject, reoCurves, arrowCurves, seenStyleNames, ref totalCurves);
    }

    private static void CollectStyledCurves(Document doc, GeometryObject geometryObject, List<Curve> reoCurves, List<Curve> arrowCurves, HashSet<string> seenStyleNames, ref int totalCurves)
    {
      if (geometryObject is Curve curve)
      {
        totalCurves++;
        string styleName = GetGraphicsStyleName(doc, curve.GraphicsStyleId);
        if (!string.IsNullOrWhiteSpace(styleName))
          seenStyleNames.Add(styleName);

        AddStyledCurve(curve, styleName, reoCurves, arrowCurves);

        return;
      }

      if (geometryObject is PolyLine polyLine)
      {
        string styleName = GetGraphicsStyleName(doc, polyLine.GraphicsStyleId);
        if (!string.IsNullOrWhiteSpace(styleName))
          seenStyleNames.Add(styleName);

        var coordinates = polyLine.GetCoordinates();
        for (int i = 0; i < coordinates.Count - 1; i++)
        {
          if (coordinates[i].DistanceTo(coordinates[i + 1]) > 0.001)
          {
            totalCurves++;
            AddStyledCurve(Line.CreateBound(coordinates[i], coordinates[i + 1]), styleName, reoCurves, arrowCurves);
          }
        }

        return;
      }

      if (geometryObject is GeometryInstance instance)
      {
        GeometryElement instanceGeometry = instance.GetInstanceGeometry();
        if (instanceGeometry != null)
          CollectStyledCurves(doc, instanceGeometry, reoCurves, arrowCurves, seenStyleNames, ref totalCurves);
      }
    }

    private static void AddStyledCurve(Curve curve, string styleName, List<Curve> reoCurves, List<Curve> arrowCurves)
    {
      if (IsArrowStyle(styleName))
        arrowCurves.Add(curve);
      else if (styleName.IndexOf("Reo", StringComparison.OrdinalIgnoreCase) >= 0)
        reoCurves.Add(curve);
    }

    private static string GetVisibilityParameterSummary(FamilyInstance element)
    {
      var values = new List<string>();
      foreach (string parameterName in new[] { "Arrow & Dot Visibility", "Arrow Visibility", "Definition: Arrow Visibility" })
      {
        values.Add($"{parameterName}={GetParameterValueText(element, parameterName)}");
      }

      return string.Join(", ", values);
    }

    private static string GetParameterValueText(Element element, string parameterName)
    {
      Parameter parameter = element.LookupParameter(parameterName);
      if (parameter == null)
        return "missing";
      if (parameter.StorageType != StorageType.Integer)
        return parameter.StorageType.ToString();

      return parameter.AsInteger().ToString();
    }

    private static string GetGraphicsStyleName(Document doc, ElementId styleId)
    {
      if (styleId == ElementId.InvalidElementId)
        return string.Empty;

      if (doc.GetElement(styleId) is GraphicsStyle graphicsStyle)
      {
        string styleName = graphicsStyle.Name ?? string.Empty;
        string categoryName = graphicsStyle.GraphicsStyleCategory?.Name ?? string.Empty;
        return string.Join(" ", new[] { styleName, categoryName }.Where(x => !string.IsNullOrWhiteSpace(x)));
      }

      return doc.GetElement(styleId)?.Name ?? string.Empty;
    }

    private static bool IsArrowStyle(string styleName)
    {
      if (styleName.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0)
        return false;

      return styleName.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0
        || styleName.IndexOf("Detail Items", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static CurveLoop CreateExtentLoop(List<Curve> reoCurves, List<Curve> arrowCurves)
    {
      var curves = reoCurves
        .Concat(arrowCurves)
        .Where(curve => curve != null && curve.IsBound)
        .ToList();
      if (reoCurves.Count == 0 || arrowCurves.Count == 0 || curves.Count == 0)
        return null;

      var points = curves.SelectMany(curve => new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) }).ToList();
      if (points.Count == 0)
        return null;

      double minX = points.Min(point => point.X);
      double maxX = points.Max(point => point.X);
      double minY = points.Min(point => point.Y);
      double maxY = points.Max(point => point.Y);
      double z = points.Average(point => point.Z);

      if (maxX - minX < 0.001 || maxY - minY < 0.001)
        return null;

      var p1 = new XYZ(minX, minY, z);
      var p2 = new XYZ(maxX, minY, z);
      var p3 = new XYZ(maxX, maxY, z);
      var p4 = new XYZ(minX, maxY, z);
      var loop = new CurveLoop();
      loop.Append(Line.CreateBound(p1, p2));
      loop.Append(Line.CreateBound(p2, p3));
      loop.Append(Line.CreateBound(p3, p4));
      loop.Append(Line.CreateBound(p4, p1));
      return loop;
    }

    private static ReoDirection ClassifyByArrow(List<Curve> arrowCurves, BoundingBoxXYZ boundingBox)
    {
      var boundArrowCurves = arrowCurves
        .Where(curve => curve != null && curve.IsBound)
        .ToList();

      double dx = boundArrowCurves.Sum(curve => Math.Abs(curve.GetEndPoint(1).X - curve.GetEndPoint(0).X));
      double dy = boundArrowCurves.Sum(curve => Math.Abs(curve.GetEndPoint(1).Y - curve.GetEndPoint(0).Y));

      if (dx > dy)
        return ReoDirection.Horizontal;
      if (dy > dx)
        return ReoDirection.Vertical;

      if (boundingBox != null)
      {
        double boxDx = Math.Abs(boundingBox.Max.X - boundingBox.Min.X);
        double boxDy = Math.Abs(boundingBox.Max.Y - boundingBox.Min.Y);
        if (boxDx > boxDy)
          return ReoDirection.Horizontal;
        if (boxDy > boxDx)
          return ReoDirection.Vertical;
      }

      return ReoDirection.None;
    }

    private static string BuildComment(FamilyInstance element, ReoDirection direction)
    {
      string directionText = direction == ReoDirection.Vertical ? "VER" : direction == ReoDirection.Horizontal ? "HOR" : "NONE";
      string mark = element.LookupParameter("Mark")?.AsString() ?? string.Empty;
      return string.IsNullOrWhiteSpace(mark) ? directionText : $"{directionText} | {mark}";
    }

    private static List<SheetViewData> GetOverViewsBySheet(Document doc, IEnumerable<NMKMTO_ModelSheetRow> sheets)
    {
      var result = new List<SheetViewData>();
      foreach (var sheetRow in sheets)
      {
        var sheet = doc.GetElement(sheetRow.SheetId) as ViewSheet;
        if (sheet == null)
          continue;

        var overViews = sheet.GetAllPlacedViews()
          .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
          .Where(view => view != null && view.Name.IndexOf(F_MtoViewNames.OverViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
          .Cast<Autodesk.Revit.DB.View>();

        foreach (var view in overViews)
        {
          result.Add(new SheetViewData
          {
            Sheet = sheetRow,
            View = view
          });
        }
      }

      return result;
    }

    private static bool IsTopOrBottomSheet(string sheetName)
    {
      return sheetName.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0
        || sheetName.IndexOf("BOTTOM", StringComparison.OrdinalIgnoreCase) >= 0
        || sheetName.IndexOf("BTM", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ClearViewTemplate(Autodesk.Revit.DB.View view)
    {
      if (view.ViewTemplateId != ElementId.InvalidElementId)
        view.ViewTemplateId = ElementId.InvalidElementId;
    }

    private static void SetStringParameter(Element element, string parameterName, string value)
    {
      Parameter parameter = element.LookupParameter(parameterName);
      if (parameter != null && !parameter.IsReadOnly)
        parameter.Set(value);
    }

    private static void ExportWarnings(string path, List<string> warnings)
    {
      var lines = new List<string> { "No,Warning" };
      for (int i = 0; i < warnings.Count; i++)
        lines.Add($"{i + 1},{EscapeCsv(warnings[i])}");

      File.WriteAllLines(path, lines);
    }

    private static string EscapeCsv(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        return value;

      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
