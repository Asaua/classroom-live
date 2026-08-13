using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

// 터미널에서 실행됐으면 그 콘솔에 붙는다. 더블클릭이면 창 없이 조용히 시작한다.
HostConsole.TryAttach();

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
// 창이 없으면 아무 표시도 없이 끝나므로 반드시 알린다.
if (!File.Exists(Path.Combine(webRoot, "index.html")))
{
    HostConsole.Error($"""
        화면 파일을 찾지 못했습니다.

        경로: {webRoot}
        dotnet publish 로 만든 폴더에서 실행해 주세요.
        """);
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
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.RecordHostPoll();
    return Results.Json(session.GetHostSnapshot(GetStudentUrls(port, session.Pin)));
});

app.MapPost("/api/host/broadcast", (HttpContext context, BroadcastRequest request, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.SetBroadcasting(request.Enabled);
    return Results.Ok();
});

app.MapPost("/api/host/share", (HttpContext context, BroadcastRequest request, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    // 확장이 다음 폴링에서 가져간다. 교수님이 Visual Studio로 돌아가지 않아도 된다.
    session.RequestShare(request.Enabled);
    return Results.Ok();
});

app.MapPost("/api/host/shutdown", (HttpContext context, ClassroomSession session,
    IHostApplicationLifetime lifetime) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    // 응답을 먼저 보내고 종료한다. 바로 멈추면 브라우저가 성공을 확인하지 못한다.
    _ = Task.Run(async () =>
    {
        await Task.Delay(300);
        lifetime.StopApplication();
    });
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
        if (process is null) return Results.Problem("방화벽 설정을 시작하지 못했어요");

        // 관리자 권한 창을 그대로 두면 요청 스레드가 무한정 묶인다.
        if (!process.WaitForExit(120_000))
            return Results.Problem("방화벽 허용이 시간 내에 끝나지 않았어요");

        return process.ExitCode == 0
            ? Results.Ok(new { message = "방화벽을 허용했어요" })
            : Results.Problem("방화벽 허용이 취소됐거나 실패했어요");
    }
    catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
    {
        return Results.Problem("관리자 권한 요청이 취소됐어요");
    }
    catch (Exception exception)
    {
        return Results.Problem($"방화벽 허용에 실패했어요: {exception.Message}");
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

    // 교수가 목록에서 뺀 파일이면 409로 알려준다. 확장이 이걸 받아 공유 목록을 정리하므로
    // 해제한 파일이 다음 동기화에 되살아나지 않는다.
    if (session.ApplyExtensionUpdate(request) == ExtensionUpdateOutcome.Unshared)
        return Results.Conflict();

    // 교수 화면에서 누른 명령과 Visual Studio 메뉴가 쓸 상태를 함께 돌려준다.
    return Results.Json(session.BuildReply());
});

app.MapDelete("/api/host/files/{id}", (HttpContext context, string id, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.Unshare(id);
    return Results.Ok();
});

app.MapPost("/api/host/files/{id}/hidden", (HttpContext context, string id, HiddenRequest request,
    ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    // 숨김은 되돌릴 수 있다. 공유 해제(DELETE)와 달리 목록에는 남는다.
    return session.SetHidden(id, request.Hidden) ? Results.Ok() : Results.NotFound();
});

// --- Visual Studio 확장이 직접 부르는 조작 --------------------------------
// 교수 화면과 같은 일을 할 수 있어야 한다. 관리자 토큰을 확장에 넘기지 않으려고
// 확장 토큰으로 인증하는 별도 경로를 둔다. 루프백에서만 받는다.

app.MapPost("/api/extension/broadcast", (HttpContext context, BroadcastRequest request,
    ClassroomSession session) =>
{
    if (!IsLocalExtension(context, session)) return Results.NotFound();

    session.SetBroadcasting(request.Enabled);
    return Results.Ok();
});

app.MapPost("/api/extension/shutdown", (HttpContext context, ClassroomSession session,
    IHostApplicationLifetime lifetime) =>
{
    if (!IsLocalExtension(context, session)) return Results.NotFound();

    _ = Task.Run(async () =>
    {
        await Task.Delay(300);
        lifetime.StopApplication();
    });
    return Results.Ok();
});

// 확장이 "실행"으로 호스트를 켜려면 실행 파일 위치를 알아야 한다.
// 핸드셰이크 파일과 달리 종료해도 남겨둔다.
try
{
    if (Environment.ProcessPath is { } exePath) HostHandshake.RememberInstall(exePath);
}
catch { /* 위치를 못 남겨도 수업 진행에는 지장이 없다. */ }

app.Lifetime.ApplicationStarted.Register(() =>
{
    var session = app.Services.GetRequiredService<ClassroomSession>();
    var hostUrl = $"http://localhost:{port}/host?token={session.AdminToken}";
    var studentUrls = GetStudentUrls(port, session.Pin);

    // 확장이 포트와 토큰을 찾을 수 있도록 남긴다. 이게 없으면 확장은 연결되지 않는다.
    try
    {
        HostHandshake.Write(port, session.ExtensionToken);
    }
    catch (Exception exception)
    {
        HostConsole.Error($"""
            Visual Studio 확장 연결 정보를 저장하지 못했습니다.
            확장이 연결되지 않을 수 있습니다.

            {exception.Message}
            경로: {HostHandshake.FilePath}
            """);
    }

    Console.WriteLine();
    Console.WriteLine("Classroom Live가 준비되었습니다.");
    Console.WriteLine($"교수 화면: {hostUrl}");
    foreach (var url in studentUrls) Console.WriteLine($"학생 주소: {url}");
    Console.WriteLine();

    if (Environment.GetEnvironmentVariable("CLASSROOM_LIVE_NO_BROWSER") == "1") return;

    try
    {
        using var browser = Process.Start(new ProcessStartInfo(hostUrl) { UseShellExecute = true });
        if (browser is null) throw new InvalidOperationException("브라우저를 시작하지 못했습니다.");
    }
    catch
    {
        // 창을 없앴으므로 여기서 못 알리면 교수님은 주소를 볼 방법이 전혀 없다.
        HostConsole.Info($"""
            브라우저를 자동으로 열지 못했습니다.
            아래 주소를 직접 열어 주세요.

            교수 화면:
            {hostUrl}
            """);
    }
});

app.Lifetime.ApplicationStopping.Register(HostHandshake.Delete);

// 교수 화면을 닫고 잊어버려도 서버가 며칠씩 남아 있지 않게 한다.
// 학생이 한 명이라도 보고 있으면 종료하지 않는다.
var idleWatch = new Timer(_ =>
{
    var session = app.Services.GetRequiredService<ClassroomSession>();
    if (session.IsIdle(TimeSpan.FromMinutes(30))) app.Lifetime.StopApplication();
}, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

try
{
    await app.RunAsync();
}
catch (IOException exception)
{
    HostConsole.Error($"""
        {port}번 포트를 쓸 수 없습니다.
        다른 프로그램이 쓰고 있거나 Classroom Live가 이미 실행 중입니다.

        {exception.Message}
        """);
    return 1;
}
catch (Exception exception)
{
    HostConsole.Error($"""
        Classroom Live를 시작하지 못했습니다.

        {exception.Message}
        """);
    return 1;
}
finally
{
    await idleWatch.DisposeAsync();
}

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

static bool IsLocalExtension(HttpContext context, ClassroomSession session)
{
    var address = context.Connection.RemoteIpAddress;
    return address is not null && IPAddress.IsLoopback(address) &&
           session.IsExtension(context.Request.Headers["X-Extension-Token"].FirstOrDefault());
}

record BroadcastRequest(bool Enabled);
record HiddenRequest(bool Hidden);
record ExtensionUpdateRequest(
    string Action,
    string? FilePath,
    string? SolutionRoot,
    string? Content,
    /// <summary>교수가 보고 있는 줄. 확장이 모르면 0.</summary>
    int ActiveLine);
