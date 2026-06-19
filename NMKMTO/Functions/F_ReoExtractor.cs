using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_ReoExtractor
  {
    private sealed class RegionData
    {
      public FilledRegion Region { get; set; }
      public string Pour { get; set; } = string.Empty;
      public string Zone { get; set; } = string.Empty;
    }

    private sealed class ReoRow
    {
      public int No { get; set; }
      public long ElementId { get; set; }
      public string Pour { get; set; } = string.Empty;
      public string Mark { get; set; } = string.Empty;
      public string Tags { get; set; } = string.Empty;
      public string Type { get; set; } = string.Empty;
      public double Measure { get; set; }
      public double Reolen { get; set; }
      public double Spacing { get; set; }
      public double Thickness { get; set; }
      public double Diameter { get; set; }
      public double CogCount { get; set; }
      public string Splice { get; set; } = string.Empty;
      public double BarCount { get; set; }
      public string SheetNumber { get; set; } = string.Empty;
      public string Level { get; set; } = string.Empty;
      public string Zone { get; set; } = string.Empty;
      public string Layer { get; set; } = string.Empty;
      public List<Solid> Solids { get; } = new();
    }

    public static NMKMTO_ModelActionResult Execute(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      NMKMTO_ModelModelDataOptions options,
      bool writeDataCsv = true)
    {
      #region 01 - Validate input and define the REO export contract

      if (doc == null)
        throw new ArgumentNullException(nameof(doc));
      if (options == null)
        throw new ArgumentNullException(nameof(options));
      if (string.IsNullOrWhiteSpace(options.ExportFolder))
        throw new InvalidOperationException("Export folder is empty.");

      Directory.CreateDirectory(options.ExportFolder);
      var sheets = selectedSheets?.ToList() ?? new List<NMKMTO_ModelSheetRow>();
      if (sheets.Count == 0)
        throw new InvalidOperationException("Please select at least one sheet.");

      const string zBarFamilyName = "Reo__ZBar[Rinco]";
      const string distributionFamilyName = "Reo__Reinforcement_DistributionAdjustable[Rinco] 1";
      const string reoGraphicStyleName = "Reo";
      const string directShapeApplicationId = "NMKMTO_REO_MTO";
      double minimumVolume = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.CubicMillimeters);
      var warnings = new List<string>();
      var rows = new List<ReoRow>();

      #endregion

      #region 02 - Detect POUR, ZONE, or LEVEL structure from the common MTO area view

      Autodesk.Revit.DB.View mtoView = new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(view =>
          !view.IsTemplate
          && view.Name.Equals(options.FilledRegionViewName, StringComparison.OrdinalIgnoreCase));
      if (mtoView == null)
        throw new InvalidOperationException($"Required view not found: {options.FilledRegionViewName}.");

      var regions = new List<RegionData>();
      foreach (FilledRegion region in new FilteredElementCollector(doc, mtoView.Id)
        .OfClass(typeof(FilledRegion))
        .Cast<FilledRegion>())
      {
        string comments = region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
          ?? region.LookupParameter("Comments")?.AsString()
          ?? string.Empty;
        comments = comments.Trim();
        if (string.IsNullOrWhiteSpace(comments))
          continue;

        regions.Add(new RegionData
        {
          Region = region,
          Pour = comments,
          Zone = region.LookupParameter("RINCO_ZONE")?.AsString()?.Trim() ?? string.Empty
        });
      }

      bool projectUsesPour = regions.Any(region =>
        region.Pour.StartsWith("POUR", StringComparison.OrdinalIgnoreCase));
      bool projectUsesZone = !projectUsesPour && regions.Any(region =>
        region.Pour.StartsWith("ZONE", StringComparison.OrdinalIgnoreCase));

      #endregion

      foreach (NMKMTO_ModelSheetRow sheetRow in sheets)
      {
        #region 03 - Identify TOP, BOTTOM, or SHEAR from SheetName and collect OVER views

        string sheetName = sheetRow.SheetName ?? string.Empty;
        string layer = sheetName.IndexOf("SHEAR", StringComparison.OrdinalIgnoreCase) >= 0
          ? "SHEAR"
          : sheetName.IndexOf("BOTTOM", StringComparison.OrdinalIgnoreCase) >= 0
            ? "BOTTOM"
            : sheetName.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0
              ? "TOP"
              : string.Empty;

        if (string.IsNullOrWhiteSpace(layer))
        {
          warnings.Add($"Sheet '{sheetRow.SheetNumber} - {sheetRow.SheetName}' is not TOP, BOTTOM, or SHEAR.");
          continue;
        }

        ViewSheet sheet = doc.GetElement(sheetRow.SheetId) as ViewSheet;
        if (sheet == null)
        {
          warnings.Add($"Sheet not found: {sheetRow.SheetNumber} - {sheetRow.SheetName}.");
          continue;
        }

        List<Autodesk.Revit.DB.View> overViews = sheet.GetAllPlacedViews()
          .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
          .Where(view =>
            view != null
            && !view.IsTemplate
            && view.Name.IndexOf(F_MtoViewNames.OverViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
          .Cast<Autodesk.Revit.DB.View>()
          .GroupBy(view => view.Id)
          .Select(group => group.First())
          .ToList();

        if (overViews.Count == 0)
        {
          warnings.Add($"Sheet '{sheetRow.SheetNumber} - {sheetRow.SheetName}' has no OVER view.");
          continue;
        }

        Level level = new FilteredElementCollector(doc)
          .OfClass(typeof(Level))
          .Cast<Level>()
          .FirstOrDefault(candidate => string.Equals(
            candidate.Name?.Trim(),
            sheetRow.LevelName?.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (level == null)
        {
          warnings.Add($"Level not found for sheet '{sheetRow.SheetNumber}': {sheetRow.LevelName}.");
          continue;
        }

        double reoElevation = layer == "BOTTOM"
          ? level.ProjectElevation - UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters)
          : level.ProjectElevation;

        List<RegionData> sheetRegions = regions
          .Where(region =>
            projectUsesPour
            && region.Pour.StartsWith("POUR", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(sheetRow.ZoneName)
              || string.Equals(region.Zone, sheetRow.ZoneName, StringComparison.OrdinalIgnoreCase)))
          .ToList();

        #endregion

        foreach (Autodesk.Revit.DB.View view in overViews)
        {
          #region 04 - Index IndependentTag.TagText by tagged REO ElementId

          var tagTextsByElementId = new Dictionary<ElementId, List<string>>();
          foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>())
          {
            string tagText;
            try
            {
              tagText = Regex.Replace(
                tag.TagText ?? string.Empty,
                @"\d+(?:\.\d+)",
                match => double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                  ? Math.Round(number, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                  : match.Value).Trim();
            }
            catch
            {
              continue;
            }

            if (string.IsNullOrWhiteSpace(tagText))
              continue;

            ICollection<ElementId> taggedIds;
            try
            {
              taggedIds = tag.GetTaggedLocalElementIds();
            }
            catch
            {
              continue;
            }

            foreach (ElementId taggedId in taggedIds)
            {
              if (!tagTextsByElementId.TryGetValue(taggedId, out List<string> texts))
              {
                texts = new List<string>();
                tagTextsByElementId.Add(taggedId, texts);
              }

              if (!texts.Any(text => string.Equals(text, tagText, StringComparison.Ordinal)))
                texts.Add(tagText);
            }
          }

          #endregion

          #region 05 - Collect the two supported REO families once per sheet

          var processedIds = new HashSet<ElementId>(rows
            .Where(row => string.Equals(row.SheetNumber, sheetRow.SheetNumber, StringComparison.OrdinalIgnoreCase))
            .Select(row => new ElementId(row.ElementId)));

          List<FamilyInstance> elements = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(element =>
            {
              string familyName = element.Symbol?.Family?.Name ?? string.Empty;
              return string.Equals(familyName, zBarFamilyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(familyName, distributionFamilyName, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

          foreach (FamilyInstance element in elements)
          {
            if (!processedIds.Add(element.Id))
              continue;

            bool isZBar = string.Equals(
              element.Symbol?.Family?.Name,
              zBarFamilyName,
              StringComparison.OrdinalIgnoreCase);

            #endregion

            #region 06 - Read all curves whose GraphicStyle is Reo

            var reoCurves = new List<Curve>();
            GeometryElement geometry = element.get_Geometry(new Options
            {
              View = view,
              IncludeNonVisibleObjects = true
            });

            if (geometry != null)
            {
              foreach (GeometryObject geometryObject in geometry)
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
              warnings.Add($"View '{view.Name}', ElementId {element.Id.Value}: no GraphicStyle 'Reo' curve.");
              continue;
            }

            #endregion

            #region 07 - Read CSV values from instance parameters only

            string mark = element.LookupParameter("Mark")?.AsString()?.Trim() ?? string.Empty;
            string comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
              ?? element.LookupParameter("Comments")?.AsString()
              ?? string.Empty;
            string notation = element.LookupParameter("Notation")?.AsString()?.Trim() ?? string.Empty;

            string tags = tagTextsByElementId.TryGetValue(element.Id, out List<string> elementTagTexts)
              ? string.Join("\n", elementTagTexts)
              : layer == "SHEAR"
                ? notation
                : comments.Trim();
            tags = Regex.Replace(
              tags ?? string.Empty,
              @"\d+(?:\.\d+)",
              match => double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? Math.Round(number, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                : match.Value);

            double measure = 0;
            double reolen = 0;
            double spacing = 0;
            double thickness = 0;
            double diameter = 0;
            double cogCount = 0;
            double barCount = 0;

            Parameter measureParameter = element.LookupParameter("Shared_Measure");
            Parameter reolenParameter = element.LookupParameter("Shared_Length");
            Parameter spacingParameter = element.LookupParameter("Shared_Spacing");
            Parameter thicknessParameter = element.LookupParameter("Thickness");
            Parameter diameterParameter = element.LookupParameter("Shared_Diameter");
            Parameter cogCountParameter = element.LookupParameter("Shared_Cog_Count");
            Parameter barCountParameter = element.LookupParameter("Shared_No. Bars");
            Parameter spliceParameter = element.LookupParameter("Splice");

            if (measureParameter?.StorageType == StorageType.Double)
              measure = UnitUtils.ConvertFromInternalUnits(measureParameter.AsDouble(), UnitTypeId.Millimeters);
            if (reolenParameter?.StorageType == StorageType.Double)
              reolen = UnitUtils.ConvertFromInternalUnits(reolenParameter.AsDouble(), UnitTypeId.Millimeters);
            if (spacingParameter?.StorageType == StorageType.Double)
              spacing = UnitUtils.ConvertFromInternalUnits(spacingParameter.AsDouble(), UnitTypeId.Millimeters);
            if (thicknessParameter?.StorageType == StorageType.Double)
              thickness = UnitUtils.ConvertFromInternalUnits(thicknessParameter.AsDouble(), UnitTypeId.Millimeters);
            if (diameterParameter?.StorageType == StorageType.Double)
              diameter = UnitUtils.ConvertFromInternalUnits(diameterParameter.AsDouble(), UnitTypeId.Millimeters);
            if (cogCountParameter?.StorageType == StorageType.Double)
              cogCount = UnitUtils.ConvertFromInternalUnits(cogCountParameter.AsDouble(), UnitTypeId.Millimeters);
            if (cogCountParameter?.StorageType == StorageType.Integer)
              cogCount = cogCountParameter.AsInteger();
            if (barCountParameter?.StorageType == StorageType.Integer)
              barCount = barCountParameter.AsInteger();
            if (barCountParameter?.StorageType == StorageType.Double)
              barCount = barCountParameter.AsDouble();

            measure = Math.Round(measure, 0, MidpointRounding.AwayFromZero);
            reolen = Math.Round(reolen, 0, MidpointRounding.AwayFromZero);
            spacing = Math.Round(spacing, 0, MidpointRounding.AwayFromZero);
            thickness = Math.Round(thickness, 0, MidpointRounding.AwayFromZero);
            diameter = Math.Round(diameter, 0, MidpointRounding.AwayFromZero);
            cogCount = Math.Round(cogCount, 0, MidpointRounding.AwayFromZero);
            barCount = Math.Round(barCount, 0, MidpointRounding.AwayFromZero);

            string splice = isZBar
              ? "No"
              : spliceParameter?.StorageType == StorageType.Integer
                ? spliceParameter.AsInteger() == 1 ? "Yes" : "No"
                : string.Empty;

            #endregion

            #region 08 - Classify Type with BEAM override taking highest priority

            OverrideGraphicSettings overrides = view.GetElementOverrides(element.Id);
            Autodesk.Revit.DB.Color overrideColor = overrides.ProjectionLineColor;
            bool isBeam = overrideColor.IsValid
              && overrideColor.Red == 0
              && overrideColor.Green == 128
              && overrideColor.Blue == 64;

            string type;
            if (isBeam)
              type = "BEAM";
            else if (mark.IndexOf("S C.J", StringComparison.OrdinalIgnoreCase) >= 0)
              type = "SURELOK";
            else if (mark.IndexOf("U C.J", StringComparison.OrdinalIgnoreCase) >= 0)
              type = "U BAR";
            else if (mark.IndexOf("C.J", StringComparison.OrdinalIgnoreCase) >= 0 && layer == "TOP")
              type = "CJ TOP";
            else if (mark.IndexOf("C.J", StringComparison.OrdinalIgnoreCase) >= 0 && layer == "BOTTOM")
              type = "CJ BOTTOM";
            else
              type = layer;

            #endregion

            #region 09 - Create round transient REO solids from every Reo curve

            var reoSolids = new List<Solid>();
            if (diameter <= 0)
            {
              warnings.Add($"View '{view.Name}', ElementId {element.Id.Value}: Shared_Diameter is zero.");
            }
            else
            {
              double radius = UnitUtils.ConvertToInternalUnits(diameter * 0.5, UnitTypeId.Millimeters);
              foreach (Curve reoCurve in reoCurves)
              {
                try
                {
                  XYZ sourceStart = reoCurve.GetEndPoint(0);
                  Transform toElevation = Transform.CreateTranslation(new XYZ(0, 0, reoElevation - sourceStart.Z));
                  Curve pathCurve = reoCurve.CreateTransformed(toElevation);
                  XYZ start = pathCurve.GetEndPoint(0);
                  double pathStartParameter = pathCurve.GetEndParameter(0);
                  XYZ tangent = pathCurve.ComputeDerivatives(pathStartParameter, false).BasisX.Normalize();
                  XYZ profileX = tangent.CrossProduct(XYZ.BasisZ);
                  if (profileX.GetLength() < 1e-9)
                    profileX = tangent.CrossProduct(XYZ.BasisX);
                  profileX = profileX.Normalize();
                  XYZ profileY = tangent.CrossProduct(profileX).Normalize();

                  var profile = new CurveLoop();
                  profile.Append(Arc.Create(start, radius, 0, Math.PI, profileX, profileY));
                  profile.Append(Arc.Create(start, radius, Math.PI, 2 * Math.PI, profileX, profileY));
                  var path = new CurveLoop();
                  path.Append(pathCurve);

                  Solid solid = GeometryCreationUtilities.CreateSweptGeometry(
                    path,
                    0,
                    pathStartParameter,
                    new List<CurveLoop> { profile });
                  if (solid != null && solid.Volume > minimumVolume)
                    reoSolids.Add(solid);
                }
                catch (Exception ex)
                {
                  warnings.Add($"View '{view.Name}', ElementId {element.Id.Value}: round REO solid failed: {ex.Message}");
                }
              }
            }

            #endregion

            #region 10 - Assign Pour by geometric intersection and choose the lowest Pour number

            var intersectingPours = new List<string>();
            foreach (RegionData regionData in sheetRegions)
            {
              IList<CurveLoop> boundaries = regionData.Region.GetBoundaries();
              if (boundaries == null || boundaries.Count == 0)
                continue;

              try
              {
                double sourceZ = boundaries[0].GetPlane().Origin.Z;
                double regionBottom = reoElevation - UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                Transform transform = Transform.CreateTranslation(new XYZ(0, 0, regionBottom - sourceZ));
                List<CurveLoop> transformedBoundaries = boundaries
                  .Select(boundary => CurveLoop.CreateViaTransform(boundary, transform))
                  .ToList();
                Solid regionSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                  transformedBoundaries,
                  XYZ.BasisZ,
                  UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters));

                bool intersects = false;
                foreach (Solid reoSolid in reoSolids)
                {
                  try
                  {
                    Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                      regionSolid,
                      reoSolid,
                      BooleanOperationsType.Intersect);
                    if (intersection != null && intersection.Volume > minimumVolume)
                    {
                      intersects = true;
                      break;
                    }
                  }
                  catch
                  {
                  }
                }

                if (intersects && !intersectingPours.Any(pour => string.Equals(pour, regionData.Pour, StringComparison.OrdinalIgnoreCase)))
                  intersectingPours.Add(regionData.Pour);
              }
              catch (Exception ex)
              {
                warnings.Add($"ElementId {element.Id.Value}, Pour '{regionData.Pour}': {ex.Message}");
              }
            }

            string selectedPour;
            if (projectUsesPour)
            {
              selectedPour = intersectingPours
                .OrderBy(pour =>
                {
                  Match match = Regex.Match(pour, @"\d+");
                  return match.Success && int.TryParse(match.Value, out int number) ? number : int.MaxValue;
                })
                .ThenBy(pour => pour, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? string.Empty;
            }
            else if (projectUsesZone)
            {
              selectedPour = sheetRow.ZoneName;
            }
            else
            {
              selectedPour = sheetRow.LevelName;
            }

            if (projectUsesPour && string.IsNullOrWhiteSpace(selectedPour))
              warnings.Add($"View '{view.Name}', ElementId {element.Id.Value}: Reo curve does not intersect a matching POUR region.");

            #endregion

            #region 11 - Add one export row for this REO element

            var row = new ReoRow
            {
              ElementId = element.Id.Value,
              Pour = selectedPour,
              Mark = mark,
              Tags = tags,
              Type = type,
              Measure = measure,
              Reolen = reolen,
              Spacing = spacing,
              Thickness = thickness,
              Diameter = diameter,
              CogCount = cogCount,
              Splice = splice,
              BarCount = barCount,
              SheetNumber = sheetRow.SheetNumber,
              Level = sheetRow.LevelName,
              Zone = sheetRow.ZoneName,
              Layer = layer
            };
            if (layer != "SHEAR")
              row.Solids.AddRange(reoSolids);
            rows.Add(row);

            #endregion
          }
        }
      }

      #region 12 - Sort by Sheet, Pour number, Mark number, and ElementId

      rows = rows
        .OrderBy(row =>
        {
          Match match = Regex.Match(row.Pour ?? string.Empty, @"\d+");
          return match.Success && int.TryParse(match.Value, out int number) ? number : int.MaxValue;
        })
        .ThenBy(row =>
        {
          Match match = Regex.Match(row.Mark ?? string.Empty, @"\d+");
          return match.Success && int.TryParse(match.Value, out int number) ? number : int.MaxValue;
        })
        .ThenBy(row => row.Mark, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.ElementId)
        .ToList();
      for (int index = 0; index < rows.Count; index++)
        rows[index].No = index + 1;

      #endregion

      #region 13 - Optionally create round DirectShapes grouped by Pour, Zone, and layer

      if (options.Create3d)
      {
        using (var transaction = new Transaction(doc, "NMKMTO REO DirectShape"))
        {
          transaction.Start();

          List<ElementId> oldIds = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(shape => shape.ApplicationId == directShapeApplicationId)
            .Select(shape => shape.Id)
            .ToList();
          if (oldIds.Count > 0)
            doc.Delete(oldIds);

          int shapeIndex = 1;
          var shapeGroups = rows
            .Where(row => row.Layer != "SHEAR" && row.Solids.Count > 0)
            .GroupBy(row => $"{row.Level}|{row.Zone}|{row.Pour}|{row.Layer}", StringComparer.OrdinalIgnoreCase);

          foreach (var shapeGroup in shapeGroups)
          {
            List<Solid> solids = shapeGroup.SelectMany(row => row.Solids).ToList();
            if (solids.Count == 0)
              continue;

            ReoRow first = shapeGroup.First();
            DirectShape shape = DirectShape.CreateElement(
              doc,
              new ElementId(BuiltInCategory.OST_GenericModel));
            shape.ApplicationId = directShapeApplicationId;
            shape.ApplicationDataId = shapeIndex.ToString(CultureInfo.InvariantCulture);
            shape.Name = $"NMKMTO REO {first.Layer} {first.Zone} {first.Pour}".Trim();
            shape.SetShape(solids.Cast<GeometryObject>().ToList());

            Parameter commentsParameter = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
              ?? shape.LookupParameter("Comments");
            if (commentsParameter != null && !commentsParameter.IsReadOnly)
              commentsParameter.Set($"REO | Level: {first.Level} | Zone: {first.Zone} | Pour: {first.Pour} | Layer: {first.Layer}");
            shapeIndex++;
          }

          transaction.Commit();
        }
      }

      #endregion

      #region 14 - Export the requested 15-column CSV without WeightPerMeter

      string datePrefix = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
      string buildingName = doc.ProjectInformation?.LookupParameter("Building Name")?.AsString()?.Trim()
        ?? doc.ProjectInformation?.Name
        ?? doc.Title;
      List<string> levelNames = sheets
        .Select(sheet => sheet.LevelName)
        .Where(levelName => !string.IsNullOrWhiteSpace(levelName))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
      string exportLevel = levelNames.Count == 1 ? levelNames[0] : "MULTI LEVEL";
      string fileName = $"{datePrefix}_MTO_{buildingName}_{exportLevel}_REO";
      foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        fileName = fileName.Replace(invalidCharacter, '_');

      string exportPath = Path.Combine(options.ExportFolder, $"{fileName}.csv");
      var csv = new StringBuilder();
      csv.AppendLine("No,ElementId,Pour,Mark,Tags,Type,Measure (mm),Reolen (mm),Spacing (mm),Thickness (mm),Diameter (mm),CogCount,Splice,BarCount,SheetNumber");

      foreach (ReoRow row in rows)
      {
        string pourValue = row.Pour.StartsWith("POUR", StringComparison.OrdinalIgnoreCase)
          ? string.IsNullOrWhiteSpace(row.Zone)
            ? row.Pour
            : $"{row.Zone} - {row.Pour}"
          : !string.IsNullOrWhiteSpace(row.Zone)
            ? row.Zone
            : row.Level;

        string[] values =
        {
          row.No.ToString(CultureInfo.InvariantCulture),
          row.ElementId.ToString(CultureInfo.InvariantCulture),
          pourValue,
          row.Mark,
          row.Tags,
          row.Type,
          row.Measure.ToString("0.###", CultureInfo.InvariantCulture),
          row.Reolen.ToString("0.###", CultureInfo.InvariantCulture),
          row.Spacing.ToString("0.###", CultureInfo.InvariantCulture),
          row.Thickness.ToString("0.###", CultureInfo.InvariantCulture),
          row.Diameter.ToString("0.###", CultureInfo.InvariantCulture),
          row.CogCount.ToString("0.###", CultureInfo.InvariantCulture),
          row.Splice,
          row.BarCount.ToString("0.###", CultureInfo.InvariantCulture),
          row.SheetNumber
        };

        csv.AppendLine(string.Join(",", values.Select(value =>
        {
          value ??= string.Empty;
          return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
        })));
      }
      string dataCsvContent = csv.ToString();
      if (writeDataCsv)
        File.WriteAllText(exportPath, dataCsvContent, Encoding.UTF8);

      string warningPath = string.Empty;
      if (warnings.Count > 0)
      {
        warningPath = Path.Combine(options.ExportFolder, $"{fileName}_WARNING.csv");
        var warningCsv = new StringBuilder("No,Warning\r\n");
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

      #region 15 - Return export summary

      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = rows.Count,
        ExportPath = writeDataCsv ? exportPath : string.Empty,
        DataCsvContent = dataCsvContent,
        WarningPath = warningPath,
        Message = warnings.Count > 0
          ? $"REO exported: {exportPath}\nRows: {rows.Count}\nWarning file: {warningPath}"
          : $"REO exported: {exportPath}\nRows: {rows.Count}"
      };
      foreach (string warning in warnings)
        result.Warnings.Add(warning);
      return result;

      #endregion
    }
  }
}
