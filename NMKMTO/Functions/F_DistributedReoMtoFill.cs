using Autodesk.Revit.DB;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_DistributedReoMtoFill
  {
    public static NMKMTO_ModelActionResult CreateOrUpdate(
      Document doc,
      IEnumerable<NMKMTO_ModelSheetRow> selectedSheets,
      string exportFolder)
    {
      return new NMKMTO_ModelActionResult
      {
        TotalCount = 0,
        Message = "DISTRIBUTED REO MTO Fill logic is cleared."
      };
    }
  }
}
