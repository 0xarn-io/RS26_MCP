using System.Text.Json;
using System.IO;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using ABB.Robotics.RobotStudio.Stations.Forms;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

/// <summary>Station development API with explicitly selected mapped-VC data operations.</summary>
public sealed partial class StationApi
{
    // The caller dispatches the entire asynchronous request to RobotStudio's main thread.
    public async Task<object> ExecuteAsync(string operation, JsonElement arguments)
    {
        RequireMainThread();
        var request = RequestParser.Parse(operation, arguments);
        if (request.Engineering is SafeMoveRequest safe)
        {
            // Read and validate exported files off the RobotStudio UI thread.
            return await Task.Run(() => safe.Edit is { } edit
                ? (object)SafeMoveInspection.EditZoneCopy(safe.File, edit.OutputName, edit.DriveModuleId,
                    edit.ZoneId, edit.BottomM, edit.TopM, edit.PointsM)
                : SafeMoveInspection.Read(safe.File, safe.Offset, safe.Limit));
        }
        if (operation == "run_review") return await CaptureStore.ReviewAsync((RunReviewRequest)RunRequests.Parse(operation, arguments));
        var station = Station.ActiveStation ?? throw Error("no_station", "Open a station in RobotStudio first.");
        if (Station.IsLoading) throw Error("station_loading", "The station is still loading.");
        if (request.BodyEdit is {} body) return BodyEdit(station, body);
        if (request.Motion is {} motion) return BodyMotion(station, motion);
        if (request.Smart is {} smart) return await SmartComponentsAsync(station, smart);
        if (request.Logic is {} logic) return StationLogic(station, logic);
        if (request.Engineering is IoProjectRequest ioProject) return IoProject(station, ioProject);
        if (request.Engineering is ConfigurationRequest parameters) return Configuration(station, parameters);
        if (request.Engineering is SimulationSetupRequest setup) return SimulationSetup(station, setup);
        if (RapidModuleRequests.Operations.Contains(operation)) return await ExecuteRapidModulesAsync(station, operation, arguments);
        if (SceneRequests.Operations.Contains(operation))
        {
            return SceneRequests.Parse(operation, arguments) switch
            {
                GeometryQuery query => GeometryInventory(station, query),
                RayQuery ray => Raycast(station, ray),
                SimulationSpeedRequest speed => SimulationSpeed(speed),
                SetCollisionRequest set => CreateCollisionSet(station, set),
                RunViewRequest view => RunView(station, view),
                _ => throw Error("unknown_operation", "Unknown scene operation.")
            };
        }
        if (operation == "tcp_record") return await TcpRecordAsync(station, (TcpRecordRequest)RunRequests.Parse(operation, arguments));

        if (GeometryRequests.Operations.Contains(operation))
        {
            return GeometryRequests.Parse(operation, arguments) switch
            {
                GeometryCreateRequest create => CreateGeometry(station, create),
                GeometryImportRequest import => await ImportGeometryAsync(station, import),
                _ => throw Error("unknown_operation", "Unknown geometry operation.")
            };
        }
        if (IoRequests.Operations.Contains(operation))
        {
            return IoRequests.Parse(operation, arguments) switch
            {
                IoReadRequest read => IoRead(station, read),
                IoWriteRequest write => IoWrite(station, write),
                IoPulseRequest pulse => await IoPulseAsync(station, pulse),
                _ => throw Error("unknown_operation", "Unknown I/O operation.")
            };
        }
        if (DevelopmentRequests.Operations.Contains(operation))
        {
            return DevelopmentRequests.Parse(operation, arguments) switch
            {
                PathDetailsRequest detail => PathDetails(station, detail.Robot, detail.Path),
                ValidateTargetsRequest batch => await ValidateTargetsAsync(station, batch.Targets.Select(t => (t.Id, t.Request)).ToArray(), batch.CheckCollisions),
                PreviewTargetsRequest preview => PreviewTargets(station, preview),
                EditTargetsRequest edits => EditTargets(station, edits),
                DataCompareRequest compare => await CompareDataAsync(station, compare),
                DataSyncRequest sync => await SyncDataAsync(station, sync),
                _ => throw Error("unknown_operation", "Unknown development operation.")
            };
        }
        return request.Operation switch
        {
            "station" => Describe(station),
            "robot_pose" => Pose(SelectRobot(station, request.Robot), request),
            "reach" => await ReachAsync(station, request),
            "jump" => await JumpAsync(station, request),
            "collisions" => await CollisionsAsync(station, request),
            "simulation" => await SimulationAsync(station, request.Action ?? "state"),
            "screenshot" => Screenshot(station),
            _ => throw Error("unknown_operation", "Unknown station operation.")
        };
    }

    private static object Screenshot(Station station)
    {
        var view = GraphicControl.ActiveGraphicControl;
        if (view is null || view.IsDisposed || !ReferenceEquals(view.RootObject, station))
            throw Error("no_station_view", "Activate the open station's graphics view before requesting a screenshot.");
        var size = view.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
            throw Error("no_station_view", "The active station graphics view has no drawable area.");
        var scale = System.Math.Min(1.0, System.Math.Min(1280.0 / size.Width, 720.0 / size.Height));
        var width = System.Math.Max(1, (int)(size.Width * scale));
        var height = System.Math.Max(1, (int)(size.Height * scale));
        using var bitmap = view.ScreenShot(width, height);
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        return new { mimeType = "image/png", data = Convert.ToBase64String(buffer.ToArray()), width, height, scope = "station_only" };
    }

    private static object Describe(Station station)
    {
        var robots = Robots(station);
        var tasks = Tasks(station);
        return new
        {
            name = station.Name,
            file = station.FileInfo?.FullName,
            scope = "station_only",
            units = new { position = "mm", robotAxes = "degrees", quaternion = "wxyz" },
            robots = robots.Select(r => new
            {
                name = r.Name, mechanism = r.Name, model = r.ModelName,
                task = r.Task?.Name, activeJoints = r.NumActiveJoints,
                activeJointIndices = r.GetActiveJoints(),
                taskFrameWorld = r.Task is null ? null : Frame(r.Task.Frame),
                mechanismFrameWorld = Frame(r.Transform.GlobalMatrix),
                jointTypes = r.GetJointTypes().Select(t => t.ToString()).ToArray()
            }).ToArray(),
            virtualControllers = station.VirtualControllers.Cast<VirtualController>().Select(c => new
            {
                name = c.Name, state = c.SystemState.ToString(), mapping = c.MappingState.ToString()
            }).ToArray(),
            tools = tasks.SelectMany(t => Data<RsToolData>(t).Select(d => new
            {
                name = d.Name, module = d.ModuleName, task = t.Name,
                robotHold = d.RobotHold, frame = Frame(d.Frame.Matrix)
            })).ToArray(),
            workObjects = tasks.SelectMany(t => Data<RsWorkObject>(t).Select(d => new
            {
                name = d.Name, module = d.ModuleName, task = t.Name, robotHold = d.RobotHold,
                userFrameProgrammed = d.UserFrameProgrammed, mechanicalUnit = d.UserFrameMechanicalUnit,
                userFrame = Frame(d.UserFrame.Matrix), objectFrame = Frame(d.ObjectFrame.Matrix),
                objectFrameWorld = Frame(d.ObjectFrame.GlobalMatrix)
            })).ToArray(),
            targets = tasks.SelectMany(t => t.Targets.Cast<RsTarget>().Select(d => new
            {
                name = d.Name, robtarget = d.RobTarget.Name, module = d.RobTarget.ModuleName,
                task = t.Name, workObject = d.WorkObject.Name, pose = Frame(d.RobTarget.Frame.Matrix),
                configuration = d.RobTarget.ConfigurationData.GetConfigurationData(),
                externalAxes = d.RobTarget.GetExternalAxes(false).GetAxisValues()
            })).ToArray(),
            targetDeclarations = tasks.SelectMany(task => Data<RsRobTarget>(task).Select(data =>
            {
                var bindings = task.FindTargetsByRobTarget(data);
                return new
                {
                    name = data.Name, module = data.ModuleName, task = task.Name, qualifiedName = Qualified(data),
                    pose = Frame(data.Frame.Matrix), poseFrame = "workObject_local",
                    configuration = data.ConfigurationData.GetConfigurationData(),
                    externalAxes = data.GetExternalAxes(false).GetAxisValues(),
                    graphicalTargets = bindings.Select(target => new
                    {
                        name = target.Name, workObject = Qualified(target.WorkObject)
                    }).ToArray(),
                    requiresExplicitWorkObject = bindings.Length == 0
                };
            })).ToArray(),
            paths = tasks.SelectMany(t => t.PathProcedures.Cast<RsPathProcedure>().Select(p => new
            {
                name = p.Name, module = p.ModuleName, task = t.Name,
                instructions = p.Instructions.Cast<RsInstruction>().Select(i => new { type = i.GetType().Name, name = i.Name }).ToArray()
            })).ToArray(),
            limitations = new[]
            {
                "Pose operations support one to six active rotational axes, a robot-held tool and a stationary programmed work object. Six-entry joint arrays use RAPID axis order; inactive axes are reported as zero.",
                "Reach is a static kinematic check, not a collision, trajectory, singularity or real-robot safety approval.",
                "Station robtarget declarations are listed separately from graphical targets. Their poses are raw work-object-local coordinates: use a graphical binding, or explicitly supply workObject when no graphical binding exists. No world frame is inferred.",
                "Controller-only declarations are not available to pose operations until synchronized into the station."
            }
        };
    }

    private static object Pose(Mechanism robot, BridgeRequest request)
    {
        var task = RequireTask(robot);
        var tool = Select(Data<RsToolData>(task), request.Tool, "tool");
        var workObject = Select(Data<RsWorkObject>(task), request.WorkObject, "work object");
        ValidateFrames(robot, tool, workObject);
        // The SDK defines Flange.GlobalMatrix in world coordinates and the tool's
        // local Frame.Matrix in wrist coordinates. Use the requested definition,
        // independent of RobotStudio's active tool or attached graphical tool.
        var flanges = robot.GetFlanges();
        if (flanges.Length != 1) throw Error("unsupported_flange", "A single robot wrist flange is required to calculate the requested TCP.");
        var worldTcp = FrameMath.Compose(ToFrame(flanges[0].GlobalMatrix), ToFrame(tool.Frame.Matrix));
        var relative = FrameMath.RelativeTo(ToFrame(workObject.ObjectFrame.GlobalMatrix), worldTcp);
        return new
        {
            robot = robot.Name, model = robot.ModelName, task = task.Name,
            tool = tool.Name, workObject = workObject.Name,
            joints = RapidAxes(robot, robot.GetJointValues()), jointUnits = "degrees",
            activeJointIndices = robot.GetActiveJoints(),
            tcp = Frame(relative), tcpFrame = "workObject",
            worldTcp = Frame(worldTcp), scope = "station_only",
            taskFrameWorld = Frame(task.Frame), workObjectFrameWorld = Frame(workObject.ObjectFrame.GlobalMatrix),
            flangeFrameWorld = Frame(flanges[0].GlobalMatrix), toolFrameInFlange = Frame(tool.Frame.Matrix)
        };
    }

    private static async Task<object> ReachAsync(Station station, BridgeRequest request)
    {
        var context = ResolveTarget(station, request);
        var result = await SolveAsync(station, context);
        return ReachResponse(context, result);
    }

    private static object ReachResponse(TargetContext context, SolveResult result) => new
    {
        robot = context.Robot.Name, tool = context.Tool.Name, workObject = context.Target.WorkObject.Name,
        target = context.Target.Name, reachable = result.Reachable,
        targetSource = context.NamedSource switch
        {
            TargetSource.Graphical => "graphical_target",
            TargetSource.Declaration => "station_declaration",
            _ => "request_pose"
        },
        joints = result.Joints is null ? null : RapidAxes(context.Robot, result.Joints), jointUnits = "degrees",
        activeJointIndices = context.Robot.GetActiveJoints(),
        requestedConfiguration = context.Target.RobTarget.ConfigurationData.GetConfigurationData(),
        targetPose = Frame(context.Target.RobTarget.Frame.Matrix),
        targetWorldPose = Frame(FrameMath.Compose(ToFrame(context.Target.WorkObject.ObjectFrame.GlobalMatrix), ToFrame(context.Target.RobTarget.Frame.Matrix))),
        taskFrameWorld = Frame(context.Task.Frame),
        workObjectFrameWorld = Frame(context.Target.WorkObject.ObjectFrame.GlobalMatrix),
        reason = result.Reason, reasonDetail = result.Detail,
        possibleCauses = result.PossibleCauses,
        alternativeJoints = result.AlternativeJoints is null ? null : RapidAxes(context.Robot, result.AlternativeJoints),
        scope = "station_only", collisionChecked = false, trajectoryChecked = false
    };

    private static async Task<SolveResult> SolveAsync(Station station, TargetContext context)
    {
        EnsureCurrent(station);
        var modelState = CaptureKinematicState(context);
        // This SDK overload respects the robtarget's cf1/cf4/cf6/cfx, tool and work object.
        // Never jump the robot to answer reachability.
        var exact = await context.Robot.CalculateInverseKinematicsAsync(context.Target, context.Tool, true);
        EnsureUnchanged(station, context, modelState);
        if (exact is not null)
        {
            ValidateSolution(context.Robot, exact);
            if (!context.Robot.InsideLimits(exact))
                return new(false, exact, "joint_limit", "The SDK joint solution exceeds the station mechanism's joint limits.", ["joint_limit"], null);
            return new(true, exact, null, null, [], null);
        }

        var alternative = await context.Robot.CalculateInverseKinematicsAsync(context.Target, context.Tool, false);
        EnsureUnchanged(station, context, modelState);
        if (alternative is not null)
        {
            ValidateSolution(context.Robot, alternative);
            if (!context.Robot.InsideLimits(alternative))
                return new(false, null, "joint_limit", "The returned alternative exceeds the mechanism's joint limits.", ["joint_limit"], alternative);
            return new(false, null, "configuration", "The requested configuration has no solution; another configuration is reachable.", ["configuration"], alternative);
        }

        // The public SDK returns null for both workspace and joint-limited failures.
        // Do not invent a more precise diagnosis or relax the owner's mechanism limits.
        return new(false, null, "out_of_reach_or_joint_limit",
            "RobotStudio returned no inverse-kinematic solution, including with configuration relaxed. Its public API does not distinguish geometric reach from joint-limit rejection here.",
            ["out_of_reach", "joint_limit"], null);
    }

    private static async Task<object> JumpAsync(Station station, BridgeRequest request)
    {
        RequireStopped();
        var robot = SelectRobot(station, request.Robot);
        var joints = await ResolveJointsAsync(station, robot, request);
        EnsureCurrent(station);
        RequireStopped();
        if (request.ApplyTo == "virtual")
        {
            var context = OwnedVirtualTask(station, request.Robot);
            using var connection = OpenOwnedVirtualConnection(station, context);
            RequireVirtualRapidStopped(connection);
            if (!await robot.SetJointValuesAsync(joints, updateController: true))
                throw Error("jump_refused", "RobotStudio refused the virtual mechanical-unit jump.");
            EnsureOwnedVirtualContext(station, context, connection);
            Station.UpdateGraphics();
            return new { robot = robot.Name, joints = RapidAxes(robot, robot.GetJointValues()), jointUnits = "degrees",
                scope = "station_virtual", controllerUpdated = true };
        }
        var old = robot.GetJointValues();
        Project.UndoContext.BeginUndoStep("Offline bridge: jump station robot");
        try
        {
            // Synchronous SetJointValues explicitly never updates a VC (ABB SDK contract).
            if (!robot.SetJointValues(joints)) throw Error("joint_limit", "RobotStudio rejected the requested joints.");
            Station.UpdateGraphics();
        }
        catch
        {
            try { robot.SetJointValues(old); }
            finally { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); }
            throw;
        }
        finally { Project.UndoContext.EndUndoStep(); }
        return new { robot = robot.Name, joints = RapidAxes(robot, robot.GetJointValues()), jointUnits = "degrees", activeJointIndices = robot.GetActiveJoints(), scope = "station_only", controllerUpdated = false };
    }

    private static async Task<object> CollisionsAsync(Station station, BridgeRequest request)
    {
        var hasPose = request.Target is not null || request.RobTarget is not null || request.JointTarget is not null;
        if (!hasPose)
        {
            CollisionDetector.CheckCollisions(station);
            return CollisionState(station, false);
        }
        RequireStopped();
        var robot = SelectRobot(station, request.Robot);
        var joints = await ResolveJointsAsync(station, robot, request);
        EnsureCurrent(station);
        RequireStopped();
        var old = robot.GetJointValues();
        Project.UndoContext.BeginUndoStep("Offline bridge: temporary collision pose");
        try
        {
            if (!robot.SetJointValues(joints)) throw Error("joint_limit", "RobotStudio rejected the collision-test pose.");
            Station.UpdateGraphics();
            CollisionDetector.CheckCollisions(station);
            return CollisionState(station, true);
        }
        finally
        {
            var restored = false;
            try { restored = robot.SetJointValues(old); }
            finally
            {
                try { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); }
                finally { Project.UndoContext.EndUndoStep(); }
            }
            Station.UpdateGraphics();
            CollisionDetector.CheckCollisions(station);
            if (!restored) throw Error("restore_failed", "RobotStudio could not restore the original station pose; inspect the station before continuing.");
        }
    }

    private static object CollisionState(Station station, bool poseChecked)
    {
        var sets = station.CollisionSets.Cast<CollisionSet>().Select(set => new
        {
            id = set.UniqueId, name = set.Name, active = set.Active,
            state = set.GetCurrentState().ToString(), checkedNow = set.Active,
            nearMissDistanceMm = set.NearMissDistance * 1000.0,
            firstGroup = set.FirstGroup.Cast<ProjectObject>().Select(x => x.Name).ToArray(),
            secondGroup = set.SecondGroup.Cast<ProjectObject>().Select(x => x.Name).ToArray()
        }).ToArray();
        return new
        {
            sets, poseChecked, poseRestored = poseChecked, scope = "station_only",
            approximate = true, boundingBoxesOnly = CollisionDetector.FastCheck,
            coverage = "Only active collision sets and their configured geometry are checked; this is a single pose, not swept motion.",
            anyCollision = sets.Any(s => s.active && s.state == nameof(CollisionType.Collision)),
            anyNearMiss = sets.Any(s => s.active && s.state == nameof(CollisionType.NearMiss))
        };
    }

    private async Task<object> SimulationAsync(Station station, string action)
    {
        var config = Simulator.ActiveConfiguration;
        if (action != "state")
        {
            if (config is null || !ReferenceEquals(config.ContainingProject, station))
                throw Error("simulation_configuration", "Select a simulation configuration belonging to the open station.");
            var controllers = station.VirtualControllers.Cast<VirtualController>().ToHashSet();
            // ControllerConfigurations is indexed by VirtualController, not enumerable.
            // SimulatedObjects is the public list of participating station objects.
            if (config.SimulatedObjects.Any(o => !ReferenceEquals(o.ContainingProject, station) ||
                (o is VirtualController controller && !controllers.Contains(controller))))
                throw Error("real_controller_permission_required", "Simulation requires controllers owned by the open station. Accessing a real controller requires explicit owner permission.");
        }

        switch (action)
        {
            case "state": break;
            case "start":
                if (Simulator.State == SimulationState.Paused) await Simulator.ResumeAsync();
                else if (Simulator.State != SimulationState.Running) await Simulator.StartAsync();
                break;
            case "stop":
                await Simulator.StopAsync();
                StopBodyMotionsForShutdown();
                break;
            case "reset":
                // Restore the owner's selected saved simulation state. Never restart RS or a VC.
                var initial = config!.InitialState;
                var saved = station.SavedStates.Cast<SavedState>().SingleOrDefault(s => s.Name == initial);
                if (saved is null)
                    throw Error("simulation_initial_state", "The selected simulation configuration must name a saved initial state before reset.");
                await Simulator.StopAsync();
                StopBodyMotionsForShutdown();
                EnsureCurrent(station);
                if (!saved.CanRestore())
                    throw Error("simulation_initial_state", "Simulation stopped, but its selected initial state cannot currently be restored.");
                await saved.RestoreAsync();
                break;
            default: throw Error("invalid_action", "Use state, start, stop or reset.");
        }
        EnsureCurrent(station);
        return new
        {
            state = Simulator.State.ToString(), timeSeconds = Simulator.CurrentTime / 1000.0,
            configuration = config?.Name, initialState = config?.InitialState,
            action, scope = "station_virtual_simulation",
            resetSemantics = "Stop simulation and restore the selected saved initial state; never restart RobotStudio or controllers."
        };
    }

    private static async Task<double[]> ResolveJointsAsync(Station station, Mechanism robot, BridgeRequest request)
    {
        ValidateRobot(robot);
        if (request.JointTarget is not null)
        {
            RejectExternalAxes(request.JointTarget.ExternalAxes);
            var values = JointMapping.FromRapidAxes(request.JointTarget.RobotAxes, robot.GetActiveJoints());
            if (!robot.InsideLimits(values)) throw Error("joint_limit", "The requested jointtarget exceeds the station mechanism limits.");
            return values;
        }
        var context = ResolveTarget(station, request);
        var solved = await SolveAsync(station, context);
        if (!solved.Reachable) throw Error(solved.Reason!, solved.Detail!);
        return solved.Joints!;
    }

    private static TargetContext ResolveTarget(Station station, BridgeRequest request)
    {
        var robot = SelectRobot(station, request.Robot);
        var task = RequireTask(robot);
        if (!station.VirtualControllers.Cast<VirtualController>().Any(c => c.Tasks.Cast<RsTask>().Contains(task)))
            throw Error("virtual_controller_required", "Inverse kinematics requires a station task owned by one of this station's virtual controllers. Real-controller access requires explicit owner permission.");
        var tool = Select(Data<RsToolData>(task), request.Tool, "tool");
        RsTarget target;
        TargetSource? namedSource = null;
        RsRobTarget? stationDeclaration = null;
        if (request.Target is not null)
        {
            var selected = ResolveNamedStationTarget(task, request.Target, request.WorkObject);
            target = selected.Target;
            namedSource = selected.Source;
            stationDeclaration = selected.StationDeclaration;
            RejectExternalAxes(target.RobTarget.GetExternalAxes(false).GetAxisValues());
        }
        else
        {
            var input = request.RobTarget ?? throw Error("missing_target", "Provide a robtarget or a named station target.");
            RejectExternalAxes(input.ExternalAxes);
            var workObject = Select(Data<RsWorkObject>(task), request.WorkObject, "work object");
            var data = new RsRobTarget();
            data.Frame.Matrix = new Matrix4(new Vector3(input.Position[0] / 1000.0, input.Position[1] / 1000.0, input.Position[2] / 1000.0),
                new Quaternion(input.Quaternion[0], input.Quaternion[1], input.Quaternion[2], input.Quaternion[3]));
            data.SetConfiguration(input.Configuration[0], input.Configuration[1], input.Configuration[2], input.Configuration[3]);
            data.SetExternalAxes(new ExternalAxisValues { Eax_a = 9E9, Eax_b = 9E9, Eax_c = 9E9, Eax_d = 9E9, Eax_e = 9E9, Eax_f = 9E9 }, false);
            // Detached request objects are not added to DataDeclarations or Targets.
            target = new RsTarget(workObject, data);
        }
        ValidateFrames(robot, tool, target.WorkObject);
        return new(robot, task, tool, target, target.RobTarget, namedSource, stationDeclaration);
    }

    private static ResolvedNamedTarget ResolveNamedStationTarget(RsTask task, string name, string? workObjectName)
    {
        var graphical = task.Targets.Cast<RsTarget>().ToArray();
        var declarations = Data<RsRobTarget>(task);
        var selection = NamedTargetSelector.Select(name, workObjectName,
            graphical.Select(target => new NamedTargetCandidate(target.RobTarget.Name, Qualified(target.RobTarget),
                target.WorkObject.Name, Qualified(target.WorkObject), target.Name)).ToArray(),
            declarations.Select(data => new NamedTargetCandidate(data.Name, Qualified(data))).ToArray());
        if (selection.Source == TargetSource.Graphical)
            return new(graphical[selection.Index], selection.Source, null);

        // A synchronized robtarget declaration has no work-object association until
        // an RsTarget binds it. Require an explicit frame and create only a detached
        // request wrapper: never insert it into the station's Targets collection.
        // RsTarget's constructor registers a backreference and changes the supplied
        // robtarget's ConfigurationStatus, even when the target is detached. Give it
        // a fresh numeric copy so a read cannot mutate or retain the station source.
        var declaration = declarations[selection.Index];
        var workObject = Select(Data<RsWorkObject>(task), workObjectName, "work object");
        var copy = new RsRobTarget { Name = declaration.Name, ModuleName = declaration.ModuleName };
        copy.Frame.Matrix = declaration.Frame.Matrix;
        var configuration = declaration.ConfigurationData.GetConfigurationData();
        copy.SetConfiguration(configuration[0], configuration[1], configuration[2], configuration[3]);
        var externalAxes = declaration.GetExternalAxes(false).GetAxisValues();
        copy.SetExternalAxes(new ExternalAxisValues
        {
            Eax_a = externalAxes[0], Eax_b = externalAxes[1], Eax_c = externalAxes[2],
            Eax_d = externalAxes[3], Eax_e = externalAxes[4], Eax_f = externalAxes[5]
        }, false);
        var detached = new RsTarget(workObject, copy) { Name = declaration.Name };
        return new(detached, selection.Source, declaration);
    }

    private static Mechanism[] Robots(Station station) => station.FindGraphicComponentsByType(typeof(Mechanism))
        .OfType<Mechanism>().Where(m => m.MechanismType == MechanismType.Robot).ToArray();

    private static RsTask[] Tasks(Station station) => station.VirtualControllers.Cast<VirtualController>()
        .SelectMany(c => c.Tasks.Cast<RsTask>())
        .Concat(Robots(station).Select(r => r.Task).Where(t => t is not null)!)
        .Append(station.DefaultTask).Where(t => t is not null).Distinct().ToArray();

    private static Mechanism SelectRobot(Station station, string? name)
    {
        var robots = Robots(station).Where(r => name is null || r.Name == name).ToArray();
        if (robots.Length != 1) throw Error("robot_selection", robots.Length == 0 ? "No matching station robot." : "More than one station robot; specify robot by mechanism name.");
        return robots[0];
    }

    private static RsTask RequireTask(Mechanism robot) => robot.Task ?? throw Error("robot_task", "The station robot has no associated station task.");
    private static T[] Data<T>(RsTask task) where T : RsDataDeclaration => task.FindDataDeclarationsByType(typeof(T)).Cast<T>().ToArray();
    private static string Qualified(RsDataDeclaration data) => data.ModuleName + "/" + data.Name;

    private static T Select<T>(IEnumerable<T> source, string? name, string kind) where T : RsDataDeclaration
    {
        if (name is null) throw Error("missing_frame", "Specify the " + kind + " by name.");
        var values = source.Where(d => d.Name == name || Qualified(d) == name).ToArray();
        if (values.Length != 1) throw Error("frame_selection", values.Length == 0 ? "No matching " + kind + " in the robot's station task." : "Ambiguous " + kind + "; use Module/name.");
        return values[0];
    }

    private static void ValidateRobot(Mechanism robot)
    {
        var types = robot.GetJointTypes();
        if (robot.NumActiveJoints is < 1 or > 6 || types.Length != robot.NumActiveJoints || types.Any(t => t != JointType.Rotational))
            throw Error("unsupported_mechanism", "Pose operations require one to six active rotational robot axes; prismatic and larger mechanisms are listed but not approximated.");
        JointMapping.ValidateMap(robot.GetActiveJoints(), robot.NumActiveJoints);
    }

    private static void ValidateFrames(Mechanism robot, RsToolData tool, RsWorkObject workObject)
    {
        ValidateRobot(robot);
        if (!tool.RobotHold || workObject.RobotHold || !workObject.UserFrameProgrammed || !string.IsNullOrEmpty(workObject.UserFrameMechanicalUnit) || RequireTask(robot).MechanismMovingTaskFrame is not null)
            throw Error("unsupported_frame", "Pose operations require a robot-held tool and stationary programmed work object; moving frames are not approximated.");
    }

    private static void RejectExternalAxes(double[]? axes)
    {
        if (axes is not null && axes.Any(x => x != 9E9))
            throw Error("unsupported_external_axes", "Active external axes are not supported by this version. Use 9E9 for each unspecified external axis.");
    }

    private static void ValidateSolution(Mechanism robot, double[] joints)
    {
        if (joints.Length != robot.NumActiveJoints || joints.Any(x => !double.IsFinite(x)))
            throw Error("invalid_sdk_solution", "RobotStudio returned an invalid joint solution.");
    }

    private static void EnsureCurrent(Station station)
    {
        RequireMainThread();
        if (!ReferenceEquals(Station.ActiveStation, station) || Station.IsLoading)
            throw Error("station_changed", "The active station changed while the operation was pending; retry against the current station.");
    }

    private static void RequireMainThread()
    {
        if (System.Threading.Thread.CurrentThread != RobotStudioAPI.MainThread)
            throw new InvalidOperationException("RobotStudio station API calls must execute on its main thread.");
    }

    private static string CaptureKinematicState(TargetContext context)
    {
        context.Robot.GetJointLimits(out var minimum, out var maximum);
        return JsonSerializer.Serialize(new
        {
            robot = context.Robot.Name, context.Robot.ModelName,
            robotFrame = context.Robot.Transform.GlobalMatrix.Elements,
            taskFrame = context.Task.Frame.Elements,
            joints = context.Robot.GetJointValues(), minimum, maximum,
            activeJointIndices = context.Robot.GetActiveJoints(),
            target = context.Target.Name,
            robtargetId = context.Target.RobTarget.UniqueId,
            robtargetName = context.Target.RobTarget.Name,
            robtargetModule = context.Target.RobTarget.ModuleName,
            targetFrame = context.Target.RobTarget.Frame.Matrix.Elements,
            configuration = context.Target.RobTarget.ConfigurationData.GetConfigurationData(),
            externalAxes = context.Target.RobTarget.GetExternalAxes(false).GetAxisValues(),
            stationDeclaration = context.StationDeclaration is null ? null : new
            {
                id = context.StationDeclaration.UniqueId,
                name = context.StationDeclaration.Name,
                module = context.StationDeclaration.ModuleName,
                frame = context.StationDeclaration.Frame.Matrix.Elements,
                configuration = context.StationDeclaration.ConfigurationData.GetConfigurationData(),
                externalAxes = context.StationDeclaration.GetExternalAxes(false).GetAxisValues()
            },
            tool = context.Tool.Name, toolFrame = context.Tool.Frame.Matrix.Elements, context.Tool.RobotHold,
            workObject = context.Target.WorkObject.Name,
            workObjectFrame = context.Target.WorkObject.ObjectFrame.GlobalMatrix.Elements,
            workObjectRobotHold = context.Target.WorkObject.RobotHold,
            context.Target.WorkObject.UserFrameProgrammed, context.Target.WorkObject.UserFrameMechanicalUnit
        });
    }

    private static void EnsureUnchanged(Station station, TargetContext context, string modelState)
    {
        EnsureCurrent(station);
        if (!ReferenceEquals(context.Robot.ContainingProject, station) || !ReferenceEquals(context.Robot.Task, context.Task) ||
            !Data<RsToolData>(context.Task).Contains(context.Tool) ||
            !Data<RsWorkObject>(context.Task).Contains(context.Target.WorkObject) ||
            !ReferenceEquals(context.Target.RobTarget, context.TargetDeclaration) ||
            (context.NamedSource == TargetSource.Graphical && !context.Task.Targets.Contains(context.Target)) ||
            (context.StationDeclaration is not null &&
                (!ReferenceEquals(context.StationDeclaration.ContainingProject, station) ||
                 !Data<RsRobTarget>(context.Task).Any(data => ReferenceEquals(data, context.StationDeclaration)))) ||
            CaptureKinematicState(context) != modelState)
            throw Error("model_changed", "The robot, target, tool or work object changed while inverse kinematics was pending; retry against the current station state.");
    }

    private static void RequireStopped()
    {
        if (Simulator.State != SimulationState.Stopped && Simulator.State != SimulationState.Ready)
            throw Error("simulation_active", "Stop the station simulation before this operation.");
    }

    // ABB GetActiveJoints supplies 1-based Rax_1..Rax_6 positions in the compact
    // mechanism vector. Never infer a robot-model-specific map or pass six
    // values directly to a four-axis mechanism's InsideLimits/SetJointValues.
    private static double[] RapidAxes(Mechanism robot, double[] values) => JointMapping.ToRapidAxes(values, robot.GetActiveJoints());
    private static RigidFrame ToFrame(Matrix4 matrix)
    {
        var q = matrix.Quaternion;
        return new(new(matrix.t.x * 1000.0, matrix.t.y * 1000.0, matrix.t.z * 1000.0), new(q.q1, q.q2, q.q3, q.q4));
    }
    private static object Frame(Matrix4 matrix) => Frame(ToFrame(matrix));
    private static object Frame(RigidFrame frame) => new { position = frame.Position.ToArray(), quaternion = FrameMath.Normalize(frame.Orientation).ToArray(), positionUnits = "mm", quaternionOrder = "wxyz" };

    private static BridgeException Error(string code, string message) => new(code, message, 409);
    private sealed record TargetContext(Mechanism Robot, RsTask Task, RsToolData Tool, RsTarget Target,
        RsRobTarget TargetDeclaration, TargetSource? NamedSource, RsRobTarget? StationDeclaration);
    private sealed record ResolvedNamedTarget(RsTarget Target, TargetSource Source, RsRobTarget? StationDeclaration);
    private sealed record SolveResult(bool Reachable, double[]? Joints, string? Reason, string? Detail, string[] PossibleCauses, double[]? AlternativeJoints);
}
