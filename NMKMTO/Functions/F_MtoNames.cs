namespace NMKMTO.Functions
{
  public static class F_MtoNames
  {
    public static class Views
    {
      public const string FilledRegionAreaTemplate = "MTO FILLED REGION AREA";
      public const string OverKeyword = "OVER";
      public const string UnderKeyword = "UNDER";
    }

    public static class Families
    {
      public const string ZBar = "Reo__ZBar[Rinco]";
      public const string Distribution = "Reo__Reinforcement_DistributionAdjustable[Rinco]";
    }

    public static class Parameters
    {
      public const string Arrow1Length = "Arrow 1 Length";
      public const string ArrowBot = "Arrow Bot";
      public const string ArrowTop = "Arrow Top";
      public const string BarFromArrow1 = "Bar From Arrow 1";
      public const string BuildingName = "Building Name";
      public const string CogCount = "Shared_Cog_Count";
      public const string Comments = "Comments";
      public const string ConsBar = "Cons Bar";
      public const string Diameter = "Shared_Diameter";
      public const string Length = "Shared_Length";
      public const string Mark = "Mark";
      public const string Measure = "Shared_Measure";
      public const string NoBars = "Shared_No. Bars";
      public const string Notation = "Notation";
      public const string RincoZone = "RINCO_ZONE";
      public const string SheetSeries = "RINCO_TB_SHEET SERIES";
      public const string Spacing = "Shared_Spacing";
      public const string Splice = "Splice";
      public const string Thickness = "Thickness";
      public const string TopBarLocation = "Top Bar Location";
    }

    public static class FamilyPrefixes
    {
      public const string ZBar = "Reo__ZBar";
    }

    public static class TagFamilies
    {
      public const string Reo = "RINCO_TAG_Reo";
    }

    public static class TagTypes
    {
      public const string Reo = "Reo Tag";
    }

    public static class GraphicStyles
    {
      public const string Reo = "Reo";
    }

    public static class DirectShapeApplications
    {
      public const string Model = "NMKMTO_MODEL";
      public const string EarthingReo = "NMKMTO_EARTHING_REO";
      public const string Reo = "NMKMTO_REO_MTO";
      public const string DistributedReo = "NMKMTO_DISTRIBUTED_REO_MTO";
    }

    public static class Keywords
    {
      public const string Bottom = "BOTTOM";
      public const string Cj = "C.J";
      public const string Pour = "POUR";
      public const string Precast = "PRECAST";
      public const string Plinth = "PLINTH";
      public const string ReoFamilyPrefix = "Reo__";
      public const string ReinforcementSheetName = "REIN";
      public const string Scj = "S C.J";
      public const string Shear = "SHEAR";
      public const string Slimdeck = "SLIMDECK";
      public const string Top = "TOP";
      public const string UBar = "U'BAR";
      public const string Ucj = "U C.J";
      public const string Zone = "ZONE";
    }

    public static class Values
    {
      public const string ReinforcementSheetSeries = "S50000 SERIES - PT&REO";
    }

    public static bool IsZBarFamily(string familyName)
    {
      return (familyName ?? string.Empty).StartsWith(Families.ZBar, StringComparison.OrdinalIgnoreCase)
        || (familyName ?? string.Empty).StartsWith(FamilyPrefixes.ZBar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDistributionFamily(string familyName)
    {
      return (familyName ?? string.Empty).StartsWith(Families.Distribution, StringComparison.OrdinalIgnoreCase);
    }
  }
}
