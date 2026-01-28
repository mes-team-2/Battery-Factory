using Newtonsoft.Json;

public class ProductionData
{
  [JsonProperty("machineCode")]
  public string MachineCode { get; set; }

  [JsonProperty("timestamp")]
  public string Timestamp { get; set; }

  [JsonProperty("qty")]
  public int Qty { get; set; }

  [JsonProperty("isBad")]
  public bool IsBad { get; set; }

  [JsonProperty("defectType")]
  public string DefectType { get; set; }

  [JsonProperty("temperature")]
  public double Temperature { get; set; }

  [JsonProperty("humidity")]
  public double Humidity { get; set; }

  [JsonProperty("voltage")]
  public double Voltage { get; set; }

  [JsonProperty("workerCode")]
  public string WorkerCode { get; set; }

  [JsonProperty("materialLotIds")]
  public List<long> MaterialLotIds { get; set; } = new List<long>();
}