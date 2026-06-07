using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_ModelDataExtractor
  {
    private const string DirectShapeApplicationId = "NMKMTO_MODEL";
    private const double MinSolidVolumeFt3 = 0.0001;
    private const double Ft2ToM2 = 0.09290304;
    private const double Ft3ToM3 = 0.028316846592;
    private const double MmToFt = 1.0 / 304.8;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static NMKMTO_ModelModelDataResult Extract(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      NMKMTO_ModelModelDataOptions options)
    {
      if (string.IsNullOrWhiteSpace(options.ExportFolder))
        throw new InvalidOperationException("Export folder is empty.");

      Directory.CreateDirectory(options.ExportFolder);

      Autodesk.Revit.DB.View filledRegionView = FindRequiredFilledRegionView(doc, options.FilledRegionViewName);
      var sheets = selectedSheets.ToList();
      var overViews = sheets
        .SelectMany(sheet => GetSheetViews(doc, sheet.SheetId))
        .Where(view => view.Name.IndexOf(F_MtoViewNames.OverViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
        .ToList();

      if (overViews.Count == 0)
        throw new InvalidOperationException("Selected sheets do not contain any OVER views.");

      var warnings = new List<string>();
      var regions = CollectFilledRegions(doc, filledRegionView);
      if (regions.Count == 0)
        throw new InvalidOperationException($"View '{F_MtoViewNames.FilledRegionAreaTemplate}' does not contain any FilledRegion.");

      var linkedFloors = CollectLinkedFloors(doc, warnings);
      if (linkedFloors.Count == 0)
        throw new InvalidOperationException("No linked floor solids found after filtering PRECAST and PLINTH types.");

      var rows = new List<NMKMTO_ModelModelDataRow>();
      var directShapeItems = new List<DirectShapeData>();
      var scopes = sheets
        .GroupBy(sheet => $"{NormalizeName(sheet.LevelName)}|{NormalizeName(sheet.ZoneName)}", StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();
      int rowNo = 1;

      foreach (var scope in scopes)
      {
        Level level = FindLevel(doc, scope.LevelName);
        double bottomZ = level.ProjectElevation - options.BottomOffsetMm * MmToFt;
        double height = (options.TopOffsetMm + options.BottomOffsetMm) * MmToFt;
        var matchedRegions = regions.Where(region => IsRegionForSheet(region, scope)).ToList();
        if (matchedRegions.Count == 0)
          warnings.Add($"Level '{scope.LevelName}' zone '{scope.ZoneName}' has no matching FilledRegion.");

        foreach (var region in matchedRegions)
        {
          Solid regionSolid = CreateSolidFromFilledRegion(region.Region, bottomZ, height);
          var normalIntersections = new List<Solid>();
          var slimdeckIntersections = new List<SlimdeckIntersectionData>();

          foreach (var floor in linkedFloors)
          {
            Solid intersection = Intersect(regionSolid, floor.Solid);
            if (intersection == null || intersection.Volume <= MinSolidVolumeFt3)
              continue;

            if (floor.IsSlimdeck)
              slimdeckIntersections.Add(new SlimdeckIntersectionData
              {
                Solid = intersection,
                TotalThicknessMm = floor.TotalThicknessMm,
                TypeName = floor.TypeName
              });
            else
              normalIntersections.Add(intersection);
          }

          var mergedNormalSolids = MergeSolidsByFilledRegion(normalIntersections, region, scope, warnings);
          var mergedSlimdeckGroups = MergeSlimdeckSolidsByFilledRegion(slimdeckIntersections, region, scope, warnings);

          double normalAreaFt2 = mergedNormalSolids.Sum(GetTopFaceArea);
          double normalVolumeM3 = mergedNormalSolids.Sum(x => x.Volume) * Ft3ToM3;
          double slimdeckAreaM2 = mergedSlimdeckGroups.Sum(group => group.Solids.Sum(GetTopFaceArea) * Ft2ToM2);
          double slimdeckVolumeM3 = CalculateSlimdeckVolumeM3(mergedSlimdeckGroups, region, scope, warnings);
          double floorAreaM2 = normalAreaFt2 * Ft2ToM2 + slimdeckAreaM2;
          double floorVolumeM3 = normalVolumeM3 + slimdeckVolumeM3;

          var row = new NMKMTO_ModelModelDataRow
          {
            No = rowNo++,
            Pour = region.Comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase) ? region.Comments : string.Empty,
            Zone = string.IsNullOrWhiteSpace(region.RincoZone) ? region.Comments : region.RincoZone,
            Level = scope.LevelName,
            FloorAreaM2 = floorAreaM2,
            FloorVolumeM3 = floorVolumeM3
          };
          rows.Add(row);

          if (mergedNormalSolids.Count > 0)
          {
            directShapeItems.Add(new DirectShapeData
            {
              Name = $"NMKMTO MODEL NORMAL {row.No}",
              Comments = BuildDirectShapeComments(row, region, "Normal"),
              Solids = mergedNormalSolids
            });
          }

          foreach (var slimdeckGroup in mergedSlimdeckGroups.Where(group => group.Solids.Count > 0))
          {
            directShapeItems.Add(new DirectShapeData
            {
              Name = $"NMKMTO MODEL SLIMDECK {row.No}",
              Comments = BuildDirectShapeComments(row, region, $"Slimdeck | Thickness: {FormatNumber(slimdeckGroup.TotalThicknessMm)} mm"),
              Solids = slimdeckGroup.Solids
            });
          }

          if (floorAreaM2 <= 0 || floorVolumeM3 <= 0)
            warnings.Add($"FilledRegion '{region.Comments}' on level '{scope.LevelName}' did not intersect any valid linked floor.");
        }
      }

      if (options.Create3d)
      {
        using (var transaction = new Transaction(doc, "NMKMTO MODEL DirectShape"))
        {
          transaction.Start();
          DeleteExistingDirectShapes(doc);
          CreateDirectShapes(doc, directShapeItems);
          transaction.Commit();
        }
      }

      if (rows.Count == 0)
        warnings.Add("No MODEL rows were exported.");

      rows = SortModelRows(rows);
      RenumberRows(rows);

      string baseFileName = BuildBaseExportFileName(doc, sheets, options);
      string exportPath = Path.Combine(options.ExportFolder, $"{baseFileName}.csv");
      string warningPath = warnings.Count > 0 ? Path.Combine(options.ExportFolder, $"{baseFileName}_WARNING.csv") : string.Empty;
      ExportCsv(exportPath, rows);
      if (warnings.Count > 0)
        ExportWarnings(warningPath, warnings);

      var result = new NMKMTO_ModelModelDataResult
      {
        SheetCount = sheets.Count,
        OverViewCount = overViews.Count,
        FloorSurfaceArea = rows.Sum(x => x.FloorAreaM2),
        FloorVolume = rows.Sum(x => x.FloorVolumeM3),
        ExportPath = exportPath,
        WarningPath = warningPath,
        Message = warnings.Count > 0
          ? $"MODEL exported: {exportPath}\nWarning file: {warningPath}"
          : $"MODEL exported: {exportPath}"
      };

      foreach (var row in rows)
        result.Rows.Add(row);
      foreach (var warning in warnings)
        result.Warnings.Add(warning);

      return result;
    }

    private sealed class FilledRegionData
    {
      public FilledRegion Region { get; set; }
      public string Comments { get; set; } = string.Empty;
      public string RincoZone { get; set; } = string.Empty;
    }

    private sealed class DirectShapeData
    {
      public string Name { get; set; } = string.Empty;
      public string Comments { get; set; } = string.Empty;
      public List<Solid> Solids { get; set; } = new();
    }

    private sealed class LinkedFloorData
    {
      public Solid Solid { get; set; }
      public bool IsSlimdeck { get; set; }
      public double TotalThicknessMm { get; set; }
      public string TypeName { get; set; } = string.Empty;
    }

    private sealed class SlimdeckIntersectionData
    {
      public Solid Solid { get; set; }
      public double TotalThicknessMm { get; set; }
      public string TypeName { get; set; } = string.Empty;
    }

    private sealed class SlimdeckSolidGroup
    {
      public double TotalThicknessMm { get; set; }
      public string TypeName { get; set; } = string.Empty;
      public List<Solid> Solids { get; set; } = new();
    }

    private static Autodesk.Revit.DB.View FindRequiredFilledRegionView(Document doc, string filledRegionViewName)
    {
      var view = new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(filledRegionViewName, StringComparison.OrdinalIgnoreCase));

      if (view == null)
        throw new InvalidOperationException($"Required view not found: {filledRegionViewName}. No tool can run until this view exists.");

      return view;
    }

    private static List<FilledRegionData> CollectFilledRegions(Document doc, Autodesk.Revit.DB.View view)
    {
      return new FilteredElementCollector(doc, view.Id)
        .OfClass(typeof(FilledRegion))
        .Cast<FilledRegion>()
        .Select(region => new FilledRegionData
        {
          Region = region,
          Comments = GetStringParameter(region, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments"),
          RincoZone = GetStringParameter(region, "RINCO_ZONE")
        })
        .Where(x => !string.IsNullOrWhiteSpace(x.Comments))
        .ToList();
    }

    private static List<LinkedFloorData> CollectLinkedFloors(Document hostDoc, List<string> warnings)
    {
      var result = new List<LinkedFloorData>();
      var links = new FilteredElementCollector(hostDoc)
        .OfClass(typeof(RevitLinkInstance))
        .Cast<RevitLinkInstance>();

      foreach (var link in links)
      {
        Document linkDoc = link.GetLinkDocument();
        if (linkDoc == null)
        {
          warnings.Add($"Revit link '{link.Name}' is unloaded or cannot be read.");
          continue;
        }

        Transform linkTransform = link.GetTotalTransform();
        var floors = new FilteredElementCollector(linkDoc)
          .OfCategory(BuiltInCategory.OST_Floors)
          .WhereElementIsNotElementType()
          .Where(element => !IsExcludedFloorType(linkDoc, element));

        foreach (var floor in floors)
        {
          var floorType = linkDoc.GetElement(floor.GetTypeId());
          string typeName = floorType?.Name ?? string.Empty;
          bool isSlimdeck = typeName.IndexOf("SLIMDECK", StringComparison.OrdinalIgnoreCase) >= 0;
          double totalThicknessMm = isSlimdeck ? GetFloorTotalThicknessMm(linkDoc, floor, floorType, warnings) : 0;

          foreach (var solid in GetElementSolids(floor))
          {
            if (solid.Volume <= MinSolidVolumeFt3)
              continue;

            result.Add(new LinkedFloorData
            {
              Solid = SolidUtils.CreateTransformed(solid, linkTransform),
              IsSlimdeck = isSlimdeck,
              TotalThicknessMm = totalThicknessMm,
              TypeName = typeName
            });
          }
        }
      }

      return result;
    }

    private static double GetFloorTotalThicknessMm(Document doc, Element floor, Element floorType, List<string> warnings)
    {
      double thicknessFt = floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0;
      if (thicknessFt <= 0)
        thicknessFt = floorType?.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0;

      if (thicknessFt <= 0 && floorType is HostObjAttributes hostObjAttributes)
      {
        CompoundStructure compoundStructure = hostObjAttributes.GetCompoundStructure();
        if (compoundStructure != null)
          thicknessFt = compoundStructure.GetWidth();
      }

      if (thicknessFt <= 0)
      {
        warnings.Add($"Slimdeck floor '{floor.Id.Value}' in '{doc.Title}' has no readable total thickness. Slimdeck volume will use model volume fallback.");
        return 0;
      }

      return thicknessFt * 304.8;
    }

    private static bool IsExcludedFloorType(Document doc, Element element)
    {
      Element type = doc.GetElement(element.GetTypeId());
      string typeName = type?.Name ?? string.Empty;
      return typeName.IndexOf("PRECAST", StringComparison.OrdinalIgnoreCase) >= 0
        || typeName.IndexOf("PLINTH", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<Solid> GetElementSolids(Element element)
    {
      var options = new Options
      {
        DetailLevel = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false
      };

      GeometryElement geometry = element.get_Geometry(options);
      if (geometry == null)
        yield break;

      foreach (GeometryObject geometryObject in geometry)
      {
        if (geometryObject is Solid solid && solid.Volume > MinSolidVolumeFt3)
        {
          yield return solid;
        }
        else if (geometryObject is GeometryInstance instance)
        {
          foreach (GeometryObject instanceObject in instance.GetInstanceGeometry())
          {
            if (instanceObject is Solid instanceSolid && instanceSolid.Volume > MinSolidVolumeFt3)
              yield return instanceSolid;
          }
        }
      }
    }

    private static bool IsRegionForSheet(FilledRegionData region, NMKMTO_ModelSheetRow sheet)
    {
      if (string.IsNullOrWhiteSpace(sheet.ZoneName))
        return true;

      if (region.Comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase))
        return EqualsName(region.RincoZone, sheet.ZoneName);

      return EqualsName(region.Comments, sheet.ZoneName);
    }

    private static Level FindLevel(Document doc, string levelName)
    {
      var level = new FilteredElementCollector(doc)
        .OfClass(typeof(Level))
        .Cast<Level>()
        .FirstOrDefault(x => EqualsName(x.Name, levelName));

      if (level == null)
        throw new InvalidOperationException($"Level not found from selected sheet name: {levelName}");

      return level;
    }

    private static Solid CreateSolidFromFilledRegion(FilledRegion region, double bottomZ, double height)
    {
      IList<CurveLoop> loops = region.GetBoundaries();
      if (loops == null || loops.Count == 0)
        throw new InvalidOperationException($"FilledRegion has no boundaries: {region.Id.Value}");

      double sourceZ = loops[0].GetPlane().Origin.Z;
      Transform toBottom = Transform.CreateTranslation(new XYZ(0, 0, bottomZ - sourceZ));
      var transformedLoops = loops.Select(loop => CurveLoop.CreateViaTransform(loop, toBottom)).ToList();
      return GeometryCreationUtilities.CreateExtrusionGeometry(transformedLoops, XYZ.BasisZ, height);
    }

    private static Solid Intersect(Solid first, Solid second)
    {
      try
      {
        return BooleanOperationsUtils.ExecuteBooleanOperation(first, second, BooleanOperationsType.Intersect);
      }
      catch
      {
        return null;
      }
    }

    private static List<Solid> MergeSolidsByFilledRegion(
      List<Solid> solids,
      FilledRegionData region,
      NMKMTO_ModelSheetRow scope,
      List<string> warnings)
    {
      var mergedSolids = solids
        .Where(x => x.Volume > MinSolidVolumeFt3)
        .ToList();

      bool changed = true;
      while (changed)
      {
        changed = false;

        for (int i = 0; i < mergedSolids.Count; i++)
        {
          for (int j = i + 1; j < mergedSolids.Count; j++)
          {
            Solid overlap = Intersect(mergedSolids[i], mergedSolids[j]);
            if (overlap == null || overlap.Volume <= MinSolidVolumeFt3)
              continue;

            Solid union = Union(mergedSolids[i], mergedSolids[j]);
            if (union == null || union.Volume <= MinSolidVolumeFt3)
            {
              warnings.Add($"FilledRegion '{region.Comments}' on level '{scope.LevelName}' has overlapping linked floor solids that could not be unioned. Check duplicated/forgotten-zone floors in links.");
              continue;
            }

            mergedSolids[i] = union;
            mergedSolids.RemoveAt(j);
            changed = true;
            break;
          }

          if (changed)
            break;
        }
      }

      return mergedSolids;
    }

    private static List<SlimdeckSolidGroup> MergeSlimdeckSolidsByFilledRegion(
      List<SlimdeckIntersectionData> intersections,
      FilledRegionData region,
      NMKMTO_ModelSheetRow scope,
      List<string> warnings)
    {
      return intersections
        .GroupBy(item => $"{NormalizeName(item.TypeName)}|{item.TotalThicknessMm:0.###}", StringComparer.OrdinalIgnoreCase)
        .Select(group => new SlimdeckSolidGroup
        {
          TypeName = group.First().TypeName,
          TotalThicknessMm = group.First().TotalThicknessMm,
          Solids = MergeSolidsByFilledRegion(group.Select(item => item.Solid).ToList(), region, scope, warnings)
        })
        .ToList();
    }

    private static double CalculateSlimdeckVolumeM3(
      List<SlimdeckSolidGroup> slimdeckGroups,
      FilledRegionData region,
      NMKMTO_ModelSheetRow scope,
      List<string> warnings)
    {
      double volumeM3 = 0;

      foreach (var group in slimdeckGroups)
      {
        double areaM2 = group.Solids.Sum(GetTopFaceArea) * Ft2ToM2;
        if (areaM2 <= 0)
          continue;

        if (group.TotalThicknessMm <= 170)
        {
          double fallbackVolume = group.Solids.Sum(x => x.Volume) * Ft3ToM3;
          volumeM3 += fallbackVolume;
          warnings.Add($"Slimdeck '{group.TypeName}' for FilledRegion '{region.Comments}' on level '{scope.LevelName}' has invalid thickness {FormatNumber(group.TotalThicknessMm)} mm. Used model volume fallback.");
          continue;
        }

        volumeM3 += areaM2 * ((group.TotalThicknessMm - 170) / 1000.0);
      }

      return volumeM3;
    }

    private static Solid Union(Solid first, Solid second)
    {
      try
      {
        return BooleanOperationsUtils.ExecuteBooleanOperation(first, second, BooleanOperationsType.Union);
      }
      catch
      {
        return null;
      }
    }

    private static double GetTopFaceArea(Solid solid)
    {
      double area = 0;

      foreach (Face face in solid.Faces)
      {
        if (face is PlanarFace planarFace)
        {
          XYZ normal = planarFace.FaceNormal.Normalize();
          if (normal.DotProduct(XYZ.BasisZ) > 0.9)
            area += planarFace.Area;
        }
      }

      return area;
    }

    private static void DeleteExistingDirectShapes(Document doc)
    {
      var ids = new FilteredElementCollector(doc)
        .OfClass(typeof(DirectShape))
        .Cast<DirectShape>()
        .Where(x => x.ApplicationId == DirectShapeApplicationId)
        .Select(x => x.Id)
        .ToList();

      if (ids.Count > 0)
        doc.Delete(ids);
    }

    private static void CreateDirectShapes(Document doc, List<DirectShapeData> directShapeItems)
    {
      int index = 1;
      foreach (var item in directShapeItems.Where(x => x.Solids.Any(solid => solid.Volume > MinSolidVolumeFt3)))
      {
        var directShape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
        directShape.ApplicationId = DirectShapeApplicationId;
        directShape.ApplicationDataId = index.ToString(CultureInfo.InvariantCulture);
        directShape.Name = string.IsNullOrWhiteSpace(item.Name) ? $"NMKMTO MODEL {index}" : item.Name;
        directShape.SetShape(item.Solids.Cast<GeometryObject>().ToList());
        SetComments(directShape, item.Comments);
        index++;
      }
    }

    private static string BuildDirectShapeComments(NMKMTO_ModelModelDataRow row, FilledRegionData region, string floorGroup)
    {
      var parts = new List<string>
      {
        "NMKMTO MODEL",
        $"Floor Group: {floorGroup}",
        $"Level: {row.Level}",
        $"Zone: {row.Zone}",
        $"FilledRegion: {region.Comments}",
        $"Floor Area: {FormatNumber(row.FloorAreaM2)} m2",
        $"Floor Volume: {FormatNumber(row.FloorVolumeM3)} m3"
      };

      if (!string.IsNullOrWhiteSpace(row.Pour))
        parts.Insert(3, $"Pour: {row.Pour}");

      return string.Join(" | ", parts);
    }

    private static void SetComments(Element element, string comments)
    {
      Parameter parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
        ?? element.LookupParameter("Comments");

      if (parameter != null && !parameter.IsReadOnly)
        parameter.Set(comments);
    }

    private static IEnumerable<Autodesk.Revit.DB.View> GetSheetViews(Document doc, ElementId sheetId)
    {
      var sheet = doc.GetElement(sheetId) as ViewSheet;
      if (sheet == null)
        return Enumerable.Empty<Autodesk.Revit.DB.View>();

      return sheet
        .GetAllPlacedViews()
        .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
        .Where(view => view != null)
        .Cast<Autodesk.Revit.DB.View>();
    }

    private static void ExportCsv(string path, List<NMKMTO_ModelModelDataRow> rows)
    {
      var builder = new StringBuilder();
      var headers = new[] { "No", "Sequence", "Distributed Top Area (m2)", "Distributed Bottom Area (m2)", "N16-1000 Area (m2)", "Floor Area (m2)", "Floor Volume (m3)" };

      builder.AppendLine(string.Join(",", headers.Select(EscapeCsv)));

      foreach (var row in rows)
      {
        var values = new[]
        {
          row.No.ToString(CultureInfo.InvariantCulture),
          GetSequenceValue(row),
          FormatNumber(row.DistributedTopAreaM2),
          FormatNumber(row.DistributedBottomAreaM2),
          FormatNumber(row.N16_1000AreaM2),
          FormatNumber(row.FloorAreaM2),
          FormatNumber(row.FloorVolumeM3)
        };

        builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
      }

      File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static List<NMKMTO_ModelModelDataRow> SortModelRows(List<NMKMTO_ModelModelDataRow> rows)
    {
      return rows
        .OrderBy(row => GetSequenceValue(row), NaturalStringComparer.Instance)
        .ThenBy(row => row.Level, NaturalStringComparer.Instance)
        .ToList();
    }

    private static void RenumberRows(List<NMKMTO_ModelModelDataRow> rows)
    {
      for (int i = 0; i < rows.Count; i++)
        rows[i].No = i + 1;
    }

    private static string GetSequenceValue(NMKMTO_ModelModelDataRow row)
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

    private static void ExportWarnings(string path, List<string> warnings)
    {
      var builder = new StringBuilder();
      builder.AppendLine("No,Warning");

      if (warnings.Count == 0)
      {
        builder.AppendLine("1,OK - No warning");
      }
      else
      {
        for (int i = 0; i < warnings.Count; i++)
          builder.AppendLine(string.Join(",", new[] { (i + 1).ToString(CultureInfo.InvariantCulture), EscapeCsv(warnings[i]) }));
      }

      File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static string BuildBaseExportFileName(Document doc, List<NMKMTO_ModelSheetRow> sheets, NMKMTO_ModelModelDataOptions options)
    {
      string datePrefix = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
      string buildingName = GetProjectInformationName(doc);
      string levelName = GetExportLevelName(sheets);
      string suffix = GetCheckedExportTypeSuffix(options);
      return SanitizeFileName($"{datePrefix}_MTO_{buildingName}_{levelName}{suffix}");
    }

    private static string GetProjectInformationName(Document doc)
    {
      ProjectInfo projectInformation = doc.ProjectInformation;
      string buildingName = GetStringParameter(projectInformation, "Building Name");

      if (string.IsNullOrWhiteSpace(buildingName))
        buildingName = projectInformation?.Name ?? string.Empty;

      if (string.IsNullOrWhiteSpace(buildingName))
        buildingName = doc.Title;

      return string.IsNullOrWhiteSpace(buildingName) ? "PROJECT INFORMATION" : buildingName;
    }

    private static string GetExportLevelName(List<NMKMTO_ModelSheetRow> sheets)
    {
      var levels = sheets
        .Select(x => x.LevelName)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

      if (levels.Count == 1)
        return levels[0];

      if (levels.Count == 0)
        return "LEVEL";

      return "MULTI LEVEL";
    }

    private static string GetCheckedExportTypeSuffix(NMKMTO_ModelModelDataOptions options)
    {
      if (options.CheckedExportTypes.Count == 0 || options.CheckedExportTypes.Count >= options.TotalExportTypeCount)
        return string.Empty;

      return "_" + string.Join("_", options.CheckedExportTypes.Select(SanitizeToken));
    }

    private static string SanitizeFileName(string value)
    {
      string sanitized = value;
      foreach (char invalidChar in Path.GetInvalidFileNameChars())
        sanitized = sanitized.Replace(invalidChar, '_');

      return sanitized.Trim();
    }

    private static string SanitizeToken(string value)
    {
      var builder = new StringBuilder();
      foreach (char character in value.Trim().ToUpperInvariant())
      {
        if (char.IsLetterOrDigit(character))
          builder.Append(character);
        else if (char.IsWhiteSpace(character) || character == '-' || character == '_')
          builder.Append('_');
      }

      return builder.ToString().Trim('_');
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

    private static bool EqualsName(string first, string second)
    {
      return string.Equals(NormalizeName(first), NormalizeName(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string value)
    {
      return (value ?? string.Empty).Trim();
    }

    private static string FormatBlankZero(double value)
    {
      return Math.Abs(value) < 0.000001 ? string.Empty : FormatNumber(value);
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
