using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class SUPPORT
  {
    private const string ReoGraphicStyleName = "Reo";
    private const string ZBarFamilyName = "Reo__ZBar[Rinco]";
    private const string DistributionFamilyName = "Reo__Reinforcement_DistributionAdjustable[Rinco] 1";
    private const string HorizontalRegionTypeName = "SUPPORT REO HORIZONTAL";
    private const string VerticalRegionTypeName = "SUPPORT REO VERTICAL";
    private const string NoneRegionTypeName = "SUPPORT REO NONE";
    private const double MinimumRegionSizeMm = 10;
    private const double DebugArrowLengthMm = 800;
    private const double DebugArrowHeadLengthMm = 120;

    public static NMKMTO_ModelActionResult Execute(UIDocument uidoc)
    {
      if (uidoc == null)
        throw new ArgumentNullException(nameof(uidoc));

      return Execute(uidoc.Document, uidoc.Document.ActiveView);
    }

    public static NMKMTO_ModelActionResult ExecuteSelectedWithDirectionArrows(UIDocument uidoc)
    {
      if (uidoc == null)
        throw new ArgumentNullException(nameof(uidoc));

      Document doc = uidoc.Document;
      Autodesk.Revit.DB.View view = doc.ActiveView;
      var ids = uidoc.Selection.GetElementIds().ToList();
      if (ids.Count == 0)
        throw new InvalidOperationException("Please select at least one reo detail family before running SUPPORT selected debug.");

      int created = 0;
      var warnings = new List<string>();

      using (var transaction = new Transaction(doc, "SUPPORT selected direction arrows"))
      {
        transaction.Start();
        var regionTypes = EnsureSupportRegionTypes(doc);

        foreach (ElementId id in ids)
        {
          Element element = doc.GetElement(id);
          if (element == null)
            continue;

          if (!IsSupportedReoFamily(element))
          {
            warnings.Add($"ElementId {ElementIdValue(id)}: unsupported family '{GetFamilyTypeName(element)}'.");
            continue;
          }

          var reoCurves = CollectReoCurves(doc, view, element);
          if (reoCurves.Count == 0)
          {
            warnings.Add($"ElementId {ElementIdValue(id)}: no geometry curve with GraphicStyle '{ReoGraphicStyleName}'.");
            continue;
          }

          XYZ primaryDirection = FindPrimaryCurveDirection(reoCurves, view);
          if (primaryDirection == null || primaryDirection.GetLength() < 1e-9)
          {
            warnings.Add($"ElementId {ElementIdValue(id)}: could not find primary Reo direction.");
            continue;
          }

          if (IsMirrored(element))
            primaryDirection = primaryDirection.Negate();

          RegionBuildResult region = CreateDistributionRegion(element, reoCurves, view);
          if (region == null)
          {
            warnings.Add($"ElementId {ElementIdValue(id)}: could not create rectangular distribution region.");
            continue;
          }

          FillDirectionType directionType = ClassifyDirection(region.PrimaryDirection, view);
          CreateFilledRegion(doc, view, regionTypes[directionType], region.Loop, $"SUPPORT SELECTED {directionType} | ElementId: {ElementIdValue(id)}");

          XYZ moveDirection = GetCounterclockwisePerpendicular(primaryDirection, view);
          XYZ stretchDirection = moveDirection.Negate();
          XYZ center = GetProjectedCenter(reoCurves, view);

          CreateDebugArrow(doc, view, center, primaryDirection, new Autodesk.Revit.DB.Color(0, 0, 255));
          CreateDebugArrow(doc, view, center, moveDirection, new Autodesk.Revit.DB.Color(0, 180, 0));
          CreateDebugArrow(doc, view, center, stretchDirection, new Autodesk.Revit.DB.Color(255, 0, 0));
          created++;
        }

        transaction.Commit();
      }

      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = created,
        Message = $"SUPPORT selected completed\nSelected: {ids.Count}\nFilledRegion created: {created}\nDirection arrows created: {created * 3}"
      };
      foreach (string warning in warnings)
        result.Warnings.Add(warning);
      if (warnings.Count > 0)
        result.Message += $"\nWarnings: {warnings.Count}";

      return result;
    }

    public static NMKMTO_ModelActionResult Execute(Document doc, Autodesk.Revit.DB.View view)
    {
      if (doc == null)
        throw new ArgumentNullException(nameof(doc));
      if (view == null)
        throw new ArgumentNullException(nameof(view));

      var elements = new FilteredElementCollector(doc, view.Id)
        .OfClass(typeof(FamilyInstance))
        .Cast<Element>()
        .Where(IsSupportedReoFamily)
        .ToList();

      int created = 0;
      int horizontalCount = 0;
      int verticalCount = 0;
      int noneCount = 0;
      var warnings = new List<string>();

      using (var transaction = new Transaction(doc, "SUPPORT reo distribution region"))
      {
        transaction.Start();
        var regionTypes = EnsureSupportRegionTypes(doc);

        foreach (Element element in elements)
        {
          var reoCurves = CollectReoCurves(doc, view, element);
          if (reoCurves.Count == 0)
          {
            warnings.Add($"ElementId {ElementIdValue(element.Id)}: no geometry curve with GraphicStyle '{ReoGraphicStyleName}'.");
            continue;
          }

          RegionBuildResult region = CreateDistributionRegion(element, reoCurves, view);
          if (region == null)
          {
            warnings.Add($"ElementId {ElementIdValue(element.Id)}: could not create rectangular distribution region.");
            continue;
          }

          FillDirectionType directionType = ClassifyDirection(region.PrimaryDirection, view);
          CreateFilledRegion(doc, view, regionTypes[directionType], region.Loop, $"SUPPORT {directionType} | ElementId: {ElementIdValue(element.Id)}");
          created++;
          if (directionType == FillDirectionType.Horizontal)
            horizontalCount++;
          else if (directionType == FillDirectionType.Vertical)
            verticalCount++;
          else
            noneCount++;
        }

        transaction.Commit();
      }

      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = created,
        Message = $"SUPPORT completed\nActive view: {view.Name}\nElements: {elements.Count}\nFilledRegion created: {created}\nHorizontal: {horizontalCount}\nVertical: {verticalCount}\nNone: {noneCount}"
      };
      foreach (string warning in warnings)
        result.Warnings.Add(warning);

      if (warnings.Count > 0)
        result.Message += $"\nWarnings: {warnings.Count}";

      return result;
    }

    private sealed class RegionBuildResult
    {
      public CurveLoop Loop { get; set; }
      public XYZ PrimaryDirection { get; set; }
      public XYZ StretchDirection { get; set; }
    }

    private enum FillDirectionType
    {
      Horizontal,
      Vertical,
      None
    }

    private static RegionBuildResult CreateDistributionRegion(Element element, List<Curve> reoCurves, Autodesk.Revit.DB.View view)
    {
      XYZ primaryDirection = FindPrimaryCurveDirection(reoCurves, view);
      if (primaryDirection == null || primaryDirection.GetLength() < 1e-9)
        return null;
      if (IsMirrored(element))
        primaryDirection = primaryDirection.Negate();

      XYZ distributionDirection = GetCounterclockwisePerpendicular(primaryDirection, view);
      if (distributionDirection.GetLength() < 1e-9)
        return null;

      string lengthParameterName = IsZBar(element) ? "Arrow Bot" : "Arrow 1 Length";
      string offsetParameterName = IsZBar(element) ? "Arrow Top" : "Bar From Arrow 1";

      double distributionLength = GetLengthParameter(element, lengthParameterName);
      if (distributionLength <= 1e-9)
        return null;

      var points = reoCurves
        .SelectMany(curve => curve.Tessellate())
        .Select(point => ProjectToViewPlane(point, view))
        .ToList();

      if (points.Count < 2)
        return null;

      XYZ origin = view.Origin;
      double minU = double.MaxValue;
      double maxU = double.MinValue;
      double minV = double.MaxValue;
      double maxV = double.MinValue;

      foreach (XYZ point in points)
      {
        XYZ vector = point - origin;
        double u = vector.DotProduct(primaryDirection);
        double v = vector.DotProduct(distributionDirection);

        minU = Math.Min(minU, u);
        maxU = Math.Max(maxU, u);
        minV = Math.Min(minV, v);
        maxV = Math.Max(maxV, v);
      }

      if (maxU - minU < MmToFeet(MinimumRegionSizeMm))
        ExpandAroundCenter(ref minU, ref maxU, MmToFeet(MinimumRegionSizeMm));

      double barFromArrow = GetLengthParameter(element, offsetParameterName);
      double moveDirectionSign = 1;
      double stretchDirectionSign = -moveDirectionSign;

      double baseV = GetReoVAtDistributionStart(points, primaryDirection, distributionDirection, origin, (minU + maxU) * 0.5);
      double regionStartV = baseV;
      double regionEndV = regionStartV + stretchDirectionSign * distributionLength;
      double regionMinV = Math.Min(regionStartV, regionEndV);
      double regionMaxV = Math.Max(regionStartV, regionEndV);
      if (regionMaxV - regionMinV < MmToFeet(MinimumRegionSizeMm))
        ExpandAroundCenter(ref regionMinV, ref regionMaxV, MmToFeet(MinimumRegionSizeMm));

      regionMinV += moveDirectionSign * barFromArrow;
      regionMaxV += moveDirectionSign * barFromArrow;

      XYZ p1 = origin + primaryDirection * minU + distributionDirection * regionMinV;
      XYZ p2 = origin + primaryDirection * maxU + distributionDirection * regionMinV;
      XYZ p3 = origin + primaryDirection * maxU + distributionDirection * regionMaxV;
      XYZ p4 = origin + primaryDirection * minU + distributionDirection * regionMaxV;

      CurveLoop loop = CreateLoop(new[] { p1, p2, p3, p4 });
      if (loop == null)
        return null;

      return new RegionBuildResult
      {
        Loop = loop,
        PrimaryDirection = primaryDirection.Normalize(),
        StretchDirection = (distributionDirection * stretchDirectionSign).Normalize()
      };
    }

    private static double GetReoVAtDistributionStart(
      List<XYZ> points,
      XYZ primaryDirection,
      XYZ distributionDirection,
      XYZ origin,
      double baseU)
    {
      double bestDistance = double.MaxValue;
      double bestV = 0;

      foreach (XYZ point in points)
      {
        XYZ vector = point - origin;
        double u = vector.DotProduct(primaryDirection);
        double distance = Math.Abs(u - baseU);
        if (distance >= bestDistance)
          continue;

        bestDistance = distance;
        bestV = vector.DotProduct(distributionDirection);
      }

      return bestV;
    }

    private static XYZ FindPrimaryCurveDirection(List<Curve> curves, Autodesk.Revit.DB.View view)
    {
      XYZ bestDirection = null;
      double bestLength = 0;

      foreach (Curve curve in curves)
      {
        IList<XYZ> points = curve.Tessellate();
        for (int i = 0; i < points.Count - 1; i++)
        {
          XYZ start = ProjectToViewPlane(points[i], view);
          XYZ end = ProjectToViewPlane(points[i + 1], view);
          XYZ direction = end - start;
          double length = direction.GetLength();
          if (length <= bestLength || length < 1e-9)
            continue;

          bestLength = length;
          bestDirection = direction.Normalize();
        }
      }

      return bestDirection;
    }

    private static XYZ GetProjectedCenter(List<Curve> curves, Autodesk.Revit.DB.View view)
    {
      var points = curves
        .SelectMany(curve => curve.Tessellate())
        .Select(point => ProjectToViewPlane(point, view))
        .ToList();

      if (points.Count == 0)
        return view.Origin;

      XYZ sum = XYZ.Zero;
      foreach (XYZ point in points)
        sum += point;

      return sum / points.Count;
    }

    private static bool IsMirrored(Element element)
    {
      if (element is FamilyInstance familyInstance)
        return familyInstance.Mirrored;

      return false;
    }

    private static List<Curve> CollectReoCurves(Document doc, Autodesk.Revit.DB.View view, Element element)
    {
      var result = new List<Curve>();
      var options = new Options
      {
        View = view,
        IncludeNonVisibleObjects = true
      };

      GeometryElement geometry = element.get_Geometry(options);
      if (geometry == null)
        return result;

      CollectReoCurves(doc, geometry, result);
      return result;
    }

    private static bool IsSupportedReoFamily(Element element)
    {
      string familyTypeName = GetFamilyTypeName(element);
      return string.Equals(familyTypeName, ZBarFamilyName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(familyTypeName, DistributionFamilyName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZBar(Element element)
    {
      return string.Equals(GetFamilyTypeName(element), ZBarFamilyName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFamilyTypeName(Element element)
    {
      if (element is FamilyInstance familyInstance)
        return familyInstance.Symbol?.Family?.Name ?? familyInstance.Symbol?.Name ?? string.Empty;

      return element.Name ?? string.Empty;
    }

    private static void CollectReoCurves(Document doc, GeometryElement geometry, List<Curve> result)
    {
      foreach (GeometryObject geometryObject in geometry)
      {
        if (geometryObject is GeometryInstance instance)
        {
          GeometryElement instanceGeometry = instance.GetInstanceGeometry();
          if (instanceGeometry != null)
            CollectReoCurves(doc, instanceGeometry, result);
          continue;
        }

        if (geometryObject is Curve curve && HasReoGraphicStyle(doc, geometryObject))
          result.Add(curve);
      }
    }

    private static bool HasReoGraphicStyle(Document doc, GeometryObject geometryObject)
    {
      if (geometryObject.GraphicsStyleId == ElementId.InvalidElementId)
        return false;

      if (doc.GetElement(geometryObject.GraphicsStyleId) is not GraphicsStyle style)
        return false;

      return string.Equals(style.Name, ReoGraphicStyleName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(style.GraphicsStyleCategory?.Name, ReoGraphicStyleName, StringComparison.OrdinalIgnoreCase);
    }

    private static CurveLoop CreateLoop(IReadOnlyList<XYZ> points)
    {
      var loop = new CurveLoop();
      for (int i = 0; i < points.Count; i++)
      {
        XYZ start = points[i];
        XYZ end = points[(i + 1) % points.Count];
        if (start.DistanceTo(end) < 1e-9)
          return null;

        loop.Append(Line.CreateBound(start, end));
      }

      return loop;
    }

    private static void CreateFilledRegion(Document doc, Autodesk.Revit.DB.View view, FilledRegionType regionType, CurveLoop loop, string comments)
    {
      FilledRegion region = FilledRegion.Create(doc, regionType.Id, view.Id, new List<CurveLoop> { loop });
      Parameter parameter = region.LookupParameter("Comments");
      if (parameter != null && !parameter.IsReadOnly)
        parameter.Set(comments);
    }

    private static void CreateDebugArrow(Document doc, Autodesk.Revit.DB.View view, XYZ start, XYZ direction, Autodesk.Revit.DB.Color color)
    {
      if (direction == null || direction.GetLength() < 1e-9)
        return;

      XYZ arrowDirection = direction.Normalize();
      XYZ arrowEnd = start + arrowDirection * MmToFeet(DebugArrowLengthMm);
      XYZ arrowSide = GetCounterclockwisePerpendicular(arrowDirection, view);
      double headLength = MmToFeet(DebugArrowHeadLengthMm);
      double headWidth = headLength * 0.55;

      XYZ headBase = arrowEnd - arrowDirection * headLength;
      CreateDebugLine(doc, view, start, arrowEnd, color);
      CreateDebugLine(doc, view, arrowEnd, headBase + arrowSide * headWidth, color);
      CreateDebugLine(doc, view, arrowEnd, headBase - arrowSide * headWidth, color);
    }

    private static void CreateDebugLine(Document doc, Autodesk.Revit.DB.View view, XYZ start, XYZ end, Autodesk.Revit.DB.Color color)
    {
      DetailCurve curve = doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));
      var overrides = new OverrideGraphicSettings();
      overrides.SetProjectionLineColor(color);
      overrides.SetProjectionLineWeight(6);
      view.SetElementOverrides(curve.Id, overrides);
    }

    private static Dictionary<FillDirectionType, FilledRegionType> EnsureSupportRegionTypes(Document doc)
    {
      FilledRegionType baseType = new FilteredElementCollector(doc)
        .OfClass(typeof(FilledRegionType))
        .Cast<FilledRegionType>()
        .FirstOrDefault();

      if (baseType == null)
        throw new InvalidOperationException("No FilledRegionType found in the document.");

      return new Dictionary<FillDirectionType, FilledRegionType>
      {
        { FillDirectionType.Horizontal, EnsureSupportRegionType(doc, baseType, HorizontalRegionTypeName, new Autodesk.Revit.DB.Color(0, 180, 0)) },
        { FillDirectionType.Vertical, EnsureSupportRegionType(doc, baseType, VerticalRegionTypeName, new Autodesk.Revit.DB.Color(255, 140, 0)) },
        { FillDirectionType.None, EnsureSupportRegionType(doc, baseType, NoneRegionTypeName, new Autodesk.Revit.DB.Color(0, 200, 200)) }
      };
    }

    private static FilledRegionType EnsureSupportRegionType(Document doc, FilledRegionType baseType, string name, Autodesk.Revit.DB.Color color)
    {
      FilledRegionType existing = new FilteredElementCollector(doc)
        .OfClass(typeof(FilledRegionType))
        .Cast<FilledRegionType>()
        .FirstOrDefault(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase));

      FilledRegionType regionType = existing ?? baseType.Duplicate(name) as FilledRegionType;
      if (regionType == null)
        throw new InvalidOperationException($"Could not create FilledRegionType '{name}'.");

      ElementId solidPatternId = GetSolidFillPatternId(doc);
      if (solidPatternId != ElementId.InvalidElementId)
        regionType.ForegroundPatternId = solidPatternId;
      regionType.ForegroundPatternColor = color;
      regionType.IsMasking = false;

      return regionType;
    }

    private static ElementId GetSolidFillPatternId(Document doc)
    {
      FillPatternElement solid = new FilteredElementCollector(doc)
        .OfClass(typeof(FillPatternElement))
        .Cast<FillPatternElement>()
        .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);

      return solid?.Id ?? ElementId.InvalidElementId;
    }

    private static FillDirectionType ClassifyDirection(XYZ direction, Autodesk.Revit.DB.View view)
    {
      XYZ normalized = direction.Normalize();
      double right = Math.Abs(normalized.DotProduct(GetViewRight(view).Normalize()));
      double up = Math.Abs(normalized.DotProduct(view.UpDirection.Normalize()));
      double tolerance = Math.Cos(Math.PI / 12.0);

      if (right >= tolerance)
        return FillDirectionType.Horizontal;
      if (up >= tolerance)
        return FillDirectionType.Vertical;

      return FillDirectionType.None;
    }

    private static XYZ ProjectToViewPlane(XYZ point, Autodesk.Revit.DB.View view)
    {
      XYZ origin = view.Origin;
      XYZ normal = view.ViewDirection.Normalize();
      double distance = (point - origin).DotProduct(normal);
      return point - normal * distance;
    }

    private static XYZ GetViewRight(Autodesk.Revit.DB.View view)
    {
      return view.UpDirection.CrossProduct(view.ViewDirection);
    }

    private static XYZ GetCounterclockwisePerpendicular(XYZ direction, Autodesk.Revit.DB.View view)
    {
      XYZ right = GetViewRight(view).Normalize();
      XYZ up = view.UpDirection.Normalize();

      double rightComponent = direction.DotProduct(right);
      double upComponent = direction.DotProduct(up);
      XYZ counterclockwise = up * rightComponent - right * upComponent;

      if (counterclockwise.GetLength() < 1e-9)
        return view.ViewDirection.CrossProduct(direction).Normalize();

      return counterclockwise.Normalize();
    }

    private static double GetLengthParameter(Element element, string name)
    {
      Parameter parameter = element.LookupParameter(name);
      if (parameter == null)
        return 0;

      if (parameter.StorageType == StorageType.Double)
        return parameter.AsDouble();

      if (parameter.StorageType == StorageType.Integer)
        return parameter.AsInteger();

      if (double.TryParse(parameter.AsValueString(), out double parsed))
        return MmToFeet(parsed);

      return 0;
    }

    private static void ExpandAroundCenter(ref double min, ref double max, double size)
    {
      double center = (min + max) * 0.5;
      min = center - size * 0.5;
      max = center + size * 0.5;
    }

    private static double MmToFeet(double value)
    {
      return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
    }

    private static long ElementIdValue(ElementId id)
    {
#if R20 || R21 || R22 || R23
      return id.IntegerValue;
#else
      return id.Value;
#endif
    }
  }
}
