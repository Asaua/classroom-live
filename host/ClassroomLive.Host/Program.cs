using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    SecurityRules.SelfTest();
    Console.WriteLine("보안 규칙 자체 검사를 통과했습니다.");
    return;
}

var publishedRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();
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
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
});

app.MapGet("/api/state", (HttpContext context, ClassroomSession session) =>
{
    var pin = context.Request.Headers["X-Classroom-Pin"].FirstOrDefault();
    if (!session.IsValidPin(pin)) return Results.Unauthorized();

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
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port} remoteip=LocalSubnet profile=private,public program=\"{executable}\" enable=yes",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });
        process?.WaitForExit();
        return process?.ExitCode == 0
            ? Results.Ok(new { message = "로컬 네트워크 방화벽 허용이 완료되었습니다." })
            : Results.Problem("방화벽 허용이 취소되었거나 실패했습니다.");
    }
    catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
    {
        return Results.Problem("관리자 권한 요청이 취소되었습니다.");
    }
});

app.MapPost("/api/extension/update", (HttpContext context, ExtensionUpdateRequest request,
    ClassroomSession session) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        return Results.NotFound();

    return session.ApplyExtensionUpdate(request)
        ? Results.Ok()
        : Results.Conflict();
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

await app.RunAsync();

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
