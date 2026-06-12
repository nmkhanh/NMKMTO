using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKMTO.Functions;
using System.Windows;

namespace NMKMTO
{
  [Transaction(TransactionMode.Manual)]
  public class NMKMTO_Revit_Distributed : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc == null)
          throw new InvalidOperationException("No active Revit document.");

        var result = DISTRIBUTED.Execute(uidoc);
        System.Windows.MessageBox.Show(
          result.Message,
          "NMKMTO DISTRIBUTED",
          MessageBoxButton.OK,
          result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.Message;
        System.Windows.MessageBox.Show(
          $"Error: {ex.Message}\n\n{ex.StackTrace}",
          "NMKMTO DISTRIBUTED",
          MessageBoxButton.OK,
          MessageBoxImage.Error);
        return Result.Failed;
      }
    }
  }
}
