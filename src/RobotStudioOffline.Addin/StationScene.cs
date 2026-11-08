using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static IEnumerable<ProjectObject> SceneObjects(Station station)
    {
        // ABB's type lookup uses exact runtime types. GetAllObjects traverses the
        // station hierarchy and deliberately omits geometry below each Part.
        foreach (var component in station.GetAllObjects().OfType<GraphicComponent>())
        {
            yield return component;
            if (component is Part part)
                foreach (var body in part.Bodies.Cast<Body>()) yield return body;
        }
    }

    private static ProjectObject ResolveSceneObject(Station station, string selector, Type? requiredType = null)
    {
        var matches = SceneObjects(station).Where(x => x.Name == selector || x.UniqueId == selector).ToArray();
        var byId = matches.FirstOrDefault(x => x.UniqueId == selector);
        if (byId != null)
        {
            if (requiredType == null || requiredType.IsInstanceOfType(byId)) return byId;
            throw Error("geometry_selection", $"'{selector}' identifies a {byId.GetType().Name}. Select a {requiredType.Name}.");
        }
        var named = matches.Where(x => requiredType == null || requiredType.IsInstanceOfType(x)).Take(2).ToArray();
        if (named.Length != 1) throw Error("geometry_selection", $"'{selector}' matched {(named.Length == 0 ? "no eligible geometry" : "multiple objects")}. Use an exact geometry id.");
        return named[0];
    }

    private static Matrix4 RobotBaseWorld(Mechanism robot)
    {
        var task = RequireTask(robot);
        var station = robot.ContainingProject as Station ?? throw Error("robot_base", "The mechanism has no open station.");
        var owners = station.VirtualControllers.Cast<VirtualController>().Where(c => c.Tasks.Cast<RsTask>().Contains(task)).ToArray();
        if (owners.Length != 1) throw Error("robot_base", "The robot task has no unique station VC.");
        var units = owners[0].MechanicalUnits.Cast<RsMechanicalUnit>().Where(unit => ReferenceEquals(unit.Mechanism, robot)).ToArray();
        if (units.Length != 1) throw Error("robot_base", "Cannot identify a unique mechanical-unit base frame for the station robot.");
        return units[0].BaseFrame.GlobalMatrix;
    }

    private static object GeometryInventory(Station station, GeometryQuery query)
    {
        var objects = SceneObjects(station).Where(x => query.Match is null || x.UniqueId == query.Match ||
            x.Name?.Contains(query.Match, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return new { total = objects.Length, query.Offset, items = objects.Skip(query.Offset).Take(query.Limit).Select(x =>
        {
            var bounds = (x as GraphicComponent)?.GetBoundingBox(true);
            var valid = bounds is { } box && box.IsValid() && Millimetres(box.min).Concat(Millimetres(box.max)).All(double.IsFinite);
            return new { id = x.UniqueId, name = x.Name, type = x.GetType().Name, parent = x.Parent?.Name,
                parentId = x.Parent?.UniqueId, worldBoundsMm = valid ? new { min = Millimetres(bounds!.Value.min), max = Millimetres(bounds.Value.max) } : null };
        }).ToArray() };
    }

    private static object CreateCollisionSet(Station station, SetCollisionRequest request)
    {
        RequireStopped();
        if (station.CollisionSets.Cast<CollisionSet>().Any(set => set.Name == request.Name)) throw Error("name_exists", "A collision set already has this name.");
        GraphicComponent[] Group(string[] selectors) => selectors.Select(s =>
            (GraphicComponent)ResolveSceneObject(station, s, typeof(GraphicComponent))).Distinct().ToArray();
        var first = Group(request.First);
        var second = Group(request.Second);
        if (first.Intersect(second).Any()) throw Error("collision_group", "A component appears on both sides of the collision set.");
        Project.UndoContext.BeginUndoStep("Offline bridge: create collision set");
        try
        {
            var set = new CollisionSet { Name = request.Name, NearMissDistance = request.NearMissMm / 1000.0 };
            foreach (var item in first) set.FirstGroup.Add(item);
            foreach (var item in second) set.SecondGroup.Add(item);
            station.CollisionSets.Add(set);
            set.Active = true;
            return new { name = set.Name, id = set.UniqueId, active = set.Active, request.NearMissMm, first = first.Select(x => x.UniqueId), second = second.Select(x => x.UniqueId) };
        }
        catch { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); throw; }
        finally { Project.UndoContext.EndUndoStep(); }
    }

    private static object SimulationSpeed(SimulationSpeedRequest request)
    {
        var previous = new { factor = Simulator.SimulationSpeed, fullSpeed = Simulator.FullSpeed, stepMs = Simulator.Resolution };
        if (request.StepMs.HasValue || request.Factor.HasValue)
        {
            try
            {
                if (request.StepMs is double step) Simulator.Resolution = step;
                if (request.Factor is double factor)
                {
                    Simulator.SimulationSpeed = factor;
                    Simulator.FullSpeed = false;
                }
            }
            catch
            {
                Simulator.Resolution = previous.stepMs;
                Simulator.SimulationSpeed = previous.factor;
                Simulator.FullSpeed = previous.fullSpeed;
                throw;
            }
        }
        return new { requested = request.Factor, previous, factor = Simulator.SimulationSpeed, fullSpeed = Simulator.FullSpeed,
            timeStepMs = Simulator.Resolution, simulationTimeMs = Simulator.CurrentTime, state = Simulator.State.ToString() };
    }

    private static object Raycast(Station station, RayQuery query)
    {
        var robot = SelectRobot(station, query.Robot);
        var tool = Select(Data<RsToolData>(RequireTask(robot)), query.Tool, "tool");
        var flanges = robot.GetFlanges();
        if (!tool.RobotHold || flanges.Length != 1) throw Error("unsupported_tool", "Raycast requires a robot-held tool on a single flange.");
        var tcp = FrameMath.Compose(ToFrame(flanges[0].GlobalMatrix), ToFrame(tool.Frame.Matrix));
        var start = tcp.Position + FrameMath.Rotate(tcp.Orientation, query.Origin);
        var direction = FrameMath.Rotate(tcp.Orientation, query.Direction);
        var rayStart = new Vector3(start.X / 1000, start.Y / 1000, start.Z / 1000);
        var rayDirection = new Vector3(direction.X, direction.Y, direction.Z);
        Part SelectPart(string name) => (Part)ResolveSceneObject(station, name, typeof(Part));
        var parts = query.Parts.Length == 0 ? station.FindGraphicComponentsByType(typeof(Part)).OfType<Part>()
            : query.Parts.Select(SelectPart);
        var excluded = query.Exclude.Select(SelectPart).ToHashSet();
        Part? closest = null;
        Face? face = null;
        double best = query.MaxDistance;
        Vector3 point = default;
        int checkedParts = 0;
        foreach (var part in parts.Distinct().Where(p => !excluded.Contains(p)))
        {
            checkedParts++;
            if (!part.IntersectRay(rayStart, rayDirection, out var hit, out var hitFace)) continue;
            var delta = hit - rayStart;
            var distance = Math.Sqrt(delta.x * delta.x + delta.y * delta.y + delta.z * delta.z) * 1000;
            if (!double.IsFinite(distance) || distance > best || delta.x * direction.X + delta.y * direction.Y + delta.z * direction.Z < 0) continue;
            closest = part;
            face = hitFace;
            point = hit;
            best = distance;
        }
        return new { hit = closest != null, distanceMm = closest is null ? (double?)null : best,
            pointWorldMm = closest is null ? null : Millimetres(point), part = closest?.Name, partId = closest?.UniqueId,
            body = face?.Body?.Name, bodyId = face?.Body?.UniqueId, originWorldMm = start.ToArray(), directionWorld = direction.ToArray(),
            checkedParts, approximate = true };
    }
}
