using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NMKMTO.Models;

namespace NMKMTO
{
  /// <summary>
  /// Interaction logic for NMKMTO_Window.xaml
  /// </summary>
  public partial class NMKMTO_Window : Window
  {
    public NMKMTOViewModel VM { get; set; }
    private bool _didLoadSheets;

    public NMKMTO_Window()
    {
      InitializeComponent();
      this.DataContext = VM = new NMKMTOViewModel();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
      if (_didLoadSheets)
        return;

      _didLoadSheets = true;
      if (VM.LoadSheetsCommand.CanExecute(null))
        VM.LoadSheetsCommand.Execute(null);
    }

    private void SheetCheckBox_Changed(object sender, RoutedEventArgs e)
    {
      if (sender is not System.Windows.Controls.CheckBox checkBox)
        return;

      if (checkBox.DataContext is not NMKMTO_ModelSheetRow currentRow)
        return;

      bool value = checkBox.IsChecked == true;
      var selectedRows = SheetDataGrid.SelectedItems
        .OfType<NMKMTO_ModelSheetRow>()
        .ToList();

      if (!selectedRows.Contains(currentRow) || selectedRows.Count <= 1)
        return;

      foreach (var row in selectedRows)
        row.IsSelected = value;
    }
  }
}
