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
        private static readonly Guid CommandSet = new Guid("0FC38C23-09B7-4C95-89F5-BEB7321757E4");
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly HashSet<string> sharedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private dynamic dte;
        private Timer syncTimer;
        private int syncRunning;

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

            syncTimer = new Timer(SyncActiveFile, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(600));
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
                        : "Classroom Live: 파일은 등록됨 · 호스트 실행 대기 중");
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
                    if (status == HttpStatusCode.Conflict && path != null)
                        sharedFiles.Remove(path);
                }
                finally
                {
                    Interlocked.Exchange(ref syncRunning, 0);
                }
            });
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

        private static async Task<HttpStatusCode?> PostAsync(ExtensionUpdate update)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(ExtensionUpdate));
                string json;
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, update);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await Client.PostAsync("http://127.0.0.1:5050/api/extension/update", content).ConfigureAwait(false))
                    return response.StatusCode;
            }
            catch
            {
                return null;
            }
        }

        [DataContract]
        private sealed class ExtensionUpdate
        {
            [DataMember] public string Action { get; set; }
            [DataMember] public string FilePath { get; set; }
            [DataMember] public string SolutionRoot { get; set; }
            [DataMember] public string Content { get; set; }
        }

        [ComImport]
        [Guid("04A72314-32E9-48E2-9B87-A63603454F3E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface SDteService
        {
        }
    }
}
