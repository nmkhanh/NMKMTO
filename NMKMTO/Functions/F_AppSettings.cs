using System.IO;
using Newtonsoft.Json;
using NMKMTO.Models;

namespace NMKMTO.Functions
{
  public static class F_AppSettings
  {
    private static readonly string SettingsFolder = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "NMKMTO");

    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

    public static NMKMTO_ModelAppSettings Load()
    {
      try
      {
        if (!File.Exists(SettingsPath))
          return new NMKMTO_ModelAppSettings();

        string json = File.ReadAllText(SettingsPath);
        return JsonConvert.DeserializeObject<NMKMTO_ModelAppSettings>(json) ?? new NMKMTO_ModelAppSettings();
      }
      catch
      {
        return new NMKMTO_ModelAppSettings();
      }
    }

    public static void Save(NMKMTO_ModelAppSettings settings)
    {
      Directory.CreateDirectory(SettingsFolder);
      string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
      File.WriteAllText(SettingsPath, json);
    }
  }
}
