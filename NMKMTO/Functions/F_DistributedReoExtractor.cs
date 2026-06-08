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
      return new NMKMTO_ModelDistributedReoResult
      {
        SheetCount = selectedSheets?.Count() ?? 0,
        Message = "DISTRIBUTED REO logic is cleared."
      };
    }
  }
}
