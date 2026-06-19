using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_EarthingReoExtractor
  {
    private const string DirectShapeApplicationId = "NMKMTO_EARTHING_REO";
    private const double MinSolidVolumeFt3 = 0.0001;
    private const double Ft2ToM2 = 0.09290304;
    private const double MmToFt = 1.0 / 304.8;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static NMKMTO_ModelEarthingReoResult Extract(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      NMKMTO_ModelModelDataOptions options,
      bool writeDataCsv = true)
    {
      if (string.IsNullOrWhiteSpace(options.ExportFolder))
        throw new InvalidOperationException("Export folder is empty.");

      Directory.CreateDirectory(options.ExportFolder);

      var warnings = new List<string>();
      var sheets = selectedSheets.ToList();
      var mtoView = FindRequiredView(doc, options.FilledRegionViewName);
      var mtoRegions = CollectFilledRegions(doc, mtoView);
      if (mtoRegions.Count == 0)
        throw new InvalidOperationException($"View '{options.FilledRegionViewName}' does not contain any FilledRegion.");

      var underRegions = CollectUnderRegions(doc, sheets, warnings);
      if (underRegions.Count == 0)
        throw new InvalidOperationException("Selected sheets do not contain any UNDER FilledRegion.");

      var rows = new List<NMKMTO_ModelModelDataRow>();
      var directShapeItems = new List<DirectShapeData>();
      var earthingTypeNames = new SortedSet<string>(NaturalStringComparer.Instance);
      var scopes = sheets
        .GroupBy(sheet => $"{NormalizeName(sheet.LevelName)}|{NormalizeName(sheet.ZoneName)}", StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();
      int rowNo = 1;

      foreach (var scope in scopes)
      {
        Level level = FindLevel(doc, scope.LevelName);
        double bottomZ = level.ProjectElevation;
        double height = 10 * MmToFt;
        var matchedMtoRegions = mtoRegions.Where(region => IsRegionForSheet(region, scope)).ToList();
        var matchedUnderRegions = underRegions.Where(region => IsUnderRegionForScope(region, scope)).ToList();

        if (matchedMtoRegions.Count == 0)
          warnings.Add($"Level '{scope.LevelName}' zone '{scope.ZoneName}' has no matching MTO FilledRegion.");
        if (matchedUnderRegions.Count == 0)
          warnings.Add($"Level '{scope.LevelName}' zone '{scope.ZoneName}' has no UNDER FilledRegion.");

        foreach (var mtoRegion in matchedMtoRegions)
        {
          Solid mtoSolid = CreateSolidFromFilledRegion(mtoRegion.Region, bottomZ, height);
          var intersectionSolids = new List<Solid>();
          double earthingAreaFt2 = 0;

          foreach (var underRegion in matchedUnderRegions)
          {
            Solid underSolid = CreateSolidFromFilledRegion(underRegion.Region, bottomZ, height);
            Solid intersection = Intersect(mtoSolid, underSolid);
            if (intersection == null || intersection.Volume <= MinSolidVolumeFt3)
              continue;

            earthingTypeNames.Add(underRegion.TypeName);
            earthingAreaFt2 += GetTopFaceArea(intersection);
            intersectionSolids.Add(intersection);
          }

          var mergedSolids = MergeSolids(intersectionSolids, mtoRegion, scope, warnings);
          double earthingAreaM2 = mergedSolids.Sum(GetTopFaceArea) * Ft2ToM2;
          var row = new NMKMTO_ModelModelDataRow
          {
            No = rowNo++,
            Pour = mtoRegion.Comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase) ? mtoRegion.Comments : string.Empty,
            Zone = string.IsNullOrWhiteSpace(mtoRegion.RincoZone) ? mtoRegion.Comments : mtoRegion.RincoZone,
            Level = scope.LevelName,
            N16_1000AreaM2 = earthingAreaM2
          };
          rows.Add(row);

          if (options.Create3d && mergedSolids.Count > 0)
          {
            directShapeItems.Add(new DirectShapeData
            {
              Name = $"NMKMTO EARTHING REO {row.No}",
              Comments = BuildDirectShapeComments(row, mtoRegion, earthingAreaM2),
              Solids = mergedSolids
            });
          }

          if (earthingAreaM2 <= 0)
            warnings.Add($"MTO FilledRegion '{mtoRegion.Comments}' on level '{scope.LevelName}' did not intersect any UNDER FilledRegion.");
        }
      }

      if (options.Create3d)
      {
        using (var transaction = new Transaction(doc, "NMKMTO EARTHING REO DirectShape"))
        {
          transaction.Start();
          DeleteExistingDirectShapes(doc);
          CreateDirectShapes(doc, directShapeItems);
          transaction.Commit();
        }
      }

      rows = SortRows(rows);
      RenumberRows(rows);

      string baseFileName = BuildBaseExportFileName(doc, sheets, options, "EARTHING_REO");
      string exportPath = Path.Combine(options.ExportFolder, $"{baseFileName}.csv");
      string warningPath = warnings.Count > 0 ? Path.Combine(options.ExportFolder, $"{baseFileName}_WARNING.csv") : string.Empty;
      string earthingHeader = BuildEarthingHeader(earthingTypeNames);
      if (writeDataCsv)
        ExportCsv(exportPath, rows, earthingHeader);
      if (warnings.Count > 0)
        ExportWarnings(warningPath, warnings);

      var result = new NMKMTO_ModelEarthingReoResult
      {
        SheetCount = sheets.Count,
        EarthingAreaM2 = rows.Sum(x => x.N16_1000AreaM2),
        ExportPath = writeDataCsv ? exportPath : string.Empty,
        WarningPath = warningPath,
        Message = warnings.Count > 0
          ? $"EARTHING REO exported: {exportPath}\nWarning file: {warningPath}"
          : $"EARTHING REO exported: {exportPath}"
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
      public string TypeName { get; set; } = string.Empty;
      public ElementId SourceSheetId { get; set; } = ElementId.InvalidElementId;
    }

    private sealed class DirectShapeData
    {
      public string Name { get; set; } = string.Empty;
      public string Comments { get; set; } = string.Empty;
      public List<Solid> Solids { get; set; } = new();
    }

    private static Autodesk.Revit.DB.View FindRequiredView(Document doc, string viewName)
    {
      var view = new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

      if (view == null)
        throw new InvalidOperationException($"Required view not found: {viewName}. No tool can run until this view exists.");

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
          RincoZone = GetStringParameter(region, "RINCO_ZONE"),
          TypeName = doc.GetElement(region.GetTypeId())?.Name ?? "EARTHING REO"
        })
        .Where(x => !string.IsNullOrWhiteSpace(x.Comments))
        .ToList();
    }

    private static List<FilledRegionData> CollectUnderRegions(Document doc, List<NMKMTO_ModelSheetRow> sheets, List<string> warnings)
    {
      var result = new List<FilledRegionData>();
      foreach (var sheetRow in sheets)
      {
        var sheet = doc.GetElement(sheetRow.SheetId) as ViewSheet;
        if (sheet == null)
          continue;

        var underViews = sheet.GetAllPlacedViews()
          .Select(id => doc.GetElement(id) as Autodesk.Revit.DB.View)
          .Where(view => view != null && view.Name.IndexOf(F_MtoViewNames.UnderViewKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
          .Cast<Autodesk.Revit.DB.View>()
          .ToList();

        if (underViews.Count == 0)
          warnings.Add($"Sheet '{sheetRow.SheetNumber} - {sheetRow.SheetName}' has no UNDER view.");

        foreach (var view in underViews)
        {
          foreach (var region in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).Cast<FilledRegion>())
          {
            result.Add(new FilledRegionData
            {
              Region = region,
              Comments = sheetRow.ZoneName,
              RincoZone = sheetRow.ZoneName,
              TypeName = doc.GetElement(region.GetTypeId())?.Name ?? "EARTHING REO",
              SourceSheetId = sheetRow.SheetId
            });
          }
        }
      }

      return result;
    }

    private static bool IsRegionForSheet(FilledRegionData region, NMKMTO_ModelSheetRow sheet)
    {
      if (string.IsNullOrWhiteSpace(sheet.ZoneName))
        return true;

      if (region.Comments.StartsWith("POUR", StringComparison.OrdinalIgnoreCase))
        return EqualsName(region.RincoZone, sheet.ZoneName);

      return EqualsName(region.Comments, sheet.ZoneName);
    }

    private static bool IsUnderRegionForScope(FilledRegionData region, NMKMTO_ModelSheetRow scope)
    {
      return string.IsNullOrWhiteSpace(scope.ZoneName) || EqualsName(region.RincoZone, scope.ZoneName);
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

    private static List<Solid> MergeSolids(List<Solid> solids, FilledRegionData region, NMKMTO_ModelSheetRow scope, List<string> warnings)
    {
      var mergedSolids = solids.Where(x => x.Volume > MinSolidVolumeFt3).ToList();
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
              warnings.Add($"Earthing FilledRegion overlap could not be unioned for '{region.Comments}' on level '{scope.LevelName}'.");
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
        if (face is PlanarFace planarFace && planarFace.FaceNormal.Normalize().DotProduct(XYZ.BasisZ) > 0.9)
          area += planarFace.Area;
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
        directShape.Name = item.Name;
        directShape.SetShape(item.Solids.Cast<GeometryObject>().ToList());
        SetComments(directShape, item.Comments);
        index++;
      }
    }

    private static string BuildDirectShapeComments(NMKMTO_ModelModelDataRow row, FilledRegionData region, double earthingAreaM2)
    {
      return $"NMKMTO EARTHING REO | Level: {row.Level} | Sequence: {GetSequenceValue(row)} | FilledRegion: {region.Comments} | Earthing Area: {FormatNumber(earthingAreaM2)} m2";
    }

    private static void SetComments(Element element, string comments)
    {
      Parameter parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) ?? element.LookupParameter("Comments");
      if (parameter != null && !parameter.IsReadOnly)
        parameter.Set(comments);
    }

    private static void ExportCsv(string path, List<NMKMTO_ModelModelDataRow> rows, string earthingHeader)
    {
      var builder = new StringBuilder();
      var headers = new[] { "No", "Sequence", "Distributed Top Area (m2)", "Distributed Bottom Area (m2)", earthingHeader, "Floor Area (m2)", "Floor Volume (m3)" };
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

    private static string BuildEarthingHeader(SortedSet<string> typeNames)
    {
      string typeName = typeNames.Count == 0 ? "EARTHING REO" : string.Join(" / ", typeNames);
      return $"Earthing Reo ({typeName}) (m2)";
    }

    private static List<NMKMTO_ModelModelDataRow> SortRows(List<NMKMTO_ModelModelDataRow> rows)
    {
      return rows.OrderBy(row => GetSequenceValue(row), NaturalStringComparer.Instance)
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
      public int Compare(string x, string y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
    }

    private static void ExportWarnings(string path, List<string> warnings)
    {
      var builder = new StringBuilder();
      builder.AppendLine("No,Warning");
      for (int i = 0; i < warnings.Count; i++)
        builder.AppendLine(string.Join(",", new[] { (i + 1).ToString(CultureInfo.InvariantCulture), EscapeCsv(warnings[i]) }));
      File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static string BuildBaseExportFileName(Document doc, List<NMKMTO_ModelSheetRow> sheets, NMKMTO_ModelModelDataOptions options, string suffixType)
    {
      string datePrefix = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
      string buildingName = GetProjectInformationName(doc);
      string levelName = GetExportLevelName(sheets);
      return SanitizeFileName($"{datePrefix}_MTO_{buildingName}_{levelName}_{suffixType}");
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
      var levels = sheets.Select(x => x.LevelName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      if (levels.Count == 1)
        return levels[0];
      return levels.Count == 0 ? "LEVEL" : "MULTI LEVEL";
    }

    private static string SanitizeFileName(string value)
    {
      string sanitized = value;
      foreach (char invalidChar in Path.GetInvalidFileNameChars())
        sanitized = sanitized.Replace(invalidChar, '_');
      return sanitized.Trim();
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

    private static string NormalizeName(string value) => (value ?? string.Empty).Trim();
    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        return value;
      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
