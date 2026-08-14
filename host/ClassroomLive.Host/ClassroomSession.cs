using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

sealed class ClassroomSession
{
    internal const int MaxPinAttempts = 10;
    private static readonly TimeSpan PinAttemptWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PendingCommandLifetime = TimeSpan.FromSeconds(5);
    /// <summary>주인이 이 시간 동안 조용하면 다른 창이 넘겨받는다. 폴링 간격(0.6초)보다 넉넉히 크게.</summary>
    private static readonly TimeSpan OwnerTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFile> _files = [];
    private readonly Dictionary<string, DateTimeOffset> _viewers = [];
    // 교수가 목록에서 내린 파일. 확장이 아직 동기화 중이면 409로 알려 되살아나지 않게 한다.
    private readonly HashSet<string> _unsharedFiles = [];
    // 내용 경고를 교수가 한 번 승인한 파일. 서버를 다시 켜면 승인은 사라진다.
    private readonly HashSet<string> _approvedSensitiveFiles = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset WindowStart)> _pinAttempts = [];
    private string? _professorActiveId;
    private string? _professorActiveName;
    private int? _professorActiveLine;
    private string? _currentFileName;
    private string? _currentFileDisplayPath;
    private string? _currentFileId;
    private bool _currentFileShareable;
    private string? _currentFileBlockReason;
    private string? _currentFileWarning;
    private string? _pendingCommand;
    private DateTimeOffset _pendingCommandAt;
    private bool _broadcasting;
    private bool _everStarted;
    private bool _ended;
    // Visual Studio를 여러 개 열면 각 창의 확장이 전부 자기 활성 파일을 보낸다.
    // 한 창만 '주인'으로 두지 않으면 교수 화면과 학생 화면이 창 사이를 오가며 깜빡인다.
    private string? _ownerInstance;
    private DateTimeOffset _ownerSeenAt;
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
        lock (_gate)
        {
            if (_ended) return;
            _broadcasting = enabled;
            // 처음 켜는 것과 멈췄다 다시 켜는 것을 화면에서 구분하려고 기억해둔다.
            if (enabled) _everStarted = true;
        }
    }

    /// <summary>정상 종료를 학생에게 알린다. 한 번 끝난 세션은 다시 방송하지 않는다.</summary>
    public void End()
    {
        lock (_gate)
        {
            _ended = true;
            _broadcasting = false;
        }
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

            // 교수가 그 창에서 직접 누른 조작이면 주인을 그쪽으로 넘긴다.
            // 그래야 다른 창으로 옮겨가서 공유를 눌렀을 때 바로 먹는다.
            var instance = string.IsNullOrWhiteSpace(request.InstanceId) ? "unknown" : request.InstanceId!;
            var userAction = action is "share" or "unshare" or "hide" or "unhide";
            if (_ownerInstance is null || userAction ||
                DateTimeOffset.UtcNow - _ownerSeenAt > OwnerTimeout)
            {
                _ownerInstance = instance;
            }
            if (_ownerInstance != instance)
            {
                // 주인이 아닌 창의 폴링은 무시한다. 무시해야 깜빡이지 않는다.
                return ExtensionUpdateOutcome.Accepted;
            }
            _ownerSeenAt = DateTimeOffset.UtcNow;
            var blockReason = string.IsNullOrWhiteSpace(request.FilePath)
                ? null
                : SecurityRules.BlockReason(request.FilePath, request.SolutionRoot ?? "", request.Content);
            var hasSafeActiveFile = !string.IsNullOrWhiteSpace(request.FilePath) && blockReason is null;
            var activeId = hasSafeActiveFile ? FileId(request.FilePath!) : null;
            var contentWarning = hasSafeActiveFile && request.Content is not null
                ? SecurityRules.ContentWarning(request.Content)
                : null;
            int? activeLine = request.ActiveLine > 0 ? request.ActiveLine : null;

            // 교수 화면 버튼과 Visual Studio 메뉴가 쓸 정보. 멈춤 중에도 최신으로 둔다.
            // 공유할 수 없는 파일도 이름은 남긴다. 그래야 왜 안 되는지 안내할 수 있다.
            _currentFileName = string.IsNullOrWhiteSpace(request.FilePath)
                ? null
                : Path.GetFileName(request.FilePath);
            _currentFileDisplayPath = hasSafeActiveFile
                ? DisplayPath(request.FilePath!, request.SolutionRoot!)
                : _currentFileName;
            _currentFileShareable = hasSafeActiveFile;
            _currentFileBlockReason = blockReason;
            _currentFileWarning = contentWarning;
            _currentFileId = activeId;

            // 숨김은 되돌릴 수 있다. 파일은 목록에 남고 학생 화면에서만 빠진다.
            if (action is "hide" or "unhide")
            {
                if (activeId is not null && _files.TryGetValue(activeId, out var target))
                {
                    _files[activeId] = target with { Hidden = action == "hide" };
                    _visualStudioStatus = target.Name + (action == "hide" ? " · 숨김" : " · 다시 보임");
                }
                return ExtensionUpdateOutcome.Accepted;
            }

            if (action == "unshare")
            {
                if (!string.IsNullOrWhiteSpace(request.FilePath))
                {
                    var id = FileId(request.FilePath);
                    _files.Remove(id);
                    _unsharedFiles.Remove(id);
                    _approvedSensitiveFiles.Remove(id);
                    if (_professorActiveId == id) ClearProfessorPointer();
                }
                _visualStudioStatus = "공유 해제됨";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (!_broadcasting)
            {
                // 멈춤 중에는 교수 포인터까지 그대로 둔다. 학생이 보던 화면이
                // 발밑에서 움직이지 않아야 '멈춤'이라는 말이 지켜진다.
                _visualStudioStatus = _everStarted
                    ? "일시정지 · 코드 갱신을 멈췄어요"
                    : "시작 전 · 아직 학생에게 전송하지 않아요";
                return ExtensionUpdateOutcome.Accepted;
            }

            if (action == "share" && activeId is not null) _unsharedFiles.Remove(activeId);

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
                _visualStudioStatus = _currentFileBlockReason ?? "공유할 수 없는 파일";
                return ExtensionUpdateOutcome.Rejected;
            }

            if (contentWarning is not null && !_approvedSensitiveFiles.Contains(activeId!))
            {
                if (action == "share" && request.AllowSensitive)
                {
                    _approvedSensitiveFiles.Add(activeId!);
                }
                else
                {
                    ClearProfessorPointer();
                    _visualStudioStatus = contentWarning;
                    return ExtensionUpdateOutcome.NeedsConfirmation;
                }
            }

            if (_unsharedFiles.Contains(activeId!))
            {
                ClearProfessorPointer();
                _visualStudioStatus = "공유 해제된 파일";
                return ExtensionUpdateOutcome.Unshared;
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
        if (!_broadcasting || !SecurityRules.IsShareable(fullPath, solutionRoot, content)) return;

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
                SecurityRules.LanguageFor(normalizedPath), now, content, Hidden: false);
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
    public void RequestShare(bool enabled) => RequestCommand(enabled ? "share" : "unshare");

    /// <summary>확장이 다음 폴링에서 가져갈 명령을 남긴다.</summary>
    public void RequestCommand(string command)
    {
        lock (_gate)
        {
            _pendingCommand = command;
            _pendingCommandAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// 확장이 폴링할 때마다 돌려주는 상태. Visual Studio 메뉴가 이걸로 글자와
    /// 활성 여부를 정한다. 별도 조회 없이 기존 통로에 실어 보낸다.
    /// </summary>
    public ExtensionReply BuildReply(string? instanceId = null, string? filePath = null,
        string? solutionRoot = null)
    {
        lock (_gate)
        {
            var instance = string.IsNullOrWhiteSpace(instanceId) ? "unknown" : instanceId!;
            var owner = _ownerInstance is null || _ownerInstance == instance;
            var shareable = _currentFileShareable;
            var blockReason = _currentFileBlockReason;
            var warning = _currentFileWarning;
            var shared = CurrentFileIsShared();
            var hidden = CurrentFileIsHidden();

            // 비주인 창에는 주인 창의 파일 상태가 아니라 그 창이 보낸 파일 상태를 돌려준다.
            // 그렇지 않으면 정상 파일에 다른 창의 .env 차단 이유가 뜨거나 버튼 동작이 뒤집힌다.
            if (!owner)
            {
                blockReason = string.IsNullOrWhiteSpace(filePath)
                    ? null
                    : SecurityRules.BlockReason(filePath, solutionRoot ?? "", null);
                shareable = !string.IsNullOrWhiteSpace(filePath) && blockReason is null;
                var fileId = shareable ? FileId(filePath!) : null;
                shared = fileId is not null && _files.ContainsKey(fileId);
                hidden = fileId is not null && _files.TryGetValue(fileId, out var file) && file.Hidden;
                warning = null;
            }

            return new ExtensionReply(
                owner,
                owner ? TakePendingCommand() : null,
                _broadcasting,
                _everStarted,
                shareable,
                blockReason,
                warning,
                shared,
                hidden);
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

    /// <summary>목록에서 완전히 뺀다. 되살리려면 다시 공유해야 한다.</summary>
    public void Unshare(string id)
    {
        lock (_gate)
        {
            // 한 수업에서 이만큼 내릴 일은 없다. 무한정 쌓이는 것만 막는다.
            if (_unsharedFiles.Count > 500) _unsharedFiles.Clear();
            _unsharedFiles.Add(id);
            _approvedSensitiveFiles.Remove(id);
            _files.Remove(id);
            if (_professorActiveId == id) ClearProfessorPointer();
        }
    }

    /// <summary>학생 화면에서만 감춘다. 목록에는 남아 있어 언제든 되돌릴 수 있다.</summary>
    public bool SetHidden(string id, bool hidden)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(id, out var file)) return false;
            _files[id] = file with { Hidden = hidden };
            if (hidden && _professorActiveId == id) _professorActiveId = null;
            return true;
        }
    }

    public ClassroomSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            PruneViewers();
            // 학생에게는 숨긴 파일을 보내지 않는다.
            return Snapshot(file => !file.Hidden);
        }
    }

    public HostSnapshot GetHostSnapshot(string[] studentUrls)
    {
        lock (_gate)
        {
            PruneViewers();
            var connected = DateTimeOffset.UtcNow - _lastExtensionHeartbeat < TimeSpan.FromSeconds(3);
            // 교수 화면은 숨긴 파일까지 봐야 되돌릴 수 있다.
            return new HostSnapshot(Snapshot(_ => true), _broadcasting, _everStarted, connected,
                connected ? _visualStudioStatus : "Visual Studio 연결 대기",
                connected ? _currentFileName : null,
                connected ? _currentFileDisplayPath : null,
                connected && _currentFileShareable,
                connected ? _currentFileBlockReason : null,
                connected && CurrentFileIsShared(),
                connected && CurrentFileIsHidden(),
                Pin, studentUrls);
        }
    }

    private bool CurrentFileIsShared() =>
        _currentFileId is not null && _files.ContainsKey(_currentFileId);

    private bool CurrentFileIsHidden() =>
        _currentFileId is not null && _files.TryGetValue(_currentFileId, out var file) && file.Hidden;

    private static string DisplayPath(string filePath, string solutionRoot)
    {
        try
        {
            var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(solutionRoot));
            var relative = Path.GetRelativePath(solutionRoot, filePath).Replace('\\', '/');
            return string.IsNullOrEmpty(rootName) ? relative : $"{rootName}/{relative}";
        }
        catch { return Path.GetFileName(filePath); }
    }

    private ClassroomSnapshot Snapshot(Func<SharedFile, bool> include)
    {
        // 경로 기준 안정 정렬. 최근 수정순으로 두면 교수가 타이핑하는 파일이
        // 학생 커서 밑에서 계속 맨 위로 튄다.
        var files = _files.Values.Where(include)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var activeIsVisible = _professorActiveId is not null &&
            files.Any(file => file.Id == _professorActiveId);

        return new ClassroomSnapshot(
            "수업 중",
            activeIsVisible ? _professorActiveId : null,
            activeIsVisible ? _professorActiveName : null,
            activeIsVisible ? _professorActiveLine : null,
            _viewers.Count,
            _broadcasting,
            _everStarted,
            _ended,
            files);
    }

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
            _approvedSensitiveFiles.Remove(oldest.Id);
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

    /// <summary>확장의 "실행"이 쓸 실행 파일 위치. 종료해도 남겨둔다.</summary>
    public static string InstallPath { get; } = Path.Combine(
        Path.GetDirectoryName(FilePath)!, "install.json");

    public static void RememberInstall(string executablePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstallPath)!);
        File.WriteAllText(InstallPath, $"{{\"exe\":{System.Text.Json.JsonSerializer.Serialize(executablePath)}}}");
    }
}

/// <summary>확장 폴링 응답. 필드 이름은 그대로 JSON 키가 된다.</summary>
sealed record ExtensionReply(
    /// <summary>이 창이 지금 수업을 몰고 있는지. 아니면 폴링이 무시된다.</summary>
    bool Owner,
    string? Command,
    bool Broadcasting,
    bool EverStarted,
    bool Shareable,
    string? BlockReason,
    string? Warning,
    bool Shared,
    bool Hidden);

enum ExtensionUpdateOutcome
{
    Accepted,
    /// <summary>내용에 비밀값이 의심되어 교수의 명시적 확인이 필요하다.</summary>
    NeedsConfirmation,
    /// <summary>경로나 파일 종류가 안전 규칙에 걸려 공유하지 않았다.</summary>
    Rejected,
    /// <summary>교수가 목록에서 내린 파일이라 공유하지 않았다. 확장에 409로 알린다.</summary>
    Unshared
}

sealed record SharedFile(
    string Id,
    string Name,
    string Path,
    string Language,
    DateTimeOffset UpdatedAt,
    string Content,
    bool Hidden);

sealed record ClassroomSnapshot(
    string ClassName,
    string? ProfessorActiveId,
    string? ProfessorActiveName,
    int? ProfessorActiveLine,
    int Viewers,
    bool Broadcasting,
    bool EverStarted,
    bool Ended,
    SharedFile[] Files);

sealed record HostSnapshot(
    ClassroomSnapshot Classroom,
    bool Broadcasting,
    /// <summary>한 번이라도 시작했는지. 버튼을 "시작"과 "재개"로 나누는 데 쓴다.</summary>
    bool EverStarted,
    bool VisualStudioConnected,
    string VisualStudioStatus,
    /// <summary>Visual Studio에서 지금 열려 있는 파일. 공유 여부와 무관하다.</summary>
    string? CurrentFileName,
    string? CurrentFileDisplayPath,
    bool CurrentFileShareable,
    /// <summary>공유할 수 없을 때 그 이유. 공유 가능하면 null.</summary>
    string? CurrentFileBlockReason,
    bool CurrentFileShared,
    bool CurrentFileHidden,
    string Pin,
    string[] StudentUrls);

static class SecurityRules
{
    private const int MaxCharacters = 1_000_000;
    private const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;

    // 예전에는 확장자 허용 목록이었다. .go, .rs, .php, Makefile처럼 멀쩡한 텍스트가
    // 전부 막혀서 뒤집었다. 무엇이 텍스트인지는 Visual Studio가 이미 판단한다.
    // TextDocument로 열리지 않는 파일은 확장이 애초에 보내지 못한다.
    // 그래서 여기서는 '텍스트인가'를 다시 묻지 않고, 새어 나가면 안 되는 것만 막는다.
    private static readonly HashSet<string> BlockedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "secrets",
        ".venv", "venv", "__pycache__", "target", ".aws", ".azure", ".kube", ".ssh",
        ".gnupg", ".docker"
    };

    private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "nuget.config", "launchsettings.json", "web.config", "gradle.properties",
        "credentials.json", "service-account.json", "local.settings.json", "settings.xml",
        "application.properties", "application.yml", "application.yaml", "wp-config.php",
        ".pypirc", "pip.conf", ".git-credentials", ".htpasswd", "google-services.json"
    };

    /// <summary>내용이 텍스트여도 학생에게 나가면 안 되는 것.</summary>
    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".crt", ".cer", ".jks", ".keystore", ".ppk", ".asc", ".gpg",
        ".kdbx", ".ovpn", ".mobileprovision"
    };

    /// <summary>공유할 수 없으면 그 이유를, 공유해도 되면 null을 돌려준다.</summary>
    public static string? BlockReason(string filePath, string solutionRoot, string? content)
    {
        if (string.IsNullOrWhiteSpace(solutionRoot)) return "솔루션이 열려 있지 않습니다";
        if ((content?.Length ?? 0) > MaxCharacters) return "파일이 너무 큽니다";

        string fullFile;
        string fullRoot;
        string relative;
        try
        {
            fullFile = Path.GetFullPath(filePath);
            fullRoot = Path.GetFullPath(solutionRoot);
            relative = Path.GetRelativePath(fullRoot, fullFile);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}"))
                return "솔루션 폴더 밖의 파일입니다";
            if (ContainsReparsePoint(fullFile, fullRoot)) return "링크를 거친 파일은 공유할 수 없습니다";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "파일 경로를 확인할 수 없습니다";
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(BlockedDirectories.Contains)) return "빌드·도구 폴더의 파일입니다";

        if (IsSecretName(Path.GetFileName(fullFile))) return "비밀이 들어 있을 수 있는 파일입니다";
        if (content is not null && LooksBinary(content)) return "텍스트 파일이 아닙니다";

        return null;
    }

    public static bool IsShareable(string filePath, string solutionRoot, string? content) =>
        BlockReason(filePath, solutionRoot, content) is null;

    /// <summary>
    /// 소스 안의 비밀값 후보는 오탐 가능성이 있으므로 차단하지 않고 교수에게 한 번 확인받는다.
    /// 실제 값은 응답이나 로그에 넣지 않는다.
    /// </summary>
    public static string? ContentWarning(string content)
    {
        if (content.Contains("-----BEGIN PRIVATE KEY-----", Ignore) ||
            content.Contains("-----BEGIN RSA PRIVATE KEY-----", Ignore) ||
            content.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", Ignore))
            return "개인 키처럼 보이는 내용이 있습니다. 그래도 공유할까요?";

        var patterns = new (string Pattern, string Message)[]
        {
            (@"\bAKIA[0-9A-Z]{16}\b", "AWS 액세스 키처럼 보이는 내용이 있습니다. 그래도 공유할까요?"),
            (@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", "GitHub 토큰처럼 보이는 내용이 있습니다. 그래도 공유할까요?"),
            (@"\bsk-[A-Za-z0-9_-]{20,}\b", "API 키처럼 보이는 내용이 있습니다. 그래도 공유할까요?"),
            ("(?im)\\b(password|passwd|client_secret|api_key|access_token)\\b\\s*[:=]\\s*['\"][^'\"\\r\\n]{8,}['\"]",
                "비밀번호나 토큰처럼 보이는 값이 있습니다. 그래도 공유할까요?")
        };

        try
        {
            foreach (var candidate in patterns)
                if (Regex.IsMatch(content, candidate.Pattern, RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)))
                    return candidate.Message;
        }
        catch (RegexMatchTimeoutException)
        {
            return "민감 정보 검사를 완료하지 못했습니다. 그래도 공유할까요?";
        }

        return null;
    }

    private static bool IsSecretName(string name) =>
        name.Equals(".env", Ignore) || name.StartsWith(".env.", Ignore) ||
        name.StartsWith("appsettings", Ignore) ||
        name.Equals("secrets.json", Ignore) ||
        SecretFileNames.Contains(name) ||
        name.Equals(".npmrc", Ignore) || name.Equals(".netrc", Ignore) ||
        name.StartsWith("id_rsa", Ignore) || name.StartsWith("id_ed25519", Ignore) ||
        name.StartsWith("firebase-adminsdk", Ignore) ||
        name.EndsWith(".user", Ignore) ||
        name.EndsWith(".pubxml", Ignore) ||
        name.EndsWith(".tfvars", Ignore) || name.EndsWith(".tfvars.json", Ignore) ||
        name.EndsWith(".tfstate", Ignore) || name.EndsWith(".tfstate.backup", Ignore) ||
        SecretExtensions.Contains(Path.GetExtension(name));

    private static bool ContainsReparsePoint(string fullFile, string fullRoot)
    {
        FileSystemInfo? current = new FileInfo(fullFile);
        while (current is not null && !current.FullName.Equals(fullRoot, Ignore))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return false;
    }

    /// <summary>
    /// 이진 파일을 소스 편집기로 억지로 연 경우를 잡는 마지막 그물.
    /// NUL이 있거나 제어문자가 지나치게 많으면 텍스트가 아니다.
    /// </summary>
    private static bool LooksBinary(string content)
    {
        var limit = Math.Min(content.Length, 4000);
        if (limit == 0) return false;

        var control = 0;
        for (var index = 0; index < limit; index++)
        {
            var character = content[index];
            if (character == '\0') return true;
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t')) control++;
        }
        return control * 100 / limit > 2;
    }

    public static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".cshtml" or ".razor" => "C#",
        ".c" or ".h" => "C",
        ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "C++",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "JavaScript",
        ".ts" or ".tsx" => "TypeScript",
        ".py" => "Python",
        ".go" => "Go",
        ".rs" => "Rust",
        ".java" => "Java",
        ".kt" or ".kts" => "Kotlin",
        ".swift" => "Swift",
        ".rb" => "Ruby",
        ".php" => "PHP",
        ".lua" => "Lua",
        ".dart" => "Dart",
        ".vb" => "VB",
        ".fs" or ".fsx" => "F#",
        ".sh" or ".bash" or ".zsh" => "Shell",
        ".ps1" or ".psm1" => "PowerShell",
        ".bat" or ".cmd" => "Batch",
        ".toml" => "TOML",
        ".ini" or ".cfg" or ".conf" => "INI",
        ".md" or ".markdown" => "Markdown",
        ".glsl" or ".hlsl" or ".cginc" or ".shader" => "Shader",
        ".html" or ".htm" => "HTML",
        ".css" or ".scss" or ".sass" or ".less" => "CSS",
        ".json" or ".jsonc" => "JSON",
        ".xml" or ".xaml" => "XML",
        ".sql" => "SQL",
        _ => "Text"
    };

    public static void SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClassroomLiveTest", "Solution");
        string Text(int length = 100) => new('x', length);
        bool Ok(string name) => IsShareable(Path.Combine(root, name), root, Text());

        Assert(Ok(Path.Combine("Scripts", "Player.cs")), "일반 코드 파일 허용");
        // 확장자 허용 목록을 없앴다. Visual Studio가 텍스트로 열 수 있으면 무엇이든 된다.
        foreach (var name in new[]
                 {
                     "main.go", "lib.rs", "app.php", "View.swift", "script.lua", "Cargo.toml",
                     "Makefile", ".gitignore", "build.gradle", "CMakeLists.txt", "notes.rst",
                     "deploy.sh", "Setup.ps1", "Player.vb", "index.vue", "Scene.unity",
                     "query.graphql", "schema.prisma", "Dockerfile", "app.r"
                 })
            Assert(Ok(name), $"텍스트 파일 허용: {name}");

        Assert(!IsShareable(Path.Combine(root, "..", "private.cs"), root, Text()), "솔루션 외부 차단");
        Assert(!Ok(".env"), ".env 차단");
        Assert(!Ok(".env.production"), ".env.* 차단");
        Assert(!Ok("appsettings.json"), "appsettings 차단");
        Assert(!Ok("server.pem"), "인증서 차단");
        Assert(!Ok("id_rsa"), "개인키 차단");
        Assert(!Ok("Project.csproj.user"), "사용자 설정 차단");
        foreach (var name in new[]
                 {
                     "nuget.config", "launchSettings.json", "web.config", "gradle.properties",
                     "terraform.tfvars", "credentials.json", "service-account.json"
                 })
            Assert(!Ok(name), $"민감 설정 파일 차단: {name}");
        Assert(!Ok(Path.Combine("bin", "Generated.cs")), "빌드 폴더 차단");
        Assert(!Ok(Path.Combine(".aws", "credentials")), "자격 증명 폴더 차단");
        Assert(!Ok(Path.Combine("node_modules", "index.js")), "의존성 폴더 차단");
        Assert(!IsShareable(Path.Combine(root, "Huge.cs"), root, Text(MaxCharacters + 1)), "대용량 파일 차단");
        // 이진 파일을 소스 편집기로 억지로 연 경우.
        Assert(!IsShareable(Path.Combine(root, "logo.png"), root, "\u0089PNG\0\u001a"), "내용이 이진이면 차단");
        Assert(BlockReason(Path.Combine(root, ".env"), root, Text()) == "비밀이 들어 있을 수 있는 파일입니다",
            "막힌 이유를 알려준다");
        Assert(ContentWarning("const api_key = \"sk-123456789012345678901234\";") is not null,
            "코드 속 API 키 후보 경고");
        Assert(ContentWarning("var password = ReadPassword();") is null,
            "값이 없는 비밀번호 처리 코드는 경고하지 않는다");

        SessionSelfTest();
    }

    private static void SessionSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClassroomLiveTest", "Solution");
        var file = Path.Combine(root, "Scripts", "Player.cs");
        ExtensionUpdateRequest Sync(string content, int line) =>
            new("sync", file, root, content, line);

        var session = new ClassroomSession();
        session.ApplyExtensionUpdate(Sync("class Player {}", 1));
        var waitingHost = session.GetHostSnapshot([]);
        Assert(waitingHost.VisualStudioStatus.StartsWith("시작 전"),
            "방송 전 상태를 일시정지라고 표시하지 않는다");
        Assert(waitingHost.CurrentFileDisplayPath == "Solution/Scripts/Player.cs",
            "현재 파일을 솔루션 기준 경로로 표시한다");

        var privateSession = new ClassroomSession();
        privateSession.SetBroadcasting(true);
        privateSession.ApplyExtensionUpdate(new ExtensionUpdateRequest(
            "heartbeat", Path.Combine(root, "Scripts", "Private.cs"), root, null, 1));
        Assert(privateSession.GetSnapshot().ProfessorActiveName is null,
            "공유하지 않은 활성 파일 이름은 학생에게 보내지 않는다");
        Assert(privateSession.GetHostSnapshot([]).CurrentFileName == "Private.cs",
            "공유하지 않은 활성 파일도 교수 화면에는 표시한다");

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
        Assert(session.GetHostSnapshot([]).VisualStudioStatus.StartsWith("일시정지"),
            "한 번 시작한 뒤 멈추면 일시정지로 표시한다");

        session.SetBroadcasting(true);
        session.ApplyExtensionUpdate(Sync("class Player { void Update() {} }", 7));
        Assert(session.GetSnapshot().Files[0].Content.Contains("Update"), "재개하면 다시 갱신된다");
        Assert(session.GetSnapshot().ProfessorActiveLine == 7, "재개하면 교수 위치도 따라온다");

        var endedSession = new ClassroomSession();
        endedSession.SetBroadcasting(true);
        endedSession.End();
        endedSession.SetBroadcasting(true);
        var ended = endedSession.GetSnapshot();
        Assert(ended.Ended, "정상 종료 상태가 학생에게 전달된다");
        Assert(!ended.Broadcasting, "끝난 세션은 다시 방송되지 않는다");

        // 교수가 ×로 내리면 확장에 409로 알려서 단축키 한 번에 다시 공유되게 한다.
        var fileId = session.GetSnapshot().Files[0].Id;

        // 숨김: 학생 화면에서만 빠지고 교수 목록에는 남아 되돌릴 수 있다.
        Assert(session.SetHidden(fileId, true), "숨김 처리 성공");
        Assert(session.GetSnapshot().Files.Length == 0, "숨기면 학생에게 안 보인다");
        Assert(session.GetHostSnapshot([]).Classroom.Files.Length == 1, "숨겨도 교수 목록에는 남는다");
        Assert(session.GetHostSnapshot([]).Classroom.Files[0].Hidden, "숨김 표시가 붙는다");
        Assert(session.SetHidden(fileId, false), "숨김 해제 성공");
        Assert(session.GetSnapshot().Files.Length == 1, "숨김을 풀면 다시 보인다");
        Assert(!session.SetHidden("없는id", true), "없는 파일은 숨길 수 없다");

        // 확장에서 숨기고 푸는 경로도 같아야 한다.
        session.ApplyExtensionUpdate(new ExtensionUpdateRequest("hide", file, root, null, 0));
        Assert(session.GetSnapshot().Files.Length == 0, "확장에서 숨겨도 학생에게 안 보인다");
        session.ApplyExtensionUpdate(new ExtensionUpdateRequest("unhide", file, root, null, 0));
        Assert(session.GetSnapshot().Files.Length == 1, "확장에서 숨김을 풀 수 있다");

        // 공유 해제는 목록에서 완전히 뺀다. 확장이 계속 동기화해도 되살아나면 안 된다.
        session.Unshare(fileId);
        Assert(session.GetHostSnapshot([]).Classroom.Files.Length == 0, "공유를 해제하면 교수 목록에서도 빠진다");
        Assert(session.ApplyExtensionUpdate(Sync("class Player {}", 1)) == ExtensionUpdateOutcome.Unshared,
            "해제된 파일은 확장에 409로 알린다");
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

        // Visual Studio 창을 여러 개 열었을 때 서로 덮어쓰지 않아야 한다.
        var multi = new ClassroomSession();
        multi.SetBroadcasting(true);
        var fileA = Path.Combine(root, "A.cs");
        var fileB = Path.Combine(root, "B.cs");
        ExtensionUpdateRequest From(string window, string action, string path, string content) =>
            new(action, path, root, content, 1, false, window);

        // 첫 창이 공유하면 그 창이 주인이 된다.
        multi.ApplyExtensionUpdate(From("win-1", "share", fileA, "class A {}"));
        Assert(multi.BuildReply("win-1").Owner, "먼저 조작한 창이 주인이 된다");
        Assert(!multi.BuildReply("win-2").Owner, "다른 창은 주인이 아니다");

        // 주인이 아닌 창의 폴링은 화면을 바꾸지 못한다. 이게 깜빡임의 원인이었다.
        multi.ApplyExtensionUpdate(From("win-2", "sync", fileB, "class B {}"));
        Assert(multi.GetSnapshot().ProfessorActiveName == "A.cs", "다른 창의 폴링은 무시된다");
        Assert(multi.GetHostSnapshot([]).CurrentFileName == "A.cs", "현재 파일 표시도 흔들리지 않는다");
        var windowBState = multi.BuildReply("win-2", fileB, root);
        Assert(windowBState.Shareable && !windowBState.Shared,
            "비주인 창도 자기 활성 파일의 공유 상태를 받는다");

        // 주인 창의 폴링은 그대로 반영된다.
        multi.ApplyExtensionUpdate(From("win-1", "sync", fileA, "class A { int x; }"));
        Assert(multi.GetSnapshot().Files[0].Content.Contains("int x"), "주인 창의 갱신은 반영된다");

        // 교수가 다른 창에서 직접 누르면 주인이 넘어간다.
        multi.ApplyExtensionUpdate(From("win-2", "share", fileB, "class B {}"));
        Assert(multi.BuildReply("win-2").Owner, "직접 조작하면 주인이 넘어간다");
        Assert(!multi.BuildReply("win-1").Owner, "이전 주인은 주인이 아니게 된다");
        Assert(multi.GetSnapshot().ProfessorActiveName == "B.cs", "넘겨받은 창이 화면을 몬다");

        // 교수 화면의 현재 파일 명령은 주인 창만 가져가야 한다.
        multi.RequestShare(true);
        Assert(multi.BuildReply("win-1", fileA, root).Command is null,
            "비주인 창은 교수 화면 명령을 가져가지 않는다");
        Assert(multi.BuildReply("win-2", fileB, root).Command == "share",
            "교수 화면 명령은 주인 창에 전달된다");

        // 주인 창의 차단 상태가 다른 창의 정상 파일에 묻어나면 공유 버튼이 막힌다.
        var secret = Path.Combine(root, ".env");
        multi.ApplyExtensionUpdate(From("win-1", "share", secret, "PASSWORD=secret"));
        var isolatedState = multi.BuildReply("win-2", fileB, root);
        Assert(isolatedState.Shareable && isolatedState.BlockReason is null,
            "각 창은 자기 활성 파일의 차단 상태를 받는다");

        // 확장 메뉴가 쓰는 상태가 그대로 전달되는지.
        var replySession = new ClassroomSession();
        replySession.SetBroadcasting(true);
        replySession.ApplyExtensionUpdate(new ExtensionUpdateRequest("share", file, root, "x", 1));
        var reply = replySession.BuildReply();
        Assert(reply.Broadcasting && reply.Shareable && reply.Shared && !reply.Hidden, "공유 중 상태가 확장에 전달된다");
        replySession.ApplyExtensionUpdate(new ExtensionUpdateRequest("hide", file, root, null, 0));
        Assert(replySession.BuildReply().Hidden, "숨김 상태가 확장에 전달된다");
        // 이름만으로 막히는 파일은 내용 없이도 바로 알 수 있다.
        replySession.ApplyExtensionUpdate(new ExtensionUpdateRequest("heartbeat", Path.Combine(root, ".env"), root, null, 0));
        Assert(!replySession.BuildReply().Shareable, "공유할 수 없는 파일이면 확장이 안다");
        // 내용을 봐야 아는 경우는 공유를 시도할 때 걸러진다.
        replySession.ApplyExtensionUpdate(new ExtensionUpdateRequest("share", Path.Combine(root, "logo.png"), root, "PNG\0", 0));
        Assert(!replySession.BuildReply().Shared, "내용이 이진이면 공유되지 않는다");

        var sensitive = new ClassroomSession();
        sensitive.SetBroadcasting(true);
        var sensitiveContent = "const api_key = \"sk-123456789012345678901234\";";
        Assert(sensitive.ApplyExtensionUpdate(new ExtensionUpdateRequest("share", file, root, sensitiveContent, 1))
            == ExtensionUpdateOutcome.NeedsConfirmation, "민감 내용은 확인 전 공유하지 않는다");
        Assert(sensitive.GetSnapshot().Files.Length == 0, "확인 전 민감 내용은 학생에게 보내지 않는다");
        Assert(sensitive.BuildReply().Warning is not null, "민감 내용 경고를 확장에 전달한다");
        Assert(sensitive.ApplyExtensionUpdate(new ExtensionUpdateRequest("share", file, root, sensitiveContent, 1, true))
            == ExtensionUpdateOutcome.Accepted, "교수가 확인하면 민감 내용을 공유한다");
        Assert(sensitive.GetSnapshot().Files.Length == 1, "확인한 민감 내용은 학생이 볼 수 있다");

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
