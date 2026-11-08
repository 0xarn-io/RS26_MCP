using System.Drawing;
using System.Runtime.CompilerServices;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using ABB.Robotics.RobotStudio.Stations.Forms;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    // A weak station key does not keep a closed station alive. Only these handles may be removed.
    private readonly ConditionalWeakTable<Station, List<TemporaryGraphic>> previewGraphics = new();

    private object PreviewTargets(Station station, PreviewTargetsRequest request)
    {
        RequireStopped();
        var owned = previewGraphics.GetValue(station, _ => []);
        if (request.Action == "clear")
        {
            var count = owned.Count;
            RemovePreviewGraphics(station, owned);
            Station.UpdateGraphics();
            return new { action = "clear", removedGraphics = count, scope = "station_only", controllerUpdated = false };
        }

        var robot = SelectRobot(station, request.Robot);
        var task = RequireTask(robot);
        // Resolve every work object and frame before touching the existing preview.
        var points = request.Targets.Select(item => ResolvePreviewPoint(task, item)).ToArray();
        var added = new List<TemporaryGraphic>();
        try
        {
            foreach (var point in points)
                added.Add(station.TemporaryGraphics.DrawFrame(point.WorldMatrix, 0.075, 2.0, point.Id, 14));
            if (points.Length > 1)
                added.Add(station.TemporaryGraphics.DrawLineStrip(points.Select(point => point.WorldMatrix.Translation), 2.0, Color.DeepSkyBlue));
            RemovePreviewGraphics(station, owned);
            owned.AddRange(added);
            Station.UpdateGraphics();
        }
        catch
        {
            foreach (var handle in added) owned.Remove(handle);
            RemovePreviewGraphics(station, added);
            throw;
        }
        return new
        {
            action = "show", robot = robot.Name, count = points.Length,
            points = points.Select(point => new { id = point.Id, workObject = point.WorkObject.Name, worldPose = Frame(point.WorldMatrix) }).ToArray(),
            scope = "station_only", persistent = false, controllerUpdated = false,
            robotMoved = false, reachabilityChecked = false, collisionsChecked = false,
            connectingLines = "Straight visual connections between TCP frames; not a simulated trajectory."
        };
    }

    private static PreviewPoint ResolvePreviewPoint(RsTask task, PreviewTargetRequest item)
    {
        RsWorkObject workObject;
        Matrix4 localMatrix;
        if (item.Target is not null)
        {
            var target = ResolveNamedStationTarget(task, item.Target, item.WorkObject).Target;
            workObject = target.WorkObject;
            RejectExternalAxes(target.RobTarget.GetExternalAxes(false).GetAxisValues());
            localMatrix = target.RobTarget.Frame.Matrix;
        }
        else
        {
            var input = item.RobTarget ?? throw Error("missing_target", "A preview point requires a target or robtarget.");
            workObject = Select(Data<RsWorkObject>(task), item.WorkObject, "work object");
            RejectExternalAxes(input.ExternalAxes);
            localMatrix = TargetMatrix(input);
        }
        RequireStationaryWorkObject(task, workObject);
        var world = FrameMath.Compose(ToFrame(workObject.ObjectFrame.GlobalMatrix), ToFrame(localMatrix));
        return new(item.Id, workObject, MatrixFromFrame(world));
    }

    private static void RemovePreviewGraphics(Station station, List<TemporaryGraphic> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            var handle = handles[index];
            if (station.TemporaryGraphics.Cast<TemporaryGraphic>().Any(candidate => ReferenceEquals(candidate, handle)))
                station.TemporaryGraphics.Remove(handle);
            handles.RemoveAt(index);
        }
    }

    private static object EditTargets(Station station, EditTargetsRequest request)
    {
        RequireStopped();
        var robot = SelectRobot(station, request.Robot);
        var task = RequireTask(robot);
        var editedDeclarations = new HashSet<RsRobTarget>();
        var createdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<TargetEditPlan>();
        foreach (var change in request.Changes)
        {
            RejectExternalAxes(change.RobTarget.ExternalAxes);
            RsTarget? target = null;
            string name;
            string module;
            if (change.Create)
            {
                (module, name) = SplitNewTargetName(change.Name);
                if (!createdNames.Add(name) || !task.IsValidRapidNameInTaskScope(name, module) ||
                    task.Targets.Cast<RsTarget>().Any(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw Error("target_exists", $"'{change.Name}' is already used or is not valid in the station task scope; no changes were made.");
            }
            else
            {
                target = SelectStationTarget(task, change.Name, null);
                if (!editedDeclarations.Add(target.RobTarget))
                    throw Error("duplicate_target_edit", "The same target declaration occurs more than once in this edit batch.");
                if (task.FindTargetsByRobTarget(target.RobTarget).Length != 1)
                    throw Error("shared_target", "This robtarget has multiple graphical target instances. Edit its shared uses explicitly in RobotStudio before using this endpoint.");
                RejectExternalAxes(target.RobTarget.GetExternalAxes(false).GetAxisValues());
                name = target.RobTarget.Name;
                module = target.RobTarget.ModuleName;
            }

            var workObject = change.WorkObject is null && target is not null
                ? target.WorkObject
                : Select(Data<RsWorkObject>(task), change.WorkObject, "work object");
            RequireStationaryWorkObject(task, workObject);
            if (target is not null && !ReferenceEquals(workObject, target.WorkObject) && target.RobTarget.GetReferencingInstructions().Length != 0)
                throw Error("workobject_in_use", "A referenced target cannot be reassigned to another work object without also reviewing its path instructions. Its pose can still be edited in its current work object.");
            plans.Add(new(change, target, workObject, name, module, TargetMatrix(change.RobTarget)));
        }

        // All request, selection, scope and reference checks above finish before the first write.
        // There are no awaits in this transaction, so another bridge/UI action cannot interleave.
        EnsureCurrent(station);
        RequireStopped();
        var results = new List<object>();
        Project.UndoContext.BeginUndoStep("Offline bridge: edit station targets");
        try
        {
            foreach (var plan in plans)
            {
                var data = plan.Existing?.RobTarget ?? new RsRobTarget { Name = plan.Name, ModuleName = plan.Module };
                if (plan.Existing is not null) plan.Existing.WorkObject = plan.WorkObject;
                data.Frame.Matrix = plan.LocalMatrix;
                var configuration = plan.Change.RobTarget.Configuration;
                data.SetConfiguration(configuration[0], configuration[1], configuration[2], configuration[3]);
                data.SetExternalAxes(new ExternalAxisValues
                {
                    Eax_a = 9E9, Eax_b = 9E9, Eax_c = 9E9, Eax_d = 9E9, Eax_e = 9E9, Eax_f = 9E9
                }, false);
                if (plan.Existing is null)
                {
                    task.DataDeclarations.Add(data);
                    task.Targets.Add(new RsTarget(plan.WorkObject, data) { Name = plan.Name });
                }
                results.Add(new
                {
                    name = plan.Name, module = plan.Module, created = plan.Existing is null,
                    workObject = plan.WorkObject.Name, pose = Frame(data.Frame.Matrix),
                    configuration, affectedInstructionCount = data.GetReferencingInstructions().Length
                });
            }
            Station.UpdateGraphics();
        }
        catch
        {
            Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback);
            throw;
        }
        finally { Project.UndoContext.EndUndoStep(); }
        return new
        {
            robot = robot.Name, task = task.Name, count = results.Count, changes = results,
            scope = "station_only", undoable = true, undoSteps = 1, stationSaved = false,
            controllerUpdated = false, reachabilityChecked = false, collisionsChecked = false
        };
    }

    private static RsTarget SelectStationTarget(RsTask task, string name, string? workObject)
    {
        var candidates = task.Targets.Cast<RsTarget>().Where(target =>
            (target.Name == name || target.RobTarget.Name == name || Qualified(target.RobTarget) == name) &&
            (workObject is null || target.WorkObject.Name == workObject || Qualified(target.WorkObject) == workObject)).ToArray();
        if (candidates.Length != 1)
            throw Error("target_selection", candidates.Length == 0
                ? $"No matching station target '{name}'."
                : $"Station target '{name}' is ambiguous; use its Module/name and work object.");
        return candidates[0];
    }

    private static (string Module, string Name) SplitNewTargetName(string qualifiedName)
    {
        var parts = qualifiedName.Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(part => !RsTask.IsValidRapidName(part)))
            throw Error("invalid_target_name", "A new target name must be a valid RAPID identifier or Module/name; names are never silently changed.");
        return parts.Length == 1 ? (string.Empty, parts[0]) : (parts[0], parts[1]);
    }

    private static void RequireStationaryWorkObject(RsTask task, RsWorkObject workObject)
    {
        if (workObject.RobotHold || !workObject.UserFrameProgrammed ||
            !string.IsNullOrEmpty(workObject.UserFrameMechanicalUnit) || task.MechanismMovingTaskFrame is not null)
            throw Error("unsupported_frame", "Preview and edit operations require a stationary programmed work object; moving frames are not approximated.");
    }

    private static Matrix4 TargetMatrix(RobTarget target) => MatrixFromFrame(FrameMath.FromRobTarget(target));

    private static Matrix4 MatrixFromFrame(RigidFrame frame) => new(
        new Vector3(frame.Position.X / 1000.0, frame.Position.Y / 1000.0, frame.Position.Z / 1000.0),
        new Quaternion(frame.Orientation.W, frame.Orientation.X, frame.Orientation.Y, frame.Orientation.Z));

    private sealed record PreviewPoint(string Id, RsWorkObject WorkObject, Matrix4 WorldMatrix);
    private sealed record TargetEditPlan(TargetEdit Change, RsTarget? Existing, RsWorkObject WorkObject, string Name, string Module, Matrix4 LocalMatrix);
}
