using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
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

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dteService = await GetServiceAsync(typeof(SDteService));
            if (dteService == null) return;
            dte = dteService;
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands != null)
            {
                var command = new OleMenuCommand(ToggleCurrentFile,
                    new CommandID(CommandSet, 0x0100));
                command.BeforeQueryStatus += UpdateCommandStatus;
                commands.AddCommand(command);
            }

            syncTimer = new Timer(SyncActiveFile, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(ActiveIntervalMs));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) syncTimer?.Dispose();
            base.Dispose(disposing);
        }

        private void UpdateCommandStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            var path = ActiveFilePath();
            command.Enabled = !string.IsNullOrWhiteSpace(path);
            command.Text = path != null && sharedFiles.Contains(path)
                ? "Classroom Live: 현재 파일 공유 해제"
                : "Classroom Live: 현재 파일 공유";
        }

        private void ToggleCurrentFile(object sender, EventArgs e)
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
                    // 교수 화면에서 ×로 내린 파일은 호스트가 409로 알려준다.
                    // 여기서 공유 목록을 맞춰야 Ctrl+Alt+L 한 번으로 다시 공유된다.
                    if (result.Status == HttpStatusCode.Conflict && path != null)
                        sharedFiles.Remove(path);
                    UpdateInterval(result.Status.HasValue);

                    // 교수 화면 버튼으로 내린 명령. Visual Studio로 돌아오지 않아도 동작한다.
                    if (result.Command != null && path != null)
                        await RunHostCommandAsync(result.Command, path);
                }
                finally
                {
                    Interlocked.Exchange(ref syncRunning, 0);
                }
            });
        }

        /// <summary>교수 화면 버튼이 보낸 공유/해제 명령을 단축키와 똑같이 처리한다.</summary>
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
                    // 학생이 "교수님 따라가기"로 같은 줄을 볼 수 있게 커서 위치를 함께 보낸다.
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

        /// <summary>호스트에 전송한다. 호스트에 닿지 못하면 Status가 null이다.</summary>
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
                            Command = await ReadCommandAsync(response).ConfigureAwait(false)
                        };
                }
            }
            catch
            {
                HostHandshake.Invalidate();
                return new PostResult();
            }
        }

        /// <summary>응답 본문에 실려 오는 교수 화면 명령을 읽는다. 없으면 null.</summary>
        private static async Task<string> ReadCommandAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) return null;
            try
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(body)) return null;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(HostReply));
                    var reply = serializer.ReadObject(stream) as HostReply;
                    return string.IsNullOrEmpty(reply?.Command) ? null : reply.Command;
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
            public string Command { get; set; }
        }

        [DataContract]
        private sealed class HostReply
        {
            [DataMember(Name = "command")] public string Command { get; set; }
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

            private static string FilePath
            {
                get
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClassroomLive", "host.json");
                }
            }

            public static HostHandshake Load()
            {
                if (cached != null && DateTime.UtcNow - readAt < CacheFor) return cached;
                readAt = DateTime.UtcNow;
                try
                {
                    var path = FilePath;
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
