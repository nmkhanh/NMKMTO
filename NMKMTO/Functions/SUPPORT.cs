using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class SUPPORT
  {
    public static NMKMTO_ModelActionResult Execute(UIDocument uidoc, bool selectedOnly, int directionMode = 0)
    {
      #region 01 - Active document, view, and selected elements

      if (uidoc == null)
        throw new ArgumentNullException(nameof(uidoc));

      Document doc = uidoc.Document;
      Autodesk.Revit.DB.View view = doc.ActiveView;
      string directionLabel = directionMode == 2 ? "VERTICAL" : "HORIZONTAL";
      List<ElementId> sourceIds = selectedOnly
        ? uidoc.Selection.GetElementIds().ToList()
        : new FilteredElementCollector(doc, view.Id)
          .OfClass(typeof(FamilyInstance))
          .Cast<FamilyInstance>()
          .Where(instance =>
          {
            string name = instance.Symbol?.Family?.Name ?? string.Empty;
            return string.Equals(name, "Reo__ZBar[Rinco]", StringComparison.OrdinalIgnoreCase)
              || string.Equals(name, "Reo__Reinforcement_DistributionAdjustable[Rinco] 1", StringComparison.OrdinalIgnoreCase);
          })
          .Select(instance => instance.Id)
          .ToList();

      if (sourceIds.Count == 0)
        throw new InvalidOperationException("Please select at least one supported reo family.");

      const string zBarFamilyName = "Reo__ZBar[Rinco]";
      const string distributionFamilyName = "Reo__Reinforcement_DistributionAdjustable[Rinco] 1";
      const string reoGraphicStyleName = "Reo";

      FilledRegionType baseRegionType = new FilteredElementCollector(doc)
        .OfClass(typeof(FilledRegionType))
        .Cast<FilledRegionType>()
        .FirstOrDefault();
      if (baseRegionType == null)
        throw new InvalidOperationException("No FilledRegionType found in the document.");

      FilledRegionType regionType = null;
      int created = 0;
      var warnings = new List<string>();
      var arrowOnGroupMemberIds = new List<ElementId>();
      var arrowOffGroupMemberIds = new List<ElementId>();

      #endregion

      using (var transaction = new Transaction(doc, "SUPPORT selected reo fill"))
      {
        transaction.Start();

        #region 01A - Get or create grey FilledRegion type

        const string greyRegionTypeName = "SUPPORT GREY";
        regionType = new FilteredElementCollector(doc)
          .OfClass(typeof(FilledRegionType))
          .Cast<FilledRegionType>()
          .FirstOrDefault(type => string.Equals(type.Name, greyRegionTypeName, StringComparison.OrdinalIgnoreCase));

        if (regionType == null)
          regionType = baseRegionType.Duplicate(greyRegionTypeName) as FilledRegionType;

        if (regionType == null)
          throw new InvalidOperationException($"Could not create FilledRegionType '{greyRegionTypeName}'.");

        FillPatternElement solidPattern = new FilteredElementCollector(doc)
          .OfClass(typeof(FillPatternElement))
          .Cast<FillPatternElement>()
          .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);

        if (solidPattern != null)
          regionType.ForegroundPatternId = solidPattern.Id;

        regionType.ForegroundPatternColor = new Autodesk.Revit.DB.Color(160, 160, 160);
        regionType.IsMasking = false;

        #endregion

        foreach (ElementId selectedId in sourceIds)
        {
          Element element = doc.GetElement(selectedId);
          if (element is not FamilyInstance familyInstance)
          {
            warnings.Add($"ElementId {selectedId}: selected element is not a FamilyInstance.");
            continue;
          }

          #region 02 - Identify the two supported family groups and parameters

          string familyName = familyInstance.Symbol?.Family?.Name ?? string.Empty;
          bool isZBar = string.Equals(familyName, zBarFamilyName, StringComparison.OrdinalIgnoreCase);
          bool isDistribution = string.Equals(familyName, distributionFamilyName, StringComparison.OrdinalIgnoreCase);
          if (!isZBar && !isDistribution)
          {
            warnings.Add($"ElementId {selectedId}: unsupported family '{familyName}'.");
            continue;
          }

          bool isArrowVisible = true;
          if (!selectedOnly)
          {
            string arrowVisibilityParameterName = isZBar
              ? "Arrow & Dot Visibility"
              : "Arrow Visibility";
            Parameter arrowVisibilityParameter = element.LookupParameter(arrowVisibilityParameterName);
            if (arrowVisibilityParameter == null || arrowVisibilityParameter.StorageType != StorageType.Integer)
            {
              warnings.Add($"ElementId {selectedId}: missing Yes/No parameter '{arrowVisibilityParameterName}'.");
              continue;
            }

            isArrowVisible = arrowVisibilityParameter.AsInteger() == 1;
          }

          double moveDistance;
          double stretchDistance;
          double zTopBarLocation = 0;

          #region 02A - Normal reinforcement parameters

          if (isDistribution)
          {
            Parameter barFromArrowParameter = element.LookupParameter("Bar From Arrow 1");
            Parameter arrowLengthParameter = element.LookupParameter("Arrow 1 Length");
            if (barFromArrowParameter == null || barFromArrowParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"ElementId {selectedId}: missing length parameter 'Bar From Arrow 1'.");
              continue;
            }
            if (arrowLengthParameter == null || arrowLengthParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"ElementId {selectedId}: missing length parameter 'Arrow 1 Length'.");
              continue;
            }

            moveDistance = barFromArrowParameter.AsDouble();
            stretchDistance = arrowLengthParameter.AsDouble();
          }

          #endregion

          #region 02B - Z bar parameters

          else
          {
            Parameter arrowTopParameter = element.LookupParameter("Arrow Top");
            Parameter arrowBotParameter = element.LookupParameter("Arrow Bot");
            Parameter topBarLocationParameter = element.LookupParameter("Top Bar Location");
            if (arrowTopParameter == null || arrowTopParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"ElementId {selectedId}: missing length parameter 'Arrow Top'.");
              continue;
            }
            if (arrowBotParameter == null || arrowBotParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"ElementId {selectedId}: missing length parameter 'Arrow Bot'.");
              continue;
            }
            if (topBarLocationParameter == null || topBarLocationParameter.StorageType != StorageType.Double)
            {
              warnings.Add($"ElementId {selectedId}: missing length parameter 'Top Bar Location'.");
              continue;
            }

            moveDistance = arrowTopParameter.AsDouble();
            stretchDistance = arrowBotParameter.AsDouble();
            zTopBarLocation = topBarLocationParameter.AsDouble();
          }

          #endregion

          if (stretchDistance <= 1e-9)
          {
            warnings.Add($"ElementId {selectedId}: stretch distance must be greater than zero.");
            continue;
          }

          #endregion

          #region 03 - Read instance geometry and collect curves with GraphicStyle Reo

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
                GraphicsStyle directStyle = directCurve.GraphicsStyleId == ElementId.InvalidElementId
                  ? null
                  : doc.GetElement(directCurve.GraphicsStyleId) as GraphicsStyle;
                if (directStyle != null
                  && (string.Equals(directStyle.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(directStyle.GraphicsStyleCategory?.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)))
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

                GraphicsStyle instanceStyle = instanceCurve.GraphicsStyleId == ElementId.InvalidElementId
                  ? null
                  : doc.GetElement(instanceCurve.GraphicsStyleId) as GraphicsStyle;
                if (instanceStyle != null
                  && (string.Equals(instanceStyle.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(instanceStyle.GraphicsStyleCategory?.Name, reoGraphicStyleName, StringComparison.OrdinalIgnoreCase)))
                {
                  reoCurves.Add(instanceCurve);
                }
              }
            }
          }

          if (reoCurves.Count == 0)
          {
            warnings.Add($"ElementId {selectedId}: no curve with GraphicStyle '{reoGraphicStyleName}'.");
            continue;
          }

          #endregion

          #region 04 - Get the director of the main Reo curve

          XYZ viewOrigin = view.Origin;
          XYZ viewNormal = view.ViewDirection.Normalize();
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
            warnings.Add($"ElementId {selectedId}: could not determine the main Reo director.");
            continue;
          }

          #region 04A - Reverse normal reinforcement director when Splice is off

          if (isDistribution)
          {
            Parameter spliceParameter = element.LookupParameter("Splice");
            if (spliceParameter == null || spliceParameter.StorageType != StorageType.Integer)
            {
              warnings.Add($"ElementId {selectedId}: missing Yes/No parameter 'Splice'.");
              continue;
            }

            if (spliceParameter.AsInteger() == 0)
              reoDirection = reoDirection.Negate();
          }

          #endregion

          if (!selectedOnly)
          {
            XYZ horizontalViewDirection = view.UpDirection.CrossProduct(view.ViewDirection).Normalize();
            XYZ verticalViewDirection = view.UpDirection.Normalize();
            double horizontalAlignment = Math.Abs(reoDirection.DotProduct(horizontalViewDirection));
            double verticalAlignment = Math.Abs(reoDirection.DotProduct(verticalViewDirection));
            double directionTolerance = Math.Cos(Math.PI / 12.0);
            bool matchesDirection = directionMode == 2
              ? verticalAlignment >= directionTolerance
              : horizontalAlignment >= directionTolerance;
            if (!matchesDirection)
              continue;
          }

          #endregion

          #region 05 - Create clockwise and counterclockwise perpendicular directions

          XYZ viewRight = view.UpDirection.CrossProduct(view.ViewDirection).Normalize();
          XYZ viewUp = view.UpDirection.Normalize();
          double rightComponent = reoDirection.DotProduct(viewRight);
          double upComponent = reoDirection.DotProduct(viewUp);

          XYZ counterclockwiseDirection = (viewUp * rightComponent - viewRight * upComponent).Normalize();
          XYZ clockwiseDirection = counterclockwiseDirection.Negate();

          XYZ moveDirection;
          XYZ stretchDirection;

          #region 05A - Normal reinforcement directions

          if (isDistribution)
          {
            moveDirection = clockwiseDirection;
            stretchDirection = counterclockwiseDirection;
          }

          #endregion

          #region 05B - Z bar directions

          else
          {
            moveDirection = counterclockwiseDirection;
            stretchDirection = clockwiseDirection;
          }

          #endregion

          if (familyInstance.Mirrored)
          {
            moveDirection = moveDirection.Negate();
            stretchDirection = stretchDirection.Negate();
          }

          #endregion

          #region 06 - Move from LocationPoint to the fill start point

          if (element.Location is not LocationPoint locationPoint)
          {
            warnings.Add($"ElementId {selectedId}: element has no LocationPoint.");
            continue;
          }

          XYZ rawLocation = locationPoint.Point;
          XYZ locationOnView = rawLocation - viewNormal * (rawLocation - viewOrigin).DotProduct(viewNormal);
          XYZ fillLocation = locationOnView;

          #region 06A - Normal reinforcement location

          if (isDistribution)
          {
            fillLocation = locationOnView;
          }

          #endregion

          #region 06B - Z bar location includes Top Bar Location

          else
          {
            fillLocation = locationOnView + moveDirection * zTopBarLocation;
          }

          #endregion

          XYZ fillStartPoint = fillLocation + moveDirection * moveDistance;

          #endregion

          #region 06C - Draw Reo direction and move direction arrows

          if (selectedOnly)
          {
            double debugArrowLength = UnitUtils.ConvertToInternalUnits(800, UnitTypeId.Millimeters);
            double debugHeadLength = UnitUtils.ConvertToInternalUnits(120, UnitTypeId.Millimeters);
            double debugHeadWidth = debugHeadLength * 0.55;

            var debugArrows = new[]
            {
              new { Direction = reoDirection, Color = new Autodesk.Revit.DB.Color(0, 0, 255) },
              new { Direction = moveDirection, Color = new Autodesk.Revit.DB.Color(0, 180, 0) }
            };

            foreach (var debugArrow in debugArrows)
            {
              XYZ arrowDirection = debugArrow.Direction.Normalize();
              XYZ arrowEnd = fillLocation + arrowDirection * debugArrowLength;

              double arrowRightComponent = arrowDirection.DotProduct(viewRight);
              double arrowUpComponent = arrowDirection.DotProduct(viewUp);
              XYZ arrowSide = (viewUp * arrowRightComponent - viewRight * arrowUpComponent).Normalize();
              XYZ arrowHeadBase = arrowEnd - arrowDirection * debugHeadLength;

              var arrowLines = new[]
              {
                Line.CreateBound(fillLocation, arrowEnd),
                Line.CreateBound(arrowEnd, arrowHeadBase + arrowSide * debugHeadWidth),
                Line.CreateBound(arrowEnd, arrowHeadBase - arrowSide * debugHeadWidth)
              };

              foreach (Line arrowLine in arrowLines)
              {
                DetailCurve detailCurve = doc.Create.NewDetailCurve(view, arrowLine);
                var overrides = new OverrideGraphicSettings();
                overrides.SetProjectionLineColor(debugArrow.Color);
                overrides.SetProjectionLineWeight(6);
                view.SetElementOverrides(detailCurve.Id, overrides);
              }
            }
          }

          #endregion

          #region 07 - Get the Reo width and stretch the fill from the start point

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

          #endregion

          #region 08 - Create the rectangular FilledRegion

          var boundary = new CurveLoop();
          boundary.Append(Line.CreateBound(firstStart, secondStart));
          boundary.Append(Line.CreateBound(secondStart, secondEnd));
          boundary.Append(Line.CreateBound(secondEnd, firstEnd));
          boundary.Append(Line.CreateBound(firstEnd, firstStart));

          FilledRegion filledRegion = FilledRegion.Create(
            doc,
            regionType.Id,
            view.Id,
            new List<CurveLoop> { boundary });

          Parameter comments = filledRegion.LookupParameter("Comments");
          if (comments != null && !comments.IsReadOnly)
          {
            comments.Set(
              $"SUPPORT {(selectedOnly ? "SELECTED" : directionLabel)} | Family: {familyName} | Source ElementId: {selectedId}");
          }

          if (!selectedOnly)
          {
            List<ElementId> groupMemberIds = isArrowVisible
              ? arrowOnGroupMemberIds
              : arrowOffGroupMemberIds;
            groupMemberIds.Add(element.Id);
            groupMemberIds.Add(filledRegion.Id);
          }
          created++;

          #endregion
        }

        if (!selectedOnly && arrowOnGroupMemberIds.Count > 1)
        {
          Group arrowOnGroup = doc.Create.NewGroup(arrowOnGroupMemberIds.Distinct().ToList());
          arrowOnGroup.GroupType.Name = $"SUPPORT {directionLabel} ARROW ON - {view.Name} - {DateTime.Now:HHmmss}";
        }

        if (!selectedOnly && arrowOffGroupMemberIds.Count > 1)
        {
          Group arrowOffGroup = doc.Create.NewGroup(arrowOffGroupMemberIds.Distinct().ToList());
          arrowOffGroup.GroupType.Name = $"SUPPORT {directionLabel} ARROW OFF - {view.Name} - {DateTime.Now:HHmmss}";
        }

        transaction.Commit();
      }

      #region 09 - Return result

      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = created,
        Message = selectedOnly
          ? $"SUPPORT selected completed\nSelected: {sourceIds.Count}\nFilledRegion created: {created}"
          : $"SUPPORT active view completed\nView: {view.Name}\nDirection: {directionLabel}\nFilledRegion created: {created}\nArrow ON pairs: {arrowOnGroupMemberIds.Count / 2}\nArrow OFF pairs: {arrowOffGroupMemberIds.Count / 2}"
      };
      foreach (string warning in warnings)
        result.Warnings.Add(warning);
      if (warnings.Count > 0)
        result.Message += $"\nWarnings: {warnings.Count}";

      return result;

      #endregion
    }
  }
}
