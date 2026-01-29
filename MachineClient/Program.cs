using System.Collections.Concurrent; // [필수] 멀티스레드 큐

class Program
{
  // 전극 완료품 -> 조립 대기
  public static ConcurrentQueue<string> Q_Electrode_To_Assembly = new ConcurrentQueue<string>();
  // 조립 완료품 -> 활성화 대기
  public static ConcurrentQueue<string> Q_Assembly_To_Formation = new ConcurrentQueue<string>();
  // 활성화 완료품 -> 팩 대기
  public static ConcurrentQueue<string> Q_Formation_To_Pack = new ConcurrentQueue<string>();
  // 팩 완료품 -> 검사 대기
  public static ConcurrentQueue<string> Q_Pack_To_Inspection = new ConcurrentQueue<string>();

  static async Task Main(string[] args)
  {
    Console.Title = "🏭 Real Factory Simulator (Conveyor Mode)";

    while (true)
    {
      Console.Clear();
      Console.WriteLine("=========================================");
      Console.WriteLine("   Battery MES Equipment Control System  ");
      Console.WriteLine("=========================================");

      Console.Write("👤 Operator ID : ");
      string id = Console.ReadLine();
      if (string.IsNullOrWhiteSpace(id)) continue;

      Console.Write("🔑 Password    : ");
      string pw = Console.ReadLine();

      Console.WriteLine("\n[System] Connecting to Server...");

      // 로그인 시도
      bool loginSuccess = await Machine.LoginAsync(id, pw);

      if (loginSuccess)
      {
        Console.WriteLine("✅ 인증 성공! 시스템을 초기화합니다...");
        await Task.Delay(1000); // 잠시 대기 후 넘어감
        break; // 루프 탈출 -> 메인 로직 진행
      }
      else
      {
        Console.WriteLine("\n❌ 로그인 실패! 아이디/비밀번호를 확인하세요.");
        Console.WriteLine("   (재시도하려면 아무 키나 누르세요...)");
        Console.ReadKey();
      }
    }

    // 2. 설비 객체 생성 (각자 자기 앞/뒤 큐를 가짐)
    // 전극(01): Input(Null=자재) -> Output(Q1)
    var m1 = new Machine("MAC-A-01", "전극공정", null, Q_Electrode_To_Assembly);

    // 조립(02): Input(Q1) -> Output(Q2)
    var m2 = new Machine("MAC-A-02", "조립공정", Q_Electrode_To_Assembly, Q_Assembly_To_Formation);

    // 활성(03): Input(Q2) -> Output(Q3)
    var m3 = new Machine("MAC-A-03", "활성공정", Q_Assembly_To_Formation, Q_Formation_To_Pack);

    // 팩(04): Input(Q3) -> Output(Q4)
    var m4 = new Machine("MAC-A-04", "팩공정  ", Q_Formation_To_Pack, Q_Pack_To_Inspection);

    // 검사(05): Input(Q4) -> Output(Null=최종완료)
    var m5 = new Machine("MAC-A-05", "검사공정", Q_Pack_To_Inspection, null);

    // 3. 자재 정보 로드 (병렬)
    await Task.WhenAll(
        m1.InitializeAsync(), m2.InitializeAsync(), m3.InitializeAsync(),
        m4.InitializeAsync(), m5.InitializeAsync()
    );

    Console.WriteLine("\n🚀 [Main] 모든 설비 라인 가동 시작! (순차 생산 모드)");
    Console.WriteLine("   (전극이 생산해야 조립이 돌아갑니다)\n");

    // 4. 가동 시작
    var t1 = m1.RunAsync();
    var t2 = m2.RunAsync();
    var t3 = m3.RunAsync();
    var t4 = m4.RunAsync();
    var t5 = m5.RunAsync();

    await Task.WhenAll(t1, t2, t3, t4, t5);
  }
}