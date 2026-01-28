using Newtonsoft.Json;

public class MachineMaterialDto
{
  [JsonProperty("machineCode")]
  public string MachineCode { get; set; }
  [JsonProperty("materialLotId")]
  public long MaterialLotId { get; set; }
  [JsonProperty("materialName")]
  public string MaterialName { get; set; }
  [JsonProperty("remainQty")]
  public double RemainQty { get; set; }
}