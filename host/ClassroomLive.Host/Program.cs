using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    SecurityRules.SelfTest();
    Console.WriteLine("자체 검사를 통과했습니다.");
    return 0;
}

var publishedRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();
var webRoot = Path.Combine(publishedRoot, "wwwroot");

// wwwroot가 없으면 정적 파일 미들웨어가 조용히 404를 뱉고 /host는 500으로 죽는다.
// 원인을 알 수 없는 빈 화면 대신 여기서 분명하게 멈춘다.
if (!File.Exists(Path.Combine(webRoot, "index.html")))
{
    Console.Error.WriteLine("화면 파일(wwwroot/index.html)을 찾지 못했습니다.");
    Console.Error.WriteLine($"  확인한 경로: {webRoot}");
    Console.Error.WriteLine("  'dotnet publish -c Release' 로 만든 폴더에서 실행해주세요.");
    return 1;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = publishedRoot
});
var port = int.TryParse(Environment.GetEnvironmentVariable("CLASSROOM_LIVE_PORT"), out var configuredPort)
    ? configuredPort
    : 5050;

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton<ClassroomSession>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var extension = Path.GetExtension(context.File.Name);
        if (extension is ".html" or ".js" or ".css")
            context.Context.Response.ContentType += "; charset=utf-8";
    }
});

app.MapGet("/host", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
});

app.MapGet("/api/state", (HttpContext context, ClassroomSession session) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (session.IsPinRateLimited(address))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    var pin = context.Request.Headers["X-Classroom-Pin"].FirstOrDefault();
    if (!session.IsValidPin(pin))
    {
        session.RecordPinFailure(address);
        return Results.Unauthorized();
    }

    session.ClearPinFailures(address);
    session.RecordViewer(context.Request.Headers["X-Viewer-Id"].FirstOrDefault());
    return Results.Json(session.GetSnapshot());
});

app.MapGet("/api/host/state", (HttpContext context, ClassroomSession session) =>
    session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault())
        ? Results.Json(session.GetHostSnapshot(GetStudentUrls(port, session.Pin)))
        : Results.Unauthorized());

app.MapPost("/api/host/broadcast", (HttpContext context, BroadcastRequest request, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.SetBroadcasting(request.Enabled);
    return Results.Ok();
});

app.MapPost("/api/host/firewall", (HttpContext context, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    try
    {
        var ruleName = $"Classroom Live {port}";
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 찾지 못했습니다.");
        // 누를 때마다 규칙이 중복 추가되지 않도록 기존 규칙을 먼저 지운다.
        // 관리자 권한 창을 한 번만 띄우려고 두 명령을 cmd 하나로 묶는다.
        var script = $"netsh advfirewall firewall delete rule name=\"{ruleName}\" >nul 2>&1 & " +
                     $"netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
                     // 학교 와이파이는 Windows에서 '공용'으로 잡히는 일이 많다. public을 빼면
                     // 정작 수업에서 학생이 못 붙는다. 실제 범위 제한은 remoteip=LocalSubnet이 한다.
                     $"protocol=TCP localport={port} remoteip=LocalSubnet profile=private,public " +
                     $"program=\"{executable}\" enable=yes";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {script}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null) return Results.Problem("방화벽 설정을 시작하지 못했습니다.");

        // 관리자 권한 창을 그대로 두면 요청 스레드가 무한정 묶인다.
        if (!process.WaitForExit(120_000))
            return Results.Problem("방화벽 허용 요청이 시간 내에 끝나지 않았습니다.");

        return process.ExitCode == 0
            ? Results.Ok(new { message = "로컬 네트워크 방화벽 허용이 완료되었습니다." })
            : Results.Problem("방화벽 허용이 취소되었거나 실패했습니다.");
    }
    catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
    {
        return Results.Problem("관리자 권한 요청이 취소되었습니다.");
    }
    catch (Exception exception)
    {
        return Results.Problem($"방화벽 허용에 실패했습니다: {exception.Message}");
    }
});

app.MapPost("/api/extension/update", (HttpContext context, ExtensionUpdateRequest request,
    ClassroomSession session) =>
{
    // 루프백만으로는 부족하다. 같은 PC의 아무 프로그램이나 교실에 코드를 밀어넣을 수 있으므로
    // 확장이 핸드셰이크 파일에서 읽은 토큰을 함께 확인한다.
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        return Results.NotFound();
    if (!session.IsExtension(context.Request.Headers["X-Extension-Token"].FirstOrDefault()))
        return Results.NotFound();

    // 교수가 ×로 내린 파일이면 409로 알려준다. 확장이 이걸 받아 공유 목록을 정리하므로
    // 단축키를 두 번 눌러야 다시 공유되던 문제가 사라진다.
    return session.ApplyExtensionUpdate(request) == ExtensionUpdateOutcome.Suppressed
        ? Results.Conflict()
        : Results.Ok();
});

app.MapDelete("/api/host/files/{id}", (HttpContext context, string id, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.Remove(id);
    return Results.Ok();
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var session = app.Services.GetRequiredService<ClassroomSession>();
    var hostUrl = $"http://localhost:{port}/host?token={session.AdminToken}";

    // 확장이 포트와 토큰을 찾을 수 있도록 남긴다. 이게 없으면 확장은 연결되지 않는다.
    try
    {
        HostHandshake.Write(port, session.ExtensionToken);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"확장 연결 정보를 저장하지 못했습니다: {exception.Message}");
        Console.Error.WriteLine($"  경로: {HostHandshake.FilePath}");
    }

    Console.WriteLine();
    Console.WriteLine("Classroom Live가 준비되었습니다.");
    Console.WriteLine($"교수 화면: {hostUrl}");
    foreach (var url in GetStudentUrls(port, session.Pin))
        Console.WriteLine($"학생 주소: {url}");
    Console.WriteLine("종료하려면 Ctrl+C를 누르세요.");
    Console.WriteLine();

    if (Environment.GetEnvironmentVariable("CLASSROOM_LIVE_NO_BROWSER") != "1")
    {
        try { Process.Start(new ProcessStartInfo(hostUrl) { UseShellExecute = true }); }
        catch { /* 브라우저 자동 실행 실패는 서버 동작에 영향을 주지 않습니다. */ }
    }

});

app.Lifetime.ApplicationStopping.Register(HostHandshake.Delete);

await app.RunAsync();
return 0;

static string[] GetStudentUrls(int port, string pin)
{
    var addresses = NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                          network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                          network.GetIPProperties().GatewayAddresses.Any(gateway =>
                              gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                              !gateway.Address.Equals(IPAddress.Any)))
        .SelectMany(network => network.GetIPProperties().UnicastAddresses
            .Select(address => new { network.Name, address.Address }))
        .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateLan(item.Address))
        .OrderBy(item => item.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .Select(item => $"http://{item.Address}:{port}/?pin={WebUtility.UrlEncode(pin)}")
        .Distinct()
        .ToArray();

    return addresses.Length > 0 ? addresses : [$"http://localhost:{port}/?pin={pin}"];
}

static bool IsPrivateLan(IPAddress address)
{
    var bytes = address.GetAddressBytes();
    return bytes[0] == 10 ||
           bytes[0] == 192 && bytes[1] == 168 ||
           bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
}

record BroadcastRequest(bool Enabled);
record ExtensionUpdateRequest(
    string Action,
    string? FilePath,
    string? SolutionRoot,
    string? Content);
