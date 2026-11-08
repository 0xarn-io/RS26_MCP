using System.Text.Json;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static object PathDetails(Station station, string? robotName, string? pathName)
    {
        EnsureCurrent(station);
        var tasks = robotName is null ? Tasks(station) : [RequireTask(SelectRobot(station, robotName))];
        var paths = tasks.SelectMany(task => task.PathProcedures.Cast<RsPathProcedure>()
            .Select(path => (Task: task, Path: path)))
            .Where(item => pathName is null || item.Path.Name == pathName || item.Path.ModuleName + "/" + item.Path.Name == pathName)
            .ToArray();
        if (pathName is not null && paths.Length != 1)
            throw Error("path_selection", paths.Length == 0
                ? "No matching station path. Synchronize the path into the station first."
                : "The path name is ambiguous; specify robot and Module/name.");

        return new
        {
            paths = paths.Select(item => new
            {
                name = item.Path.Name, module = item.Path.ModuleName,
                qualifiedName = item.Path.ModuleName + "/" + item.Path.Name, task = item.Task.Name,
                instructions = item.Path.Instructions.Cast<RsInstruction>().Select((instruction, index) => new
                {
                    index, name = instruction.Name, type = instruction.GetType().Name,
                    arguments = instruction.InstructionArguments.Cast<RsInstructionArgument>().Select(InstructionArgument).ToArray(),
                    motion = instruction is RsMoveInstruction move ? MoveInstructionDetails(move) : null
                }).ToArray()
            }).ToArray(),
            scope = "station_only", controllerRead = false,
            limitations = "Station path instructions only. Argument expressions are returned verbatim; unresolved expressions are not evaluated and cached reachability is not a new kinematic check."
        };
    }

    private static object? InstructionArgument(RsInstructionArgument? argument) => argument is null ? null : new
    {
        name = argument.Name, value = argument.Value, enabled = argument.Enabled
    };

    private static object MoveInstructionDetails(RsMoveInstruction instruction) => new
    {
        motionType = ReadStationValue(() => (instruction.GetInstructionDescription() as RsMoveInstructionDescription)?.MotionType.ToString()),
        processDefinition = instruction.ProcessDefinitionName, processTemplate = instruction.ProcessTemplateName,
        cachedReachability = instruction.Reachable.ToString(),
        target = ReadStationValue(() => InstructionArgument(instruction.GetToPointArgument())),
        jointTarget = ReadStationValue(() => InstructionArgument(instruction.GetToJointPosArgument())),
        circularPoint = ReadStationValue(() => InstructionArgument(instruction.GetCirPointArgument())),
        viaPoint = ReadStationValue(() => InstructionArgument(instruction.GetViaPointArgument())),
        tool = ReadStationValue(() => InstructionArgument(instruction.GetToolArgument())),
        workObject = ReadStationValue(() => InstructionArgument(instruction.GetWObjArgument())),
        speed = ReadStationValue(() => instruction.GetArgumentsByDataType("speeddata").Select(InstructionArgument).ToArray()),
        zone = ReadStationValue(() => instruction.GetArgumentsByDataType("zonedata").Select(InstructionArgument).ToArray()),
        resolved = new
        {
            target = ReadStationValue(() => DescribeInstructionTarget(instruction.GetToRobTarget())),
            circularPoint = ReadStationValue(() => DescribeInstructionTarget(instruction.GetCirRobTarget())),
            viaPoint = ReadStationValue(() => DescribeInstructionTarget(instruction.GetViaRobTarget())),
            jointTarget = ReadStationValue(() => DescribeInstructionJointTarget(instruction.GetToJointTarget())),
            tool = ReadStationValue(() => DescribeInstructionData(instruction.GetToolData())),
            workObject = ReadStationValue(() => DescribeInstructionData(instruction.GetWorkObject()))
        }
    };

    // Missing/inline/expression arguments must not hide the remaining instruction details.
    private static object ReadStationValue(Func<object?> read)
    {
        try
        {
            var value = read();
            return new { resolved = value is not null, value };
        }
        catch (Exception exception)
        {
            return new { resolved = false, error = new BridgeError("unresolved_station_argument", exception.Message) };
        }
    }

    private static object? DescribeInstructionData(RsDataDeclaration? data) => data is null ? null : new
    {
        name = data.Name, module = data.ModuleName, qualifiedName = Qualified(data)
    };

    private static object? DescribeInstructionTarget(RsRobTarget? target) => target is null ? null : new
    {
        name = target.Name, module = target.ModuleName, qualifiedName = Qualified(target),
        pose = Frame(target.Frame.Matrix), configuration = target.ConfigurationData.GetConfigurationData(),
        externalAxes = target.GetExternalAxes(false).GetAxisValues()
    };

    private static object? DescribeInstructionJointTarget(RsJointTarget? target) => target is null ? null : new
    {
        name = target.Name, module = target.ModuleName, qualifiedName = Qualified(target),
        robotAxes = target.GetRobotAxes(false).GetAxisValues(),
        externalAxes = target.GetExternalAxes(false).GetAxisValues(), jointUnits = "degrees"
    };

    private static async Task<object> ValidateTargetsAsync(Station station,
        IReadOnlyList<(string Id, BridgeRequest Request)> targets, bool checkCollisions)
    {
        EnsureCurrent(station);
        if (checkCollisions) RequireStopped();

        // Resolve all inputs before yielding so editing a later target while an earlier
        // IK call is pending cannot silently turn this into a mixed-model batch.
        var prepared = targets.Select(item => PrepareBatchTarget(station, item.Id, item.Request)).ToArray();
        var results = new List<object>(prepared.Length);
        BridgeError? abortedError = null;
        var reachableCount = 0;
        var collisionChecks = 0;
        foreach (var item in prepared)
        {
            try
            {
                EnsureBatchUnchanged(station, prepared);
                if (checkCollisions) RequireStopped();
                if (item.Error is not null) throw item.Error;

                object reach;
                bool reachable;
                double[]? joints;
                if (item.Context is not null)
                {
                    var solved = await SolveAsync(station, item.Context);
                    EnsureBatchUnchanged(station, prepared);
                    reachable = solved.Reachable;
                    joints = solved.Joints;
                    reach = ReachResponse(item.Context, solved);
                }
                else
                {
                    joints = item.Joints!;
                    reachable = item.Robot!.InsideLimits(joints);
                    reach = new
                    {
                        robot = item.Robot.Name, reachable,
                        joints = RapidAxes(item.Robot, joints), jointUnits = "degrees",
                        activeJointIndices = item.Robot.GetActiveJoints(), targetType = "jointtarget",
                        reason = reachable ? null : "joint_limit",
                        reasonDetail = reachable ? null : "The requested jointtarget exceeds the station mechanism limits.",
                        scope = "station_only", collisionChecked = false, trajectoryChecked = false
                    };
                }

                object? collisions = null;
                if (reachable && checkCollisions)
                {
                    collisions = CheckBatchCollisionPose(station, item.Robot!, joints!);
                    EnsureBatchUnchanged(station, prepared);
                    collisionChecks++;
                }
                if (reachable) reachableCount++;
                results.Add(new { id = item.Id, ok = true, data = new { reach, collisions } });
            }
            catch (Exception exception)
            {
                var error = exception as BridgeException ?? Error(exception is OperationCanceledException ? "sdk_canceled" : "sdk_error", exception.Message);
                var responseError = new BridgeError(error.Code, error.Message);
                results.Add(new { id = item.Id, ok = false, error = responseError });
                if (error.Code is "station_changed" or "model_changed" or "restore_failed" or "simulation_active" or "sdk_canceled")
                {
                    abortedError = responseError;
                    break;
                }
            }
        }
        return new
        {
            requested = targets.Count, completed = results.Count, reachable = reachableCount, collisionChecks,
            aborted = abortedError is not null, error = abortedError, results,
            skippedIds = prepared.Skip(results.Count).Select(item => item.Id).ToArray(),
            scope = "station_only", controllerUpdated = false, trajectoryChecked = false,
            limitations = "Independent target poses only; no path interpolation or swept collision check. Collision probes restore the starting station pose and use only active configured collision sets and obstacle geometry at the time of each probe."
        };
    }

    private static PreparedBatchTarget PrepareBatchTarget(Station station, string id, BridgeRequest request)
    {
        try
        {
            if (request.JointTarget is null)
            {
                var context = ResolveTarget(station, request);
                return new(id, context.Robot, context, null, CaptureKinematicState(context), null);
            }
            var robot = SelectRobot(station, request.Robot);
            ValidateRobot(robot);
            RejectExternalAxes(request.JointTarget.ExternalAxes);
            var joints = JointMapping.FromRapidAxes(request.JointTarget.RobotAxes, robot.GetActiveJoints());
            return new(id, robot, null, joints, CaptureBatchRobotState(robot), null);
        }
        catch (Exception exception)
        {
            return new(id, null, null, null, null, exception as BridgeException ?? Error("sdk_error", exception.Message));
        }
    }

    private static void EnsureBatchUnchanged(Station station, PreparedBatchTarget[] prepared)
    {
        EnsureCurrent(station);
        foreach (var item in prepared)
        {
            if (item.Error is not null) continue;
            if (item.Context is not null) EnsureUnchanged(station, item.Context, item.Snapshot!);
            else if (!ReferenceEquals(item.Robot!.ContainingProject, station) || CaptureBatchRobotState(item.Robot) != item.Snapshot)
                throw Error("model_changed", "A batch robot changed while validation was pending; retry against the current station state.");
        }
    }

    private static string CaptureBatchRobotState(Mechanism robot)
    {
        robot.GetJointLimits(out var minimum, out var maximum);
        return JsonSerializer.Serialize(new
        {
            robot.Name, robot.ModelName, robot.NumActiveJoints, robotFrame = robot.Transform.GlobalMatrix.Elements,
            taskId = robot.Task?.UniqueId, taskFrame = robot.Task?.Frame.Elements,
            joints = robot.GetJointValues(), minimum, maximum, activeJointIndices = robot.GetActiveJoints(),
            jointTypes = robot.GetJointTypes()
        });
    }

    private static object CheckBatchCollisionPose(Station station, Mechanism robot, double[] joints)
    {
        EnsureCurrent(station);
        RequireStopped();
        var old = robot.GetJointValues();
        Project.UndoContext.BeginUndoStep("Offline bridge: temporary batch collision pose");
        try
        {
            // Reuse the computed IK solution. This synchronous API never updates a VC.
            if (!robot.SetJointValues(joints)) throw Error("joint_limit", "RobotStudio rejected the collision-test pose.");
            Station.UpdateGraphics();
            CollisionDetector.CheckCollisions(station);
            return CollisionState(station, true);
        }
        finally
        {
            var restored = false;
            Exception? restoreException = null;
            try { restored = robot.SetJointValues(old); }
            catch (Exception exception) { restoreException = exception; }
            finally
            {
                try { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); }
                catch (Exception exception) { restoreException ??= exception; }
                finally
                {
                    try { Project.UndoContext.EndUndoStep(); }
                    catch (Exception exception) { restoreException ??= exception; }
                }
            }
            if (!restored || restoreException is not null)
                throw Error("restore_failed", "The original station pose could not be confirmed restored; inspect the station before continuing. " + restoreException?.Message);
            Station.UpdateGraphics();
            CollisionDetector.CheckCollisions(station);
        }
    }

    private sealed record PreparedBatchTarget(string Id, Mechanism? Robot, TargetContext? Context,
        double[]? Joints, string? Snapshot, BridgeException? Error);
}
