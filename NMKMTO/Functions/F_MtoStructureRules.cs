using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_MtoStructureRules
  {
    public static MtoProjectScale DetectScale(IEnumerable<NMKMTO_ModelFilledRegionInfo> regions)
    {
      bool hasPour = regions.Any(x => x.Comments.StartsWith(F_MtoNames.Keywords.Pour, StringComparison.OrdinalIgnoreCase));
      if (hasPour)
        return MtoProjectScale.Large;

      bool hasZone = regions.Any(x => x.Comments.StartsWith(F_MtoNames.Keywords.Zone, StringComparison.OrdinalIgnoreCase));
      return hasZone ? MtoProjectScale.Medium : MtoProjectScale.Small;
    }
  }
}
