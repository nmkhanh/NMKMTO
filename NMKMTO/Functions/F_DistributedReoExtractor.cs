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
    private const string DirectShapeApplicationId = "NMKMTO_DISTRIBUTED_REO";
    private const double MinSolidVolumeFt3 = 0.0001;
    private const double Ft2ToM2 = 0.09290304;
    private const double MmToFt = 1.0 / 304.8;
    private const string MtoPrefix = "MTO_";

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static NMKMTO_ModelDistributedReoResult Extract(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      NMKMTO_ModelModelDataOptions options)
    {
      if (string.IsNullOrWhiteSpace(options.ExportFolder))
        throw new InvalidOperationException("Export folder is empty.");

      Directory.CreateDirectory(options.ExportFolder);

      var warnings = new List<string>();
      var sheets = selectedSheets.ToList();
      var rows = new List<NMKMTO_ModelModelDataRow>();
      var directShapeItems = new List<DirectShapeData>();
      int rowNo = 1;

      foreach (var sheet in sheets)
      {
        var overViews = F_SheetViewCollector.GetViewsByKeyword(doc, new[] { sheet }, F_MtoViewNames.OverViewKeyword)
          .ToList();
        bool isTopSheet = IsTopSheet(sheet.SheetName);
        bool isBottomSheet = IsBottomSheet(sheet.SheetName);

        if (!isTopSheet && !isBottomSheet)
          continue;

        foreach (var sourceView in overViews)
        {
          var mtoView = FindMtoView(doc, sourceView.Name);
          if (mtoView == null)
          {
            warnings.Add($"MTO view not found: {MtoPrefix}{sourceView.Name}");
            continue;
          }

          var fills = CollectFilledRegions(doc, mtoView);
          if (fills.Count == 0)
          {
            warnings.Add($"MTO view '{mtoView.Name}' has no FilledRegion.");
            continue;
          }

          for (int i = 0; i < fills.Count; i++)
          {
            Solid solid = CreateSolidFromFilledRegion(fills[i].Region);
            Solid remaining = solid;

            for (int j = 0; j < fills.Count; j++)
            {
              if (i == j || remaining == null || remaining.Volume <= MinSolidVolumeFt3)
                continue;

              Solid other = CreateSolidFromFilledRegion(fills[j].Region);
              Solid difference = Difference(remaining, other);
              if (difference != null && difference.Volume > MinSolidVolumeFt3)
                remaining = difference;
            }

            double areaM2 = remaining == null ? 0 : GetTopFaceArea(remaining) * Ft2ToM2;
            var row = new NMKMTO_ModelModelDataRow
            {
              No = rowNo++,
              Pour = string.IsNullOrWhiteSpace(sheet.PourName) ? string.Empty : sheet.PourName,
              Zone = sheet.ZoneName,
              Level = sheet.LevelName,
              DistributedTopAreaM2 = isTopSheet ? areaM2 : 0,
              DistributedBottomAreaM2 = isBottomSheet ? areaM2 : 0
            };
            rows.Add(row);

            if (options.Create3d && remaining != null && remaining.Volume > MinSolidVolumeFt3)
            {
              directShapeItems.Add(new DirectShapeData
              {
                Name = $"NMKMTO DISTRIBUTED REO {row.No}",
                Comments = BuildDirectShapeComments(row, fills[i], mtoView),
                Solid = remaining
              });
            }
          }
        }
      }

      if (options.Create3d)
      {
        using (var transaction = new Transaction(doc, "NMKMTO DISTRIBUTED REO DirectShape"))
        {
          transaction.Start();
          DeleteExistingDirectShapes(doc);
          CreateDirectShapes(doc, directShapeItems);
          transaction.Commit();
        }
      }

      rows = SortRows(rows);
      RenumberRows(rows);

      string baseFileName = BuildBaseExportFileName(doc, sheets, "DISTRIBUTED_REO");
      string exportPath = Path.Combine(options.ExportFolder, $"{baseFileName}.csv");
      string warningPath = warnings.Count > 0 ? Path.Combine(options.ExportFolder, $"{baseFileName}_WARNING.csv") : string.Empty;
      ExportCsv(exportPath, rows);
      if (warnings.Count > 0)
        ExportWarnings(warningPath, warnings);

      var result = new NMKMTO_ModelDistributedReoResult
      {
        SheetCount = sheets.Count,
        DistributedTopAreaM2 = rows.Sum(x => x.DistributedTopAreaM2),
        DistributedBottomAreaM2 = rows.Sum(x => x.DistributedBottomAreaM2),
        ExportPath = exportPath,
        WarningPath = warningPath,
        Message = warnings.Count > 0
          ? $"DISTRIBUTED REO exported: {exportPath}\nWarning file: {warningPath}"
          : $"DISTRIBUTED REO exported: {exportPath}"
      };
      foreach (var warning in warnings)
        result.Warnings.Add(warning);

      return result;
    }

    private sealed class FillData
    {
      public FilledRegion Region { get; set; }
      public string TypeName { get; set; } = string.Empty;
      public string Comments { get; set; } = string.Empty;
    }

    private sealed class DirectShapeData
    {
      public string Name { get; set; } = string.Empty;
      public string Comments { get; set; } = string.Empty;
      public Solid Solid { get; set; }
    }

    private static Autodesk.Revit.DB.View FindMtoView(Document doc, string sourceViewName)
    {
      string mtoViewName = MtoPrefix + sourceViewName;
      return new FilteredElementCollector(doc)
        .OfClass(typeof(Autodesk.Revit.DB.View))
        .Cast<Autodesk.Revit.DB.View>()
        .FirstOrDefault(view => !view.IsTemplate && view.Name.Equals(mtoViewName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<FillData> CollectFilledRegions(Document doc, Autodesk.Revit.DB.View view)
    {
      return new FilteredElementCollector(doc, view.Id)
        .OfClass(typeof(FilledRegion))
        .Cast<FilledRegion>()
        .Select(region => new FillData
        {
          Region = region,
          TypeName = doc.GetElement(region.GetTypeId())?.Name ?? string.Empty,
          Comments = region.LookupParameter("Comments")?.AsString() ?? string.Empty
        })
        .Where(data => IsMtoDistributedFillType(data.TypeName))
        .ToList();
    }

    private static bool IsMtoDistributedFillType(string typeName)
    {
      return typeName.Equals("MTO VER", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("MTO HOR", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("MTO NONE", StringComparison.OrdinalIgnoreCase);
    }

    private static Solid CreateSolidFromFilledRegion(FilledRegion region)
    {
      IList<CurveLoop> loops = region.GetBoundaries();
      if (loops == null || loops.Count == 0)
        return null;

      return GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 10 * MmToFt);
    }

    private static Solid Difference(Solid first, Solid second)
    {
      try
      {
        return BooleanOperationsUtils.ExecuteBooleanOperation(first, second, BooleanOperationsType.Difference);
      }
      catch
      {
        return first;
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

    private static void CreateDirectShapes(Document doc, List<DirectShapeData> items)
    {
      int index = 1;
      foreach (var item in items.Where(x => x.Solid != null && x.Solid.Volume > MinSolidVolumeFt3))
      {
        var directShape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
        directShape.ApplicationId = DirectShapeApplicationId;
        directShape.ApplicationDataId = index.ToString(CultureInfo.InvariantCulture);
        directShape.Name = item.Name;
        directShape.SetShape(new List<GeometryObject> { item.Solid });
        SetComments(directShape, item.Comments);
        index++;
      }
    }

    private static string BuildDirectShapeComments(NMKMTO_ModelModelDataRow row, FillData fill, Autodesk.Revit.DB.View view)
    {
      return $"NMKMTO DISTRIBUTED REO | View: {view.Name} | Sequence: {GetSequenceValue(row)} | Type: {fill.TypeName} | Top: {FormatNumber(row.DistributedTopAreaM2)} m2 | Bottom: {FormatNumber(row.DistributedBottomAreaM2)} m2";
    }

    private static void SetComments(Element element, string comments)
    {
      Parameter parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) ?? element.LookupParameter("Comments");
      if (parameter != null && !parameter.IsReadOnly)
        parameter.Set(comments);
    }

    private static void ExportCsv(string path, List<NMKMTO_ModelModelDataRow> rows)
    {
      var builder = new StringBuilder();
      var headers = new[] { "No", "Sequence", "Distributed Top Area (m2)", "Distributed Bottom Area (m2)", "Earthing Reo (m2)", "Floor Area (m2)", "Floor Volume (m3)" };
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

    private static string BuildBaseExportFileName(Document doc, List<NMKMTO_ModelSheetRow> sheets, string suffixType)
    {
      string datePrefix = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
      string buildingName = doc.ProjectInformation?.Name ?? doc.Title;
      string levelName = GetExportLevelName(sheets);
      return SanitizeFileName($"{datePrefix}_MTO_{buildingName}_{levelName}_{suffixType}");
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

    private static bool IsTopSheet(string sheetName)
    {
      return sheetName.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBottomSheet(string sheetName)
    {
      return sheetName.IndexOf("BOTTOM", StringComparison.OrdinalIgnoreCase) >= 0
        || sheetName.IndexOf("BTM", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value)
    {
      if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        return value;
      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
