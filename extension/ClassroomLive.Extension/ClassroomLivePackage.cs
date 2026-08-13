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
                    SetStatus("Classroom Live: 공유할 코드 파일을 선택해주세요.");
                    return;
                }

                // 방금 사용자가 조작했으므로 느린 주기에서 즉시 빠져나온다.
                SetInterval(ActiveIntervalMs);

                if (sharedFiles.Remove(update.FilePath))
                {
                    update.Action = "unshare";
                    update.Content = null;
                    await PostAsync(update);
                    SetStatus("Classroom Live: " + Path.GetFileName(update.FilePath) + " 공유 해제");
                }
                else
                {
                    sharedFiles.Add(update.FilePath);
                    update.Action = "share";
                    var status = await PostAsync(update);
                    SetStatus(status == HttpStatusCode.OK
                        ? "Classroom Live: " + Path.GetFileName(update.FilePath) + " 공유 등록"
                        : "Classroom Live: 파일은 등록됨 · ClassroomLive.exe 실행 대기 중");
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
                    var status = await PostAsync(update);
                    // 교수 화면에서 ×로 내린 파일은 호스트가 409로 알려준다.
                    // 여기서 공유 목록을 맞춰야 Ctrl+Alt+L 한 번으로 다시 공유된다.
                    if (status == HttpStatusCode.Conflict && path != null)
                        sharedFiles.Remove(path);
                    UpdateInterval(status.HasValue);
                }
                finally
                {
                    Interlocked.Exchange(ref syncRunning, 0);
                }
            });
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

        /// <summary>호스트에 전송한다. 호스트에 닿지 못하면 null을 돌려준다.</summary>
        private static async Task<HttpStatusCode?> PostAsync(ExtensionUpdate update)
        {
            var handshake = HostHandshake.Load();
            if (handshake == null) return null;

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
                        return response.StatusCode;
                }
            }
            catch
            {
                HostHandshake.Invalidate();
                return null;
            }
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
