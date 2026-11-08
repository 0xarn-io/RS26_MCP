using System.Net;
using System.Text.Json;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Controllers;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;
using Pc = ABB.Robotics.Controllers;
using Rapid = ABB.Robotics.Controllers.RapidDomain;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static Task<object> CompareDataAsync(Station station, DataCompareRequest request)
    {
        var context = OwnedVirtualTask(station, request.Robot);
        // ConnectionType.RobotStudio reuses the host's existing connection/authentication.
        // Selection and StationVC checks happen before obtaining the PC SDK wrapper.
        using var controller = OpenOwnedVirtualConnection(station, context);
        var results = new List<object>();
        foreach (var selection in request.Data)
        {
            try
            {
                EnsureOwnedVirtualContext(station, context, controller);
                var symbol = ResolveDataSymbol(context.Task, selection);
                var stationValue = ReadStationValue(symbol.StationData);
                var controllerValue = ReadVirtualValue(controller, context.Task.Name, symbol);
                results.Add(ComparisonItem(selection, symbol, stationValue, controllerValue));
            }
            catch (Exception exception)
            {
                results.Add(new { name = selection.Name, kind = selection.Kind, status = "error", error = DataError(exception) });
            }
        }
        return Task.FromResult<object>(new
        {
            robot = context.Robot.Name, task = context.Task.Name, virtualController = context.Controller.Name,
            scope = "station_owned_virtual_controller", readOnly = true, synchronized = false,
            numericAbsoluteTolerance = DataValueComparison.AbsoluteTolerance,
            numericRelativeTolerance = DataValueComparison.RelativeTolerance,
            quaternionComponentTolerance = DataValueComparison.QuaternionTolerance,
            comparison = "Values only; declaration storage/locality and graphical work-object associations are not compared. Opposite-sign unit quaternions represent the same orientation. PC SDK single-precision external-axis 9E9 sentinels are normalized.",
            results
        });
    }

    private static async Task<object> SyncDataAsync(Station station, DataSyncRequest request)
    {
        RequireStopped();
        var context = OwnedVirtualTask(station, request.Robot);
        using var controller = OpenOwnedVirtualConnection(station, context);
        RequireVirtualRapidStopped(controller);
        var toStation = request.Direction == "to_station";
        var direction = toStation ? SyncDirection.ToStation : SyncDirection.ToController;
        // Resolve and validate the complete selection before the first write. In particular
        // dataId is constructed from validated identifiers, never a client-supplied URL.
        var plans = request.Data.Select(selection =>
        {
            var symbol = ResolveDataSymbol(context.Task, selection);
            var stationValue = ReadStationValue(symbol.StationData);
            var controllerValue = ReadVirtualValue(controller, context.Task.Name, symbol);
            if (toStation && controllerValue is null)
                throw Error("data_missing", $"The virtual controller has no {symbol.Id} to import.");
            if (!toStation && stationValue is null)
                throw Error("data_missing", $"The station has no {symbol.Id} to export.");
            return new DataSyncPlan(selection, symbol, stationValue, controllerValue);
        }).ToArray();
        if (plans.Select(plan => plan.Symbol.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plans.Length)
            throw Error("duplicate_data_selection", "The selection resolves to the same declaration more than once.");

        var results = new List<object>();
        var completed = 0;
        var aborted = false;
        foreach (var plan in plans)
        {
            if (aborted)
            {
                results.Add(new { name = plan.Symbol.Id, kind = plan.Selection.Kind, status = "not_attempted" });
                continue;
            }
            var messages = new List<SyncLogMessage>();
            var attempted = false;
            var applied = false;
            var undoStarted = false;
            try
            {
                EnsureOwnedVirtualContext(station, context, controller);
                RequireStopped();
                RequireVirtualRapidStopped(controller);
                var currentSymbol = ResolveDataSymbol(context.Task, plan.Selection);
                if (currentSymbol.Id != plan.Symbol.Id)
                    throw Error("data_changed", "The selected declaration changed during the batch; compare again.");
                var currentSource = toStation
                    ? ReadVirtualValue(controller, context.Task.Name, currentSymbol)
                    : ReadStationValue(currentSymbol.StationData);
                var plannedSource = toStation ? plan.ControllerValue : plan.StationValue;
                if (currentSource?.GetRawText() != plannedSource?.GetRawText())
                    throw Error("data_changed", "The source data changed during the batch; compare again before synchronizing.");

                if (toStation)
                {
                    Project.UndoContext.BeginUndoStep("Offline bridge: import selected virtual data");
                    undoStarted = true;
                }
                attempted = true;
                await context.Task.SyncDataAsync(plan.Symbol.Id, direction, messages);
                EnsureOwnedVirtualContext(station, context, controller);
                RequireStopped();
                RequireVirtualRapidStopped(controller);
                if (messages.Any(message => message.Severity is SyncLogMessageSeverity.Error or SyncLogMessageSeverity.FatalError))
                    throw Error("sync_failed", "RobotStudio reported an error while synchronizing the selected declaration.");
                if (undoStarted)
                {
                    Project.UndoContext.EndUndoStep();
                    undoStarted = false;
                }
                applied = true;
                completed++;
                var afterSymbol = ResolveDataSymbol(context.Task, plan.Selection);
                var afterStation = ReadStationValue(afterSymbol.StationData);
                var afterController = ReadVirtualValue(controller, context.Task.Name, afterSymbol);
                results.Add(new
                {
                    name = plan.Symbol.Id, kind = plan.Selection.Kind, status = "synchronized",
                    before = new { station = plan.StationValue, virtualController = plan.ControllerValue },
                    after = ComparisonItem(plan.Selection, afterSymbol, afterStation, afterController),
                    log = SyncMessages(messages)
                });
            }
            catch (Exception exception)
            {
                object? rollbackError = null;
                if (undoStarted)
                {
                    try { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); }
                    catch (Exception rollbackException) { rollbackError = DataError(rollbackException); }
                    finally
                    {
                        try { Project.UndoContext.EndUndoStep(); }
                        catch (Exception endException) { rollbackError ??= DataError(endException); }
                        undoStarted = false;
                    }
                }
                aborted = true;
                results.Add(new
                {
                    name = plan.Symbol.Id, kind = plan.Selection.Kind,
                    status = applied ? "synchronized_verification_failed" : "error", attempted, synchronized = applied,
                    outcomeMayHaveChanged = applied || (attempted && (!toStation || rollbackError is not null)),
                    error = applied ? null : DataError(exception),
                    verificationError = applied ? DataError(exception) : null,
                    rollbackError, log = SyncMessages(messages)
                });
            }
        }
        if (toStation) Station.UpdateGraphics();
        return new
        {
            robot = context.Robot.Name, task = context.Task.Name, virtualController = context.Controller.Name,
            direction = request.Direction, scope = "station_owned_virtual_controller", selectedCount = plans.Length,
            completedCount = completed, allSucceeded = !aborted, partial = aborted && completed > 0,
            atomic = false, results,
            note = "Only selected data IDs were passed to the SDK. A batch stops after its first failure; completed virtual-controller writes cannot be undone by station Undo. Inspect results and compare before retrying."
        };
    }

    private static VirtualTaskContext OwnedVirtualTask(Station station, string? robotName)
    {
        var robot = SelectRobot(station, robotName);
        var task = RequireTask(robot);
        var owners = station.VirtualControllers.Cast<VirtualController>().Where(c => c.Tasks.Cast<RsTask>().Contains(task)).ToArray();
        if (owners.Length != 1 || !ReferenceEquals(task.ContainingProject, station))
            throw Error("virtual_task_required", "Select a robot task owned by one virtual controller in the open station.");
        return new(robot, task, owners[0]);
    }

    private static Pc.Controller OpenOwnedVirtualConnection(Station station, VirtualTaskContext context)
    {
        EnsureCurrent(station);
        if (!Guid.TryParse(context.Controller.SystemId, out var id))
            throw Error("virtual_controller_identity", "The station virtual controller has no valid system identity.");
        var references = ControllerManager.ControllerReferences.ToArray().Where(reference => reference.SystemId == id).ToArray();
        if (references.Length != 1 || references[0].ControllerType != ControllerType.StationVC || !references[0].IsStationController)
            throw Error("virtual_connection_required", "The selected station virtual controller must already be connected in RobotStudio. This bridge does not discover or add connections.");
        // Never use Standalone, ControllerReferences.Add, NetworkScanner, Logon or an address.
        var connection = Pc.Controller.Connect(id, Pc.ConnectionType.RobotStudio, validateServerCertificate: true);
        try
        {
            EnsureOwnedVirtualContext(station, context, connection);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void EnsureOwnedVirtualContext(Station station, VirtualTaskContext context, Pc.Controller connection)
    {
        EnsureCurrent(station);
        if (!station.VirtualControllers.Cast<VirtualController>().Contains(context.Controller) ||
            !context.Controller.Tasks.Cast<RsTask>().Contains(context.Task) || !ReferenceEquals(context.Robot.Task, context.Task) ||
            !Guid.TryParse(context.Controller.SystemId, out var id) || connection.SystemId != id ||
            !connection.IsVirtual || !IPAddress.IsLoopback(connection.IPAddress))
            throw Error("real_controller_permission_required", "The selected connection is no longer the open station's local virtual controller. Real-controller access requires separate explicit owner permission and is not exposed by this bridge.");
        if (!connection.Connected)
            throw Error("virtual_connection_required", "The station virtual controller is disconnected; connect it through RobotStudio before retrying.");
    }

    private static void RequireVirtualRapidStopped(Pc.Controller controller)
    {
        if (controller.Rapid.ExecutionStatus != Rapid.ExecutionStatus.Stopped)
            throw Error("virtual_program_running", "Stop RAPID in the virtual controller before synchronizing selected data.");
    }

    private static DataSymbol ResolveDataSymbol(RsTask task, DataSelection selection)
    {
        var parts = selection.Name.Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(part => !ValidDataIdentifier(part)))
            throw Error("data_name", "Select a declaration name or Module/name using RAPID identifiers only; file paths, URLs, array indices and expressions are not accepted.");
        var declarationName = parts[^1];
        var module = parts.Length == 2 ? parts[0] : null;
        var candidates = task.DataDeclarations.Cast<RsDataDeclaration>()
            .Concat(task.Targets.Cast<RsTarget>().Select(target => target.RobTarget)).Distinct()
            .Where(data => string.Equals(data.Name, declarationName, StringComparison.OrdinalIgnoreCase) &&
                (module is null || string.Equals(data.ModuleName, module, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length > 1)
            throw Error("data_selection", "The declaration name is ambiguous; specify Module/name.");
        var stationData = candidates.SingleOrDefault();
        if (stationData is not null && !MatchesKind(stationData, selection.Kind))
            throw Error("data_kind", "The station declaration does not match the selected kind.");
        module ??= stationData?.ModuleName;
        if (string.IsNullOrEmpty(module) || !ValidDataIdentifier(module))
            throw Error("data_module_required", "Specify Module/name for data not present in the station. Built-in declarations without a module are not synchronized by this tool.");
        return new(module, stationData?.Name ?? declarationName, selection.Kind, stationData);
    }

    private static bool ValidDataIdentifier(string value) => value.Length is > 0 and <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool MatchesKind(RsDataDeclaration value, string kind) => kind switch
    {
        "target" => value is RsRobTarget, "tool" => value is RsToolData, "workObject" => value is RsWorkObject, _ => false
    };

    private static JsonElement? ReadStationValue(RsDataDeclaration? data)
    {
        object? value = data switch
        {
            null => null,
            RsRobTarget target => new
            {
                frame = DataFrame(target.Frame.Matrix), configuration = target.ConfigurationData.GetConfigurationData(),
                externalAxes = target.GetExternalAxes(false).GetAxisValues().Select(NormalizeExternalAxis).ToArray()
            },
            RsToolData tool => new
            {
                robotHold = tool.RobotHold, frame = DataFrame(tool.Frame.Matrix),
                load = new
                {
                    mass = tool.LoadData.Mass,
                    cog = new[] { tool.LoadData.Cog.x * 1000.0, tool.LoadData.Cog.y * 1000.0, tool.LoadData.Cog.z * 1000.0 },
                    quaternion = new[] { tool.LoadData.Aom.q1, tool.LoadData.Aom.q2, tool.LoadData.Aom.q3, tool.LoadData.Aom.q4 },
                    inertia = new[] { tool.LoadData.Inertia.x, tool.LoadData.Inertia.y, tool.LoadData.Inertia.z }
                }
            },
            RsWorkObject workObject => new
            {
                robotHold = workObject.RobotHold, userFrameProgrammed = workObject.UserFrameProgrammed,
                mechanicalUnit = workObject.UserFrameMechanicalUnit ?? string.Empty,
                userFrame = DataFrame(workObject.UserFrame.Matrix), objectFrame = DataFrame(workObject.ObjectFrame.Matrix)
            },
            _ => throw Error("data_kind", "Only robtarget, tooldata and wobjdata are supported.")
        };
        return value is null ? null : JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
    }

    private static JsonElement? ReadVirtualValue(Pc.Controller controller, string task, DataSymbol symbol)
    {
        using var data = controller.Rapid.GetRapidData(task, symbol.Module, symbol.Name);
        if (data is null) return null;
        if (data.IsArray) throw Error("data_array", "Selected data must be a scalar declaration; array declarations are not supported.");
        var expectedType = symbol.Kind switch { "target" => "robtarget", "tool" => "tooldata", "workObject" => "wobjdata", _ => string.Empty };
        if (!string.Equals(data.RapidType, expectedType, StringComparison.OrdinalIgnoreCase))
            throw Error("data_kind", "The virtual-controller declaration does not match the selected kind.");
        object value = data.Value switch
        {
            Rapid.RobTarget target => new
            {
                frame = DataFrame(target.Trans, target.Rot),
                configuration = new[] { target.Robconf.Cf1, target.Robconf.Cf4, target.Robconf.Cf6, target.Robconf.Cfx },
                externalAxes = new double[] { target.Extax.Eax_a, target.Extax.Eax_b, target.Extax.Eax_c, target.Extax.Eax_d, target.Extax.Eax_e, target.Extax.Eax_f }.Select(NormalizeExternalAxis).ToArray()
            },
            Rapid.ToolData tool => new
            {
                robotHold = tool.Robhold, frame = DataFrame(tool.Tframe.Trans, tool.Tframe.Rot),
                load = new
                {
                    mass = (double)tool.Tload.Mass,
                    cog = new double[] { tool.Tload.Cog.X, tool.Tload.Cog.Y, tool.Tload.Cog.Z },
                    quaternion = new[] { tool.Tload.Aom.Q1, tool.Tload.Aom.Q2, tool.Tload.Aom.Q3, tool.Tload.Aom.Q4 },
                    inertia = new double[] { tool.Tload.Ix, tool.Tload.Iy, tool.Tload.Iz }
                }
            },
            Rapid.WobjData workObject => new
            {
                robotHold = workObject.Robhold, userFrameProgrammed = workObject.Ufprog,
                mechanicalUnit = workObject.Ufmec ?? string.Empty,
                userFrame = DataFrame(workObject.Uframe.Trans, workObject.Uframe.Rot), objectFrame = DataFrame(workObject.Oframe.Trans, workObject.Oframe.Rot)
            },
            _ => throw Error("data_kind", "The PC SDK did not return the expected typed RAPID value.")
        };
        return JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
    }

    private static object DataFrame(Matrix4 matrix) => new
    {
        position = new[] { matrix.t.x * 1000.0, matrix.t.y * 1000.0, matrix.t.z * 1000.0 },
        quaternion = new[] { matrix.Quaternion.q1, matrix.Quaternion.q2, matrix.Quaternion.q3, matrix.Quaternion.q4 }
    };
    private static object DataFrame(Rapid.Pos position, Rapid.Orient orientation) => new
    {
        position = new double[] { position.X, position.Y, position.Z },
        quaternion = new[] { orientation.Q1, orientation.Q2, orientation.Q3, orientation.Q4 }
    };
    private static double NormalizeExternalAxis(double value) => value == (double)(float)9E9 ? 9E9 : value;

    private static object ComparisonItem(DataSelection selection, DataSymbol symbol, JsonElement? station, JsonElement? controller)
    {
        var differences = station is not null && controller is not null ? DataValueComparison.DifferencePaths(station.Value, controller.Value) : [];
        return new
        {
            requestedName = selection.Name, name = symbol.Id, kind = selection.Kind,
            status = station is null ? (controller is null ? "missing_both" : "missing_station") : controller is null ? "missing_virtual_controller" : differences.Length == 0 ? "equal" : "different",
            equal = station is not null && controller is not null && differences.Length == 0,
            station, virtualController = controller, differences
        };
    }
    private static object[] SyncMessages(IEnumerable<SyncLogMessage> messages) => messages.Select(message => (object)new
    {
        id = message.Id.ToString(), severity = message.Severity.ToString(), arguments = message.StringArguments?.Cast<string>().ToArray() ?? []
    }).ToArray();
    private static object DataError(Exception exception) => new { code = exception is BridgeException bridge ? bridge.Code : "sdk_data_error", message = exception.Message };
    private sealed record VirtualTaskContext(Mechanism Robot, RsTask Task, VirtualController Controller);
    private sealed record DataSymbol(string Module, string Name, string Kind, RsDataDeclaration? StationData)
    {
        public string Id => Module + "/" + Name;
    }
    private sealed record DataSyncPlan(DataSelection Selection, DataSymbol Symbol, JsonElement? StationValue, JsonElement? ControllerValue);
}
