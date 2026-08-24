using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using EnvDTE;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;

namespace ClassroomLive.Extension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Classroom Live", "현재 파일을 수업에 공유합니다.", "1.2.6")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // 솔루션이 없어도 로드돼야 한다. 명령이 DefaultDisabled라 패키지가 안 뜨면
    // 메뉴와 툴바가 통째로 회색이 되고, 정작 "실행"조차 누를 수 없다.
    [ProvideAutoLoad(SolutionExistsContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(NoSolutionContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ClassroomLivePackage : AsyncPackage
    {
        public const string PackageGuidString = "A58CD6A3-33DC-4901-90A2-192C7615B45D";
        public const string SolutionExistsContextGuid = "F1536EF8-92EC-443C-9ED7-FDADF150DA82";
        public const string NoSolutionContextGuid = "ADFC4E64-0397-11D1-9F4E-00A0C911004F";

        // vsct의 IDSymbol과 같아야 한다.
        private const int ToggleShareCommandId = 0x0100;
        private const int StartCommandId = 0x0101;
        private const int TogglePauseCommandId = 0x0103;
        private const int ToggleHideCommandId = 0x0104;
        private const string SettingsCollection = "ClassroomLive";
        private const string ToolbarVisibleSetting = "ToolbarShown";

        // 호스트가 살아 있을 때만 빠르게 돈다. 연결이 없으면 느리게 돌려서
        // Classroom Live를 안 쓰는 날에도 UI 스레드를 계속 건드리지 않게 한다.
        private const int ActiveIntervalMs = 600;
        private const int IdleIntervalMs = 5000;
        private const int FailuresBeforeIdle = 3;
        private const uint MessageBoxYesNo = 0x00000004;
        private const uint MessageBoxOk = 0x00000000;
        private const uint MessageBoxIconError = 0x00000010;
        private const uint MessageBoxIconWarning = 0x00000030;
        private const uint MessageBoxDefaultNo = 0x00000100;
        private const int MessageBoxYes = 6;
        // .NET Framework 4.7.2의 HttpStatusCode에는 422 이름이 없다.
        private const HttpStatusCode UnprocessableEntity = (HttpStatusCode)422;

        private static readonly Guid CommandSet = new Guid("0FC38C23-09B7-4C95-89F5-BEB7321757E4");
        // Visual Studio 창마다 다른 값. 여러 개를 열었을 때 호스트가 누가 보낸 것인지
        // 구분하지 못하면 창들이 서로 활성 파일을 덮어써서 화면이 깜빡인다.
        private static readonly string InstanceId = Guid.NewGuid().ToString("N");
        private static readonly uint CurrentProcessId = (uint)Process.GetCurrentProcess().Id;
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly HashSet<string> sharedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> hiddenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private dynamic dte;
        private string lastActiveFilePath;
        private OleMenuCommand hostCommand;
        private OleMenuCommand pauseCommand;
        private OleMenuCommand shareCommand;
        private OleMenuCommand hideCommand;
        private Timer syncTimer;
        private int syncRunning;
        private int intervalMs = ActiveIntervalMs;
        private int failureStreak;
        private int refreshingSharedFiles;
        private string lastRestoreId;
        private string lastSessionId;
        private readonly HashSet<string> restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool? toolbarVisible;

        // 마지막 폴링에서 받은 호스트 상태. 메뉴 글자와 활성 여부를 여기서 정한다.
        private bool hostReachable;
        private bool broadcasting;
        private bool everStarted;
        private bool isOwner = true;
        private bool hostStarting;
        private bool hostStopping;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dteService = await GetServiceAsync(typeof(SDteService));
            if (dteService == null) return;
            dte = dteService;

            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands != null)
            {
                hostCommand = Add(commands, StartCommandId, ToggleHost, QueryHost);
                pauseCommand = Add(commands, TogglePauseCommandId, TogglePause, QueryPause);
                shareCommand = Add(commands, ToggleShareCommandId, ToggleShare, QueryShare);
                hideCommand = Add(commands, ToggleHideCommandId, ToggleHide, QueryHide);
            }

            SyncToolbarPreference();

            syncTimer = new Timer(SyncActiveFile, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(ActiveIntervalMs));
        }

        private OleMenuCommand Add(OleMenuCommandService commands, int id, EventHandler invoke, EventHandler query)
        {
            var command = new OleMenuCommand(invoke, new CommandID(CommandSet, id));
            command.BeforeQueryStatus += query;
            commands.AddCommand(command);
            return command;
        }

        private void SyncToolbarPreference()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var settings = new ShellSettingsManager(this).GetWritableSettingsStore(SettingsScope.UserSettings);
                if (!settings.CollectionExists(SettingsCollection)) settings.CreateCollection(SettingsCollection);
                dynamic toolbar = dte.CommandBars["Classroom Live"];

                if (!toolbarVisible.HasValue)
                {
                    // 최초 설치는 표시한다. 이후 창은 사용자가 마지막으로 선택한 표시 상태를 따른다.
                    var preferred = settings.GetBoolean(SettingsCollection, ToolbarVisibleSetting, true);
                    toolbar.Visible = preferred;
                    toolbarVisible = preferred;
                    settings.SetBoolean(SettingsCollection, ToolbarVisibleSetting, preferred);
                    return;
                }

                var visible = (bool)toolbar.Visible;
                if (visible == toolbarVisible.Value) return;
                toolbarVisible = visible;
                settings.SetBoolean(SettingsCollection, ToolbarVisibleSetting, visible);
            }
            catch
            {
                // 셸이 아직 도구 모음을 만들지 않았다면 다음 폴링에서 다시 시도한다.
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                syncTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        // --- 메뉴 상태 ------------------------------------------------------

        private void QueryHost(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            command.Enabled = !hostStarting && !hostStopping;
            command.Text = hostStarting ? "실행 중..." : hostStopping ? "종료 중..." : hostReachable ? "종료" : "실행";
        }

        private void QueryPause(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            command.Enabled = hostReachable && !hostStopping;
            command.Text = !hostReachable || hostStopping
                ? "시작"
                : broadcasting ? "일시정지" : everStarted ? "재개" : "시작";
        }

        private void QueryShare(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            var path = ActiveFilePath();
            command.Enabled = hostReachable && !hostStopping && !string.IsNullOrWhiteSpace(path);
            command.Text = hostReachable && !hostStopping &&
                           path != null && sharedFiles.Contains(path) ? "공유 해제" : "공유";
        }

        private void QueryHide(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            var path = ActiveFilePath();
            // 공유 목록에 없는 파일은 숨길 것도 없다.
            command.Enabled = hostReachable && !hostStopping && path != null && sharedFiles.Contains(path);
            command.Text = hostReachable && !hostStopping &&
                           path != null && hiddenFiles.Contains(path) ? "다시 보이기" : "숨김";
        }

        private void RefreshCommands()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (hostCommand != null) QueryHost(hostCommand, EventArgs.Empty);
            if (pauseCommand != null) QueryPause(pauseCommand, EventArgs.Empty);
            if (shareCommand != null) QueryShare(shareCommand, EventArgs.Empty);
            if (hideCommand != null) QueryHide(hideCommand, EventArgs.Empty);
        }

        // --- 명령 ------------------------------------------------------------

        private void ToggleHost(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (hostStarting || hostStopping) return;
            if (hostReachable)
            {
                hostStopping = true;
                SetStatus("Classroom Live · 종료 중...");
                RefreshCommands();
                _ = JoinableTaskFactory.RunAsync(async delegate
                {
                    var ok = await PostControlAsync("shutdown", null);
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!ok)
                    {
                        hostStopping = false;
                        SetStatus("Classroom Live · 종료하지 못했습니다");
                        RefreshCommands();
                        return;
                    }

                    await WaitForHostStopAsync();
                });
                return;
            }

            var exe = BundledHostExecutable();
            if (exe == null)
            {
                ShowLaunchError(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location) ?? "", "Host", "ClassroomLive.exe"),
                    "VSIX에 포함된 호스트를 찾지 못했습니다. Classroom Live 확장을 다시 설치해 주세요.");
                return;
            }

            try
            {
                using (var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }))
                {
                    if (process == null) throw new InvalidOperationException("프로세스를 시작하지 못했습니다.");
                }
                hostStarting = true;
                SetStatus("Classroom Live · 실행 중");
                SetInterval(ActiveIntervalMs);
                RefreshCommands();
            }
            catch (Exception exception)
            {
                ShowLaunchError(exe, exception.Message);
                return;
            }

            _ = JoinableTaskFactory.RunAsync(() => WaitForHostAsync(exe));
        }

        private async Task WaitForHostAsync(string exe)
        {
            // Process.Start 성공은 서버 준비 성공을 뜻하지 않는다. 런타임 누락처럼
            // 프로세스가 곧바로 끝나는 경우까지 잡으려고 실제 응답을 기다린다.
            for (var attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                var result = await PostAsync(new ExtensionUpdate { Action = "heartbeat" }).ConfigureAwait(false);
                if (!result.ReachedHost) continue;

                await JoinableTaskFactory.SwitchToMainThreadAsync();
                hostStarting = false;
                ApplyReply(result);
                RefreshCommands();
                SetStatus("Classroom Live · 실행했습니다");
                return;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            hostStarting = false;
            RefreshCommands();
            ShowLaunchError(exe, "30초 안에 서버 응답을 받지 못했습니다.");
        }

        private async Task WaitForHostStopAsync()
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                var result = await PostAsync(new ExtensionUpdate { Action = "heartbeat" }).ConfigureAwait(false);
                if (result.ReachedHost) continue;

                await JoinableTaskFactory.SwitchToMainThreadAsync();
                hostStopping = false;
                hostReachable = false;
                SetStatus("Classroom Live · 종료했습니다");
                RefreshCommands();
                return;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            hostStopping = false;
            SetStatus("Classroom Live · 종료가 지연되고 있습니다");
            RefreshCommands();
        }

        private static string BundledHostExecutable()
        {
            try
            {
                var extensionFolder = Path.GetDirectoryName(typeof(ClassroomLivePackage).Assembly.Location);
                var path = Path.Combine(extensionFolder ?? "", "Host", "ClassroomLive.exe");
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        private void ShowLaunchError(string path, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var message = "Classroom Live를 실행하지 못했습니다.\n\n" +
                          "경로: " + path + "\n\n" + reason;
            SetStatus("Classroom Live · 실행하지 못했습니다");
            MessageBoxW(IntPtr.Zero, message, "Classroom Live · 실행 오류",
                MessageBoxOk | MessageBoxIconError);
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
                var resuming = next && everStarted;
                broadcasting = next;
                if (next) everStarted = true;
                if (resuming) await RefreshSharedFilesAsync();
                SetStatus(next
                    ? (resuming ? "Classroom Live · 재개" : "Classroom Live · 시작")
                    : "Classroom Live · 일시정지");
                RefreshCommands();
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

                // 방금 사용자가 조작했으므로 느린 주기에서 즉시 빠져나온다.
                SetInterval(ActiveIntervalMs);

                PostResult result;
                if (sharedFiles.Contains(update.FilePath))
                {
                    update.Action = "unshare";
                    update.Content = null;
                    result = await PostAsync(update);
                    ApplyReply(result, update.FilePath);
                    SetStatus(result.Status == HttpStatusCode.OK
                        ? "Classroom Live · " + Path.GetFileName(update.FilePath) + " 공유 해제"
                        : "Classroom Live · " + ReplyError(result));
                }
                else
                {
                    update.Action = "share";
                    result = await PostWithSensitiveConfirmationAsync(update);
                    ApplyReply(result, update.FilePath);
                    if (result.Status == HttpStatusCode.OK)
                    {
                        SetStatus("Classroom Live · " + Path.GetFileName(update.FilePath) +
                            (everStarted ? " 공유" : " 공유 예정"));
                    }
                    else
                    {
                        SetStatus("Classroom Live · " + ReplyError(result));
                    }
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

                var hide = !hiddenFiles.Contains(update.FilePath);
                update.Action = hide ? "hide" : "unhide";
                var result = await PostAsync(update);
                ApplyReply(result, update.FilePath);
                if (result.Status != HttpStatusCode.OK)
                {
                    SetStatus("Classroom Live · 호스트에 연결하지 못했습니다");
                    return;
                }

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
                    SyncToolbarPreference();
                    var update = CaptureActiveFile(includeContent: false) ?? new ExtensionUpdate
                    {
                        Action = "heartbeat",
                        Focused = IsVisualStudioForeground()
                    };
                    var path = update.FilePath;
                    if (!string.Equals(lastActiveFilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        lastActiveFilePath = path;
                        RefreshCommands();
                    }
                    // 주인이 아닌 창은 호스트가 어차피 무시한다. 문서 전체를 읽어
                    // UI 스레드를 붙잡을 이유가 없다.
                    var isShared = !hostStopping && isOwner && path != null && sharedFiles.Contains(path);
                    if (isShared) update = CaptureActiveFile(includeContent: true) ?? update;
                    update.Action = isShared ? "sync" : "heartbeat";
                    var result = await PostAsync(update);

                    // 교수 화면에서 공유 해제한 파일은 호스트가 409로 알려준다.
                    // 여기서 목록을 맞춰야 다음 동기화에 되살아나지 않는다.
                    if ((result.Status == HttpStatusCode.Conflict ||
                         result.Status == UnprocessableEntity) && path != null)
                    {
                        sharedFiles.Remove(path);
                        if (result.Status == UnprocessableEntity)
                            SetStatus("Classroom Live · " + ReplyError(result));
                    }

                    ApplyReply(result, path);
                    UpdateInterval(result.ReachedHost);

                    // 교수 화면 버튼으로 내린 명령. Visual Studio로 돌아오지 않아도 동작한다.
                    if (result.Reply != null && result.Reply.Owner &&
                        result.Reply.Command != null && path != null)
                        await RunHostCommandAsync(result.Reply.Command, path);
                }
                finally
                {
                    Interlocked.Exchange(ref syncRunning, 0);
                }
            });
        }

        private void ApplyReply(PostResult result, string path = null)
        {
            var oldReachable = hostReachable;
            var oldBroadcasting = broadcasting;
            var oldEverStarted = everStarted;
            var oldStopping = hostStopping;
            hostReachable = result.ReachedHost;
            if (result.Reply == null)
            {
                if (!hostReachable) hostStopping = false;
                if (oldReachable != hostReachable || oldStopping != hostStopping) RefreshCommands();
                return;
            }

            isOwner = result.Reply.Owner;
            broadcasting = result.Reply.Broadcasting;
            everStarted = result.Reply.EverStarted;
            if (result.Reply.Ended) hostStopping = true;
            var resumedFromHost = !oldBroadcasting && oldEverStarted && broadcasting && result.Reply.Owner;
            var fileStateChanged = false;
            if (!string.IsNullOrEmpty(result.Reply.SessionId) &&
                !string.Equals(lastSessionId, result.Reply.SessionId, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(lastSessionId))
                {
                    sharedFiles.Clear();
                    hiddenFiles.Clear();
                    restoredPaths.Clear();
                    lastRestoreId = null;
                    fileStateChanged = true;
                }
                lastSessionId = result.Reply.SessionId;
            }
            if (path != null)
            {
                fileStateChanged = SetMembership(sharedFiles, path, result.Reply.Shared) |
                                   SetMembership(hiddenFiles, path, result.Reply.Hidden);
            }
            if (!string.IsNullOrEmpty(result.Reply.RestoreId) &&
                result.Reply.RestoreFiles != null && result.Reply.RestoreFiles.Length > 0)
            {
                if (!string.Equals(lastRestoreId, result.Reply.RestoreId, StringComparison.Ordinal))
                {
                    lastRestoreId = result.Reply.RestoreId;
                    restoredPaths.Clear();
                }
                var newFiles = result.Reply.RestoreFiles.Where(file => restoredPaths.Add(file.Path)).ToArray();
                foreach (var file in newFiles)
                {
                    sharedFiles.Add(file.Path);
                    SetMembership(hiddenFiles, file.Path, file.Hidden);
                }
                if (newFiles.Length > 0)
                {
                    fileStateChanged = true;
                    _ = JoinableTaskFactory.RunAsync(() => RestoreSharedFilesAsync(newFiles));
                }
            }
            if (fileStateChanged || oldReachable != hostReachable ||
                oldBroadcasting != broadcasting || oldEverStarted != everStarted || oldStopping != hostStopping)
                RefreshCommands();
            if (resumedFromHost) _ = JoinableTaskFactory.RunAsync(RefreshSharedFilesAsync);
        }

        private static bool SetMembership(HashSet<string> files, string path, bool contains) =>
            contains ? files.Add(path) : files.Remove(path);

        private async Task RefreshSharedFilesAsync()
        {
            if (Interlocked.Exchange(ref refreshingSharedFiles, 1) != 0) return;
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var updates = sharedFiles.Select(CaptureSharedFile).Where(update => update != null).ToArray();
                foreach (var update in updates)
                {
                    var result = await PostAsync(update);
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (result.Status == HttpStatusCode.Conflict)
                        sharedFiles.Remove(update.FilePath);
                }
            }
            finally
            {
                Interlocked.Exchange(ref refreshingSharedFiles, 0);
            }
        }

        private async Task RestoreSharedFilesAsync(RestoreFile[] files)
        {
            if (Interlocked.Exchange(ref refreshingSharedFiles, 1) != 0) return;
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var updates = files.Select(file => CaptureSharedFile(file.Path))
                    .Where(update => update != null).ToArray();
                foreach (var update in updates)
                {
                    // 직전 목록이라도 민감 내용 검사는 새 세션에서 다시 확인한다.
                    update.Action = "share";
                    var result = await PostWithSensitiveConfirmationAsync(update);
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (result.Status == HttpStatusCode.Conflict || result.Status == UnprocessableEntity)
                        sharedFiles.Remove(update.FilePath);
                }
            }
            finally
            {
                Interlocked.Exchange(ref refreshingSharedFiles, 0);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshCommands();
            }
        }

        /// <summary>교수 화면 버튼이 보낸 공유/해제 명령을 메뉴와 똑같이 처리한다.</summary>
        private async Task RunHostCommandAsync(string command, string path)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (command == "share" && !sharedFiles.Contains(path))
            {
                var update = CaptureActiveFile(includeContent: true);
                if (update == null) return;
                update.Action = "share";
                var result = await PostWithSensitiveConfirmationAsync(update);
                ApplyReply(result, path);
                if (result.Status == HttpStatusCode.OK)
                {
                    SetStatus("Classroom Live · " + Path.GetFileName(path) +
                        (everStarted ? " 공유" : " 공유 예정"));
                }
                else
                {
                    SetStatus("Classroom Live · " + ReplyError(result));
                }
            }
            else if (command == "unshare" && sharedFiles.Contains(path))
            {
                var update = CaptureActiveFile(includeContent: false);
                if (update == null) return;
                update.Action = "unshare";
                update.Content = null;
                var result = await PostAsync(update);
                ApplyReply(result, path);
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
                    SolutionRoot = Path.GetDirectoryName(solutionFile),
                    Focused = IsVisualStudioForeground()
                };
                CaptureProject(document, update);
                if (includeContent)
                {
                    dynamic textDocument = document.Object("TextDocument");
                    if (textDocument == null) return null;
                    var editPoint = textDocument.StartPoint.CreateEditPoint();
                    update.Content = editPoint.GetText(textDocument.EndPoint);
                    // ActivePoint는 드래그를 끝낸 쪽, AnchorPoint는 시작한 쪽이다.
                    // 둘 다 보내면 위에서 아래로든 아래에서 위로든 같은 범위를 표시할 수 있다.
                    try
                    {
                        update.ActiveLine = (int)textDocument.Selection.ActivePoint.Line;
                        update.AnchorLine = (int)textDocument.Selection.AnchorPoint.Line;
                    }
                    catch
                    {
                        update.ActiveLine = 0;
                        update.AnchorLine = 0;
                    }
                }
                return update;
            }
            catch
            {
                return null;
            }
        }

        private ExtensionUpdate CaptureSharedFile(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var solutionFile = dte?.Solution?.FullName;
                if (string.IsNullOrWhiteSpace(solutionFile) || string.IsNullOrWhiteSpace(filePath)) return null;

                string content = null;
                var update = new ExtensionUpdate
                {
                    Action = "refresh",
                    FilePath = filePath,
                    SolutionRoot = Path.GetDirectoryName(solutionFile),
                    Focused = IsVisualStudioForeground()
                };
                foreach (Document document in dte.Documents)
                {
                    if (!string.Equals(document.FullName, filePath, StringComparison.OrdinalIgnoreCase)) continue;
                    dynamic textDocument = document.Object("TextDocument");
                    if (textDocument == null) return null;
                    var editPoint = textDocument.StartPoint.CreateEditPoint();
                    content = editPoint.GetText(textDocument.EndPoint);
                    CaptureProject(document, update);
                    break;
                }

                // 닫힌 문서는 VS에서 수정할 수 없으므로 다시 읽지 않는다. 디스크를 임의의
                // UTF-8로 읽으면 CP949 같은 기존 소스 파일을 깨뜨릴 수 있다.
                if (content == null) return null;
                update.Content = content;
                return update;
            }
            catch
            {
                return null;
            }
        }

        private static void CaptureProject(Document document, ExtensionUpdate update)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var project = document?.ProjectItem?.ContainingProject;
                if (project == null) return;
                update.ProjectName = project.Name;
                update.ProjectKey = string.IsNullOrWhiteSpace(project.UniqueName)
                    ? project.Name
                    : project.UniqueName;
            }
            catch
            {
                // Miscellaneous Files처럼 프로젝트에 속하지 않은 문서는 기타 파일로 묶는다.
            }
        }

        private string ActiveFilePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return dte?.ActiveDocument?.FullName; }
            catch { return null; }
        }

        private static bool IsVisualStudioForeground()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            uint processId;
            GetWindowThreadProcessId(foreground, out processId);
            return processId == CurrentProcessId;
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
                update.InstanceId = InstanceId;
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
                    {
                        if (!IsClassroomLiveResponse(response))
                        {
                            HostHandshake.Invalidate(handshake, invalidIdentity: true);
                            return new PostResult();
                        }

                        return new PostResult
                        {
                            ReachedHost = true,
                            Status = response.StatusCode,
                            Reply = await ReadReplyAsync(response).ConfigureAwait(false)
                        };
                    }
                }
            }
            catch
            {
                HostHandshake.Invalidate(handshake, invalidIdentity: false);
                return new PostResult();
            }
        }

        private async Task<PostResult> PostWithSensitiveConfirmationAsync(ExtensionUpdate update)
        {
            var result = await PostAsync(update);
            if (result.Status != UnprocessableEntity || result.Reply == null ||
                string.IsNullOrEmpty(result.Reply.Warning)) return result;

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var answer = MessageBoxW(IntPtr.Zero,
                result.Reply.Warning + "\n\n파일: " + Path.GetFileName(update.FilePath),
                "Classroom Live · 민감 정보 확인",
                MessageBoxYesNo | MessageBoxIconWarning | MessageBoxDefaultNo);
            if (answer != MessageBoxYes) return result;

            update.AllowSensitive = true;
            return await PostAsync(update);
        }

        private static string ReplyError(PostResult result)
        {
            if (result.Reply != null)
            {
                if (!string.IsNullOrEmpty(result.Reply.Warning)) return "공유를 취소했습니다";
                if (!string.IsNullOrEmpty(result.Reply.BlockReason)) return result.Reply.BlockReason;
            }
            return result.Status.HasValue ? "공유하지 못했습니다" : "호스트 실행 대기";
        }

        /// <summary>실행/종료/일시정지처럼 파일과 무관한 조작.</summary>
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
                    {
                        if (!IsClassroomLiveResponse(response))
                        {
                            HostHandshake.Invalidate(handshake, invalidIdentity: true);
                            return false;
                        }
                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                HostHandshake.Invalidate(handshake, invalidIdentity: false);
                return false;
            }
        }

        private static bool IsClassroomLiveResponse(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            return response.StatusCode != HttpStatusCode.NotFound &&
                   response.Headers.TryGetValues("X-Classroom-Live", out values) &&
                   values.Contains("1");
        }

        private static async Task<HostReply> ReadReplyAsync(HttpResponseMessage response)
        {
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
            public bool ReachedHost { get; set; }
            public HttpStatusCode? Status { get; set; }
            public HostReply Reply { get; set; }
        }

        [DataContract]
        private sealed class HostReply
        {
            [DataMember(Name = "command")] public string Command { get; set; }
            [DataMember(Name = "owner")] public bool Owner { get; set; }
            [DataMember(Name = "broadcasting")] public bool Broadcasting { get; set; }
            [DataMember(Name = "everStarted")] public bool EverStarted { get; set; }
            [DataMember(Name = "ended")] public bool Ended { get; set; }
            [DataMember(Name = "shareable")] public bool Shareable { get; set; }
            [DataMember(Name = "blockReason")] public string BlockReason { get; set; }
            [DataMember(Name = "warning")] public string Warning { get; set; }
            [DataMember(Name = "shared")] public bool Shared { get; set; }
            [DataMember(Name = "hidden")] public bool Hidden { get; set; }
            [DataMember(Name = "restoreId")] public string RestoreId { get; set; }
            [DataMember(Name = "restoreFiles")] public RestoreFile[] RestoreFiles { get; set; }
            [DataMember(Name = "sessionId")] public string SessionId { get; set; }
        }

        [DataContract]
        private sealed class RestoreFile
        {
            [DataMember(Name = "path")] public string Path { get; set; }
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
            private static readonly object Gate = new object();
            private static HostHandshake cached;
            private static DateTime readAt;

            [DataMember(Name = "port")] public int Port { get; set; }
            [DataMember(Name = "token")] public string Token { get; set; }
            [DataMember(Name = "pid")] public int ProcessId { get; set; }

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
                lock (Gate)
                {
                    if (cached != null && DateTime.UtcNow - readAt < CacheFor &&
                        IsProcessAlive(cached.ProcessId)) return cached;
                    readAt = DateTime.UtcNow;
                    try
                    {
                        var handshake = ReadHandshake();
                        if (handshake != null && handshake.Port > 0 &&
                            !string.IsNullOrEmpty(handshake.Token) && IsProcessAlive(handshake.ProcessId))
                            return cached = handshake;

                        cached = null;
                        if (handshake != null) DeleteIfCurrent(handshake);
                        return null;
                    }
                    catch
                    {
                        return cached = null;
                    }
                }
            }

            /// <summary>응답하지 않은 정보가 아직 디스크의 현재 값일 때만 지운다.</summary>
            public static void Invalidate(HostHandshake failed, bool invalidIdentity)
            {
                lock (Gate)
                {
                    cached = null;
                    if (invalidIdentity || !IsProcessAlive(failed.ProcessId)) DeleteIfCurrent(failed);
                }
            }

            private static HostHandshake ReadHandshake()
            {
                var path = Path.Combine(Folder, "host.json");
                if (!File.Exists(path)) return null;
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(HostHandshake));
                    return serializer.ReadObject(stream) as HostHandshake;
                }
            }

            private static bool IsProcessAlive(int processId)
            {
                if (processId <= 0) return false;
                try
                {
                    using (var process = Process.GetProcessById(processId))
                        return !process.HasExited;
                }
                catch
                {
                    return false;
                }
            }

            private static void DeleteIfCurrent(HostHandshake expected)
            {
                try
                {
                    var current = ReadHandshake();
                    if (current == null || current.Port != expected.Port ||
                        current.ProcessId != expected.ProcessId ||
                        !string.Equals(current.Token, expected.Token, StringComparison.Ordinal)) return;
                    TryDelete(Path.Combine(Folder, "host.json"));
                }
                catch { }
            }

            private static void TryDelete(string path)
            {
                try { File.Delete(path); }
                catch { }
            }
        }

        [DataContract]
        private sealed class ExtensionUpdate
        {
            [DataMember] public string Action { get; set; }
            [DataMember] public string FilePath { get; set; }
            [DataMember] public string SolutionRoot { get; set; }
            [DataMember] public string ProjectName { get; set; }
            [DataMember] public string ProjectKey { get; set; }
            [DataMember] public string Content { get; set; }
            [DataMember] public int ActiveLine { get; set; }
            [DataMember] public int AnchorLine { get; set; }
            [DataMember] public string InstanceId { get; set; }
            [DataMember] public bool AllowSensitive { get; set; }
            [DataMember] public bool Focused { get; set; }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

        [ComImport]
        [Guid("04A72314-32E9-48E2-9B87-A63603454F3E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface SDteService
        {
        }
    }
}
