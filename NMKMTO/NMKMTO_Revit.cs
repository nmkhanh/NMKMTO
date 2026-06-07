using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Revit.Async;
using System.Windows;
using System.Windows.Interop;
namespace NMKMTO
{
  [Transaction(TransactionMode.Manual)]
  public class NMKMTO_Revit : IExternalCommand
  {
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
      try
      {
        UIApplication uiapp = commandData.Application;

        var hwndSource = HwndSource.FromHwnd(uiapp.MainWindowHandle);
        Window? revit = hwndSource.RootVisual as Window;

        RevitTask.Initialize(uiapp);

        NMKMTO_Window window = new NMKMTO_Window();
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Owner = revit;
        window.Show();

        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        System.Windows.MessageBox.Show($"Error: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        return Result.Failed;
      }
    }

  }
}
