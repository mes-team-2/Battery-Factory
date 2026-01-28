using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataCollector
{
  class Program
  {
    private const int PORT = 8000;
    // [중요] 백엔드 포트 확인 (8080 또는 8088)
    private const string BACKEND_URL = "http://localhost:8088";
    private static readonly HttpClient _httpClient = new HttpClient();

    static async Task Main(string[] args)
    {
      Console.Title = "📡 Data Collector Server (MiddleWare)";
      TcpListener server = new TcpListener(IPAddress.Any, PORT);
      server.Start();

      Console.WriteLine("=============================================");
      Console.WriteLine($"[Collector] 데이터 수집기 가동 시작 (Port: {PORT})");
      Console.WriteLine($"[Backend]   타겟 주소: {BACKEND_URL}");
      Console.WriteLine("=============================================");

      while (true)
      {
        var client = await server.AcceptTcpClientAsync();
        _ = HandleClientAsync(client); // 별도 스레드로 클라이언트 처리
      }
    }

    static async Task HandleClientAsync(TcpClient client)
    {
      string clientIp = client.Client.RemoteEndPoint.ToString();
      Console.WriteLine($"[접속] 설비 연결됨: {clientIp}");

      using (NetworkStream stream = client.GetStream())
      {
        byte[] buffer = new byte[4096];
        int bytesRead;

        try
        {
          while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
          {
            string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // [업그레이드] 수신 로그 출력 (눈으로 확인!)
            // 너무 길면 잘라서 보여줌
            string preview = json.Length > 50 ? json.Substring(0, 50) + "..." : json;
            // Console.WriteLine($"[수신] {preview}"); 

            try
            {
              // JSON 파싱
              JObject packet = JObject.Parse(json);
              string type = packet["Type"].ToString();
              string token = packet["Token"]?.ToString();
              string bodyJson = packet["Body"].ToString();

              // [업그레이드] 타입별 백엔드 전송 (STATUS 추가됨)
              switch (type)
              {
                case "SENSOR":
                  // 센서 데이터는 너무 많으니 로그 생략하거나 간단히
                  await SendToBackendAsync("/api/log/sensor", bodyJson, token);
                  break;

                case "PRODUCTION":
                  Console.WriteLine($"[전송] 🏭 생산 실적 -> 백엔드");
                  await SendToBackendAsync("/api/log/production", bodyJson, token);
                  break;

                case "STATUS":
                  Console.WriteLine($"[전송] 🚦 설비 상태 -> 백엔드");
                  await SendToBackendAsync("/api/log/status", bodyJson, token);
                  break;

                default:
                  Console.WriteLine($"[주의] 알 수 없는 패킷 타입: {type}");
                  break;
              }
            }
            catch (JsonException)
            {
              // TCP 패킷이 뭉쳐서 올 경우 JSON 파싱 에러가 날 수 있음 (단순 무시)
            }
          }
        }
        catch (Exception ex)
        {
          Console.WriteLine($"[종료] 연결 끊김 ({clientIp}): {ex.Message}");
        }
      }
      Console.WriteLine($"[해제] 설비 연결 해제: {clientIp}");
    }

    static async Task SendToBackendAsync(string endpoint, string jsonBody, string token)
    {
      try
      {
        using (var request = new HttpRequestMessage(HttpMethod.Post, BACKEND_URL + endpoint))
        {
          // 토큰이 있으면 헤더에 추가
          if (!string.IsNullOrEmpty(token))
          {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
          }

          request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

          var response = await _httpClient.SendAsync(request);

          if (!response.IsSuccessStatusCode)
          {
            Console.WriteLine($"[Backend Error] {response.StatusCode} : {endpoint}");
            string errorDetail = await response.Content.ReadAsStringAsync();
            // 에러가 나면 상세 내용을 보여줌
            Console.WriteLine($"   └─ {errorDetail}");
          }
        }
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[Backend Fail] 서버 통신 실패: {ex.Message}");
      }
    }
  }
}