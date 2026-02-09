using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

public class Machine
{
  public string Code { get; private set; }
  public string Name { get; private set; }
  private ConcurrentQueue<string> _inputQueue;
  private ConcurrentQueue<string> _outputQueue;

  private double _temp = 25.0;
  private double _humid = 45.0;
  private double _volt = 220.0;

  // 설비에 장착된 자재 Lot ID 목록
  private List<long> _mountedMaterialIds = new List<long>();

  // [New] 현재 작업 제품의 BOM 정보 (API로 받아와서 저장)
  private List<BomDto> _currentBomList = new List<BomDto>();

  // 현재 설비 상태 추적
  private string _currentStatus = "STOP";
  private string _lastCompletedWoNo = "";

  private const string BACKEND_URL = "http://localhost:8088";
  private const string COLLECTOR_IP = "127.0.0.1";
  private const int COLLECTOR_PORT = 8000;
  private static readonly HttpClient _httpClient = new HttpClient();
  private static string _jwtToken = string.Empty;
  private static string _workerId = "UNKNOWN";

  public Machine(string code, string name, ConcurrentQueue<string> inputQ, ConcurrentQueue<string> outputQ)
  {
    Code = code;
    Name = name;
    _inputQueue = inputQ;
    _outputQueue = outputQ;
  }

  public static async Task<bool> LoginAsync(string id, string pw)
  {
    try
    {
      var loginData = new { workerCode = id, password = pw };
      var content = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json");
      var response = await _httpClient.PostAsync($"{BACKEND_URL}/auth/login", content);
      if (response.IsSuccessStatusCode)
      {
        var result = await response.Content.ReadAsStringAsync();
        dynamic tokenObj = JsonConvert.DeserializeObject(result);
        _jwtToken = (string)tokenObj.accessToken ?? (string)tokenObj.token;
        _workerId = id;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
        return true;
      }
      return false;
    }
    catch { return false; }
  }

  public async Task InitializeAsync()
  {
    if (string.IsNullOrEmpty(_jwtToken)) return;
    try
    {
      var response = await _httpClient.GetStringAsync($"{BACKEND_URL}/api/machines/{Code}/material-lots");
      var materials = JsonConvert.DeserializeObject<List<MachineMaterialDto>>(response);

      _mountedMaterialIds.Clear();
      foreach (var mat in materials)
      {
        _mountedMaterialIds.Add(mat.MaterialLotId);
      }
    }
    catch { }
  }

  public async Task RunAsync()
  {
    using (TcpClient client = new TcpClient())
    {
      try
      {
        await client.ConnectAsync(COLLECTOR_IP, COLLECTOR_PORT);
        using (NetworkStream stream = client.GetStream())
        {
          await Task.WhenAll(SensorLoop(stream), ProductionProcess(stream));
        }
      }
      catch (Exception ex) { Console.WriteLine($"[{Code}] ❌ 수집기 연결 실패: {ex.Message}"); }
    }
  }

  private async Task SensorLoop(NetworkStream stream)
  {
    while (true)
    {
      UpdateSensorValues();
      var packet = new
      {
        Type = "SENSOR",
        Token = _jwtToken,
        Body = new SensorData
        {
          MachineCode = Code,
          Timestamp = DateTime.Now.ToString("s"),
          Data = new SensorData.EnvData { Temperature = Math.Round(_temp, 1), Humidity = Math.Round(_humid, 1), Voltage = Math.Round(_volt, 1) }
        }
      };
      await SendJsonAsync(stream, packet);
      await Task.Delay(5000);
    }
  }

  // 생산 프로세스
  private async Task ProductionProcess(NetworkStream stream)
  {
    await ReportStatusAsync(stream, "WAIT", "READY_FOR_WORK");

    while (true)
    {
      var wo = await FetchWorkOrderAsync();

      // 작업이 없거나 이미 완료한 작업이면 대기
      if (wo == null || wo.WorkOrderNo == _lastCompletedWoNo)
      {
        if (_currentStatus != "WAIT")
          await ReportStatusAsync(stream, "WAIT", "IDLE");

        await Task.Delay(5000);
        continue;
      }

      // [핵심] 작업 시작 전, 백엔드에서 BOM 정보 동적 조회
      await FetchBomAsync(wo.ProductCode);

      await ReportStatusAsync(stream, "RUN", $"START_WO:{wo.WorkOrderNo}");

      int targetQty = wo.PlannedQty;
      int currentQty = 0;
      string woNo = wo.WorkOrderNo;
      string pCode = wo.ProductCode;

      int productionLimit = targetQty;
      bool isHeadMachine = (_inputQueue == null);

      if (isHeadMachine)
      {
        productionLimit = (int)(targetQty * 1.5);
        Console.WriteLine($"[{Code}] 📋 [HEAD] {pCode} 작업 시작: {woNo} (목표: {targetQty} -> 투입: {productionLimit}EA)");
      }
      else
      {
        Console.WriteLine($"[{Code}] 📋 [LINE] {pCode} 작업 시작: {woNo} (목표: {targetQty} 이상 생산 대기)");
      }

      Random rnd = new Random();
      int timeoutCount = 0;

      // 생산 루프
      while (true)
      {
        // 종료 조건
        if (isHeadMachine)
        {
          if (currentQty >= productionLimit)
          {
            Console.WriteLine($"[{Code}] 🎉 투입 목표 달성 ({currentQty}/{productionLimit}). 생산 종료.");
            break;
          }
        }

        // 자재 가져오기 (Head가 아닌 경우)
        if (!isHeadMachine)
        {
          string item;
          if (!_inputQueue.TryDequeue(out item))
          {
            timeoutCount++;
            if (timeoutCount == 5)
            {
              await ReportStatusAsync(stream, "WAIT", "NO_MATERIAL");
              Console.WriteLine($"[{Code}] ⏳ 자재 대기 중... (Status: WAIT)");
            }

            if (timeoutCount > 30)
            {
              Console.WriteLine($"[{Code}] 🛑 라인 종료 (자재 공급 중단됨). 작업 마감.");
              await ReportStatusAsync(stream, "STOP", "MATERIAL_TIMEOUT");
              break;
            }
            await Task.Delay(1000);
            continue;
          }

          if (timeoutCount >= 5 || _currentStatus != "RUN")
          {
            await ReportStatusAsync(stream, "RUN", "RESUME_WORK");
          }

          timeoutCount = 0;
          await Task.Delay(1000);
        }
        else
        {
          await Task.Delay(1000); // Head 설비 속도
        }

        UpdateSensorValues();

        bool isBad = rnd.Next(0, 100) < 5;
        string defectType = isBad ? GetRandomDefect(Code) : "NONE";

        var packet = new
        {
          Type = "PRODUCTION",
          Token = _jwtToken,
          Body = new ProductionData
          {
            MachineCode = Code,
            Timestamp = DateTime.Now.ToString("s"),
            Qty = 1,
            IsBad = isBad,
            DefectType = defectType,
            MaterialLotIds = _mountedMaterialIds,
            Temperature = Math.Round(_temp, 1),
            Humidity = Math.Round(_humid, 1),
            Voltage = Math.Round(_volt, 1),
            WorkerCode = _workerId
          }
        };
        await SendJsonAsync(stream, packet);

        if (!isBad)
        {
          currentQty++;
          if (_outputQueue != null) _outputQueue.Enqueue("ITEM");

          double progress = (double)currentQty / targetQty * 100;

          // [핵심] 동적 로그 생성 함수 호출 (하드코딩 X)
          string materialLog = GenerateDynamicMaterialLog();

          Console.WriteLine($"[{Code}] ✅ 생산: {currentQty}/{targetQty} ({progress:F1}%) | {materialLog}");
        }
        else
        {
          Console.WriteLine($"[{Code}] ❌ 불량 폐기 ({defectType})");
        }
      }

      Console.WriteLine($"[{Code}] 🏁 배치 최종 완료: {currentQty}EA (목표: {targetQty})");
      await ReportWorkOrderCompletionAsync(woNo, currentQty);

      _lastCompletedWoNo = woNo;
      await ReportStatusAsync(stream, "WAIT", "BATCH_COMPLETED");
      Console.WriteLine($"[{Code}] 🔄 대기 모드 진입...");
      await Task.Delay(3000);
    }
  }

  // [New] 백엔드 API에서 BOM 정보 가져오기
  private async Task FetchBomAsync(string productCode)
  {
    try
    {
      var res = await _httpClient.GetAsync($"{BACKEND_URL}/api/bom/{productCode}");
      if (res.IsSuccessStatusCode)
      {
        var json = await res.Content.ReadAsStringAsync();
        // 백엔드 BomResponseDto 구조에 맞춰 역직렬화
        _currentBomList = JsonConvert.DeserializeObject<List<BomDto>>(json);
      }
      else
      {
        _currentBomList = new List<BomDto>();
      }
    }
    catch
    {
      _currentBomList = new List<BomDto>();
    }
  }

  // [New] 동적 자재 소모 로그 생성 (내 공정에 맞는 자재만 필터링)
  private string GenerateDynamicMaterialLog()
  {
    // 1. 내 설비 코드를 기반으로 '공정명' 매핑 (DB의 BOM.note 값과 일치해야 함)
    string myProcessName = MapMachineCodeToProcessName(Code);

    // 해당 공정이 아니거나 매핑되지 않으면 심플하게 리턴
    if (string.IsNullOrEmpty(myProcessName)) return "공정 진행 중";

    // 2. 받아온 BOM 리스트에서 내 공정 자재만 필터링
    var myMaterials = _currentBomList
                        .Where(b => b.Process == myProcessName)
                        .ToList();

    if (myMaterials.Count == 0) return "소모 자재 없음";

    // 3. 로그 문자열 조립 (예: "소모: 납(6.00KG), 양극판(5.00EA)")
    var sb = new StringBuilder("소모: ");
    foreach (var mat in myMaterials)
    {
      // 소수점 2자리까지만 표시
      sb.Append($"{mat.MaterialName}({mat.Qty:F2}{mat.Unit}), ");
    }

    return sb.ToString().TrimEnd(',', ' ');
  }

  // [New] 설비 코드 -> BOM 공정명 매핑
  private string MapMachineCodeToProcessName(string machineCode)
  {
    switch (machineCode)
    {
      case "MAC-A-01": return "전극공정";
      case "MAC-A-02": return "조립공정";
      // 3번은 활성화공정인데 보통 자재 소모가 없음. 필요 시 추가
      case "MAC-A-04": return "팩공정";
      default: return "";
    }
  }

  // 상태 보고
  private async Task ReportStatusAsync(NetworkStream stream, string status, string reason)
  {
    if (_currentStatus == status) return;
    _currentStatus = status;

    var packet = new
    {
      Type = "STATUS",
      Token = _jwtToken,
      Body = new
      {
        machineCode = Code,
        workerCode = _workerId,
        status = status,
        reason = reason,
        timestamp = DateTime.Now.ToString("s")
      }
    };
    await SendJsonAsync(stream, packet);
  }

  private string GetRandomDefect(string machineCode)
  {
    Random r = new Random();
    switch (machineCode)
    {
      case "MAC-A-01": return r.Next(0, 2) == 0 ? "SCRATCH" : "THICKNESS_ERROR";
      case "MAC-A-02": return r.Next(0, 2) == 0 ? "MISALIGNMENT" : "MISSING_PART";
      case "MAC-A-03": return r.Next(0, 2) == 0 ? "LOW_VOLTAGE" : "HIGH_TEMP";
      case "MAC-A-04": return r.Next(0, 2) == 0 ? "WELDING_ERROR" : "LABEL_ERROR";
      case "MAC-A-05": return r.Next(0, 2) == 0 ? "DIMENSION_ERROR" : "FOREIGN_MATERIAL";
      default: return "ETC";
    }
  }

  private async Task<WorkOrderDto> FetchWorkOrderAsync()
  {
    try
    {
      var res = await _httpClient.GetAsync($"{BACKEND_URL}/api/machines/{Code}/workorder");
      if (res.IsSuccessStatusCode)
      {
        var json = await res.Content.ReadAsStringAsync();
        dynamic data = JsonConvert.DeserializeObject(json);
        return new WorkOrderDto
        {
          WorkOrderNo = data.workOrderNo,
          PlannedQty = (int)data.plannedQty,
          ProductCode = (string)data.productCode,
          DueDate = data.dueDate
        };
      }
    }
    catch { }
    return null;
  }

  private async Task ReportWorkOrderCompletionAsync(string woNo, int actualQty)
  {
    try
    {
      var data = new { workOrderNo = woNo, actualQty = actualQty };
      var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
      await _httpClient.PostAsync($"{BACKEND_URL}/api/machines/{Code}/workorder/complete", content);
    }
    catch { }
  }

  private async Task SendJsonAsync(NetworkStream stream, object data)
  {
    try
    {
      byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
      await stream.WriteAsync(bytes, 0, bytes.Length);
    }
    catch { }
  }

  private void UpdateSensorValues()
  {
    Random rand = new Random();
    _temp = Math.Clamp(_temp + (rand.NextDouble() - 0.5) * 1.5, 23, 27);
    _humid = Math.Clamp(_humid + (rand.NextDouble() - 0.5) * 2.0, 40, 50);
    _volt = Math.Clamp(_volt + (rand.NextDouble() - 0.5) * 3.0, 217, 223);
  }

  // === DTO Classes ===

  // [New] BOM 정보 수신용 DTO (백엔드 BomResponseDto와 매핑)
  private class BomDto
  {
    [JsonProperty("materialName")]
    public string MaterialName { get; set; }

    [JsonProperty("qty")]
    public double Qty { get; set; }

    [JsonProperty("unit")]
    public string Unit { get; set; }

    [JsonProperty("process")]
    public string Process { get; set; }
  }

  private class WorkOrderDto
  {
    public string WorkOrderNo { get; set; }
    public int PlannedQty { get; set; }
    public string ProductCode { get; set; }
    public DateTime? DueDate { get; set; }
  }

  private class MachineMaterialDto
  {
    public long MaterialLotId { get; set; }
    public string MaterialName { get; set; }
    public string MaterialCode { get; set; }
    public double RemainQty { get; set; }
  }
}