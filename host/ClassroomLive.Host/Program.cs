using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;

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
var locales = new LocaleStore(webRoot);
var port = int.TryParse(Environment.GetEnvironmentVariable("CLASSROOM_LIVE_PORT"), out var configuredPort)
    ? configuredPort
    : 5050;

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
// 코드 100만 글자를 JSON/UTF-8로 보낼 수 있게 하되 Kestrel 기본 30MB보다 훨씬 작게 막는다.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
builder.Services.AddSingleton(locales);
builder.Services.AddSingleton(_ => ClassroomSession.CreatePersistent(locales.Language));
builder.Services.AddResponseCompression(options =>
{
    // 소스 코드는 압축률이 높다. 선택 파일이 바뀐 순간의 JSON 응답도 자동 압축한다.
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("student-state", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 40,
            TokensPerPeriod = 3,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
});

var app = builder.Build();
app.UseResponseCompression();

// 이 앱은 코드와 세션 토큰을 다룬다. 프레임 삽입과 브라우저/프록시 저장을 막는다.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
            "img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        headers["X-Frame-Options"] = "DENY";
        headers["X-Content-Type-Options"] = "nosniff";
        // 확장은 이 값으로 오래된 host.json이 가리키는 엉뚱한 서비스를 거른다.
        headers["X-Classroom-Live"] = "1";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store, private";
        headers["Pragma"] = "no-cache";
        return Task.CompletedTask;
    });
    await next();
});

// Minimal API 매개변수는 핸들러 진입 전에 JSON으로 변환된다. 이 검사를 미들웨어에
// 두어 외부의 큰 요청은 본문을 읽기 전에 끊는다.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var address = context.Connection.RemoteIpAddress;
    if (path.StartsWithSegments("/api/extension"))
    {
        var session = context.RequestServices.GetRequiredService<ClassroomSession>();
        if (address is null || !IPAddress.IsLoopback(address) ||
            !session.IsExtension(context.Request.Headers["X-Extension-Token"].FirstOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    else if (path == "/host" || path.StartsWithSegments("/api/host"))
    {
        if (address is null || !IPAddress.IsLoopback(address))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (path.StartsWithSegments("/api/host"))
        {
            var session = context.RequestServices.GetRequiredService<ClassroomSession>();
            if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
    }
    await next();
});

app.UseRateLimiter();
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

app.MapGet("/api/locales", (LocaleStore store) => Results.Json(new
{
    language = store.Language,
    locales = store.Locales.Select(locale => new
    {
        code = locale.Code,
        name = locale.Name,
        direction = locale.Direction
    })
}));

app.MapGet("/api/state", (HttpContext context, ClassroomSession session,
    string? fileId, long? revision) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var failure = StudentAuthenticationFailure(context, session, address);
    if (failure is not null) return failure;
    session.RecordViewer(address);
    return Results.Json(session.GetClientSnapshot(fileId, revision));
}).RequireRateLimiting("student-state");

// 백그라운드 탭에서는 setInterval이 크게 늦어질 수 있다. 이 요청을 서버가 잡아 두었다가
// 정상 종료 순간에 깨워 주면 5초 유예 안에 종료 상태를 확실히 전달할 수 있다.
app.MapGet("/api/end", async (HttpContext context, ClassroomSession session,
    string? fileId, long? revision) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var failure = StudentAuthenticationFailure(context, session, address);
    if (failure is not null) return failure;
    if (!session.TryBeginEndWait(address))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await session.WaitForEndAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.NoContent();
        }
        return Results.Json(session.GetClientSnapshot(fileId, revision));
    }
    finally
    {
        session.EndEndWait(address);
    }
}).RequireRateLimiting("student-state");

app.MapPost("/api/viewer/leave", (HttpContext context, ClassroomSession session) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var failure = StudentAuthenticationFailure(context, session, address);
    if (failure is not null) return failure;
    session.RemoveViewer(address);
    return Results.Ok();
}).RequireRateLimiting("student-state");

app.MapGet("/api/host/state", (HttpContext context, ClassroomSession session,
    string? fileId, long? revision) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.RecordHostPoll();
    return Results.Json(session.GetHostClientSnapshot(GetStudentUrls(port, session.Pin), fileId, revision));
});

app.MapGet("/api/host/end", async (HttpContext context, ClassroomSession session,
    string? fileId, long? revision) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    await session.WaitForEndAsync(context.RequestAborted);
    return Results.Json(session.GetHostClientSnapshot(GetStudentUrls(port, session.Pin), fileId, revision));
});

app.MapPost("/api/host/broadcast", (HttpContext context, BroadcastRequest request, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    session.SetBroadcasting(request.Enabled);
    return Results.Ok();
});

app.MapPost("/api/host/language", (HttpContext context, LanguageRequest request,
    ClassroomSession session, LocaleStore store) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    if (!store.SetLanguage(request.Code)) return Results.BadRequest();
    session.SetLanguage(store.Language);
    return Results.Ok();
});

app.MapPost("/api/host/restore", (HttpContext context, RestoreRequest request, ClassroomSession session) =>
{
    if (!session.IsAdmin(context.Request.Headers["X-Admin-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    return session.DecideRestore(request.Enabled) ? Results.Ok() : Results.Conflict();
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

    ScheduleShutdown(session, lifetime);
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
        // 관리자 권한 창을 한 번만 띄우려고 명령들을 cmd 하나로 묶는다.
        //
        // 이 실행 파일에 걸린 인바운드 규칙을 먼저 전부 지운다. 허용뿐 아니라
        // '차단' 규칙도 지워야 한다. Windows의 "네트워크 액세스 허용" 팝업에서
        // 공용 네트워크를 해제하면 Windows가 차단 규칙을 만드는데, 방화벽은
        // 차단이 허용을 이기기 때문에 그것이 남아 있으면 아래 허용 규칙을
        // 아무리 잘 만들어도 무시된다. 실제로 교실에서 이것 때문에 막혔다.
        // (netsh delete 는 action= 을 받지 않으므로 프로그램 기준으로 지운다)
        var script = $"netsh advfirewall firewall delete rule name=all dir=in program=\"{executable}\" >nul 2>&1 & " +
                     $"netsh advfirewall firewall delete rule name=\"{ruleName}\" >nul 2>&1 & " +
                     $"netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
                     // remoteip은 사설 대역 전체로 잡는다. LocalSubnet으로 좁혔더니
                     // 학교 와이파이가 SSID 하나에 서브넷 여러 개(192.168.104.0/21,
                     // 192.168.48.0/21 …)로 쪼개진 환경에서 다른 서브넷 학생이 전부 막혔다.
                     // 사설 대역만 여는 것이므로 인터넷에서는 여전히 들어올 수 없다.
                     $"protocol=TCP localport={port} " +
                     $"remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16 " +
                     // 교직원 PC는 도메인 조인된 경우가 많다. 도메인 프로필을 빼면
                     // 규칙이 만들어져도 적용되지 않는다.
                     $"profile=any " +
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
    var outcome = session.ApplyExtensionUpdate(request);
    if (outcome == ExtensionUpdateOutcome.Unshared) return Results.Conflict();
    if (outcome is ExtensionUpdateOutcome.NeedsConfirmation or ExtensionUpdateOutcome.Rejected)
        return Results.Json(session.BuildReply(request.InstanceId, request.FilePath, request.SolutionRoot),
            statusCode: StatusCodes.Status422UnprocessableEntity);

    // 교수 화면에서 누른 명령과 Visual Studio 메뉴가 쓸 상태를 함께 돌려준다.
    return Results.Json(session.BuildReply(request.InstanceId, request.FilePath, request.SolutionRoot));
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

    ScheduleShutdown(session, lifetime);
    return Results.Ok();
});

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
            .Select(address => new { network.Name, network.Description, address.Address }))
        .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateLan(item.Address))
        // 교수 PC는 Visual Studio가 깔린 개발 머신이라 Hyper-V, WSL, Docker 같은
        // 가상 어댑터가 흔하다. 그쪽 주소를 학생에게 주면 아무도 못 붙는다.
        .OrderBy(item => IsVirtualAdapter(item.Name) || IsVirtualAdapter(item.Description) ? 1 : 0)
        .ThenBy(item => item.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .Select(item => $"http://{item.Address}:{port}/?pin={WebUtility.UrlEncode(pin)}")
        .Distinct()
        .ToArray();

    return addresses.Length > 0 ? addresses : [$"http://localhost:{port}/?pin={pin}"];
}

static void ScheduleShutdown(ClassroomSession session, IHostApplicationLifetime lifetime)
{
    // 학생 화면이 다음 폴링에서 "종료됨"을 받은 뒤 서버를 내린다.
    // VM 부하와 백그라운드 탭의 타이머 지연까지 감안해 잠시 유지한다.
    session.End();
    _ = Task.Run(async () =>
    {
        await Task.Delay(5000);
        lifetime.StopApplication();
    });
}

static bool IsVirtualAdapter(string name) =>
    new[] { "vEthernet", "Hyper-V", "VirtualBox", "VMware", "Docker", "WSL",
            "Tailscale", "ZeroTier", "Radmin", "TAP-", "Npcap", "Loopback" }
        .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

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

static IResult? StudentAuthenticationFailure(HttpContext context, ClassroomSession session, string address)
{
    var validation = session.ValidatePin(address, context.Request.Headers["X-Classroom-Pin"].FirstOrDefault());
    if (validation == PinValidation.Valid) return null;
    if (validation == PinValidation.RateLimited)
    {
        context.Response.Headers["X-Classroom-Pin-Locked"] = "1";
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    return Results.Unauthorized();
}

record BroadcastRequest(bool Enabled);
record RestoreRequest(bool Enabled);
record HiddenRequest(bool Hidden);
record ExtensionUpdateRequest(
    string Action,
    string? FilePath,
    string? SolutionRoot,
    string? Content,
    /// <summary>교수가 보고 있는 줄. 확장이 모르면 0.</summary>
    int ActiveLine,
    /// <summary>민감 내용 경고를 교수가 이번 수업에서 승인했는지.</summary>
    bool AllowSensitive = false,
    /// <summary>Visual Studio 창마다 다른 값. 창을 여러 개 열었을 때 누가 보낸 것인지 구분한다.</summary>
    string? InstanceId = null,
    /// <summary>선택을 시작한 줄. 선택이 없거나 확장이 모르면 0.</summary>
    int AnchorLine = 0,
    /// <summary>이 Visual Studio 프로세스가 현재 전경 창인지.</summary>
    bool Focused = false,
    /// <summary>Visual Studio가 알려준 프로젝트 표시 이름.</summary>
    string? ProjectName = null,
    /// <summary>같은 솔루션 안에서 프로젝트를 구분하는 Visual Studio 고유 이름.</summary>
    string? ProjectKey = null,
    /// <summary>Visual Studio가 알려준 프로젝트 파일의 전체 경로.</summary>
    string? ProjectFilePath = null);
