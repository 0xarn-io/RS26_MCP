using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static object SimulationSetup(Station station, SimulationSetupRequest request)
    {
        RequireMainThread();
        var active = Simulator.ActiveConfiguration;
        if (!ReferenceEquals(active?.ContainingProject, station)) active = null;
        if (request.Action != "read")
            return EditSimulationSetup(station, active, request);

        object Page<T>(IEnumerable<T> ordered, Func<T, object> describe, string? configurationId = null)
        {
            var rows = ordered.ToArray();
            var items = rows.Skip(request.Offset).Take(request.Limit).Select(describe).ToArray();
            return new
            {
                station = station.Name, section = request.Section, configurationId,
                offset = request.Offset, limit = request.Limit, total = rows.Length, items,
                nextOffset = items.Length > 0 && request.Offset + items.Length < rows.Length
                    ? (int?)(request.Offset + items.Length) : null
            };
        }

        switch (request.Section)
        {
            case "overview":
                return new
                {
                    station = station.Name, section = request.Section, physics = PhysicsWorld(station),
                    simulator = new
                    {
                        state = Simulator.State.ToString(), simulationTimeMs = Simulator.CurrentTime,
                        timeStepMs = Simulator.Resolution, speedFactor = Simulator.SimulationSpeed,
                        fullSpeed = Simulator.FullSpeed, activeConfigurationId = active?.UniqueId,
                        activeConfiguration = active?.Name
                    },
                    configurationCount = station.SimulationConfigurations.Count,
                    savedStateCount = station.SavedStates.Count(), physicsConstraintCount = station.PhysicsConstraints.Count
                };
            case "configurations":
                return Page(station.SimulationConfigurations.Cast<SimulationConfiguration>()
                    .OrderBy(c => c.UniqueId, StringComparer.Ordinal), c => DescribeConfiguration(c, active));
            case "participants":
                var configuration = ResolveSetupConfiguration(station, active, request.Configuration);
                return Page(configuration.SimulatedObjects.OrderBy(o => o.UniqueId, StringComparer.Ordinal),
                    o => new { id = o.UniqueId, name = o.Name, type = o.GetType().Name }, configuration.UniqueId);
            case "eligibleParticipants":
                var selected = ResolveSetupConfiguration(station, active, request.Configuration);
                return Page(EligibleSimulationParticipants(station).OrderBy(o => o.UniqueId, StringComparer.Ordinal),
                    o => new { id = o.UniqueId, name = o.Name, type = o.GetType().Name, included = selected.SimulatedObjects.Contains(o) },
                    selected.UniqueId);
            case "savedStates":
                return Page(station.SavedStates.Cast<SavedState>().OrderBy(s => s.Name, StringComparer.Ordinal),
                    s => new { name = s.Name, description = s.Description, savedAt = s.Time });
            case "parts":
                return Page(station.GetAllObjects().OfType<Part>().OrderBy(p => p.UniqueId, StringComparer.Ordinal),
                    DescribePhysicsPart);
            default:
                throw Error("invalid_section", "Unknown simulation setup section.");
        }
    }

    private static object PhysicsWorld(Station station)
    {
        var world = station.PhysicsSimulationProperties;
        return new { floorEnabled = world.Floor, floorHeightMm = world.FloorHeight * 1000.0, gravityMps2 = world.Gravity };
    }

    private static object DescribeConfiguration(SimulationConfiguration configuration, SimulationConfiguration? active) =>
        new
        {
            id = configuration.UniqueId, name = configuration.Name, active = ReferenceEquals(configuration, active),
            virtualTimeMode = configuration.VirtualTimeMode.ToString(), initialState = configuration.InitialState,
            dataRecordingEnabled = configuration.DataRecordingEnabled, participantCount = configuration.SimulatedObjects.Count,
            stopwatchCount = configuration.Stopwatches.Count
        };

    private static object DescribePhysicsPart(Part part)
    {
        var material = part.GetPhysicsMaterial();
        var surface = part.GetPhysicsSurfaceVelocity();
        return new
        {
            id = part.UniqueId, name = part.Name, type = part.GetType().Name,
            motionControl = part.PhysicsMotionControl.ToString(),
            detectableBySensors = part.Detectable,
            useOriginalGeometry = part.GetPhysicsCollisionProperties().UseOriginalGeometry,
            material = new
            {
                densityKgPerM3 = material.Density, surfaceRoughness = material.SurfaceRoughness,
                restitution = material.Restitution, damping = material.Damping
            },
            surfaceVelocity = new
            {
                enabled = surface.Enabled, speedMps = surface.Speed,
                direction = new[] { surface.Direction.x, surface.Direction.y, surface.Direction.z },
                controlPointCount = surface.ControlPoints?.Length ?? 0
            }
        };
    }

    private static SimulationConfiguration ResolveSetupConfiguration(Station station, SimulationConfiguration? active, string? selector)
    {
        if (selector is null) return active ?? throw Error("simulation_configuration", "Select an active station configuration or supply its ID.");
        var configurations = station.SimulationConfigurations.Cast<SimulationConfiguration>().ToArray();
        var byId = configurations.FirstOrDefault(c => c.UniqueId == selector);
        if (byId != null) return byId;
        var named = configurations.Where(c => c.Name == selector).Take(2).ToArray();
        if (named.Length != 1) throw Error("simulation_configuration", "Select a single station simulation configuration by its exact ID or name.");
        return named[0];
    }

    private static IEnumerable<ProjectObject> EligibleSimulationParticipants(Station station) =>
        station.GetAllObjects().Where(o => o is SmartComponent or Conveyor)
            .Concat(station.VirtualControllers.Cast<ProjectObject>())
            .Where(o => ReferenceEquals(o.ContainingProject, station));

    private static ProjectObject ResolveSimulationParticipant(Station station, string selector)
    {
        var eligible = EligibleSimulationParticipants(station)
            .Where(o => o.UniqueId == selector || o.Name == selector).ToArray();
        var byId = eligible.FirstOrDefault(o => o.UniqueId == selector);
        if (byId != null) return byId;
        if (eligible.Length != 1) throw Error("simulation_participant",
            "Select a station virtual controller, smart component or conveyor by its exact ID or unique name.");
        return eligible[0];
    }

    private static object EditSimulationSetup(Station station, SimulationConfiguration? active, SimulationSetupRequest request)
    {
        if (Simulator.State != SimulationState.Stopped)
            throw Error("simulation_active", "Stop the simulation before editing its setup.");
        object Edit(Func<object> apply)
        {
            Project.UndoContext.BeginUndoStep("Offline bridge: edit simulation setup");
            try { return new { station = station.Name, action = request.Action, updated = apply() }; }
            catch
            {
                Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback);
                throw;
            }
            finally { Project.UndoContext.EndUndoStep(); }
        }

        switch (request.Action)
        {
            case "set_world":
                var world = station.PhysicsSimulationProperties;
                if (request.FloorHeightMm is double height) world.FloorHeight = height / 1000.0;
                if (request.FloorEnabled is bool floor) world.Floor = floor;
                if (request.GravityMps2 is double gravity) world.Gravity = gravity;
                return Edit(() =>
                {
                    station.PhysicsSimulationProperties = world;
                    return PhysicsWorld(station);
                });
            case "set_configuration":
                var configuration = ResolveSetupConfiguration(station, active, request.Configuration);
                if (request.InitialState is string initialState &&
                    !station.SavedStates.Cast<SavedState>().Any(s => s.Name == initialState))
                    throw Error("simulation_initial_state", "Use a saved state name from this station.");
                var timeMode = request.VirtualTimeMode is null ? (VirtualTimeMode?)null :
                    Enum.Parse<VirtualTimeMode>(request.VirtualTimeMode);
                return Edit(() =>
                {
                    if (timeMode.HasValue) configuration.VirtualTimeMode = timeMode.Value;
                    if (request.InitialState is not null) configuration.InitialState = request.InitialState;
                    if (request.DataRecordingEnabled is bool record) configuration.DataRecordingEnabled = record;
                    return DescribeConfiguration(configuration, active);
                });
            case "set_participant":
                var selected = ResolveSetupConfiguration(station, active, request.Configuration);
                var participant = ResolveSimulationParticipant(station, request.Participant!);
                var included = request.Included!.Value;
                var members = selected.SimulatedObjects;
                object Membership() => new
                {
                    configurationId = selected.UniqueId, participantId = participant.UniqueId,
                    participant = participant.Name, included = members.Contains(participant)
                };
                if (members.Contains(participant) == included)
                    return new { station = station.Name, action = request.Action, updated = Membership() };
                return Edit(() =>
                {
                    if (included) members.Add(participant);
                    else members.Remove(participant);
                    if (members.Contains(participant) != included)
                        throw Error("simulation_participant", "RobotStudio did not apply the requested participant membership.");
                    return Membership();
                });
            case "set_part":
                var part = (Part)ResolveSceneObject(station, request.Part!, typeof(Part));
                var motion = request.MotionControl is null ? (PhysicsMotionControl?)null :
                    Enum.Parse<PhysicsMotionControl>(request.MotionControl);
                return Edit(() =>
                {
                    if (motion.HasValue) part.PhysicsMotionControl = motion.Value;
                    if (request.DetectableBySensors is bool detectable) part.Detectable = detectable;
                    if (request.UseOriginalGeometry is bool originalGeometry)
                    {
                        var collision = part.GetPhysicsCollisionProperties();
                        collision.UseOriginalGeometry = originalGeometry;
                        part.SetPhysicsCollisionProperties(collision);
                    }
                    if (request.DensityKgPerM3.HasValue || request.SurfaceRoughness.HasValue ||
                        request.Restitution.HasValue || request.Damping.HasValue)
                    {
                        var material = part.GetPhysicsMaterial();
                        material.Density = request.DensityKgPerM3 ?? material.Density;
                        material.SurfaceRoughness = request.SurfaceRoughness ?? material.SurfaceRoughness;
                        material.Restitution = request.Restitution ?? material.Restitution;
                        material.Damping = request.Damping ?? material.Damping;
                        part.SetPhysicsMaterial(material);
                    }
                    if (request.SurfaceVelocityEnabled.HasValue || request.SurfaceSpeedMps.HasValue || request.SurfaceDirection.HasValue)
                    {
                        var surface = part.GetPhysicsSurfaceVelocity();
                        surface.Enabled = request.SurfaceVelocityEnabled ?? surface.Enabled;
                        surface.Speed = request.SurfaceSpeedMps ?? surface.Speed;
                        if (request.SurfaceDirection is {} direction)
                        {
                            surface.ControlPoints = [];
                            surface.Direction = new(direction.X, direction.Y, direction.Z);
                        }
                        part.SetPhysicsSurfaceVelocity(surface);
                    }
                    return DescribePhysicsPart(part);
                });
            default:
                throw Error("invalid_action", "Unknown simulation setup action.");
        }
    }
}
