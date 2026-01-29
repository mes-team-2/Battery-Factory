using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
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

  // [New] 현재 설비 상태 추적 (중복 전송 방지용)
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
      // [복구] 사용자님 원본 URL (/auth/login) 유지
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
      await Task.Delay(5000); // 5초 주기
    }
  }

  // [핵심] 생산 프로세스 (Push Logic + Status Report)
  private async Task ProductionProcess(NetworkStream stream)
  {
    // [New] 시작 시 대기 상태 보고
    await ReportStatusAsync(stream, "WAIT", "READY_FOR_WORK");

    while (true)
    {
      var wo = await FetchWorkOrderAsync();

      // 작업이 없거나 이미 완료한 작업이면 대기
      if (wo == null || wo.WorkOrderNo == _lastCompletedWoNo)
      {
        // [New] IDLE 상태 보고 (중복 방지됨)
        if (_currentStatus != "WAIT")
          await ReportStatusAsync(stream, "WAIT", "IDLE");

        await Task.Delay(5000);
        continue;
      }

      // [New] 작업 시작 상태 보고 (RUN)
      await ReportStatusAsync(stream, "RUN", $"START_WO:{wo.WorkOrderNo}");

      int targetQty = wo.PlannedQty;
      int currentQty = 0;
      string woNo = wo.WorkOrderNo;
      string pCode = wo.ProductCode;

      // [Push Logic] 맨 앞 설비는 1.5배수 투입
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
        // 1. 종료 조건 (Head 설비)
        if (isHeadMachine)
        {
          if (currentQty >= productionLimit)
          {
            Console.WriteLine($"[{Code}] 🎉 투입 목표 달성 ({currentQty}/{productionLimit}). 생산 종료.");
            break;
          }
        }

        // 2. 자재 가져오기 (Line 설비)
        if (!isHeadMachine)
        {
          string item;
          if (!_inputQueue.TryDequeue(out item))
          {
            // 자재가 없어서 대기
            timeoutCount++;

            // [New] 5초 이상 대기 시 자재 부족 상태 보고 (WAIT)
            if (timeoutCount == 5)
            {
              await ReportStatusAsync(stream, "WAIT", "NO_MATERIAL");
              Console.WriteLine($"[{Code}] ⏳ 자재 대기 중... (Status: WAIT)");
            }

            if (timeoutCount % 10 == 0 && timeoutCount < 60)
              Console.WriteLine($"[{Code}] ⏳ 자재 대기 중... ({timeoutCount}/60s)");

            if (timeoutCount > 60)
            {
              Console.WriteLine($"[{Code}] 🛑 라인 종료 (자재 공급 중단됨). 작업 마감.");
              // [New] 타임아웃 중단 상태 보고 (STOP)
              await ReportStatusAsync(stream, "STOP", "MATERIAL_TIMEOUT");
              break;
            }
            await Task.Delay(1000);
            continue;
          }

          // 자재가 들어옴! 
          // [New] WAIT 상태였다면 다시 RUN으로 복귀 보고
          if (timeoutCount >= 5 || _currentStatus != "RUN")
          {
            await ReportStatusAsync(stream, "RUN", "RESUME_WORK");
          }

          timeoutCount = 0; // 리셋
          await Task.Delay(1000); // 가공 시간
        }
        else
        {
          await Task.Delay(1000); // Head 설비 가공 속도
        }

        // 3. 생산 데이터 전송
        UpdateSensorValues();

        bool isBad = rnd.Next(0, 100) < 5; // 5% 불량
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

        // 4. 결과 처리 (양품만 전달)
        if (!isBad)
        {
          currentQty++;
          if (_outputQueue != null) _outputQueue.Enqueue("ITEM");

          double progress = (double)currentQty / targetQty * 100;
          string materialLog = GetMaterialConsumptionLog(Code, pCode);

          Console.WriteLine($"[{Code}] ✅ 생산: {currentQty}/{targetQty} ({progress:F1}%) | {materialLog}");
        }
        else
        {
          Console.WriteLine($"[{Code}] ❌ 불량 폐기 ({defectType})");
        }
      }

      // 5. 완료 보고 (실적 포함)
      Console.WriteLine($"[{Code}] 🏁 배치 최종 완료: {currentQty}EA (목표: {targetQty})");
      await ReportWorkOrderCompletionAsync(woNo, currentQty);

      _lastCompletedWoNo = woNo;

      // [New] 배치 완료 후 대기 상태 보고 (WAIT)
      await ReportStatusAsync(stream, "WAIT", "BATCH_COMPLETED");
      Console.WriteLine($"[{Code}] 🔄 대기 모드 진입...");
      await Task.Delay(3000);
    }
  }

  // [New] 상태 보고 메서드 (수집기로 STATUS 패킷 전송)
  private async Task ReportStatusAsync(NetworkStream stream, string status, string reason)
  {
    // 상태가 변할 때만 전송 (중복 방지)
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
        status = status,   // RUN, WAIT, STOP
        reason = reason,   // NO_MATERIAL, IDLE, etc.
        timestamp = DateTime.Now.ToString("s")
      }
    };

    await SendJsonAsync(stream, packet);
  }

  // 제품별 자재 소모량 로그
  private string GetMaterialConsumptionLog(string machineCode, string productCode)
  {
    if (machineCode == "MAC-A-03" || machineCode == "MAC-A-05") return "공정 진행 중";

    string size = "S";
    if (productCode.Contains("65AH")) size = "M";
    if (productCode.Contains("90AH")) size = "L";

    switch (machineCode)
    {
      case "MAC-A-01": // 전극
        if (size == "S") return "소모: 납(6kg), 양극판(5ea), 음극판(5ea)";
        if (size == "M") return "소모: 납(9kg), 양극판(6ea), 음극판(6ea)";
        return "소모: 납(12kg), 양극판(8ea), 음극판(8ea)";

      case "MAC-A-02": // 조립
        if (size == "S") return "소모: 분리판(10ea), 전해액(2L), 케이스(1ea)";
        if (size == "M") return "소모: 분리판(12ea), 전해액(3L), 케이스(1ea)";
        return "소모: 분리판(16ea), 전해액(4L), 케이스(1ea)";

      case "MAC-A-04": // 팩
        return "소모: 라벨(1ea), 포장박스(1ea)";

      default: return "";
    }
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

  // DTO 클래스들
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
}