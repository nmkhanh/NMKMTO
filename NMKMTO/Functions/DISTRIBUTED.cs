using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class DISTRIBUTED
  {
    public static NMKMTO_ModelActionResult Execute(UIDocument uidoc)
    {
      if (uidoc == null)
        throw new ArgumentNullException(nameof(uidoc));

      Document doc = uidoc.Document;
      Autodesk.Revit.DB.View view = doc.ActiveView;
      const string applicationId = "NMKMTO_DISTRIBUTED";
      double thickness = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
      double minimumVolume = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.CubicMillimeters);
      var warnings = new List<string>();

      #region 01 - Get calculated CurveLoops directly from SUPPORT

      var boundaryGroups = new List<List<CurveLoop>>();

      NMKMTO_ModelActionResult supportResult = SUPPORT.Execute(
        uidoc,
        selectedOnly: false,
        directionMode: 3,
        groupResults: false,
        createFilledRegions: false,
        calculatedBoundaries: boundaryGroups);
      warnings.AddRange(supportResult.Warnings);

      if (boundaryGroups.Count == 0)
        throw new InvalidOperationException("SUPPORT did not return any valid reinforcement region boundary.");

      #endregion

      #region 02 - Create one 10 mm solid for every calculated region

      var sourceSolids = new List<Solid>();
      XYZ extrusionDirection = view.ViewDirection.Normalize();

      foreach (List<CurveLoop> boundaries in boundaryGroups)
      {
        try
        {
          Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
            boundaries,
            extrusionDirection,
            thickness);

          if (solid != null && solid.Volume > minimumVolume)
            sourceSolids.Add(solid);
        }
        catch (Exception ex)
        {
          warnings.Add($"Create 10 mm solid: {ex.Message}");
        }
      }

      if (sourceSolids.Count == 0)
        throw new InvalidOperationException("No valid 10 mm reinforcement solid could be created.");

      #endregion

      #region 03 - Keep only solid portions that do not intersect any other solid

      var nonIntersectingSolids = new List<Solid>();

      for (int sourceIndex = 0; sourceIndex < sourceSolids.Count; sourceIndex++)
      {
        Solid remaining = sourceSolids[sourceIndex];

        for (int otherIndex = 0; otherIndex < sourceSolids.Count; otherIndex++)
        {
          if (sourceIndex == otherIndex || remaining == null || remaining.Volume <= minimumVolume)
            continue;

          try
          {
            Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
              remaining,
              sourceSolids[otherIndex],
              BooleanOperationsType.Intersect);

            if (intersection == null || intersection.Volume <= minimumVolume)
              continue;

            remaining = BooleanOperationsUtils.ExecuteBooleanOperation(
              remaining,
              sourceSolids[otherIndex],
              BooleanOperationsType.Difference);
          }
          catch (Exception ex)
          {
            warnings.Add(
              $"Boolean operation between solids {sourceIndex + 1} and {otherIndex + 1}: {ex.Message}");
          }
        }

        if (remaining != null && remaining.Volume > minimumVolume)
          nonIntersectingSolids.Add(remaining);
      }

      if (nonIntersectingSolids.Count == 0)
        throw new InvalidOperationException("No non-intersecting solid remains.");

      #endregion

      #region 04 - Create DirectShape only

      using (var transaction = new Transaction(doc, "DISTRIBUTED DirectShape"))
      {
        transaction.Start();

        List<ElementId> oldDirectShapeIds = new FilteredElementCollector(doc)
          .OfClass(typeof(DirectShape))
          .Cast<DirectShape>()
          .Where(shape => shape.ApplicationId == applicationId)
          .Select(shape => shape.Id)
          .ToList();

        if (oldDirectShapeIds.Count > 0)
          doc.Delete(oldDirectShapeIds);

        DirectShape directShape = DirectShape.CreateElement(
          doc,
          new ElementId(BuiltInCategory.OST_GenericModel));
        directShape.ApplicationId = applicationId;
        directShape.ApplicationDataId = view.Id.ToString();
        directShape.Name = $"DISTRIBUTED NON-INTERSECT - {view.Name}";
        directShape.SetShape(nonIntersectingSolids.Cast<GeometryObject>().ToList());

        Parameter comments = directShape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
          ?? directShape.LookupParameter("Comments");
        if (comments != null && !comments.IsReadOnly)
        {
          comments.Set(
            $"DISTRIBUTED | View: {view.Name} | Source regions: {boundaryGroups.Count} | Non-intersecting solids: {nonIntersectingSolids.Count} | Thickness: 10 mm");
        }

        /*
        Group creation intentionally disabled.
        DISTRIBUTED now commits only the final DirectShape.
        SUPPORT FilledRegions are temporary and rolled back.
        */

        transaction.Commit();
      }

      #endregion

      #region 05 - Result

      var result = new NMKMTO_ModelActionResult
      {
        TotalCount = nonIntersectingSolids.Count,
        Message =
          $"DISTRIBUTED completed\n" +
          $"View: {view.Name}\n" +
          $"Calculated regions: {boundaryGroups.Count}\n" +
          $"Source solids: {sourceSolids.Count}\n" +
          $"Non-intersecting solids: {nonIntersectingSolids.Count}\n" +
          $"Created FilledRegions: 0\n" +
          $"Created groups: 0\n" +
          $"DirectShape thickness: 10 mm"
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
