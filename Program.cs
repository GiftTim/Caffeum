using System.IO.Pipes;
using System.Text;

namespace CoffeeViewer;

static class Program
{
    private const string PipeName = "Caffeum.SingleInstance.Pipe";

    /// <summary>
    /// 진입점. 프로그램은 하나만 떠 있는다: 이미 실행 중이면 새로 띄우지 않고 받은 인자(폴더 · 파일)를 기존 프로그램에 넘긴 뒤 끝낸다
    /// (기존 창이 앞으로 나오고, 파일을 넘겼다면 PIP 창이 하나 더 열린다. PIP 창은 여러 개 있어도 된다).
    /// 인자 없이 실행하면 마지막에 열었던 볼트(폴더)가 자동으로 열리고, 처음이면 폴더를 고르게 한다.
    /// 폴더 경로를 인자로 주면 그 폴더를 볼트로 열고, 파일 경로 하나를 주면(더블클릭 등) 마지막 볼트에서 그 문서를 탭으로 연다.
    /// 볼트 창이 모두 닫히면 프로그램이 끝난다(AppShared).
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Caffeum.SingleInstance", out bool first);
        if (!first)
        {
            if (!SendToRunning(args)) MessageBox.Show("Caffeum이 이미 실행 중이지만 응답이 없어요. 잠시 뒤에 다시 시도해 주세요.", "Caffeum");
            return;
        }

        ApplicationConfiguration.Initialize();
        string? initialPath = args.Length > 0 ? args[0] : null;
        var context = AppShared.Start(initialPath, args.Skip(1).ToArray());
        StartListener();
        Updater.CleanupOldInstallers();
        Application.Run(context);
    }

    private static bool SendToRunning(string[] args)
    {
        try
        {
            for (int attempt = 0; attempt < 5; attempt++) // 서버가 다음 연결을 준비하는 짧은 틈에 걸리면 다시 시도
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(3000);
                    using var w = new StreamWriter(client, new UTF8Encoding(false));
                    foreach (var a in args) w.WriteLine(Path.IsPathRooted(a) || Directory.Exists(a) || File.Exists(a) ? Path.GetFullPath(a) : a);
                    w.Flush();
                    return true;
                }
                catch (IOException) { Thread.Sleep(150); }
                catch (TimeoutException) { }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>다른 실행이 보낸 인자를 받아 기존 프로그램에서 처리한다.</summary>
    private static void StartListener()
    {
        var sync = new Control();
        sync.CreateControl();
        _ = sync.Handle; // 창 핸들을 지금 만들어 둬야 다른 스레드에서 BeginInvoke를 쓸 수 있다
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    // 여러 파일을 한꺼번에 열면 실행이 동시에 여러 개 들어온다 → 연결마다 따로 읽어서 줄 세우지 않는다
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using (server)
                            using (var r = new StreamReader(server, new UTF8Encoding(false)))
                            {
                                var list = new List<string>();
                                for (string? line; (line = r.ReadLine()) != null;) if (line.Length > 0) list.Add(line);
                                sync.BeginInvoke(() => { try { AppShared.HandleExternal(list.ToArray()); } catch { } });
                            }
                        }
                        catch { }
                    });
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "CaffeumPipe" }.Start();
    }
}
