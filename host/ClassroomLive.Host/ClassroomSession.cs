using System.Security.Cryptography;
using System.Text;

sealed class ClassroomSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFile> _files = [];
    private readonly Dictionary<string, DateTimeOffset> _viewers = [];
    private readonly HashSet<string> _suppressedFiles = [];
    private string? _professorActiveId;
    private string? _professorActiveName;
    private bool _broadcasting;
    private DateTimeOffset _lastExtensionHeartbeat = DateTimeOffset.MinValue;
    private string _visualStudioStatus = "Visual Studio 확장 연결 대기 중";

    public string Pin { get; } = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
    public string AdminToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public bool IsValidPin(string? pin) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(pin ?? string.Empty), Encoding.UTF8.GetBytes(Pin));

    public bool IsAdmin(string? token) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(token ?? string.Empty), Encoding.UTF8.GetBytes(AdminToken));

    public bool IsBroadcasting
    {
        get { lock (_gate) return _broadcasting; }
    }

    public void SetBroadcasting(bool enabled)
    {
        lock (_gate)
        {
            _broadcasting = enabled;
            if (!enabled) _professorActiveId = null;
        }
    }

    public bool ApplyExtensionUpdate(ExtensionUpdateRequest request)
    {
        lock (_gate)
        {
            _lastExtensionHeartbeat = DateTimeOffset.UtcNow;

            var hasSafeActiveFile = !string.IsNullOrWhiteSpace(request.FilePath) &&
                                    !string.IsNullOrWhiteSpace(request.SolutionRoot) &&
                                    SecurityRules.IsShareable(request.FilePath, request.SolutionRoot, request.Content?.Length ?? 0);
            _professorActiveName = hasSafeActiveFile ? Path.GetFileName(request.FilePath) : null;
            _professorActiveId = hasSafeActiveFile ? FileId(request.FilePath!) : null;
            if (_professorActiveId is not null && !_files.ContainsKey(_professorActiveId))
                _professorActiveId = null;

            switch ((request.Action ?? string.Empty).ToLowerInvariant())
            {
                case "share":
                    if (!hasSafeActiveFile) return false;
                    _suppressedFiles.Remove(FileId(request.FilePath!));
                    goto case "sync";
                case "sync":
                    if (hasSafeActiveFile && request.Content is not null &&
                        !_suppressedFiles.Contains(FileId(request.FilePath!)))
                    {
                        UpdateSharedFile(request.FilePath!, request.Content, request.SolutionRoot!);
                        var id = FileId(request.FilePath!);
                        _professorActiveId = _files.ContainsKey(id) ? id : null;
                        _visualStudioStatus = $"연결됨 · {_professorActiveName} 추적 중";
                        return true;
                    }
                    else if (hasSafeActiveFile && _suppressedFiles.Contains(FileId(request.FilePath!)))
                    {
                        _professorActiveId = null;
                        _visualStudioStatus = "연결됨 · 현재 파일은 호스트 목록에서 숨겨짐";
                        return false;
                    }
                    else
                    {
                        _visualStudioStatus = "현재 파일은 보안 규칙으로 공유할 수 없습니다.";
                        return false;
                    }
                case "unshare":
                    if (!string.IsNullOrWhiteSpace(request.FilePath))
                    {
                        var id = FileId(request.FilePath);
                        _files.Remove(id);
                        _suppressedFiles.Remove(id);
                    }
                    _professorActiveId = null;
                    _visualStudioStatus = "연결됨 · 현재 파일 공유 해제됨";
                    return true;
                default:
                    _visualStudioStatus = _professorActiveName is null
                        ? "연결됨 · 코드 파일을 선택해주세요."
                        : $"연결됨 · {_professorActiveName} (공유 안 함)";
                    return true;
            }
        }
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
            _suppressedFiles.Add(id);
            _files.Remove(id);
            if (_professorActiveId == id) _professorActiveId = null;
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
        _broadcasting ? _professorActiveId : null,
        _broadcasting ? _professorActiveName : null,
        _viewers.Count,
        _broadcasting,
        _files.Values.OrderByDescending(file => file.UpdatedAt).ToArray());

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
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"자체 검사 실패: {name}");
    }
}
