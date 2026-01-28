using Newtonsoft.Json;

public class SensorData
{
  [JsonProperty("machineCode")]
  public string MachineCode { get; set; }

  [JsonProperty("timestamp")]
  public string Timestamp { get; set; }

  [JsonProperty("data")]
  public EnvData Data { get; set; }

  public class EnvData
  {
    [JsonProperty("temperature")]
    public double Temperature { get; set; }

    [JsonProperty("humidity")]
    public double Humidity { get; set; }

    [JsonProperty("voltage")]
    public double Voltage { get; set; }
  }
}