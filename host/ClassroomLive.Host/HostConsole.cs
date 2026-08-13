using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 창 없이(WinExe) 돌면서도 터미널에서 실행하면 출력이 보이게 해주는 도우미.
/// 교수님이 더블클릭하면 검은 창이 뜨지 않고, 개발자가 터미널에서 --self-test를
/// 돌리면 결과가 그대로 보인다.
/// </summary>
/// <remarks>
/// LibraryImport 대신 DllImport를 쓴다. 소스 생성기가 unsafe 코드를 만들어서
/// 프로젝트 전체에 AllowUnsafeBlocks를 켜야 하는데, 이 두 함수 때문에 그럴 이유는 없다.
/// </remarks>
static class HostConsole
{
    private const int AttachParentProcess = -1;
    private const uint IconError = 0x00000010;
    private const uint IconInfo = 0x00000040;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    /// <summary>
    /// 출력을 볼 수 있는 상태로 만든다. 더블클릭이면 아무 일도 하지 않는다.
    /// </summary>
    /// <remarks>
    /// 부모가 표준 출력을 물려줬으면(터미널에서 직접 실행, dotnet run 등) 그대로 쓴다.
    /// 여기서 콘솔 버퍼로 갈아끼우면 dotnet run이 넘겨주는 파이프를 건너뛰어
    /// 출력이 사라진다. 물려받은 게 없을 때만 부모 콘솔에 붙는다.
    /// </remarks>
    public static bool TryAttach()
    {
        try
        {
            if (Console.OpenStandardOutput() == Stream.Null)
            {
                if (!AttachConsole(AttachParentProcess)) return false;
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }

            // 어느 경로로 왔든 한글이 깨지지 않게 한다. 콘솔이 없으면 예외가 나지만 무해하다.
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 무시 */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>콘솔이 없을 때도 반드시 보여야 하는 안내.</summary>
    public static void Info(string text)
    {
        Console.WriteLine(text);
        MessageBox(IntPtr.Zero, text, "Classroom Live", IconInfo);
    }

    /// <summary>콘솔이 없을 때도 반드시 보여야 하는 오류. 이게 없으면 아무것도 안 뜨고 끝난다.</summary>
    public static void Error(string text)
    {
        Console.Error.WriteLine(text);
        MessageBox(IntPtr.Zero, text, "Classroom Live 오류", IconError);
    }
}
