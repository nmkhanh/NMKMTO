namespace NMKMTO.Models
{
  public enum MtoProjectScale
  {
    Small,
    Medium,
    Large
  }

  public sealed class NMKMTO_ModelProjectScope
  {
    public MtoProjectScale Scale { get; set; } = MtoProjectScale.Small;
    public List<NMKMTO_ModelScopeNode> ScopeNodes { get; } = new();
  }
}
