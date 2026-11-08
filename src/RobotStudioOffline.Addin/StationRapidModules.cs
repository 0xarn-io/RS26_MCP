using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ABB.Robotics.RobotStudio.Controllers;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;
using Pc = ABB.Robotics.Controllers;
using Rapid = ABB.Robotics.Controllers.RapidDomain;
using Events = ABB.Robotics.Controllers.EventLogDomain;
using Forms = System.Windows.Forms;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static IPAddress ConfiguredRealAddress()
    {
        var configured = System.Environment.GetEnvironmentVariable("RS_BRIDGE_REAL_ADDRESS");
        if (!IPAddress.TryParse(configured, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            address.ToString() != configured || IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] is 0 or >= 224)
            throw Error("real_connection_required", "Set RS_BRIDGE_REAL_ADDRESS to the intended controller's service-port IPv4 address before starting RobotStudio.");
        return address;
    }
    private sealed record VerifiedModule(string SourceSha256, string ExportSha256);
    private static readonly ConditionalWeakTable<Station, Dictionary<(Guid Controller, string Task, string Module), VerifiedModule>> VerifiedCloneLoads = new();

    private static async Task<object> ExecuteRapidModulesAsync(Station station, string operation, JsonElement arguments)
    {
        var request = RapidModuleRequests.Parse(operation, arguments);
        var inspectSource = operation is "rapid_check" or "rapid_load" or "rapid_sync_real";
        var sources = new List<RapidSource>();
        foreach (var path in request.Files)
        {
            var source = RapidSource.Read(path, allowUnidentifiedModule: inspectSource);
            if (source.Module.Length != 0 && sources.Any(other => other.Module.Equals(source.Module, StringComparison.OrdinalIgnoreCase)))
                throw Error("duplicate_module", $"Two inputs declare MODULE {source.Module}.");
            if (sources.Sum(other => other.Bytes.Length) + source.Bytes.Length > 16 * 1024 * 1024)
                throw Error("rapid_source_size", "The selected module batch exceeds 16 MiB.");
            sources.Add(source);
        }
        if (request.Pers.Any(selector => !sources.Any(source => source.Module.Equals(selector.Split('/')[0], StringComparison.OrdinalIgnoreCase))))
            throw Error("rapid_module_selection", "Every selected PERS must belong to one of the requested modules.");

        RapidSourceInspection[] sourceChecks = [];
        if (inspectSource && sources.Count > 0)
        {
            sourceChecks = await Task.Run(() => sources.Select(source => RapidSourceDiagnostics.Inspect(source)).ToArray());
            var failed = sourceChecks.Any(result => result.Correct == false);
            bool? correct = failed ? false : sourceChecks.All(result => result.Correct == true) ? true : null;
            if (operation == "rapid_check" || failed)
            {
                return new
                {
                    scope = "local_source_only", correct, selected = sources.Count, loaded = 0, stoppedByBridge = false,
                    allSucceeded = correct, sourceChecks, programMemoryModified = false,
                    results = sourceChecks.Select(result => new
                    {
                        result.File, result.Module, result.Sha256,
                        status = result.Correct == false ? "source_syntax_error" : operation == "rapid_check"
                            ? result.Available ? "source_checked" : "source_check_unavailable" : "not_attempted",
                        submittedFileLine = result.Errors.FirstOrDefault(e => e.Line.HasValue)?.Line,
                        submittedFileColumn = result.Errors.FirstOrDefault(e => e.Line.HasValue)?.Column
                    }).ToArray(),
                    diagnosticScope = "The installed RobotStudio editor parser checks submitted file syntax. Use rapid_check without files for the loaded task's RobotWare semantic check."
                };
            }
        }
        EnsureCurrent(station);
        var context = OwnedVirtualTask(station, request.Robot);
        var taskName = request.TaskName ?? context.Task.Name;
        using var vc = OpenOwnedVirtualConnection(station, context);
        void CheckVc() => EnsureOwnedVirtualContext(station, context, vc);
        using var task = vc.Rapid.GetTask(taskName) ?? throw Error("rapid_task_missing", $"Task {taskName} is missing from the selected virtual controller.");
        if (operation == "rapid_check") return CheckRapidTask(vc, task, CheckVc);
        var cleanupWarnings = new List<string>();
        using var scratch = new ModuleScratch(cleanupWarnings);
        var verified = VerifiedCloneLoads.GetOrCreateValue(station);
        (Guid, string, string) Key(string module) => (vc.SystemId, taskName.ToUpperInvariant(), module.ToUpperInvariant());
        if (operation == "rapid_load")
        {
            foreach (var source in sources) verified.Remove(Key(source.Module));
            void VerifyLoad()
            {
                var exports = ReadModuleExports(vc, task, CheckVc, sources, scratch, null);
                foreach (var source in sources)
                {
                    var export = exports[source.Module] ?? throw Error("rapid_readback_missing", $"Missing virtual readback for {source.Module}.");
                    verified[Key(source.Module)] = new(source.Sha256, export.Sha256);
                }
            }
            return await LoadRapidBatch(vc, task, CheckVc, sources, scratch, null, cleanupWarnings, sourceChecks, VerifyLoad);
        }

        // Export actual in-memory modules, including current PERS values as serialized by ABB.
        // SaveToFile accepts a PC directory only for a virtual controller.
        var vcModules = ReadModuleExports(vc, task, CheckVc, sources, scratch, null);
        void RequireMatchingClone(Dictionary<string, RapidSource?> exports)
        {
            foreach (var source in sources)
            {
                var export = exports[source.Module];
                if (export is not null && (export.Text == source.Text ||
                    (verified.TryGetValue(Key(source.Module), out var match) &&
                    match.SourceSha256 == source.Sha256 && match.ExportSha256 == export.Sha256))) continue;
                throw Error("rapid_clone_source_mismatch",
                    $"The selected source for {source.Module} does not match the current clone. Load and check these files with rapid_load first.");
            }
            var checkedTask = CheckRapidTask(vc, task, CheckVc);
            if (!checkedTask.Correct)
                throw Error("rapid_clone_check_failed", "Resolve virtual task diagnostics with rapid_check before loading these modules into the real controller.");
        }
        if (operation == "rapid_sync_real") RequireMatchingClone(vcModules);

        var realAddress = request.IncludeReal ? ConfiguredRealAddress() : null;
        using var rc = realAddress is null ? null : OpenApprovedReal(station, request, taskName, sources, operation, realAddress);
        var approvedId = rc?.SystemId;
        void CheckReal()
        {
            CheckVc();
            if (rc is null || rc.IsVirtual || !rc.Connected || rc.SystemId != approvedId ||
                realAddress is null || !realAddress.Equals(rc.IPAddress) || CachedRealReference(realAddress).SystemId != approvedId)
                throw Error("real_controller_identity_changed", "The owner-approved controller connection changed.");
        }
        using var realTask = rc?.Rapid.GetTask(taskName);
        if (rc is not null && realTask is null) throw Error("rapid_task_missing", $"Task {taskName} is missing from the approved real controller.");
        if (operation == "rapid_sync_real")
        {
            CheckReal();
            RequireRealStopped(rc!, realTask!);
            RequireRw8Access(rc!);
            foreach (var source in sources)
                if (RapidSource.Read(source.File).Sha256 != source.Sha256)
                    throw Error("rapid_source_changed", $"The selected file for {source.Module} changed after approval. Review it again.");
            RequireMatchingClone(ReadModuleExports(vc, task, CheckVc, sources, scratch, null));
        }
        using var remote = rc is null ? null : new RemoteModuleScratch(rc, CheckReal, cleanupWarnings);
        if (operation == "rapid_sync_real")
            return await LoadRapidBatch(rc!, realTask!, CheckReal, sources, scratch, remote, cleanupWarnings, sourceChecks);

        var realModules = rc is null ? null : ReadModuleExports(rc, realTask!, CheckReal, sources, scratch, remote);
        var modules = sources.Select(source =>
        {
            var virtualSource = vcModules[source.Module];
            var realSource = realModules?[source.Module];
            return new
            {
                module = source.Module, file = source.File, sourceSha256 = source.Sha256,
                virtualStatus = virtualSource is null ? "missing" : "exported",
                realStatus = realModules is null ? "not_requested" : realSource is null ? "missing" : "exported",
                localVsVirtual = virtualSource is null ? null : RapidSource.TextDifference(source.Text, virtualSource.Text),
                localVsReal = realSource is null ? null : RapidSource.TextDifference(source.Text, realSource.Text),
                virtualVsReal = virtualSource is null || realSource is null ? null : RapidSource.TextDifference(virtualSource.Text, realSource.Text)
            };
        }).ToArray();
        var selectors = request.Pers;
        bool truncated = false;
        if (selectors.Length == 0)
        {
            var discovered = PersistentNames(task, CheckVc, sources);
            if (realTask is not null) discovered.UnionWith(PersistentNames(realTask, CheckReal, sources));
            truncated = discovered.Count > 100;
            selectors = discovered.Order(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
        }
        var values = new List<object>();
        foreach (var selector in selectors)
        {
            var parts = selector.Split('/');
            var source = sources.Single(item => item.Module.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            var initializer = source.PersistentInitializer(parts[1]);
            var (virtualValue, virtualError) = ReadPersistent(task, CheckVc, source.Module, parts[1]);
            var (realValue, realError) = realTask is null ? (null, null) : ReadPersistent(realTask, CheckReal, source.Module, parts[1]);
            values.Add(new
            {
                name = selector, localInitializer = ValueSummary(initializer), virtualValue = ValueSummary(virtualValue), realValue = ValueSummary(realValue), virtualError, realError,
                virtualVsRealEqual = realValue is null || virtualValue is null ? (bool?)null : virtualValue == realValue,
                localVsVirtualEqual = initializer is null || virtualValue is null ? (bool?)null : initializer == virtualValue,
                localVsRealEqual = initializer is null || realValue is null ? (bool?)null : initializer == realValue
            });
        }
        return new { task = taskName, modules, pers = values, persTruncated = truncated, comparedReal = rc is not null,
            valueComparison = "Exact RAPID strings. Local values are source initializers, controller values are current values. A missing or unavailable value is null.",
            snapshot = "Sequential reads across modules and controllers.", cleanupWarnings };
    }

    private static HashSet<string> PersistentNames(Rapid.Task task, Action check, List<RapidSource> sources)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var search = Rapid.RapidSymbolSearchProperties.CreateDefaultForData();
        search.Types = Rapid.SymbolTypes.Persistent;
        search.Recursive = false;
        search.LocalSymbols = true;
        search.GlobalSymbols = true;
        foreach (var source in sources)
        {
            check();
            using var module = task.GetModule(source.Module);
            if (module is null) continue;
            foreach (var symbol in module.SearchRapidSymbol(search))
                names.Add(source.Module + "/" + symbol.Name);
        }
        return names;
    }

    private static (string? Value, object? Error) ReadPersistent(Rapid.Task task, Action check, string moduleName, string name)
    {
        check();
        try
        {
            using var module = task.GetModule(moduleName);
            if (module is null) return (null, null);
            using var data = module.GetRapidData(name);
            if (data is null || data.Symbol.Type != Rapid.SymbolTypes.Persistent) return (null, null);
            return (data.StringValue, null);
        }
        catch (Exception ex) { return (null, DataError(ex)); }
    }

    private static object? ValueSummary(string? value) => value is null ? null : new
    {
        text = value[..Math.Min(2048, value.Length)], truncated = value.Length > 2048,
        sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
    };

    private sealed record TaskDiagnostics(string Task, bool Correct, int ErrorCount, object[] Errors, bool Truncated);
    private static TaskDiagnostics CheckRapidTask(Pc.Controller controller, Rapid.Task task, Action check)
    {
        check();
        RequireRw8Access(controller);
        Rapid.CheckProgramResult result;
        try { result = task.CheckProgram(); }
        catch (Exception ex) when (IsRapidAccessDenial(ex)) { throw RapidAccessRequired(controller); }
        var errors = result.Errors.Select(error => (object)new { task = error.TaskName, module = error.ModuleName,
            line = error.Line > 0 ? (int?)error.Line : null, column = error.Column > 0 ? (int?)error.Column : null, indexBase = 1 }).Take(500).ToArray();
        return new(task.Name, result.Errors.Count == 0, result.Errors.Count, errors, errors.Length < result.Errors.Count);
    }

    private static Dictionary<string, RapidSource?> ReadModuleExports(Pc.Controller controller, Rapid.Task task, Action check,
        List<RapidSource> sources, ModuleScratch scratch, RemoteModuleScratch? remote)
    {
        var result = new Dictionary<string, RapidSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            check();
            using var module = task.GetModule(source.Module);
            if (module is null) { result[source.Module] = null; continue; }
            if (module.IsEncoded || module.IsNoView) throw Error("rapid_module_protected", $"RobotStudio cannot export readable text for {module.Name}.");
            var extension = (module.IsSystem ? ".sys" : ".mod") + (controller.HasUnicodeSupport ? "x" : "");
            var filename = source.Module + extension;
            var local = Path.Combine(scratch.Directory, filename);
            if (File.Exists(local)) File.Delete(local); // Only a preceding export in this request's unique scratch directory.
            if (remote is null) module.SaveToFile(scratch.Directory);
            else
            {
                var remotePath = remote.OwnFile(filename);
                module.SaveToFile(remote.Directory);
                check();
                controller.FileSystem.GetFile(remotePath, local, false);
            }
            result[source.Module] = RapidSource.Read(local);
        }
        return result;
    }

    private static async Task<object> LoadRapidBatch(Pc.Controller controller, Rapid.Task task, Action check,
        List<RapidSource> sources, ModuleScratch scratch, RemoteModuleScratch? remote, List<string> cleanupWarnings,
        RapidSourceInspection[] sourceChecks, Action? verifyCloneLoad = null)
    {
        bool real = remote is not null;
        var stoppedByBridge = false;
        var results = new List<object>();
        var accepted = 0;
        bool aborted = false;
        check();
        using var access = BeginRapidLoadAccess(controller, !real, cleanupWarnings);
        void CheckLoadAccess() { check(); RequireRw8Access(controller); }
        CheckLoadAccess();
        if (real) RequireRealStopped(controller, task);
        else if (task.ExecutionStatus == Rapid.TaskExecutionStatus.Running)
        {
            try { task.Stop(Rapid.StopMode.Instruction); }
            catch (Exception ex) when (IsRapidAccessDenial(ex)) { throw RapidAccessRequired(controller); }
            stoppedByBridge = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (task.ExecutionStatus == Rapid.TaskExecutionStatus.Running && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100);
                CheckLoadAccess();
            }
        }
        if (task.ExecutionStatus is not (Rapid.TaskExecutionStatus.Stopped or Rapid.TaskExecutionStatus.Ready))
            throw Error("rapid_task_not_stopped", $"Task {task.Name} did not reach a stopped/ready state. Stop requested by bridge: {stoppedByBridge}.");
        using var eventLog = controller.EventLog;
        foreach (var source in sources)
        {
            if (aborted) { results.Add(new { module = source.Module, file = source.File, status = "not_attempted" }); continue; }
            bool submitted = false;
            var events = new ConcurrentQueue<object>();
            void OnEvent(object? sender, Events.MessageWrittenEventArgs args)
            {
                try
                {
                    if (events.Count >= 32) return;
                    var item = args.Message;
                    static string Clip(string? text) => (text ?? "")[..Math.Min(text?.Length ?? 0, 800)];
                    events.Enqueue(new { number = item.Number, sequence = item.SequenceNumber, type = item.Type.ToString(),
                        title = Clip(item.Title), message = Clip(item.Body), timestamp = item.Timestamp });
                }
                catch (Exception) { /* Event payload can expire as the native log changes. */ }
            }
            bool subscribed = false;
            try
            {
                CheckLoadAccess();
                if (real) RequireRealStopped(controller, task);
                if (task.ExecutionStatus is not (Rapid.TaskExecutionStatus.Stopped or Rapid.TaskExecutionStatus.Ready))
                    throw Error("rapid_task_not_stopped", $"Task {task.Name} is no longer stopped.");
                var filename = Guid.NewGuid().ToString("N") + Path.GetExtension(source.File);
                var staged = Path.Combine(scratch.Directory, filename);
                File.WriteAllBytes(staged, source.Bytes);
                var loadPath = staged;
                if (remote is not null)
                {
                    loadPath = remote.OwnFile(filename);
                    controller.FileSystem.PutFile(staged, loadPath, false);
                    CheckLoadAccess();
                    RequireRealStopped(controller, task);
                }
                // Event logs are supporting evidence from the same time window. Only CheckProgram
                // provides structured source coordinates, and it checks the installed task.
                try { eventLog.MessageWrittenNoCache += OnEvent; subscribed = true; }
                catch (Exception ex) { cleanupWarnings.Add("Live RAPID event diagnostics unavailable: " + ex.Message); }
                CheckLoadAccess();
                if (real) RequireRealStopped(controller, task);
                else if (task.ExecutionStatus is not (Rapid.TaskExecutionStatus.Stopped or Rapid.TaskExecutionStatus.Ready))
                    throw Error("rapid_task_not_stopped", $"Task {task.Name} is no longer stopped.");
                submitted = true;
                bool loaded = task.LoadModuleFromFile(loadPath, Rapid.RapidLoadMode.Replace);
                if (loaded) accepted++;
                else aborted = true;
                results.Add(new { module = source.Module, file = source.File, sha256 = source.Sha256, accepted = loaded,
                    outcomeMayHaveChanged = !loaded,
                    status = loaded ? "loaded" : "load_rejected", submittedFileLine = (int?)null, submittedFileColumn = (int?)null,
                    eventsDuringLoad = events.ToArray() });
            }
            catch (Exception ex)
            {
                aborted = true;
                results.Add(new { module = source.Module, file = source.File, sha256 = source.Sha256,
                    status = "error", outcomeMayHaveChanged = submitted, submittedFileLine = (int?)null,
                    submittedFileColumn = (int?)null, error = DataError(IsRapidAccessDenial(ex) ? RapidAccessRequired(controller) : ex), eventsDuringLoad = events.ToArray() });
            }
            finally
            {
                if (subscribed)
                {
                    try { eventLog.MessageWrittenNoCache -= OnEvent; }
                    catch (Exception ex) { cleanupWarnings.Add("Event subscription cleanup: " + ex.Message); }
                }
            }
        }
        TaskDiagnostics? diagnostics = null;
        object? checkError = null;
        try { diagnostics = CheckRapidTask(controller, task, check); }
        catch (Exception ex) { checkError = DataError(ex); }
        bool? cloneReadbackVerified = null;
        object? readbackError = null;
        if (!aborted && diagnostics?.Correct == true && verifyCloneLoad is not null)
        {
            try { verifyCloneLoad(); cloneReadbackVerified = true; }
            catch (Exception ex) { cloneReadbackVerified = false; readbackError = DataError(ex); }
        }
        return new { task = task.Name, scope = real ? "owner_approved_real_controller" : "station_owned_virtual_controller",
            selected = sources.Count, loaded = accepted, allSucceeded = !aborted && diagnostics?.Correct == true && cloneReadbackVerified != false,
            partial = accepted > 0 && accepted < sources.Count,
            stoppedByBridge, restarted = false, results, sourceChecks, installedTaskCheck = diagnostics, checkError, cloneReadbackVerified, readbackError, cleanupWarnings,
            diagnosticScope = "sourceChecks refer to submitted files. CheckProgram coordinates refer to the installed task. Load events are concurrent controller messages, with no inferred file coordinates." };
    }

    private static void RequireRealStopped(Pc.Controller controller, Rapid.Task task)
    {
        if (controller.Rapid.ExecutionStatus != Rapid.ExecutionStatus.Stopped ||
            task.ExecutionStatus is not (Rapid.TaskExecutionStatus.Stopped or Rapid.TaskExecutionStatus.Ready))
            throw Error("real_rapid_running", "The owner must stop real RAPID before loading modules.");
    }

    private static ControllerObjectReference CachedRealReference(IPAddress realAddress)
    {
        // The 2026 SDK's public IsServicePortController flag tests only the subnet prefix.
        // Read the cached connection metadata to pin the exact endpoint before PC SDK access.
        var candidates = ControllerManager.ControllerReferences.ToArray().Where(reference =>
        {
            if (reference.ControllerType != ControllerType.RC || !reference.IsServicePortController) return false;
            var cached = reference.RobControllerConnection;
            return cached is not null && cached.IsConnected && IPAddress.TryParse(cached.ControllerInfo.IPAddress, out var address) &&
                realAddress.Equals(address) && Guid.TryParse(cached.ControllerInfo.SystemId, out var id) && id == reference.SystemId;
        }).ToArray();
        if (candidates.Length != 1) throw Error("real_connection_required",
            "The owner must already have exactly one matching real controller connected on the configured service-port address in RobotStudio.");
        return candidates[0];
    }

    private static Pc.Controller OpenApprovedReal(Station station, RapidModuleRequest request, string task, List<RapidSource> sources, string operation, IPAddress realAddress)
    {
        EnsureCurrent(station);
        var reference = CachedRealReference(realAddress);
        var id = reference.SystemId;
        bool writing = operation == "rapid_sync_real";
        var details = new StringBuilder()
            .AppendLine(writing ? "REPLACE the selected RAPID modules on the REAL controller." : "Read the selected module text and PERS values from the REAL controller.")
            .AppendLine($"Controller: {reference.Name} at {realAddress}").AppendLine($"System ID: {id}").AppendLine($"Task: {task}").AppendLine();
        foreach (var source in sources)
            details.AppendLine($"{source.Module}: {source.File}").AppendLine($"SHA-256: {source.Sha256}");
        if (!writing)
            details.AppendLine().AppendLine("PERS: " + (request.Pers.Length == 0 ? "Up to 100 persistent declarations in these modules" : string.Join(", ", request.Pers)));
        details.AppendLine().AppendLine(writing
            ? "The approved file snapshots will be staged in a new controller HOME subdirectory, loaded with Replace, checked and the staging files removed. Existing module code and persistent values may change. The controller must already be stopped."
            : "Text export creates a temporary directory and exported files in controller HOME, reads them, then removes those temporary files. Program memory is read only.");
        details.AppendLine("This approval applies to this request only.");
        using var dialog = new Forms.Form { Text = writing ? "Approve real RAPID module replacement" : "Approve real RAPID comparison",
            Width = 680, Height = 520, ShowInTaskbar = false, StartPosition = Forms.FormStartPosition.CenterParent };
        var summary = new Forms.TextBox { Text = details.ToString(), ReadOnly = true, Multiline = true, ScrollBars = Forms.ScrollBars.Vertical,
            Dock = Forms.DockStyle.Fill, WordWrap = true };
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, AutoSize = true, FlowDirection = Forms.FlowDirection.RightToLeft };
        var cancel = new Forms.Button { Text = "Cancel", DialogResult = Forms.DialogResult.Cancel, AutoSize = true };
        var allow = new Forms.Button { Text = writing ? "Approve replacement" : "Approve read", DialogResult = Forms.DialogResult.Yes, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(allow);
        dialog.Controls.Add(summary); dialog.Controls.Add(buttons); dialog.AcceptButton = cancel; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        if (dialog.ShowDialog(ABB.Robotics.RobotStudio.Environment.UIEnvironment.MainWindow) != Forms.DialogResult.Yes)
            throw new BridgeException("real_controller_permission_required", "The owner declined this real-controller operation.", 403);
        EnsureCurrent(station);
        if (CachedRealReference(realAddress).SystemId != id) throw Error("real_controller_identity_changed", "The approved connection changed.");
        var controller = Pc.Controller.Connect(id, Pc.ConnectionType.RobotStudio, validateServerCertificate: true);
        try
        {
            if (!controller.Connected || controller.IsVirtual || controller.SystemId != id || !realAddress.Equals(controller.IPAddress))
                throw Error("real_controller_identity_changed", "The connection does not match the approved real controller.");
            return controller;
        }
        catch { controller.Dispose(); throw; }
    }

    private sealed class ModuleScratch(List<string> warnings) : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("RSOfflineRapid_").FullName;
        public void Dispose()
        {
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory)) File.Delete(file);
                System.IO.Directory.Delete(Directory, false);
            }
            catch (Exception ex) { warnings.Add("Local temporary module cleanup: " + ex.Message); }
        }
    }

    private sealed class RemoteModuleScratch : IDisposable
    {
        private readonly Pc.Controller controller;
        private readonly Action check;
        private readonly List<string> warnings;
        private readonly HashSet<string> created = new(StringComparer.Ordinal);
        public string Directory { get; }

        public RemoteModuleScratch(Pc.Controller controller, Action check, List<string> warnings)
        {
            this.controller = controller; this.check = check; this.warnings = warnings;
            check();
            var home = controller.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home)) throw Error("rapid_home_missing", "The controller did not provide its HOME directory.");
            Directory = home.TrimEnd('/', '\\') + "/RSOfflineRapid_" + Guid.NewGuid().ToString("N");
            controller.FileSystem.CreateDirectory(Directory);
        }

        public string OwnFile(string name)
        {
            if (Path.GetFileName(name) != name) throw new ArgumentException("Expected a module filename.", nameof(name));
            var path = Directory + "/" + name;
            created.Add(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                check();
                foreach (var path in created)
                    if (controller.FileSystem.FileExists(path)) controller.FileSystem.RemoveFile(path);
                controller.FileSystem.RemoveDirectory(Directory, false);
            }
            catch (Exception ex) { warnings.Add($"Inspect temporary controller directory {Directory}: {ex.Message}"); }
        }
    }
}
