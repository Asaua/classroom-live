using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace ClassroomLive.Extension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Classroom Live", "현재 파일을 수업에 공유합니다.", "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(SolutionExistsContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ClassroomLivePackage : AsyncPackage
    {
        public const string PackageGuidString = "A58CD6A3-33DC-4901-90A2-192C7615B45D";
        public const string SolutionExistsContextGuid = "F1536EF8-92EC-443C-9ED7-FDADF150DA82";

        // vsct의 IDSymbol과 같아야 한다.
        private const int ToggleShareCommandId = 0x0100;
        private const int StartCommandId = 0x0101;
        private const int StopCommandId = 0x0102;
        private const int TogglePauseCommandId = 0x0103;
        private const int ToggleHideCommandId = 0x0104;

        // 호스트가 살아 있을 때만 빠르게 돈다. 연결이 없으면 느리게 돌려서
        // Classroom Live를 안 쓰는 날에도 UI 스레드를 계속 건드리지 않게 한다.
        private const int ActiveIntervalMs = 600;
        private const int IdleIntervalMs = 5000;
        private const int FailuresBeforeIdle = 3;

        private static readonly Guid CommandSet = new Guid("0FC38C23-09B7-4C95-89F5-BEB7321757E4");
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly HashSet<string> sharedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private dynamic dte;
        private Timer syncTimer;
        private int syncRunning;
        private int intervalMs = ActiveIntervalMs;
        private int failureStreak;

        // 마지막 폴링에서 받은 호스트 상태. 메뉴 글자와 활성 여부를 여기서 정한다.
        private bool hostReachable;
        private bool broadcasting;
        private bool currentShareable;
        private bool currentShared;
        private bool currentHidden;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dteService = await GetServiceAsync(typeof(SDteService));
            if (dteService == null) return;
            dte = dteService;

            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands != null)
            {
                Add(commands, StartCommandId, StartHost, QueryStart);
                Add(commands, StopCommandId, StopHost, QueryStop);
                Add(commands, TogglePauseCommandId, TogglePause, QueryPause);
                Add(commands, ToggleShareCommandId, ToggleShare, QueryShare);
                Add(commands, ToggleHideCommandId, ToggleHide, QueryHide);
            }

            syncTimer = new Timer(SyncActiveFile, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(ActiveIntervalMs));
        }

        private void Add(OleMenuCommandService commands, int id, EventHandler invoke, EventHandler query)
        {
            var command = new OleMenuCommand(invoke, new CommandID(CommandSet, id));
            command.BeforeQueryStatus += query;
            commands.AddCommand(command);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) syncTimer?.Dispose();
            base.Dispose(disposing);
        }

        // --- 메뉴 상태 ------------------------------------------------------

        private void QueryStart(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ((OleMenuCommand)sender).Enabled = !hostReachable;
        }

        private void QueryStop(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ((OleMenuCommand)sender).Enabled = hostReachable;
        }

        private void QueryPause(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            command.Enabled = hostReachable;
            command.Text = broadcasting ? "멈춤" : "시작";
        }

        private void QueryShare(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            command.Enabled = hostReachable && !string.IsNullOrWhiteSpace(ActiveFilePath());
            command.Text = currentShared ? "현재 파일 공유 해제" : "현재 파일 공유";
        }

        private void QueryHide(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            // 공유 목록에 없는 파일은 숨길 것도 없다.
            command.Enabled = hostReachable && currentShared;
            command.Text = currentHidden ? "현재 파일 다시 보이기" : "현재 파일 숨김";
        }

        // --- 명령 ------------------------------------------------------------

        private void StartHost(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var exe = HostHandshake.InstalledExecutable();
            if (string.IsNullOrEmpty(exe))
            {
                SetStatus("Classroom Live · ClassroomLive.exe를 한 번 직접 실행해 주세요");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                SetStatus("Classroom Live · 실행 중");
                SetInterval(ActiveIntervalMs);
            }
            catch (Exception exception)
            {
                SetStatus("Classroom Live · 실행하지 못했습니다: " + exception.Message);
            }
        }

        private void StopHost(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var ok = await PostControlAsync("shutdown", null);
                SetStatus(ok ? "Classroom Live · 종료했습니다" : "Classroom Live · 종료하지 못했습니다");
            });
        }

        private void TogglePause(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var next = !broadcasting;
                var ok = await PostControlAsync("broadcast", "{\"enabled\":" + (next ? "true" : "false") + "}");
                if (!ok)
                {
                    SetStatus("Classroom Live · 호스트에 연결하지 못했습니다");
                    return;
                }
                broadcasting = next;
                SetStatus(next ? "Classroom Live · 시작" : "Classroom Live · 멈춤");
            });
        }

        private void ToggleShare(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var update = CaptureActiveFile(includeContent: true);
                if (update == null)
                {
                    SetStatus("Classroom Live · 코드 파일을 선택해 주세요");
                    return;
                }

                // 확장자, 크기, 솔루션 밖 여부는 호스트의 보안 규칙이 정한다.
                if (!currentShared && !currentShareable)
                {
                    SetStatus("Classroom Live · 추가할 수 없는 타입의 파일입니다");
                    return;
                }

                // 방금 사용자가 조작했으므로 느린 주기에서 즉시 빠져나온다.
                SetInterval(ActiveIntervalMs);

                if (sharedFiles.Remove(update.FilePath))
                {
                    update.Action = "unshare";
                    update.Content = null;
                    await PostAsync(update);
                    SetStatus("Classroom Live · " + Path.GetFileName(update.FilePath) + " 공유 해제");
                }
                else
                {
                    sharedFiles.Add(update.FilePath);
                    update.Action = "share";
                    var result = await PostAsync(update);
                    SetStatus(result.Status == HttpStatusCode.OK
                        ? "Classroom Live · " + Path.GetFileName(update.FilePath) + " 공유"
                        : "Classroom Live · 호스트 실행 대기");
                }
            });
        }

        private void ToggleHide(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var update = CaptureActiveFile(includeContent: false);
                if (update == null) return;

                var hide = !currentHidden;
                update.Action = hide ? "hide" : "unhide";
                var result = await PostAsync(update);
                if (result.Status != HttpStatusCode.OK)
                {
                    SetStatus("Classroom Live · 호스트에 연결하지 못했습니다");
                    return;
                }

                currentHidden = hide;
                SetStatus("Classroom Live · " + Path.GetFileName(update.FilePath) +
                    (hide ? " 숨김" : " 다시 보임"));
            });
        }

        // --- 폴링 ------------------------------------------------------------

        private void SyncActiveFile(object state)
        {
            if (Interlocked.Exchange(ref syncRunning, 1) != 0) return;
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    var path = ActiveFilePath();
                    var isShared = path != null && sharedFiles.Contains(path);
                    var update = CaptureActiveFile(includeContent: isShared) ?? new ExtensionUpdate
                    {
                        Action = "heartbeat"
                    };
                    update.Action = isShared ? "sync" : "heartbeat";
                    var result = await PostAsync(update);

                    // 교수 화면에서 공유 해제한 파일은 호스트가 409로 알려준다.
                    // 여기서 목록을 맞춰야 다음 동기화에 되살아나지 않는다.
                    if (result.Status == HttpStatusCode.Conflict && path != null)
                        sharedFiles.Remove(path);

                    ApplyReply(result);
                    UpdateInterval(result.Status.HasValue);

                    // 교수 화면 버튼으로 내린 명령. Visual Studio로 돌아오지 않아도 동작한다.
                    if (result.Reply != null && result.Reply.Command != null && path != null)
                        await RunHostCommandAsync(result.Reply.Command, path);
                }
                finally
                {
                    Interlocked.Exchange(ref syncRunning, 0);
                }
            });
        }

        private void ApplyReply(PostResult result)
        {
            hostReachable = result.Status.HasValue;
            if (result.Reply == null) return;

            broadcasting = result.Reply.Broadcasting;
            currentShareable = result.Reply.Shareable;
            currentShared = result.Reply.Shared;
            currentHidden = result.Reply.Hidden;
        }

        /// <summary>교수 화면 버튼이 보낸 공유/해제 명령을 메뉴와 똑같이 처리한다.</summary>
        private async Task RunHostCommandAsync(string command, string path)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (command == "share" && !sharedFiles.Contains(path))
            {
                var update = CaptureActiveFile(includeContent: true);
                if (update == null) return;
                sharedFiles.Add(update.FilePath);
                update.Action = "share";
                await PostAsync(update);
                SetStatus("Classroom Live · " + Path.GetFileName(path) + " 공유");
            }
            else if (command == "unshare" && sharedFiles.Remove(path))
            {
                var update = CaptureActiveFile(includeContent: false);
                if (update == null) return;
                update.Action = "unshare";
                update.Content = null;
                await PostAsync(update);
                SetStatus("Classroom Live · " + Path.GetFileName(path) + " 공유 해제");
            }
        }

        private void UpdateInterval(bool reachedHost)
        {
            if (reachedHost)
            {
                failureStreak = 0;
                if (intervalMs != ActiveIntervalMs) SetInterval(ActiveIntervalMs);
            }
            else if (++failureStreak >= FailuresBeforeIdle && intervalMs != IdleIntervalMs)
            {
                SetInterval(IdleIntervalMs);
            }
        }

        private void SetInterval(int milliseconds)
        {
            intervalMs = milliseconds;
            try { syncTimer?.Change(milliseconds, milliseconds); }
            catch (ObjectDisposedException) { }
        }

        // --- Visual Studio 접근 ------------------------------------------------

        private ExtensionUpdate CaptureActiveFile(bool includeContent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var document = dte?.ActiveDocument;
                var solutionFile = dte?.Solution?.FullName;
                if (document == null || string.IsNullOrWhiteSpace(solutionFile)) return null;

                var update = new ExtensionUpdate
                {
                    FilePath = document.FullName,
                    SolutionRoot = Path.GetDirectoryName(solutionFile)
                };
                if (includeContent)
                {
                    dynamic textDocument = document.Object("TextDocument");
                    if (textDocument == null) return null;
                    var editPoint = textDocument.StartPoint.CreateEditPoint();
                    update.Content = editPoint.GetText(textDocument.EndPoint);
                    // 학생이 "따라가기"로 같은 줄을 볼 수 있게 커서 위치를 함께 보낸다.
                    try { update.ActiveLine = (int)textDocument.Selection.ActivePoint.Line; }
                    catch { update.ActiveLine = 0; }
                }
                return update;
            }
            catch
            {
                return null;
            }
        }

        private string ActiveFilePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return dte?.ActiveDocument?.FullName; }
            catch { return null; }
        }

        private void SetStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { dte.StatusBar.Text = message; }
            catch { }
        }

        // --- 호스트 통신 --------------------------------------------------------

        /// <summary>호스트에 상태를 전송하고 응답을 읽는다. 닿지 못하면 Status가 null이다.</summary>
        private static async Task<PostResult> PostAsync(ExtensionUpdate update)
        {
            var handshake = HostHandshake.Load();
            if (handshake == null) return new PostResult();

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(ExtensionUpdate));
                string json;
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, update);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }

                var url = "http://127.0.0.1:" + handshake.Port + "/api/extension/update";
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                {
                    request.Headers.Add("X-Extension-Token", handshake.Token);
                    using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                        return new PostResult
                        {
                            Status = response.StatusCode,
                            Reply = await ReadReplyAsync(response).ConfigureAwait(false)
                        };
                }
            }
            catch
            {
                HostHandshake.Invalidate();
                return new PostResult();
            }
        }

        /// <summary>실행/종료/멈춤처럼 파일과 무관한 조작.</summary>
        private static async Task<bool> PostControlAsync(string path, string body)
        {
            var handshake = HostHandshake.Load();
            if (handshake == null) return false;

            try
            {
                var url = "http://127.0.0.1:" + handshake.Port + "/api/extension/" + path;
                var content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                using (content)
                using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                {
                    request.Headers.Add("X-Extension-Token", handshake.Token);
                    using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                        return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                HostHandshake.Invalidate();
                return false;
            }
        }

        private static async Task<HostReply> ReadReplyAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) return null;
            try
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(body)) return null;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(HostReply));
                    return serializer.ReadObject(stream) as HostReply;
                }
            }
            catch
            {
                return null;
            }
        }

        private sealed class PostResult
        {
            public HttpStatusCode? Status { get; set; }
            public HostReply Reply { get; set; }
        }

        [DataContract]
        private sealed class HostReply
        {
            [DataMember(Name = "command")] public string Command { get; set; }
            [DataMember(Name = "broadcasting")] public bool Broadcasting { get; set; }
            [DataMember(Name = "shareable")] public bool Shareable { get; set; }
            [DataMember(Name = "shared")] public bool Shared { get; set; }
            [DataMember(Name = "hidden")] public bool Hidden { get; set; }
        }

        /// <summary>
        /// 호스트가 남기는 연결 정보. 포트를 하드코딩하지 않게 해주고,
        /// 같은 PC의 다른 프로그램이 교실에 코드를 밀어넣지 못하게 토큰을 함께 싣는다.
        /// </summary>
        [DataContract]
        private sealed class HostHandshake
        {
            private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);
            private static HostHandshake cached;
            private static DateTime readAt;

            [DataMember(Name = "port")] public int Port { get; set; }
            [DataMember(Name = "token")] public string Token { get; set; }

            private static string Folder
            {
                get
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClassroomLive");
                }
            }

            public static HostHandshake Load()
            {
                if (cached != null && DateTime.UtcNow - readAt < CacheFor) return cached;
                readAt = DateTime.UtcNow;
                try
                {
                    var path = Path.Combine(Folder, "host.json");
                    if (!File.Exists(path)) return cached = null;
                    using (var stream = File.OpenRead(path))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(HostHandshake));
                        var handshake = serializer.ReadObject(stream) as HostHandshake;
                        return cached = (handshake != null && handshake.Port > 0 &&
                                         !string.IsNullOrEmpty(handshake.Token))
                            ? handshake
                            : null;
                    }
                }
                catch
                {
                    return cached = null;
                }
            }

            /// <summary>호스트가 재시작하면 포트·토큰이 바뀌므로 다음 호출에서 다시 읽는다.</summary>
            public static void Invalidate()
            {
                cached = null;
            }

            /// <summary>"실행"이 켤 실행 파일. 호스트가 한 번이라도 돌았으면 남아 있다.</summary>
            public static string InstalledExecutable()
            {
                try
                {
                    var path = Path.Combine(Folder, "install.json");
                    if (!File.Exists(path)) return null;
                    using (var stream = File.OpenRead(path))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(InstallInfo));
                        var info = serializer.ReadObject(stream) as InstallInfo;
                        if (info == null || string.IsNullOrEmpty(info.Executable)) return null;
                        return File.Exists(info.Executable) ? info.Executable : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        [DataContract]
        private sealed class InstallInfo
        {
            [DataMember(Name = "exe")] public string Executable { get; set; }
        }

        [DataContract]
        private sealed class ExtensionUpdate
        {
            [DataMember] public string Action { get; set; }
            [DataMember] public string FilePath { get; set; }
            [DataMember] public string SolutionRoot { get; set; }
            [DataMember] public string Content { get; set; }
            [DataMember] public int ActiveLine { get; set; }
        }

        [ComImport]
        [Guid("04A72314-32E9-48E2-9B87-A63603454F3E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface SDteService
        {
        }
    }
}
