using System.Security.Cryptography;
using System.Text;

sealed class ClassroomSession
{
    internal const int MaxPinAttempts = 10;
    private static readonly TimeSpan PinAttemptWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PendingCommandLifetime = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFile> _files = [];
    private readonly Dictionary<string, DateTimeOffset> _viewers = [];
    private readonly HashSet<string> _suppressedFiles = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset WindowStart)> _pinAttempts = [];
    private string? _professorActiveId;
    private string? _professorActiveName;
    private int? _professorActiveLine;
    private string? _currentFileName;
    private bool _currentFileShared;
    private string? _pendingCommand;
    private DateTimeOffset _pendingCommandAt;
    private bool _broadcasting;
    private DateTimeOffset _lastHostPoll = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastExtensionHeartbeat = DateTimeOffset.MinValue;
    private string _visualStudioStatus = "연결 대기";

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
    /// 실시간 갱신을 켜고 끈다. 끄면 '멈춤'이다. 학생 화면은 마지막 상태 그대로 남고
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

            // 교수 화면의 "현재 파일 공유" 버튼이 쓸 정보. 멈춤 중에도 최신으로 둔다.
            // 이건 표시용일 뿐이고 학생에게 나가는 내용에는 영향을 주지 않는다.
            _currentFileName = hasSafeActiveFile ? Path.GetFileName(request.FilePath) : null;
            _currentFileShared = hasSafeActiveFile && action is "share" or "sync";

            if (action == "unshare")
            {
                if (!string.IsNullOrWhiteSpace(request.FilePath))
                {
                    var id = FileId(request.FilePath);
                    _files.Remove(id);
                    _suppressedFiles.Remove(id);
                    if (_professorActiveId == id) ClearProfessorPointer();
                }
                _visualStudioStatus = "공유 해제됨";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (!_broadcasting)
            {
                // 멈춤 중에는 교수 포인터까지 그대로 둔다. 학생이 보던 화면이
                // 발밑에서 움직이지 않아야 '멈춤'이라는 말이 지켜진다.
                _visualStudioStatus = "멈춤 · 학생은 마지막 화면을 봐요";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (action == "share" && activeId is not null) _suppressedFiles.Remove(activeId);

            if (action is not ("share" or "sync"))
            {
                _professorActiveName = hasSafeActiveFile ? Path.GetFileName(request.FilePath) : null;
                _professorActiveId = activeId is not null && _files.ContainsKey(activeId) ? activeId : null;
                _professorActiveLine = _professorActiveId is null ? null : activeLine;
                _visualStudioStatus = _professorActiveName is null
                    ? "코드 파일을 선택해 주세요"
                    : $"{_professorActiveName} · 공유 안 함";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (!hasSafeActiveFile || request.Content is null)
            {
                ClearProfessorPointer();
                _visualStudioStatus = "공유할 수 없는 파일";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (_suppressedFiles.Contains(activeId!))
            {
                ClearProfessorPointer();
                _visualStudioStatus = "내려둔 파일";
                return ExtensionUpdateOutcome.Suppressed;
            }

            UpdateSharedFile(request.FilePath!, request.Content, request.SolutionRoot!);
            _professorActiveName = Path.GetFileName(request.FilePath);
            _professorActiveId = _files.ContainsKey(activeId!) ? activeId : null;
            _professorActiveLine = _professorActiveId is null ? null : activeLine;
            _visualStudioStatus = $"{_professorActiveName} · 공유 중";
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

    public void RecordHostPoll()
    {
        lock (_gate) _lastHostPoll = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 교수 화면에서 현재 파일을 공유/해제하도록 요청한다. 확장이 다음 폴링(최대 0.6초)에서
    /// 가져가 실행한다. 호스트에서 확장으로 가는 유일한 통로다.
    /// </summary>
    public void RequestShare(bool enabled)
    {
        lock (_gate)
        {
            _pendingCommand = enabled ? "share" : "unshare";
            _pendingCommandAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>확장이 명령을 가져간다. 한 번만 나가고, 확장이 죽어 있으면 곧 버려진다.</summary>
    public string? TakePendingCommand() => TakePendingCommand(PendingCommandLifetime);

    /// <param name="lifetime">이 시간이 지난 명령은 버린다. 자체 검사에서 만료를 확인하려고 열어둔다.</param>
    internal string? TakePendingCommand(TimeSpan lifetime)
    {
        lock (_gate)
        {
            if (_pendingCommand is null) return null;
            // 확장이 꺼져 있는 동안 눌린 버튼이 나중에 되살아나면 곤란하다.
            if (DateTimeOffset.UtcNow - _pendingCommandAt > lifetime)
            {
                _pendingCommand = null;
                return null;
            }

            var command = _pendingCommand;
            _pendingCommand = null;
            return command;
        }
    }

    /// <summary>
    /// 교수 화면이 오래 닫혀 있고 보고 있는 학생도 없는 상태.
    /// 콘솔 창을 없앤 대신 잊힌 서버가 계속 떠 있지 않게 하는 안전장치다.
    /// </summary>
    public bool IsIdle(TimeSpan after)
    {
        lock (_gate)
        {
            PruneViewers();
            return _viewers.Count == 0 && DateTimeOffset.UtcNow - _lastHostPoll > after;
        }
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
                connected ? _visualStudioStatus : "Visual Studio 연결 대기",
                connected ? _currentFileName : null,
                connected && _currentFileShared,
                Pin, studentUrls);
        }
    }

    private ClassroomSnapshot Snapshot() => new(
        "수업 중",
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
    /// <summary>Visual Studio에서 지금 열려 있는 파일. 공유 여부와 무관하다.</summary>
    string? CurrentFileName,
    bool CurrentFileShared,
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
        Assert(live.Files.Length == 1, "실시간일 때 학생이 파일을 본다");
        Assert(live.ProfessorActiveLine == 3, "교수가 보는 줄이 전달된다");

        // 멈춤: 마지막 상태는 남고 갱신만 멈춘다.
        session.SetBroadcasting(false);
        session.ApplyExtensionUpdate(Sync("class Player { void Update() {} }", 99));
        var frozen = session.GetSnapshot();
        Assert(frozen.Files.Length == 1, "멈춰도 학생 화면은 남는다");
        Assert(frozen.Files[0].Content == "class Player {}", "멈춤 중에는 내용이 갱신되지 않는다");
        Assert(frozen.ProfessorActiveLine == 3, "멈춤 중에는 교수 위치도 움직이지 않는다");

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

        // 교수 화면 버튼 -> 확장 명령 통로. 한 번만 나가야 확장이 두 번 실행하지 않는다.
        Assert(session.TakePendingCommand() is null, "대기 중인 명령이 없으면 null");
        session.RequestShare(true);
        Assert(session.TakePendingCommand() == "share", "공유 요청이 확장에 전달된다");
        Assert(session.TakePendingCommand() is null, "명령은 한 번만 나간다");
        session.RequestShare(false);
        Assert(session.TakePendingCommand() == "unshare", "해제 요청도 전달된다");

        // 확장이 꺼져 있는 동안 눌린 버튼이 나중에 되살아나면 안 된다.
        var stale = new ClassroomSession();
        stale.RequestShare(true);
        Assert(stale.TakePendingCommand(TimeSpan.FromSeconds(-1)) is null, "오래된 명령은 버린다");

        // 유휴 종료가 엉뚱할 때 서버를 내리면 수업이 끊긴다. 두 안전장치를 확인한다.
        var fresh = new ClassroomSession();
        Assert(!fresh.IsIdle(TimeSpan.FromMinutes(30)), "방금 시작한 서버를 종료하지 않는다");
        // 음수 기준으로 두면 '시간은 충분히 지났다' 조건만 참이 되어 학생 유무만 본다.
        var elapsed = TimeSpan.FromMilliseconds(-1);
        Assert(fresh.IsIdle(elapsed), "교수 화면도 학생도 없으면 유휴다");
        fresh.RecordViewer("student-1");
        Assert(!fresh.IsIdle(elapsed), "학생이 보고 있으면 종료하지 않는다");
        fresh.RecordHostPoll();
        Assert(!fresh.IsIdle(TimeSpan.FromMinutes(30)), "교수 화면이 살아 있으면 종료하지 않는다");

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
