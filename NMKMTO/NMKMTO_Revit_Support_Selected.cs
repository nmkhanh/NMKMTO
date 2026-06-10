using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKMTO.Functions;
using System.Windows;

namespace NMKMTO
{
  [Transaction(TransactionMode.Manual)]
  public class NMKMTO_Revit_Support_Selected : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        UIDocument uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
          throw new InvalidOperationException("No active Revit document.");

        var result = SUPPORT.ExecuteSelectedWithDirectionArrows(uidoc);
        System.Windows.MessageBox.Show(
          result.Message,
          "NMKMTO SUPPORT SELECTED",
          MessageBoxButton.OK,
          result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.Message;
        System.Windows.MessageBox.Show($"Error: {ex.Message}\n\n{ex.StackTrace}", "NMKMTO SUPPORT SELECTED", MessageBoxButton.OK, MessageBoxImage.Error);
        return Result.Failed;
      }
    }
  }
}
