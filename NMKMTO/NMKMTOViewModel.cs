using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NMKMTO.Functions;
using NMKMTO.Models;
using Revit.Async;

namespace NMKMTO
{
  public partial class NMKMTOViewModel : ObservableObject
  {
    public NMKMTOViewModel()
    {
      var settings = F_AppSettings.Load();
      _exportFolder = settings.ExportFolder;

      SheetsView = CollectionViewSource.GetDefaultView(Sheets);
      SheetsView.Filter = FilterSheet;
    }

    public ObservableCollection<NMKMTO_ModelSheetRow> Sheets { get; } = new();
    public ICollectionView SheetsView { get; }

    [ObservableProperty]
    private string _exportFolder = string.Empty;

    [ObservableProperty]
    private string _sheetFilterText = "L 3";

    [ObservableProperty]
    private string _filledRegionViewName = F_MtoViewNames.FilledRegionAreaTemplate;

    [ObservableProperty]
    private bool _isAllSheetsChecked;

    [ObservableProperty]
    private bool _exportReo;

    [ObservableProperty]
    private bool _exportDistributedReo;

    [ObservableProperty]
    private bool _exportEarthingReo = true;

    [ObservableProperty]
    private bool _exportModel;

    [ObservableProperty]
    private bool _export3d = true;

    [ObservableProperty]
    private string _defaultWastePercent = "0";

    [ObservableProperty]
    private string _defaultConcreteDensity = "2400";

    [ObservableProperty]
    private string _defaultModelTopOffset = "1000";

    [ObservableProperty]
    private string _defaultModelBottomOffset = "1000";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _checkedSheetCount;

    partial void OnExportFolderChanged(string value)
    {
      F_AppSettings.Save(new NMKMTO_ModelAppSettings { ExportFolder = value });
    }

    partial void OnSheetFilterTextChanged(string value)
    {
      SheetsView.Refresh();
    }

    partial void OnIsAllSheetsCheckedChanged(bool value)
    {
      foreach (var sheet in SheetsView.Cast<NMKMTO_ModelSheetRow>())
        sheet.IsSelected = value;

      UpdateCheckedSheetCount();
    }

    [RelayCommand]
    private void BrowseExportFolder()
    {
      using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
      {
        dialog.Description = "Select export folder";
#if NET5_0_OR_GREATER
        dialog.UseDescriptionForTitle = true;
#endif

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
          ExportFolder = dialog.SelectedPath;
      }
    }

    [RelayCommand]
    private async Task LoadSheets()
    {
      try
      {
        StatusText = "Loading sheets...";
        List<NMKMTO_ModelSheetRow> rows = new();

        await RevitTask.RunAsync(uiapp =>
        {
          rows = F_SheetCollector.CollectSheets(uiapp.ActiveUIDocument.Document, FilledRegionViewName);
        });

        Sheets.Clear();
        foreach (var row in rows)
        {
          row.IsSelectedChanged += Sheet_IsSelectedChanged;
          Sheets.Add(row);
        }

        SheetsView.Refresh();
        UpdateCheckedSheetCount();
        StatusText = $"Loaded {Sheets.Count} sheets";
      }
      catch (Exception ex)
      {
        StatusText = "Load sheets failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task AutoMark()
    {
      try
      {
        StatusText = "Auto Mark...";
        var result = new NMKMTO_ModelActionResult();
        var selectedSheets = GetSelectedSheetsOrThrow();
        await RevitTask.RunAsync(uiapp =>
        {
          var doc = uiapp.ActiveUIDocument.Document;
          var overViews = F_SheetViewCollector.GetViewsByKeyword(doc, selectedSheets, F_MtoViewNames.OverViewKeyword);
          if (overViews.Count == 0)
            throw new InvalidOperationException("Selected sheets do not contain any OVER views.");

          result = F_AutoMark.Execute(doc, overViews, ExportFolder);
        });

        StatusText = $"Auto Mark completed: {result.TotalCount}";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO Auto Mark", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "Auto Mark failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO Auto Mark", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task ConsBar()
    {
      try
      {
        StatusText = "Cons Bar...";
        var result = new NMKMTO_ModelActionResult();
        var selectedSheets = GetSelectedSheetsOrThrow();
        await RevitTask.RunAsync(uiapp =>
        {
          var doc = uiapp.ActiveUIDocument.Document;
          var overViews = F_SheetViewCollector.GetViewsByKeyword(doc, selectedSheets, F_MtoViewNames.OverViewKeyword);
          if (overViews.Count == 0)
            throw new InvalidOperationException("Selected sheets do not contain any OVER views.");

          result = F_ConsBar.Execute(doc, overViews, ExportFolder);
        });

        StatusText = $"Cons Bar completed: {result.TotalCount}";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO Cons Bar", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "Cons Bar failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO Cons Bar", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private void NumberShear()
    {
      try
      {
        var sourceSheets = Sheets.Where(sheet => sheet.IsSelected).ToList();
        if (sourceSheets.Count == 0)
          sourceSheets = Sheets.ToList();

        var result = F_NumberShearExporter.Export(sourceSheets, ExportFolder);
        StatusText = $"Number Shear exported: {result.TotalCount}";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO Number Shear", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "Number Shear failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO Number Shear", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task CreateDistributedMtoFill()
    {
      try
      {
        StatusText = "Creating DISTRIBUTED REO MTO fill...";
        var selectedSheets = GetSelectedSheetsOrThrow();
        var result = new NMKMTO_ModelActionResult();

        await RevitTask.RunAsync(uiapp =>
        {
          result = F_DistributedReoMtoFill.CreateOrUpdate(uiapp.ActiveUIDocument.Document, selectedSheets, ExportFolder);
        });

        StatusText = $"DISTRIBUTED REO MTO fill: {result.TotalCount}";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO Distributed Reo", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "DISTRIBUTED REO MTO fill failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO Distributed Reo", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task Export()
    {
      if (!ExportReo && !ExportDistributedReo && !ExportEarthingReo && !ExportModel)
      {
        System.Windows.MessageBox.Show("Please select at least one export type.", "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
      }

      var selectedSheets = Sheets.Where(x => x.IsSelected).ToList();
      if (selectedSheets.Count == 0)
      {
        System.Windows.MessageBox.Show("Please select at least one sheet.", "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
      }

      try
      {
        StatusText = "Exporting combined MTO data...";
        var result = new NMKMTO_ModelActionResult();
        var options = CreateExportOptions();

        await RevitTask.RunAsync(uiapp =>
        {
          result = F_CombinedMtoExporter.Execute(
            uiapp.ActiveUIDocument.Document,
            selectedSheets,
            options,
            ExportReo,
            ExportDistributedReo,
            ExportEarthingReo,
            ExportModel);
        });

        StatusText = $"Combined MTO exported: {result.ExportPath}";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "Combined MTO export failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    private async Task GetReoData()
    {
      var selectedSheets = Sheets.Where(x => x.IsSelected).ToList();
      if (selectedSheets.Count == 0)
      {
        System.Windows.MessageBox.Show("Please select at least one sheet.", "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
      }

      try
      {
        StatusText = "Getting REO data...";
        var result = new NMKMTO_ModelActionResult();
        var options = CreateExportOptions();

        await RevitTask.RunAsync(uiapp =>
        {
          result = F_ReoExtractor.Execute(uiapp.ActiveUIDocument.Document, selectedSheets, options);
        });

        StatusText = $"REO exported: {result.TotalCount} rows";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO REO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "REO failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO REO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task GetModelData()
    {
      var selectedSheets = Sheets.Where(x => x.IsSelected).ToList();
      if (selectedSheets.Count == 0)
      {
        System.Windows.MessageBox.Show("Please select at least one sheet.", "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
      }

      try
      {
        StatusText = "Getting MODEL data...";
        var result = new NMKMTO_ModelModelDataResult();
        var options = new NMKMTO_ModelModelDataOptions
        {
          ExportFolder = ExportFolder,
          FilledRegionViewName = FilledRegionViewName,
          TopOffsetMm = ParseDouble(DefaultModelTopOffset, 1000),
          BottomOffsetMm = ParseDouble(DefaultModelBottomOffset, 1000),
          Create3d = Export3d,
          TotalExportTypeCount = 5
        };
        if (ExportReo)
          options.CheckedExportTypes.Add("REO");
        if (ExportDistributedReo)
          options.CheckedExportTypes.Add("DISTRIBUTED REO");
        if (ExportEarthingReo)
          options.CheckedExportTypes.Add("EARTHING REO");
        if (ExportModel)
          options.CheckedExportTypes.Add("MODEL");
        if (Export3d)
          options.CheckedExportTypes.Add("3D");

        await RevitTask.RunAsync(uiapp =>
        {
          result = F_ModelDataExtractor.Extract(uiapp.ActiveUIDocument.Document, selectedSheets, options);
        });

        StatusText = $"MODEL exported: {result.Rows.Count} rows, {result.FloorSurfaceArea:0.###} m2, {result.FloorVolume:0.###} m3";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO MODEL", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "MODEL failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO MODEL", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task GetEarthingReoData()
    {
      var selectedSheets = Sheets.Where(x => x.IsSelected).ToList();
      if (selectedSheets.Count == 0)
      {
        System.Windows.MessageBox.Show("Please select at least one sheet.", "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
      }

      try
      {
        StatusText = "Getting EARTHING REO data...";
        var result = new NMKMTO_ModelEarthingReoResult();
        var options = CreateExportOptions();

        await RevitTask.RunAsync(uiapp =>
        {
          result = F_EarthingReoExtractor.Extract(uiapp.ActiveUIDocument.Document, selectedSheets, options);
        });

        StatusText = $"EARTHING REO exported: {result.EarthingAreaM2:0.###} m2";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO EARTHING REO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "EARTHING REO failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO EARTHING REO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    [RelayCommand]
    private async Task GetDistributedReoData()
    {
      var selectedSheets = Sheets.Where(x => x.IsSelected).ToList();
      if (selectedSheets.Count == 0)
      {
        System.Windows.MessageBox.Show("Please select at least one sheet.", "NMKMTO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
      }

      try
      {
        StatusText = "Getting DISTRIBUTED REO data...";
        var result = new NMKMTO_ModelDistributedReoResult();
        var options = CreateExportOptions();

        await RevitTask.RunAsync(uiapp =>
        {
          result = F_DistributedReoExtractor.Extract(uiapp.ActiveUIDocument.Document, selectedSheets, options);
        });

        StatusText = $"DISTRIBUTED REO exported: Top {result.DistributedTopAreaM2:0.###} m2, Bottom {result.DistributedBottomAreaM2:0.###} m2";
        System.Windows.MessageBox.Show(result.Message, "NMKMTO DISTRIBUTED REO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
      }
      catch (Exception ex)
      {
        StatusText = "DISTRIBUTED REO failed";
        System.Windows.MessageBox.Show(ex.Message, "NMKMTO DISTRIBUTED REO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
      }
    }

    private bool FilterSheet(object item)
    {
      if (item is not NMKMTO_ModelSheetRow sheet)
        return false;

      if (string.IsNullOrWhiteSpace(SheetFilterText))
        return true;

      string filter = SheetFilterText.Trim();
      return Contains(sheet.SheetNumber, filter)
        || Contains(sheet.SheetName, filter)
        || Contains(sheet.LevelName, filter)
        || Contains(sheet.ZoneName, filter)
        || Contains(sheet.PourName, filter);
    }

    private static bool Contains(string source, string filter)
    {
      return source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static double ParseDouble(string value, double fallback)
    {
      return double.TryParse(value, out double result) ? result : fallback;
    }

    private List<NMKMTO_ModelSheetRow> GetSelectedSheetsOrThrow()
    {
      var selectedSheets = Sheets.Where(x => x.IsSelected).ToList();
      if (selectedSheets.Count == 0)
        throw new InvalidOperationException("Please select at least one sheet.");

      return selectedSheets;
    }

    private NMKMTO_ModelModelDataOptions CreateExportOptions()
    {
      var options = new NMKMTO_ModelModelDataOptions
      {
        ExportFolder = ExportFolder,
        FilledRegionViewName = FilledRegionViewName,
        TopOffsetMm = ParseDouble(DefaultModelTopOffset, 1000),
        BottomOffsetMm = ParseDouble(DefaultModelBottomOffset, 1000),
        Create3d = Export3d,
        TotalExportTypeCount = 5
      };
      if (ExportReo)
        options.CheckedExportTypes.Add("REO");
      if (ExportDistributedReo)
        options.CheckedExportTypes.Add("DISTRIBUTED REO");
      if (ExportEarthingReo)
        options.CheckedExportTypes.Add("EARTHING REO");
      if (ExportModel)
        options.CheckedExportTypes.Add("MODEL");
      if (Export3d)
        options.CheckedExportTypes.Add("3D");

      return options;
    }

    private void Sheet_IsSelectedChanged(object sender, EventArgs e)
    {
      UpdateCheckedSheetCount();
    }

    private void UpdateCheckedSheetCount()
    {
      CheckedSheetCount = Sheets.Count(x => x.IsSelected);
    }
  }
}
