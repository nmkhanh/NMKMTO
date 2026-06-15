using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_DistributedReoExtractor
  {
    private const string DirectShapeApplicationId = "NMKMTO_DISTRIBUTED_REO_MTO";
    private const string ZBarFamilyName = "Reo__ZBar[Rinco]";
    private const string DistributionFamilyName = "Reo__Reinforcement_DistributionAdjustable[Rinco] 1";
    private const string ReoGraphicStyleName = "Reo";
    private const double Ft2ToM2 = 0.09290304;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static NMKMTO_ModelDistributedReoResult Extract(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      NMKMTO_ModelModelDataOptions options)
    {
      if (doc == null)
        throw new ArgumentNullException(nameof(doc));
      if (options == null)
        throw new ArgumentNullException(nameof(options));
      if (string.IsNullOrWhiteSpace(options.ExportFolder))
        throw new InvalidOperationException("Export folder is empty.");

      Directory.CreateDirectory(options.ExportFolder);

      double thickness = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
      double minimumVolume = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.CubicMillimeters);
      double boundingTolerance = UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Millimeters);
      var warnings = new List<string>();
      var sheets = selectedSheets?.ToList() ?? new List<NMKMTO_ModelSheetRow>();
      if (sheets.Count == 0)
        throw new InvalidOperationException("Please select at least one sheet.");

      #region 01 - Get MTO FilledRegions and group selected sheets by Level and Zone

      Autodesk.Revit.DB.View mtoView = FindRequiredView(doc, options.FilledRegionViewName);
      List<MtoRegionData> mtoRegions = CollectMtoRegions(doc, mtoView);
      if (mtoRegions.Count == 0)
        throw new InvalidOperationException($"View '{options.FilledRegionViewName}' does not contain any FilledRegion.");

      var scopes = sheets
        .GroupBy(
          sheet => $"{NormalizeName(sheet.LevelName)}|{NormalizeName(sheet.ZoneName)}",
          StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

      var rows = new List<DistributedRow>();
      var directShapeItems = new List<DirectShapeData>();
      int sourceCurveLoopCount = 0;
      int rowNo = 1;

      #endregion

      foreach (NMKMTO_ModelSheetRow scope in scopes)
      {
        #region 02 - Match MTO regions and OVER views for this Level and Zone

        Level level = FindLevel(doc, scope.LevelName);
        double topZ = level.ProjectElevation;
        double bottomZ = level.ProjectElevation
          - UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
        List<MtoRegionData> matchedMtoRegions = mtoRegions
          .Where(region => IsRegionForSheet(region, scope))
          .ToList();

        if (matchedMtoRegions.Count == 0)
        {
          warnings.Add($"Level '{scope.LevelName}' zone '{scope.ZoneName}' has no matching MTO FilledRegion.");
          continue;
        }

        List<NMKMTO_ModelSheetRow> scopeSheets = sheets
          .Where(sheet =>
            EqualsName(sheet.LevelName, scope.LevelName)
            && EqualsName(sheet.ZoneName, scope.ZoneName))
          .ToList();

        List<Autodesk.Revit.DB.View> topViews = GetOverViews(
          doc,
          scopeSheets.Where(sheet =>
            sheet.SheetName.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0),
          warnings);
        List<Autodesk.Revit.DB.View> bottomViews = GetOverViews(
          doc,
          scopeSheets.Where(sheet =>
            sheet.SheetName.IndexOf("BOTTOM", StringComparison.OrdinalIgnoreCase) >= 0),
          warnings);

        if (topViews.Count == 0)
          warnings.Add($"Level '{scope.LevelName}' zone '{scope.ZoneName}' has no TOP REIN OVER view.");
        if (bottomViews.Count == 0)
          warnings.Add($"Level '{scope.LevelName}' zone '{scope.ZoneName}' has no BOTTOM REIN OVER view.");

        #endregion

        #region 03 - Create Top and Bottom solids from supported families in OVER views

        List<SourceSolidData> topSourceSolids = CreateSourceSolids(
          doc,
          topViews,
          topZ,
          thickness,
          minimumVolume,
          "TOP",
          warnings);
        List<SourceSolidData> bottomSourceSolids = CreateSourceSolids(
          doc,
          bottomViews,
          bottomZ,
          thickness,
          minimumVolume,
          "BOTTOM",
          warnings);
        sourceCurveLoopCount += topSourceSolids.Count + bottomSourceSolids.Count;

        #endregion

        #region 04 - Union same-direction solids, then remove intersections between direction groups

        List<Solid> topNonIntersectingSolids = CreateDistributedSolids(
          topSourceSolids,
          minimumVolume,
          boundingTolerance,
          "TOP",
          warnings);
        List<Solid> bottomNonIntersectingSolids = CreateDistributedSolids(
          bottomSourceSolids,
          minimumVolume,
          boundingTolerance,
          "BOTTOM",
          warnings);

        #endregion

        foreach (MtoRegionData mtoRegion in matchedMtoRegions)
        {
          #region 05 - Intersect non-overlapping Distributed solids with each MTO FilledRegion

          Solid topMtoSolid;
          Solid bottomMtoSolid;
          try
          {
            topMtoSolid = CreateSolidFromFilledRegion(mtoRegion.Region, topZ, thickness);
            bottomMtoSolid = CreateSolidFromFilledRegion(mtoRegion.Region, bottomZ, thickness);
          }
          catch (Exception ex)
          {
            warnings.Add(
              $"MTO FilledRegion '{mtoRegion.Comments}' on level '{scope.LevelName}': {ex.Message}");
            continue;
          }

          List<Solid> topIntersections = IntersectWithMtoRegion(
            topMtoSolid,
            topNonIntersectingSolids,
            minimumVolume);
          List<Solid> bottomIntersections = IntersectWithMtoRegion(
            bottomMtoSolid,
            bottomNonIntersectingSolids,
            minimumVolume);

          List<Solid> mergedTopSolids = MergeSolids(
            topIntersections,
            minimumVolume,
            $"TOP | MTO FilledRegion '{mtoRegion.Comments}' | Level '{scope.LevelName}'",
            warnings);
          List<Solid> mergedBottomSolids = MergeSolids(
            bottomIntersections,
            minimumVolume,
            $"BOTTOM | MTO FilledRegion '{mtoRegion.Comments}' | Level '{scope.LevelName}'",
            warnings);

          double topAreaM2 = mergedTopSolids.Sum(GetTopFaceArea) * Ft2ToM2;
          double bottomAreaM2 = mergedBottomSolids.Sum(GetTopFaceArea) * Ft2ToM2;

          var row = new DistributedRow
          {
            No = rowNo++,
            Pour = mtoRegion.Comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase)
              ? mtoRegion.Comments
              : string.Empty,
            Zone = string.IsNullOrWhiteSpace(mtoRegion.RincoZone)
              ? mtoRegion.Comments
              : mtoRegion.RincoZone,
            Level = scope.LevelName,
            DistributedTopAreaM2 = topAreaM2,
            DistributedBottomAreaM2 = bottomAreaM2
          };
          rows.Add(row);

          if (options.Create3d && mergedTopSolids.Count > 0)
          {
            directShapeItems.Add(new DirectShapeData
            {
              Name = $"NMKMTO DISTRIBUTED TOP {row.No}",
              Comments =
                $"NMKMTO DISTRIBUTED TOP | Level: {row.Level} | Sequence: {GetSequenceValue(row)} | Area: {FormatNumber(topAreaM2)} m2",
              Solids = mergedTopSolids
            });
          }

          if (options.Create3d && mergedBottomSolids.Count > 0)
          {
            directShapeItems.Add(new DirectShapeData
            {
              Name = $"NMKMTO DISTRIBUTED BOTTOM {row.No}",
              Comments =
                $"NMKMTO DISTRIBUTED BOTTOM | Level: {row.Level} | Sequence: {GetSequenceValue(row)} | Area: {FormatNumber(bottomAreaM2)} m2",
              Solids = mergedBottomSolids
            });
          }

          if (topAreaM2 <= 0 && bottomAreaM2 <= 0)
          {
            warnings.Add(
              $"MTO FilledRegion '{mtoRegion.Comments}' on level '{scope.LevelName}' did not intersect any valid Distributed Reo area.");
          }

          #endregion
        }
      }

      #region 06 - Create optional DirectShapes for exported Top and Bottom areas

      if (options.Create3d)
      {
        using (var transaction = new Transaction(doc, "NMKMTO DISTRIBUTED REO DirectShape"))
        {
          transaction.Start();

          List<ElementId> oldShapeIds = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(shape => shape.ApplicationId == DirectShapeApplicationId)
            .Select(shape => shape.Id)
            .ToList();
          if (oldShapeIds.Count > 0)
            doc.Delete(oldShapeIds);

          int directShapeIndex = 1;
          foreach (DirectShapeData item in directShapeItems)
          {
            DirectShape directShape = DirectShape.CreateElement(
              doc,
              new ElementId(BuiltInCategory.OST_GenericModel));
            directShape.ApplicationId = DirectShapeApplicationId;
            directShape.ApplicationDataId = directShapeIndex.ToString(CultureInfo.InvariantCulture);
            directShape.Name = item.Name;
            directShape.SetShape(item.Solids.Cast<GeometryObject>().ToList());

            Parameter comments = directShape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
              ?? directShape.LookupParameter("Comments");
            if (comments != null && !comments.IsReadOnly)
              comments.Set(item.Comments);

            directShapeIndex++;
          }

          transaction.Commit();
        }
      }

      #endregion

      #region 07 - Sort and export rows using the same structure as Earthing Reo

      rows = rows
        .OrderBy(row => GetSequenceValue(row), NaturalStringComparer.Instance)
        .ThenBy(row => row.Level, NaturalStringComparer.Instance)
        .ToList();
      for (int index = 0; index < rows.Count; index++)
        rows[index].No = index + 1;

      string baseFileName = BuildBaseExportFileName(doc, sheets);
      string exportPath = Path.Combine(options.ExportFolder, $"{baseFileName}.csv");
      string warningPath = warnings.Count > 0
        ? Path.Combine(options.ExportFolder, $"{baseFileName}_WARNING.csv")
        : string.Empty;

      ExportCsv(exportPath, rows);
      if (warnings.Count > 0)
        ExportWarnings(warningPath, warnings);

      double totalTopAreaM2 = rows.Sum(row => row.DistributedTopAreaM2);
      double totalBottomAreaM2 = rows.Sum(row => row.DistributedBottomAreaM2);

      var result = new NMKMTO_ModelDistributedReoResult
      {
        SheetCount = sheets.Count,
        DistributedTopAreaM2 = totalTopAreaM2,
        DistributedBottomAreaM2 = totalBottomAreaM2,
        ExportPath = exportPath,
        WarningPath = warningPath,
        Message = warnings.Count > 0
          ? $"DISTRIBUTED REO exported: {exportPath}\nRows: {rows.Count}\nCurveLoops: {sourceCurveLoopCount}\nWarning file: {warningPath}"
          : $"DISTRIBUTED REO exported: {exportPath}\nRows: {rows.Count}\nCurveLoops: {sourceCurveLoopCount}"
      };
      foreach (string warning in warnings)
        result.Warnings.Add(warning);

      return result;

      #endregion
    }

    private sealed class MtoRegionData
    {
      public FilledRegion Region { get; set; }
      public string Comments { get; set; } = string.Empty;
      public string RincoZone { get; set; } = string.Empty;
    }

    private sealed class SourceSolidData
    {
      public Solid Solid { get; set; }
      public XYZ Min { get; set; }
      public XYZ Max { get; set; }
      public ElementId SourceId { get; set; } = ElementId.InvalidElementId;
      public string DirectionGroup { get; set; } = string.Empty;
    }

    private sealed class DistributedRow
    {
      public int No { get; set; }
      public string Pour { get; set; } = string.Empty;
      public string Zone { get; set; } = string.Empty;
      public string Level { get; set; } = string.Empty;
      public double DistributedTopAreaM2 { get; set; }
      public double DistributedBottomAreaM2 { get; set; }
    }

    private sealed class DirectShapeData
    {
      public string Name { get; set; } = string.Empty;
      public string Comments { get; set; } = string.Empty;
      public List<Solid> Solids { get; set; } = new();
    }

    private static Autodesk.Revit.DB.View FindRequiredView(Document doc, string viewName)
    {
      Autodesk.Revit.DB.View view = new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(candidate =>
          !candidate.IsTemplate
          && candidate.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

      if (view == null)
        throw new InvalidOperationException($"Required view not found: {viewName}.");

      return view;
    }

    private static List<MtoRegionData> CollectMtoRegions(Document doc, Autodesk.Revit.DB.View view)
    {
      return new FilteredElementCollector(doc, view.Id)
        .OfClass(typeof(FilledRegion))
        .Cast<FilledRegion>()
        .Select(region => new MtoRegionData
        {
          Region = region,
          Comments = GetStringParameter(
            region,
            BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            "Comments"),
          RincoZone = GetStringParameter(region, "RINCO_ZONE")
        })
        .Where(region => !string.IsNullOrWhiteSpace(region.Comments))
        .ToList();
    }

    private static List<Autodesk.Revit.DB.View> GetOverViews(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> sheetRows,
      List<string> warnings)
    {
      var views = new List<Autodesk.Revit.DB.View>();
      foreach (NMKMTO_ModelSheetRow sheetRow in sheetRows)
      {
        ViewSheet sheet = doc.GetElement(sheetRow.SheetId) as ViewSheet;
        if (sheet == null)
          continue;

        List<Autodesk.Revit.DB.View> sheetViews = sheet
          .GetAllPlacedViews()
          .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
          .Where(view =>
            view != null
            && !view.IsTemplate
            && view.Name.IndexOf(F_MtoViewNames.OverViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
          .Cast<Autodesk.Revit.DB.View>()
          .ToList();

        if (sheetViews.Count == 0)
          warnings.Add($"Sheet '{sheetRow.SheetNumber} - {sheetRow.SheetName}' has no OVER view.");

        views.AddRange(sheetViews);
      }

      return views
        .GroupBy(view => view.Id)
        .Select(group => group.First())
        .ToList();
    }

    private static List<SourceSolidData> CreateSourceSolids(
      Document doc,
      List<Autodesk.Revit.DB.View> views,
      double bottomZ,
      double thickness,
      double minimumVolume,
      string layerName,
      List<string> warnings)
    {
      var result = new List<SourceSolidData>();
      var processedElementIds = new HashSet<ElementId>();

      foreach (Autodesk.Revit.DB.View view in views)
      {
        XYZ viewOrigin = view.Origin;
        XYZ viewNormal = view.ViewDirection.Normalize();
        XYZ viewRight = view.UpDirection.CrossProduct(view.ViewDirection).Normalize();
        XYZ viewUp = view.UpDirection.Normalize();

        var elements = new FilteredElementCollector(doc, view.Id)
          .OfClass(typeof(FamilyInstance))
          .Cast<FamilyInstance>()
          .Where(instance =>
          {
            string name = instance.Symbol?.Family?.Name ?? string.Empty;
            return string.Equals(name, ZBarFamilyName, StringComparison.OrdinalIgnoreCase)
              || string.Equals(name, DistributionFamilyName, StringComparison.OrdinalIgnoreCase);
          })
          .ToList();

        foreach (FamilyInstance element in elements)
        {
          if (!processedElementIds.Add(element.Id))
            continue;

          string familyName = element.Symbol?.Family?.Name ?? string.Empty;
          bool isZBar = string.Equals(familyName, ZBarFamilyName, StringComparison.OrdinalIgnoreCase);
          bool isDistribution = string.Equals(
            familyName,
            DistributionFamilyName,
            StringComparison.OrdinalIgnoreCase);

          double moveDistance;
          double stretchDistance;
          double zTopBarLocation = 0;

          if (isDistribution)
          {
            Parameter moveParameter = element.LookupParameter("Bar From Arrow 1");
            Parameter stretchParameter = element.LookupParameter("Arrow 1 Length");
            if (moveParameter == null || moveParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: missing 'Bar From Arrow 1'.");
              continue;
            }
            if (stretchParameter == null || stretchParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: missing 'Arrow 1 Length'.");
              continue;
            }

            moveDistance = moveParameter.AsDouble();
            stretchDistance = stretchParameter.AsDouble();
          }
          else
          {
            Parameter moveParameter = element.LookupParameter("Arrow Top");
            Parameter stretchParameter = element.LookupParameter("Arrow Bot");
            Parameter topBarLocationParameter = element.LookupParameter("Top Bar Location");
            if (moveParameter == null || moveParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: missing 'Arrow Top'.");
              continue;
            }
            if (stretchParameter == null || stretchParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: missing 'Arrow Bot'.");
              continue;
            }
            if (topBarLocationParameter == null || topBarLocationParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: missing 'Top Bar Location'.");
              continue;
            }

            moveDistance = moveParameter.AsDouble();
            stretchDistance = stretchParameter.AsDouble();
            zTopBarLocation = topBarLocationParameter.AsDouble();
          }

          if (stretchDistance <= 1e-9)
          {
            warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: stretch distance is zero.");
            continue;
          }

          var geometryOptions = new Options
          {
            View = view,
            IncludeNonVisibleObjects = true
          };
          GeometryElement elementGeometry = element.get_Geometry(geometryOptions);
          var reoCurves = new List<Curve>();

          if (elementGeometry != null)
          {
            foreach (GeometryObject geometryObject in elementGeometry)
            {
              AddReoCurve(doc, geometryObject as Curve, reoCurves);

              if (geometryObject is not GeometryInstance geometryInstance)
                continue;

              GeometryElement instanceGeometry = geometryInstance.GetInstanceGeometry();
              if (instanceGeometry == null)
                continue;

              foreach (GeometryObject instanceObject in instanceGeometry)
                AddReoCurve(doc, instanceObject as Curve, reoCurves);
            }
          }

          if (reoCurves.Count == 0)
          {
            warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: no GraphicStyle 'Reo' curve.");
            continue;
          }

          XYZ reoDirection = null;
          double longestSegment = 0;
          var reoPoints = new List<XYZ>();

          foreach (Curve reoCurve in reoCurves)
          {
            IList<XYZ> points = reoCurve.Tessellate();
            foreach (XYZ point in points)
            {
              double distanceToPlane = (point - viewOrigin).DotProduct(viewNormal);
              reoPoints.Add(point - viewNormal * distanceToPlane);
            }

            for (int index = 0; index < points.Count - 1; index++)
            {
              XYZ start = points[index]
                - viewNormal * (points[index] - viewOrigin).DotProduct(viewNormal);
              XYZ end = points[index + 1]
                - viewNormal * (points[index + 1] - viewOrigin).DotProduct(viewNormal);
              XYZ segment = end - start;
              double segmentLength = segment.GetLength();
              if (segmentLength <= longestSegment || segmentLength < 1e-9)
                continue;

              longestSegment = segmentLength;
              reoDirection = segment.Normalize();
            }
          }

          if (reoDirection == null)
          {
            warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: Reo director not found.");
            continue;
          }

          if (isDistribution)
          {
            Parameter spliceParameter = element.LookupParameter("Splice");
            if (spliceParameter == null || spliceParameter.StorageType != StorageType.Integer)
            {
              warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: missing 'Splice'.");
              continue;
            }
            if (spliceParameter.AsInteger() == 0)
              reoDirection = reoDirection.Negate();
          }

          double rightComponent = reoDirection.DotProduct(viewRight);
          double upComponent = reoDirection.DotProduct(viewUp);
          double directionTolerance = Math.Cos(Math.PI / 12.0);
          string directionGroup = Math.Abs(rightComponent) >= directionTolerance
            ? "HORIZONTAL"
            : Math.Abs(upComponent) >= directionTolerance
              ? "VERTICAL"
              : "NONE";
          XYZ counterclockwiseDirection =
            (viewUp * rightComponent - viewRight * upComponent).Normalize();
          XYZ clockwiseDirection = counterclockwiseDirection.Negate();
          XYZ moveDirection = isDistribution
            ? clockwiseDirection
            : counterclockwiseDirection;
          XYZ stretchDirection = isDistribution
            ? counterclockwiseDirection
            : clockwiseDirection;

          if (element.Mirrored)
          {
            moveDirection = moveDirection.Negate();
            stretchDirection = stretchDirection.Negate();
          }

          if (element.Location is not LocationPoint locationPoint)
          {
            warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: LocationPoint not found.");
            continue;
          }

          XYZ rawLocation = locationPoint.Point;
          XYZ locationOnView = rawLocation
            - viewNormal * (rawLocation - viewOrigin).DotProduct(viewNormal);
          XYZ fillLocation = isZBar
            ? locationOnView + moveDirection * zTopBarLocation
            : locationOnView;
          XYZ fillStartPoint = fillLocation + moveDirection * moveDistance;

          double minAlongReo = double.MaxValue;
          double maxAlongReo = double.MinValue;
          foreach (XYZ reoPoint in reoPoints)
          {
            double distance = (reoPoint - locationOnView).DotProduct(reoDirection);
            minAlongReo = Math.Min(minAlongReo, distance);
            maxAlongReo = Math.Max(maxAlongReo, distance);
          }

          if (maxAlongReo - minAlongReo < UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters))
          {
            double center = (minAlongReo + maxAlongReo) * 0.5;
            double halfWidth = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
            minAlongReo = center - halfWidth;
            maxAlongReo = center + halfWidth;
          }

          XYZ firstStart = fillStartPoint + reoDirection * minAlongReo;
          XYZ secondStart = fillStartPoint + reoDirection * maxAlongReo;
          XYZ firstEnd = firstStart + stretchDirection * stretchDistance;
          XYZ secondEnd = secondStart + stretchDirection * stretchDistance;

          double sourceZ = firstStart.Z;
          XYZ translation = new XYZ(0, 0, bottomZ - sourceZ);
          firstStart += translation;
          secondStart += translation;
          firstEnd += translation;
          secondEnd += translation;

          var boundary = new CurveLoop();
          boundary.Append(Line.CreateBound(firstStart, secondStart));
          boundary.Append(Line.CreateBound(secondStart, secondEnd));
          boundary.Append(Line.CreateBound(secondEnd, firstEnd));
          boundary.Append(Line.CreateBound(firstEnd, firstStart));

          try
          {
            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
              new List<CurveLoop> { boundary },
              XYZ.BasisZ,
              thickness);
            if (solid == null || solid.Volume <= minimumVolume)
              continue;

            var points = new[]
            {
              firstStart,
              secondStart,
              firstEnd,
              secondEnd,
              firstStart + XYZ.BasisZ * thickness,
              secondStart + XYZ.BasisZ * thickness,
              firstEnd + XYZ.BasisZ * thickness,
              secondEnd + XYZ.BasisZ * thickness
            };

            result.Add(new SourceSolidData
            {
              Solid = solid,
              Min = new XYZ(
                points.Min(point => point.X),
                points.Min(point => point.Y),
                points.Min(point => point.Z)),
              Max = new XYZ(
                points.Max(point => point.X),
                points.Max(point => point.Y),
                points.Max(point => point.Z)),
              SourceId = element.Id,
              DirectionGroup = directionGroup
            });
          }
          catch (Exception ex)
          {
            warnings.Add($"{layerName} | View '{view.Name}', ElementId {element.Id}: {ex.Message}");
          }
        }
      }

      return result;
    }

    private static void AddReoCurve(Document doc, Curve curve, List<Curve> result)
    {
      if (curve == null || curve.GraphicsStyleId == ElementId.InvalidElementId)
        return;

      GraphicsStyle style = doc.GetElement(curve.GraphicsStyleId) as GraphicsStyle;
      if (style != null
        && (string.Equals(style.Name, ReoGraphicStyleName, StringComparison.OrdinalIgnoreCase)
          || string.Equals(
            style.GraphicsStyleCategory?.Name,
            ReoGraphicStyleName,
            StringComparison.OrdinalIgnoreCase)))
      {
        result.Add(curve);
      }
    }

    private static List<Solid> CreateDistributedSolids(
      List<SourceSolidData> sources,
      double minimumVolume,
      double boundingTolerance,
      string layerName,
      List<string> warnings)
    {
      var directionNames = new[] { "HORIZONTAL", "VERTICAL", "NONE" };
      var mergedByDirection = new Dictionary<string, List<Solid>>(StringComparer.OrdinalIgnoreCase);

      foreach (string directionName in directionNames)
      {
        List<Solid> directionSolids = sources
          .Where(source => string.Equals(
            source.DirectionGroup,
            directionName,
            StringComparison.OrdinalIgnoreCase))
          .Select(source => source.Solid)
          .ToList();

        mergedByDirection[directionName] = MergeSolids(
          directionSolids,
          minimumVolume,
          $"{layerName} {directionName}",
          warnings);
      }

      var result = new List<Solid>();
      foreach (string sourceDirection in directionNames)
      {
        List<Solid> sourceGroup = mergedByDirection[sourceDirection];
        List<Solid> otherDirectionSolids = directionNames
          .Where(direction => !string.Equals(
            direction,
            sourceDirection,
            StringComparison.OrdinalIgnoreCase))
          .SelectMany(direction => mergedByDirection[direction])
          .ToList();

        foreach (Solid sourceSolid in sourceGroup)
        {
          Solid remaining = sourceSolid;
          BoundingBoxXYZ sourceBox = sourceSolid.GetBoundingBox();

          foreach (Solid otherSolid in otherDirectionSolids)
          {
            if (remaining == null || remaining.Volume <= minimumVolume)
              break;

            BoundingBoxXYZ otherBox = otherSolid.GetBoundingBox();
            bool boundingBoxesOverlap =
              sourceBox.Min.X <= otherBox.Max.X + boundingTolerance
              && sourceBox.Max.X + boundingTolerance >= otherBox.Min.X
              && sourceBox.Min.Y <= otherBox.Max.Y + boundingTolerance
              && sourceBox.Max.Y + boundingTolerance >= otherBox.Min.Y
              && sourceBox.Min.Z <= otherBox.Max.Z + boundingTolerance
              && sourceBox.Max.Z + boundingTolerance >= otherBox.Min.Z;
            if (!boundingBoxesOverlap)
              continue;

            try
            {
              Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                remaining,
                otherSolid,
                BooleanOperationsType.Intersect);
              if (intersection == null || intersection.Volume <= minimumVolume)
                continue;

              remaining = BooleanOperationsUtils.ExecuteBooleanOperation(
                remaining,
                otherSolid,
                BooleanOperationsType.Difference);
            }
            catch (Exception ex)
            {
              warnings.Add(
                $"{layerName} {sourceDirection} against another direction: {ex.Message}");
            }
          }

          if (remaining != null && remaining.Volume > minimumVolume)
            result.Add(remaining);
        }
      }

      return result;
    }

    private static List<Solid> IntersectWithMtoRegion(
      Solid mtoSolid,
      List<Solid> distributedSolids,
      double minimumVolume)
    {
      var result = new List<Solid>();
      foreach (Solid distributedSolid in distributedSolids)
      {
        try
        {
          Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
            mtoSolid,
            distributedSolid,
            BooleanOperationsType.Intersect);
          if (intersection != null && intersection.Volume > minimumVolume)
            result.Add(intersection);
        }
        catch
        {
        }
      }

      return result;
    }

    private static List<Solid> MergeSolids(
      List<Solid> solids,
      double minimumVolume,
      string context,
      List<string> warnings)
    {
      var result = solids.Where(solid => solid.Volume > minimumVolume).ToList();
      bool changed = true;

      while (changed)
      {
        changed = false;
        for (int firstIndex = 0; firstIndex < result.Count; firstIndex++)
        {
          for (int secondIndex = firstIndex + 1; secondIndex < result.Count; secondIndex++)
          {
            Solid overlap;
            try
            {
              overlap = BooleanOperationsUtils.ExecuteBooleanOperation(
                result[firstIndex],
                result[secondIndex],
                BooleanOperationsType.Intersect);
            }
            catch
            {
              continue;
            }

            if (overlap == null || overlap.Volume <= minimumVolume)
              continue;

            try
            {
              Solid union = BooleanOperationsUtils.ExecuteBooleanOperation(
                result[firstIndex],
                result[secondIndex],
                BooleanOperationsType.Union);
              if (union == null || union.Volume <= minimumVolume)
              {
                warnings.Add($"{context}: overlapping solids could not be unioned.");
                continue;
              }

              result[firstIndex] = union;
              result.RemoveAt(secondIndex);
              changed = true;
              break;
            }
            catch (Exception ex)
            {
              warnings.Add($"{context}: {ex.Message}");
            }
          }

          if (changed)
            break;
        }
      }

      return result;
    }

    private static Solid CreateSolidFromFilledRegion(
      FilledRegion region,
      double bottomZ,
      double height)
    {
      IList<CurveLoop> loops = region.GetBoundaries();
      if (loops == null || loops.Count == 0)
        throw new InvalidOperationException($"FilledRegion has no boundaries: {region.Id.Value}");

      double sourceZ = loops[0].GetPlane().Origin.Z;
      Transform translation = Transform.CreateTranslation(new XYZ(0, 0, bottomZ - sourceZ));
      List<CurveLoop> transformedLoops = loops
        .Select(loop => CurveLoop.CreateViaTransform(loop, translation))
        .ToList();
      return GeometryCreationUtilities.CreateExtrusionGeometry(
        transformedLoops,
        XYZ.BasisZ,
        height);
    }

    private static double GetTopFaceArea(Solid solid)
    {
      double area = 0;
      foreach (Face face in solid.Faces)
      {
        if (face is PlanarFace planarFace
          && planarFace.FaceNormal.Normalize().DotProduct(XYZ.BasisZ) > 0.9)
        {
          area += planarFace.Area;
        }
      }

      return area;
    }

    private static Level FindLevel(Document doc, string levelName)
    {
      Level level = new FilteredElementCollector(doc)
        .OfClass(typeof(Level))
        .Cast<Level>()
        .FirstOrDefault(candidate => EqualsName(candidate.Name, levelName));

      if (level == null)
        throw new InvalidOperationException($"Level not found from selected sheet name: {levelName}");

      return level;
    }

    private static bool IsRegionForSheet(MtoRegionData region, NMKMTO_ModelSheetRow sheet)
    {
      if (string.IsNullOrWhiteSpace(sheet.ZoneName))
        return true;

      if (region.Comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase))
        return EqualsName(region.RincoZone, sheet.ZoneName);

      return EqualsName(region.Comments, sheet.ZoneName);
    }

    private static void ExportCsv(string path, List<DistributedRow> rows)
    {
      var builder = new StringBuilder();
      var headers = new[]
      {
        "No",
        "Sequence",
        "Distributed Top Area (m2)",
        "Distributed Bottom Area (m2)",
        "N16-1000 Area (m2)",
        "Floor Area (m2)",
        "Floor Volume (m3)"
      };
      builder.AppendLine(string.Join(",", headers.Select(EscapeCsv)));

      foreach (DistributedRow row in rows)
      {
        var values = new[]
        {
          row.No.ToString(CultureInfo.InvariantCulture),
          GetSequenceValue(row),
          FormatNumber(row.DistributedTopAreaM2),
          FormatNumber(row.DistributedBottomAreaM2),
          "0",
          "0",
          "0"
        };
        builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
      }

      File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static void ExportWarnings(string path, List<string> warnings)
    {
      var builder = new StringBuilder();
      builder.AppendLine("No,Warning");
      for (int index = 0; index < warnings.Count; index++)
      {
        builder.AppendLine(string.Join(",", new[]
        {
          (index + 1).ToString(CultureInfo.InvariantCulture),
          EscapeCsv(warnings[index])
        }));
      }

      File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static string BuildBaseExportFileName(
      Document doc,
      List<NMKMTO_ModelSheetRow> sheets)
    {
      string datePrefix = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
      string buildingName = GetStringParameter(doc.ProjectInformation, "Building Name");
      if (string.IsNullOrWhiteSpace(buildingName))
        buildingName = doc.ProjectInformation?.Name ?? string.Empty;
      if (string.IsNullOrWhiteSpace(buildingName))
        buildingName = doc.Title;
      if (string.IsNullOrWhiteSpace(buildingName))
        buildingName = "PROJECT INFORMATION";

      List<string> levels = sheets
        .Select(sheet => sheet.LevelName)
        .Where(level => !string.IsNullOrWhiteSpace(level))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      string levelName = levels.Count == 1
        ? levels[0]
        : levels.Count == 0
          ? "LEVEL"
          : "MULTI LEVEL";

      string fileName = $"{datePrefix}_MTO_{buildingName}_{levelName}_DISTRIBUTED_REO";
      foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        fileName = fileName.Replace(invalidCharacter, '_');
      return fileName.Trim();
    }

    private static string GetSequenceValue(DistributedRow row)
    {
      if (!string.IsNullOrWhiteSpace(row.Pour))
        return $"{row.Zone} - {row.Pour}";
      if (!string.IsNullOrWhiteSpace(row.Zone))
        return row.Zone;
      return "0";
    }

    private sealed class NaturalStringComparer : IComparer<string>
    {
      public static NaturalStringComparer Instance { get; } = new();
      public int Compare(string x, string y)
      {
        return StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
      }
    }

    private static string GetStringParameter(
      Element element,
      BuiltInParameter builtInParameter,
      string fallbackName)
    {
      string value = element.get_Parameter(builtInParameter)?.AsString() ?? string.Empty;
      return string.IsNullOrWhiteSpace(value)
        ? GetStringParameter(element, fallbackName)
        : value.Trim();
    }

    private static string GetStringParameter(Element element, string parameterName)
    {
      return element?.LookupParameter(parameterName)?.AsString()?.Trim() ?? string.Empty;
    }

    private static bool EqualsName(string first, string second)
    {
      return string.Equals(
        NormalizeName(first),
        NormalizeName(second),
        StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string value)
    {
      return (value ?? string.Empty).Trim();
    }

    private static string FormatNumber(double value)
    {
      return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        return value;
      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
