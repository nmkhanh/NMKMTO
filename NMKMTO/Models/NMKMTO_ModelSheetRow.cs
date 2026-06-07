namespace NMKMTO.Models
{
  public sealed partial class NMKMTO_ModelSheetRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
  {
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected;

    public event EventHandler IsSelectedChanged;

    partial void OnIsSelectedChanged(bool value)
    {
      IsSelectedChanged?.Invoke(this, EventArgs.Empty);
    }

    public Autodesk.Revit.DB.ElementId SheetId { get; set; } = Autodesk.Revit.DB.ElementId.InvalidElementId;
    public string SheetNumber { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public string LevelName { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;
    public string PourName { get; set; } = string.Empty;
    public MtoSheetReinforcementLocation Location { get; set; } = MtoSheetReinforcementLocation.Unknown;
  }
}
