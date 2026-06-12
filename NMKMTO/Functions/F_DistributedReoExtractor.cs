using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_DistributedReoExtractor
  {
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

      const string zBarFamilyName = "Reo__ZBar[Rinco]";
      const string distributionFamilyName = "Reo__Reinforcement_DistributionAdjustable[Rinco] 1";
      const string reoGraphicStyleName = "Reo";
      const string directShapeApplicationId = "NMKMTO_DISTRIBUTED_REO_MTO";
      const double ft2ToM2 = 0.09290304;
      double thickness = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
      double minimumVolume = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.CubicMillimeters);
      double boundingTolerance = UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Millimeters);

      var warnings = new List<string>();
      var sheets = selectedSheets?.ToList() ?? new List<NMKMTO_ModelSheetRow>();
      if (sheets.Count == 0)
        throw new InvalidOperationException("Please select at least one sheet.");

      #region 01 - Collect unique OVER views from selected sheets

      var views = sheets
        .Select(sheet => doc.GetElement(sheet.SheetId) as ViewSheet)
        .Where(sheet => sheet != null)
        .Cast<ViewSheet>()
        .SelectMany(sheet => sheet.GetAllPlacedViews())
        .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
        .Where(view =>
          view != null
          && !view.IsTemplate
          && view.Name.IndexOf(F_MtoViewNames.OverViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
        .Cast<Autodesk.Revit.DB.View>()
        .GroupBy(view => view.Id)
        .Select(group => group.First())
        .ToList();

      if (views.Count == 0)
        throw new InvalidOperationException("Selected sheets do not contain any OVER view.");

      #endregion

      #region 02 - Read the two supported families and create independent CurveLoops

      var topSources = new List<(Solid Solid, XYZ Min, XYZ Max, ElementId SourceId, string ViewName)>();
      var bottomSources = new List<(Solid Solid, XYZ Min, XYZ Max, ElementId SourceId, string ViewName)>();
      int curveLoopCount = 0;

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
            return string.Equals(name, zBarFamilyName, StringComparison.OrdinalIgnoreCase)
              || string.Equals(name, distributionFamilyName, StringComparison.OrdinalIgnoreCase);
          })
          .ToList();

        foreach (FamilyInstance element in elements)
        {
          string familyName = element.Symbol?.Family?.Name ?? string.Empty;
          bool isZBar = string.Equals(familyName, zBarFamilyName, StringComparison.OrdinalIgnoreCase);
          bool isDistribution = string.Equals(familyName, distributionFamilyName, StringComparison.OrdinalIgnoreCase);

          Parameter topParameter = element.LookupParameter("Top");
          Parameter bottomParameter = element.LookupParameter("Bottom");
          bool isTop = topParameter != null
            && topParameter.StorageType == StorageType.Integer
            && topParameter.AsInteger() == 1;
          bool isBottom = bottomParameter != null
            && bottomParameter.StorageType == StorageType.Integer
            && bottomParameter.AsInteger() == 1;
          if (!isTop && !isBottom)
          {
            warnings.Add($"View '{view.Name}', ElementId {element.Id}: both 'Top' and 'Bottom' are off.");
            continue;
          }

          double moveDistance;
          double stretchDistance;
          double zTopBarLocation = 0;

          if (isDistribution)
          {
            Parameter moveParameter = element.LookupParameter("Bar From Arrow 1");
            Parameter stretchParameter = element.LookupParameter("Arrow 1 Length");
            if (moveParameter == null || moveParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: missing length parameter 'Bar From Arrow 1'.");
              continue;
            }
            if (stretchParameter == null || stretchParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: missing length parameter 'Arrow 1 Length'.");
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
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: missing length parameter 'Arrow Top'.");
              continue;
            }
            if (stretchParameter == null || stretchParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: missing length parameter 'Arrow Bot'.");
              continue;
            }
            if (topBarLocationParameter == null || topBarLocationParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: missing length parameter 'Top Bar Location'.");
              continue;
            }

            moveDistance = moveParameter.AsDouble();
            stretchDistance = stretchParameter.AsDouble();
            zTopBarLocation = topBarLocationParameter.AsDouble();
          }

          if (stretchDistance <= 1e-9)
          {
            warnings.Add($"View '{view.Name}', ElementId {element.Id}: stretch distance must be greater than zero.");
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
              if (geometryObject is Curve directCurve)
              {
                GraphicsStyle style = directCurve.GraphicsStyleId == ElementId.InvalidElementId
                  ? null
                  : doc.GetElement(directCurve.GraphicsStyleId) as GraphicsStyle;
                if (style != null
                  && (string.Equals(style.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(style.GraphicsStyleCategory?.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)))
                {
                  reoCurves.Add(directCurve);
                }
              }

              if (geometryObject is not GeometryInstance geometryInstance)
                continue;

              GeometryElement instanceGeometry = geometryInstance.GetInstanceGeometry();
              if (instanceGeometry == null)
                continue;

              foreach (GeometryObject instanceObject in instanceGeometry)
              {
                if (instanceObject is not Curve instanceCurve)
                  continue;

                GraphicsStyle style = instanceCurve.GraphicsStyleId == ElementId.InvalidElementId
                  ? null
                  : doc.GetElement(instanceCurve.GraphicsStyleId) as GraphicsStyle;
                if (style != null
                  && (string.Equals(style.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(style.GraphicsStyleCategory?.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)))
                {
                  reoCurves.Add(instanceCurve);
                }
              }
            }
          }

          if (reoCurves.Count == 0)
          {
            warnings.Add($"View '{view.Name}', ElementId {element.Id}: no curve with GraphicStyle '{reoGraphicStyleName}'.");
            continue;
          }

          XYZ reoDirection = null;
          double longestSegment = 0;
          var reoPoints = new List<XYZ>();

          foreach (Curve reoCurve in reoCurves)
          {
            IList<XYZ> tessellatedPoints = reoCurve.Tessellate();
            foreach (XYZ point in tessellatedPoints)
            {
              double distanceToViewPlane = (point - viewOrigin).DotProduct(viewNormal);
              reoPoints.Add(point - viewNormal * distanceToViewPlane);
            }

            for (int index = 0; index < tessellatedPoints.Count - 1; index++)
            {
              XYZ rawStart = tessellatedPoints[index];
              XYZ rawEnd = tessellatedPoints[index + 1];
              XYZ start = rawStart - viewNormal * (rawStart - viewOrigin).DotProduct(viewNormal);
              XYZ end = rawEnd - viewNormal * (rawEnd - viewOrigin).DotProduct(viewNormal);
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
            warnings.Add($"View '{view.Name}', ElementId {element.Id}: could not determine the main Reo director.");
            continue;
          }

          if (isDistribution)
          {
            Parameter spliceParameter = element.LookupParameter("Splice");
            if (spliceParameter == null || spliceParameter.StorageType != StorageType.Integer)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: missing Yes/No parameter 'Splice'.");
              continue;
            }

            if (spliceParameter.AsInteger() == 0)
              reoDirection = reoDirection.Negate();
          }

          double rightComponent = reoDirection.DotProduct(viewRight);
          double upComponent = reoDirection.DotProduct(viewUp);
          XYZ counterclockwiseDirection = (viewUp * rightComponent - viewRight * upComponent).Normalize();
          XYZ clockwiseDirection = counterclockwiseDirection.Negate();
          XYZ moveDirection = isDistribution ? clockwiseDirection : counterclockwiseDirection;
          XYZ stretchDirection = isDistribution ? counterclockwiseDirection : clockwiseDirection;

          if (element.Mirrored)
          {
            moveDirection = moveDirection.Negate();
            stretchDirection = stretchDirection.Negate();
          }

          if (element.Location is not LocationPoint locationPoint)
          {
            warnings.Add($"View '{view.Name}', ElementId {element.Id}: element has no LocationPoint.");
            continue;
          }

          XYZ rawLocation = locationPoint.Point;
          XYZ locationOnView = rawLocation - viewNormal * (rawLocation - viewOrigin).DotProduct(viewNormal);
          XYZ fillLocation = isZBar
            ? locationOnView + moveDirection * zTopBarLocation
            : locationOnView;
          XYZ fillStartPoint = fillLocation + moveDirection * moveDistance;

          double minAlongReo = double.MaxValue;
          double maxAlongReo = double.MinValue;
          foreach (XYZ reoPoint in reoPoints)
          {
            double distanceAlongReo = (reoPoint - locationOnView).DotProduct(reoDirection);
            minAlongReo = Math.Min(minAlongReo, distanceAlongReo);
            maxAlongReo = Math.Max(maxAlongReo, distanceAlongReo);
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

          var boundary = new CurveLoop();
          boundary.Append(Line.CreateBound(firstStart, secondStart));
          boundary.Append(Line.CreateBound(secondStart, secondEnd));
          boundary.Append(Line.CreateBound(secondEnd, firstEnd));
          boundary.Append(Line.CreateBound(firstEnd, firstStart));

          try
          {
            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
              new List<CurveLoop> { boundary },
              viewNormal,
              thickness);
            if (solid == null || solid.Volume <= minimumVolume)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id}: generated solid is empty.");
              continue;
            }

            var boundingPoints = new[]
            {
              firstStart,
              secondStart,
              firstEnd,
              secondEnd,
              firstStart + viewNormal * thickness,
              secondStart + viewNormal * thickness,
              firstEnd + viewNormal * thickness,
              secondEnd + viewNormal * thickness
            };
            XYZ minimum = new XYZ(
              boundingPoints.Min(point => point.X),
              boundingPoints.Min(point => point.Y),
              boundingPoints.Min(point => point.Z));
            XYZ maximum = new XYZ(
              boundingPoints.Max(point => point.X),
              boundingPoints.Max(point => point.Y),
              boundingPoints.Max(point => point.Z));

            var source = (solid, minimum, maximum, element.Id, view.Name);
            if (isTop)
              topSources.Add(source);
            if (isBottom)
              bottomSources.Add(source);
            curveLoopCount++;
          }
          catch (Exception ex)
          {
            warnings.Add($"View '{view.Name}', ElementId {element.Id}: create 10 mm solid failed: {ex.Message}");
          }
        }
      }

      if (topSources.Count == 0 && bottomSources.Count == 0)
        throw new InvalidOperationException("No valid DISTRIBUTED REO CurveLoop could be calculated.");

      #endregion

      #region 03 - Difference each solid against all overlapping solids in the same Top or Bottom group

      var topRemainingSolids = new List<Solid>();
      var bottomRemainingSolids = new List<Solid>();
      double topAreaFt2 = 0;
      double bottomAreaFt2 = 0;
      var sourceGroups = new[] { topSources, bottomSources };
      var remainingGroups = new[] { topRemainingSolids, bottomRemainingSolids };
      var areaTotals = new double[2];
      var layerNames = new[] { "TOP", "BOTTOM" };

      for (int layerIndex = 0; layerIndex < sourceGroups.Length; layerIndex++)
      {
        var sourceSolids = sourceGroups[layerIndex];
        var remainingSolids = remainingGroups[layerIndex];

        for (int sourceIndex = 0; sourceIndex < sourceSolids.Count; sourceIndex++)
        {
          var source = sourceSolids[sourceIndex];
          Solid remaining = source.Solid;

          for (int otherIndex = 0; otherIndex < sourceSolids.Count; otherIndex++)
          {
            if (sourceIndex == otherIndex || remaining == null || remaining.Volume <= minimumVolume)
              continue;

            var other = sourceSolids[otherIndex];
            bool boundingBoxesOverlap =
              source.Min.X <= other.Max.X + boundingTolerance
              && source.Max.X + boundingTolerance >= other.Min.X
              && source.Min.Y <= other.Max.Y + boundingTolerance
              && source.Max.Y + boundingTolerance >= other.Min.Y
              && source.Min.Z <= other.Max.Z + boundingTolerance
              && source.Max.Z + boundingTolerance >= other.Min.Z;
            if (!boundingBoxesOverlap)
              continue;

            try
            {
              Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                remaining,
                other.Solid,
                BooleanOperationsType.Intersect);
              if (intersection == null || intersection.Volume <= minimumVolume)
                continue;

              remaining = BooleanOperationsUtils.ExecuteBooleanOperation(
                remaining,
                other.Solid,
                BooleanOperationsType.Difference);
            }
            catch (Exception ex)
            {
              warnings.Add(
                $"{layerNames[layerIndex]} ElementId {source.SourceId} against ElementId {other.SourceId}: {ex.Message}");
            }
          }

          if (remaining == null || remaining.Volume <= minimumVolume)
            continue;

          remainingSolids.Add(remaining);
          areaTotals[layerIndex] += remaining.Volume / thickness;
        }
      }

      topAreaFt2 = areaTotals[0];
      bottomAreaFt2 = areaTotals[1];

      #endregion

      #region 04 - Optionally create DirectShape from the final non-intersecting solids

      if (options.Create3d)
      {
        using (var transaction = new Transaction(doc, "NMKMTO DISTRIBUTED REO DirectShape"))
        {
          transaction.Start();

          List<ElementId> oldShapeIds = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(shape => shape.ApplicationId == directShapeApplicationId)
            .Select(shape => shape.Id)
            .ToList();
          if (oldShapeIds.Count > 0)
            doc.Delete(oldShapeIds);

          var shapeGroups = new[]
          {
            new { Name = "TOP", Solids = topRemainingSolids, AreaM2 = topAreaFt2 * ft2ToM2 },
            new { Name = "BOTTOM", Solids = bottomRemainingSolids, AreaM2 = bottomAreaFt2 * ft2ToM2 }
          };

          foreach (var shapeGroup in shapeGroups)
          {
            if (shapeGroup.Solids.Count == 0)
              continue;

            DirectShape directShape = DirectShape.CreateElement(
              doc,
              new ElementId(BuiltInCategory.OST_GenericModel));
            directShape.ApplicationId = directShapeApplicationId;
            directShape.ApplicationDataId = shapeGroup.Name;
            directShape.Name = $"NMKMTO DISTRIBUTED REO {shapeGroup.Name}";
            directShape.SetShape(shapeGroup.Solids.Cast<GeometryObject>().ToList());

            Parameter comments = directShape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
              ?? directShape.LookupParameter("Comments");
            if (comments != null && !comments.IsReadOnly)
            {
              comments.Set(
                $"DISTRIBUTED REO {shapeGroup.Name} | Non-intersecting area: {shapeGroup.AreaM2:0.###} m2 | Thickness: 10 mm");
            }
          }

          transaction.Commit();
        }
      }

      #endregion

      #region 05 - Export aggregate Top and Bottom areas

      double topAreaM2 = topAreaFt2 * ft2ToM2;
      double bottomAreaM2 = bottomAreaFt2 * ft2ToM2;
      string datePrefix = DateTime.Now.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
      string exportPath = Path.Combine(options.ExportFolder, $"{datePrefix}_MTO_DISTRIBUTED_REO.csv");
      var csv = new StringBuilder();
      csv.AppendLine("Sheet Count,OVER View Count,CurveLoop Count,Top Source Count,Bottom Source Count,Distributed Top Area (m2),Distributed Bottom Area (m2)");
      csv.AppendLine(string.Join(",", new[]
      {
        sheets.Count.ToString(CultureInfo.InvariantCulture),
        views.Count.ToString(CultureInfo.InvariantCulture),
        curveLoopCount.ToString(CultureInfo.InvariantCulture),
        topSources.Count.ToString(CultureInfo.InvariantCulture),
        bottomSources.Count.ToString(CultureInfo.InvariantCulture),
        topAreaM2.ToString("0.###", CultureInfo.InvariantCulture),
        bottomAreaM2.ToString("0.###", CultureInfo.InvariantCulture)
      }));
      File.WriteAllText(exportPath, csv.ToString(), Encoding.UTF8);

      string warningPath = string.Empty;
      if (warnings.Count > 0)
      {
        warningPath = Path.Combine(options.ExportFolder, $"{datePrefix}_MTO_DISTRIBUTED_REO_WARNING.csv");
        var warningCsv = new StringBuilder();
        warningCsv.AppendLine("No,Warning");
        for (int index = 0; index < warnings.Count; index++)
        {
          string warning = warnings[index] ?? string.Empty;
          if (warning.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            warning = "\"" + warning.Replace("\"", "\"\"") + "\"";
          warningCsv.AppendLine($"{index + 1},{warning}");
        }
        File.WriteAllText(warningPath, warningCsv.ToString(), Encoding.UTF8);
      }

      #endregion

      #region 06 - Return MTO result

      var result = new NMKMTO_ModelDistributedReoResult
      {
        SheetCount = sheets.Count,
        DistributedTopAreaM2 = topAreaM2,
        DistributedBottomAreaM2 = bottomAreaM2,
        ExportPath = exportPath,
        WarningPath = warningPath,
        Message =
          $"DISTRIBUTED REO exported: {exportPath}\n" +
          $"OVER views: {views.Count}\n" +
          $"CurveLoops: {curveLoopCount}\n" +
          $"Top: {topAreaM2:0.###} m2\n" +
          $"Bottom: {bottomAreaM2:0.###} m2" +
          (warnings.Count > 0 ? $"\nWarnings: {warnings.Count}\nWarning file: {warningPath}" : string.Empty)
      };
      foreach (string warning in warnings)
        result.Warnings.Add(warning);

      return result;

      #endregion
    }
  }
}
