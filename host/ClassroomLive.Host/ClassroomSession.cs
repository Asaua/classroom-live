using System.Security.Cryptography;
using System.Text;

sealed class ClassroomSession
{
    internal const int MaxPinAttempts = 10;
    private static readonly TimeSpan PinAttemptWindow = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFile> _files = [];
    private readonly Dictionary<string, DateTimeOffset> _viewers = [];
    private readonly HashSet<string> _suppressedFiles = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset WindowStart)> _pinAttempts = [];
    private string? _professorActiveId;
    private string? _professorActiveName;
    private int? _professorActiveLine;
    private bool _broadcasting;
    private DateTimeOffset _lastExtensionHeartbeat = DateTimeOffset.MinValue;
    private string _visualStudioStatus = "Visual Studio 확장 연결 대기 중";

    public string Pin { get; } = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
    public string AdminToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string ExtensionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public bool IsValidPin(string? pin) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(pin ?? string.Empty), Encoding.UTF8.GetBytes(Pin));

    public bool IsAdmin(string? token) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(token ?? string.Empty), Encoding.UTF8.GetBytes(AdminToken));

    public bool IsExtension(string? token) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(token ?? string.Empty), Encoding.UTF8.GetBytes(ExtensionToken));

    /// <summary>6자리 PIN은 무차별 대입이 쉬우므로 주소별로 실패 횟수를 제한한다.</summary>
    public bool IsPinRateLimited(string address)
    {
        lock (_gate)
        {
            if (!_pinAttempts.TryGetValue(address, out var attempt)) return false;
            if (DateTimeOffset.UtcNow - attempt.WindowStart > PinAttemptWindow)
            {
                _pinAttempts.Remove(address);
                return false;
            }
            return attempt.Count >= MaxPinAttempts;
        }
    }

    public void RecordPinFailure(string address)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _pinAttempts[address] =
                _pinAttempts.TryGetValue(address, out var attempt) && now - attempt.WindowStart <= PinAttemptWindow
                    ? (attempt.Count + 1, attempt.WindowStart)
                    : (1, now);
        }
    }

    public void ClearPinFailures(string address)
    {
        lock (_gate) _pinAttempts.Remove(address);
    }

    public bool IsBroadcasting
    {
        get { lock (_gate) return _broadcasting; }
    }

    /// <summary>
    /// 방송을 켜고 끈다. 끄면 '화면 고정'이다. 학생 화면은 마지막 상태 그대로 남고
    /// 갱신만 멈춘다. 파일을 학생에게서 완전히 내리려면 Remove를 쓴다.
    /// </summary>
    public void SetBroadcasting(bool enabled)
    {
        lock (_gate) _broadcasting = enabled;
    }

    /// <summary>
    /// 확장이 보낸 상태를 반영한다. 교수가 화면에서 내린 파일이면 Suppressed를 돌려주고,
    /// 호출부가 409로 알려주면 확장이 공유 목록을 정리해 단축키 한 번으로 다시 공유된다.
    /// </summary>
    public ExtensionUpdateOutcome ApplyExtensionUpdate(ExtensionUpdateRequest request)
    {
        lock (_gate)
        {
            _lastExtensionHeartbeat = DateTimeOffset.UtcNow;

            var action = request.Action?.ToLowerInvariant();
            var hasSafeActiveFile = !string.IsNullOrWhiteSpace(request.FilePath) &&
                                    !string.IsNullOrWhiteSpace(request.SolutionRoot) &&
                                    SecurityRules.IsShareable(request.FilePath, request.SolutionRoot, request.Content?.Length ?? 0);
            var activeId = hasSafeActiveFile ? FileId(request.FilePath!) : null;
            int? activeLine = request.ActiveLine > 0 ? request.ActiveLine : null;

            if (action == "unshare")
            {
                if (!string.IsNullOrWhiteSpace(request.FilePath))
                {
                    var id = FileId(request.FilePath);
                    _files.Remove(id);
                    _suppressedFiles.Remove(id);
                    if (_professorActiveId == id) ClearProfessorPointer();
                }
                _visualStudioStatus = "연결됨 · 현재 파일 공유 해제됨";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (!_broadcasting)
            {
                // 화면 고정 중에는 교수 포인터까지 그대로 둔다. 학생이 보던 화면이
                // 발밑에서 움직이지 않아야 '고정'이라는 말이 지켜진다.
                _visualStudioStatus = "연결됨 · 화면 고정 중 (학생에게는 마지막 화면이 보입니다)";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (action == "share" && activeId is not null) _suppressedFiles.Remove(activeId);

            if (action is not ("share" or "sync"))
            {
                _professorActiveName = hasSafeActiveFile ? Path.GetFileName(request.FilePath) : null;
                _professorActiveId = activeId is not null && _files.ContainsKey(activeId) ? activeId : null;
                _professorActiveLine = _professorActiveId is null ? null : activeLine;
                _visualStudioStatus = _professorActiveName is null
                    ? "연결됨 · 코드 파일을 선택해주세요."
                    : $"연결됨 · {_professorActiveName} (공유 안 함)";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (!hasSafeActiveFile || request.Content is null)
            {
                ClearProfessorPointer();
                _visualStudioStatus = "현재 파일은 보안 규칙으로 공유할 수 없습니다.";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (_suppressedFiles.Contains(activeId!))
            {
                ClearProfessorPointer();
                _visualStudioStatus = "연결됨 · 현재 파일은 호스트 목록에서 숨겨짐";
                return ExtensionUpdateOutcome.Suppressed;
            }

            UpdateSharedFile(request.FilePath!, request.Content, request.SolutionRoot!);
            _professorActiveName = Path.GetFileName(request.FilePath);
            _professorActiveId = _files.ContainsKey(activeId!) ? activeId : null;
            _professorActiveLine = _professorActiveId is null ? null : activeLine;
            _visualStudioStatus = $"연결됨 · {_professorActiveName} 추적 중";
            return ExtensionUpdateOutcome.Accepted;
        }
    }

    private void ClearProfessorPointer()
    {
        _professorActiveId = null;
        _professorActiveName = null;
        _professorActiveLine = null;
    }

    private void UpdateSharedFile(string fullPath, string content, string solutionRoot)
    {
        if (!_broadcasting || !SecurityRules.IsShareable(fullPath, solutionRoot, content.Length)) return;

        var normalizedPath = Path.GetFullPath(fullPath);
        var id = FileId(normalizedPath);
        var relativePath = Path.GetRelativePath(solutionRoot, normalizedPath).Replace('\\', '/');
        var now = DateTimeOffset.Now;

        if (_files.TryGetValue(id, out var existing))
        {
            if (!string.Equals(existing.Content, content, StringComparison.Ordinal))
                _files[id] = existing with { Content = content, UpdatedAt = now };
        }
        else
        {
            _files[id] = new SharedFile(id, Path.GetFileName(normalizedPath), relativePath,
                SecurityRules.LanguageFor(normalizedPath), now, content);
        }

        TrimOldFiles();
    }

    public void RecordViewer(string? viewerId)
    {
        if (string.IsNullOrWhiteSpace(viewerId) || viewerId.Length > 100) return;
        lock (_gate) _viewers[viewerId] = DateTimeOffset.UtcNow;
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            // 한 수업에서 이만큼 숨길 일은 없다. 무한정 쌓이는 것만 막는다.
            if (_suppressedFiles.Count > 500) _suppressedFiles.Clear();
            _suppressedFiles.Add(id);
            _files.Remove(id);
            if (_professorActiveId == id) ClearProfessorPointer();
        }
    }

    public ClassroomSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            PruneViewers();
            return Snapshot();
        }
    }

    public HostSnapshot GetHostSnapshot(string[] studentUrls)
    {
        lock (_gate)
        {
            PruneViewers();
            var connected = DateTimeOffset.UtcNow - _lastExtensionHeartbeat < TimeSpan.FromSeconds(3);
            return new HostSnapshot(Snapshot(), _broadcasting, connected,
                connected ? _visualStudioStatus : "Visual Studio에서 Classroom Live 확장을 설치·실행해주세요.",
                Pin, studentUrls);
        }
    }

    private ClassroomSnapshot Snapshot() => new(
        "Classroom Live 수업",
        _professorActiveId,
        _professorActiveName,
        _professorActiveLine,
        _viewers.Count,
        _broadcasting,
        // 경로 기준 안정 정렬. 최근 수정순으로 두면 교수가 타이핑하는 파일이
        // 학생 커서 밑에서 계속 맨 위로 튄다.
        _files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());

    private void PruneViewers()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-4);
        foreach (var id in _viewers.Where(item => item.Value < cutoff).Select(item => item.Key).ToArray())
            _viewers.Remove(id);
    }

    private void TrimOldFiles()
    {
        // ponytail: 수업 규모에서는 최근 40개로 충분하다. 실제 대규모 강의에서만 영속 저장소를 추가한다.
        while (_files.Count > 40)
        {
            var oldest = _files.Values.Where(file => file.Id != _professorActiveId)
                .MinBy(file => file.UpdatedAt);
            if (oldest is null) break;
            _files.Remove(oldest.Id);
        }
    }

    private static string FileId(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..16];
    }
}

/// <summary>
/// 확장과 호스트가 포트·토큰을 주고받는 파일. 사용자 프로필 안에 두므로
/// 같은 PC의 다른 사용자 계정에서는 읽을 수 없다.
/// </summary>
static class HostHandshake
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassroomLive", "host.json");

    public static void Write(int port, string extensionToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // 토큰은 16진수라 JSON 이스케이프가 필요 없다.
        File.WriteAllText(FilePath, $"{{\"port\":{port},\"token\":\"{extensionToken}\"}}");
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); }
        catch { /* 종료 중 실패는 무시한다. */ }
    }
}

enum ExtensionUpdateOutcome
{
    Accepted,
    /// <summary>교수가 화면에서 내린 파일이라 공유하지 않았다.</summary>
    Suppressed
}

sealed record SharedFile(
    string Id,
    string Name,
    string Path,
    string Language,
    DateTimeOffset UpdatedAt,
    string Content);

sealed record ClassroomSnapshot(
    string ClassName,
    string? ProfessorActiveId,
    string? ProfessorActiveName,
    int? ProfessorActiveLine,
    int Viewers,
    bool Broadcasting,
    SharedFile[] Files);

sealed record HostSnapshot(
    ClassroomSnapshot Classroom,
    bool Broadcasting,
    bool VisualStudioConnected,
    string VisualStudioStatus,
    string Pin,
    string[] StudentUrls);

static class SecurityRules
{
    private const int MaxCharacters = 1_000_000;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cshtml", ".razor", ".cpp", ".c", ".h", ".hpp", ".java", ".kt",
        ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css", ".scss", ".sql",
        ".xml", ".xaml", ".json", ".yaml", ".yml", ".md", ".txt", ".shader"
    };
    private static readonly HashSet<string> BlockedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", "secrets"
    };

    public static bool IsShareable(string filePath, string solutionRoot, int characterCount)
    {
        if (characterCount > MaxCharacters || string.IsNullOrWhiteSpace(solutionRoot)) return false;

        var fullFile = Path.GetFullPath(filePath);
        var fullRoot = Path.GetFullPath(solutionRoot);
        var relative = Path.GetRelativePath(fullRoot, fullFile);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}"))
            return false;

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var fileName = Path.GetFileName(fullFile);
        return AllowedExtensions.Contains(Path.GetExtension(fullFile)) &&
               !segments.Any(BlockedDirectories.Contains) &&
               !fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) &&
               !fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) &&
               !fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Equals("secrets.json", StringComparison.OrdinalIgnoreCase);
    }

    public static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".cshtml" or ".razor" => "C#",
        ".cpp" or ".c" or ".h" or ".hpp" => "C++",
        ".js" or ".jsx" => "JavaScript",
        ".ts" or ".tsx" => "TypeScript",
        ".py" => "Python",
        ".html" => "HTML",
        ".css" or ".scss" => "CSS",
        ".json" => "JSON",
        ".xml" or ".xaml" => "XML",
        ".sql" => "SQL",
        _ => "Text"
    };

    public static void SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClassroomLiveTest", "Solution");
        Assert(IsShareable(Path.Combine(root, "Scripts", "Player.cs"), root, 100), "일반 코드 파일 허용");
        Assert(!IsShareable(Path.Combine(root, "..", "private.cs"), root, 100), "솔루션 외부 차단");
        Assert(!IsShareable(Path.Combine(root, ".env"), root, 100), ".env 차단");
        Assert(!IsShareable(Path.Combine(root, "bin", "Generated.cs"), root, 100), "빌드 폴더 차단");
        Assert(!IsShareable(Path.Combine(root, "logo.png"), root, 100), "바이너리 확장자 차단");
        Assert(!IsShareable(Path.Combine(root, "Huge.cs"), root, MaxCharacters + 1), "대용량 파일 차단");

        SessionSelfTest();
    }

    private static void SessionSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClassroomLiveTest", "Solution");
        var file = Path.Combine(root, "Scripts", "Player.cs");
        ExtensionUpdateRequest Sync(string content, int line) =>
            new("sync", file, root, content, line);

        var session = new ClassroomSession();
        session.SetBroadcasting(true);
        session.ApplyExtensionUpdate(new ExtensionUpdateRequest("share", file, root, "class Player {}", 3));

        var live = session.GetSnapshot();
        Assert(live.Files.Length == 1, "방송 중에는 학생이 파일을 본다");
        Assert(live.ProfessorActiveLine == 3, "교수가 보는 줄이 전달된다");

        // 화면 고정: 마지막 상태는 남고 갱신만 멈춘다.
        session.SetBroadcasting(false);
        session.ApplyExtensionUpdate(Sync("class Player { void Update() {} }", 99));
        var frozen = session.GetSnapshot();
        Assert(frozen.Files.Length == 1, "고정해도 학생 화면은 남는다");
        Assert(frozen.Files[0].Content == "class Player {}", "고정 중에는 내용이 갱신되지 않는다");
        Assert(frozen.ProfessorActiveLine == 3, "고정 중에는 교수 위치도 움직이지 않는다");

        session.SetBroadcasting(true);
        session.ApplyExtensionUpdate(Sync("class Player { void Update() {} }", 7));
        Assert(session.GetSnapshot().Files[0].Content.Contains("Update"), "재개하면 다시 갱신된다");
        Assert(session.GetSnapshot().ProfessorActiveLine == 7, "재개하면 교수 위치도 따라온다");

        // 교수가 ×로 내리면 확장에 409로 알려서 단축키 한 번에 다시 공유되게 한다.
        var fileId = session.GetSnapshot().Files[0].Id;
        session.Remove(fileId);
        Assert(session.ApplyExtensionUpdate(Sync("class Player {}", 1)) == ExtensionUpdateOutcome.Suppressed,
            "내린 파일은 확장에 Suppressed로 알린다");
        Assert(session.ApplyExtensionUpdate(new ExtensionUpdateRequest("share", file, root, "class Player {}", 1))
            == ExtensionUpdateOutcome.Accepted, "다시 공유하면 정상 수락된다");

        // Action이 null이어도 죽지 않아야 한다.
        session.ApplyExtensionUpdate(new ExtensionUpdateRequest(null!, null, null, null, 0));

        Assert(!session.IsPinRateLimited("1.2.3.4"), "처음에는 제한 없음");
        for (var i = 0; i < ClassroomSession.MaxPinAttempts; i++) session.RecordPinFailure("1.2.3.4");
        Assert(session.IsPinRateLimited("1.2.3.4"), "PIN 반복 실패 시 차단");
        Assert(!session.IsPinRateLimited("5.6.7.8"), "다른 주소는 영향 없음");
        session.ClearPinFailures("1.2.3.4");
        Assert(!session.IsPinRateLimited("1.2.3.4"), "성공하면 카운터 초기화");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"자체 검사 실패: {name}");
    }
}
