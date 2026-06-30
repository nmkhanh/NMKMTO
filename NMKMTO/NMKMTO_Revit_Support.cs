using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NMKMTO.Functions;
using System.Windows;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace NMKMTO
{
  [Transaction(TransactionMode.Manual)]
  public class NMKMTO_Revit_Support : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;
        UIDocument uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null)
          throw new InvalidOperationException("No active Revit document.");

        var directionDialog = new TaskDialog("NMKMTO SUPPORT")
        {
          MainInstruction = "Chọn phương thép cần tạo FilledRegion",
          MainContent = "Các family trong active view sẽ được lọc theo phương Reo chính.",
          CommonButtons = TaskDialogCommonButtons.Cancel
        };
        directionDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ngang", "Tạo fill cho thép có phương ngang.");
        directionDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Dọc", "Tạo fill cho thép có phương dọc.");

        TaskDialogResult directionResult = directionDialog.Show();
        if (directionResult == TaskDialogResult.Cancel)
          return Result.Cancelled;

        int directionMode = directionResult == TaskDialogResult.CommandLink2 ? 2 : 1;
        var result = SUPPORT.Execute(uidoc, false, directionMode);
        System.Windows.MessageBox.Show(
          result.Message,
          "NMKMTO SUPPORT",
          MessageBoxButton.OK,
          result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        message = ex.Message;
        System.Windows.MessageBox.Show($"Error: {ex.Message}\n\n{ex.StackTrace}", "NMKMTO SUPPORT", MessageBoxButton.OK, MessageBoxImage.Error);
        return Result.Failed;
      }
    }
  }
}
