using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static object CreateGeometry(Station station, GeometryCreateRequest request)
    {
        EnsureGeometryCanBeAdded(station, request.Name);
        var body = request.Shape switch
        {
            "box" => Body.CreateSolidBox(Matrix4.Identity,
                new Vector3(request.Size![0] / 1000, request.Size[1] / 1000, request.Size[2] / 1000)),
            "cylinder" => Body.CreateSolidCylinder(Matrix4.Identity, request.Radius!.Value / 1000, request.Height!.Value / 1000),
            "sphere" => Body.CreateSolidSphere(new Vector3(0, 0, 0), request.Radius!.Value / 1000),
            _ => throw Error("geometry_shape", "Supported solids are box, cylinder and sphere.")
        };
        if (body is null) throw Error("geometry_create_failed", "RobotStudio could not construct the requested solid.");
        var part = new Part();
        body.Name = request.Shape;
        part.Bodies.Add(body);
        var bounds = GeometryBounds(part, global: false);
        if (request.Shape == "box")
        {
            // Place the observed minimum corner at the local origin before rotating the part.
            body.Transform.Translation -= bounds.min;
        }
        var anchor = request.Shape switch
        {
            "box" => "lower_corner", "cylinder" => "base_center_positive_z", _ => "center"
        };
        return AddGeometry(station, request, part, anchor, new
        {
            shape = request.Shape, size = request.Size, radius = request.Radius, height = request.Height, units = "mm"
        });
    }

    private static async Task<object> ImportGeometryAsync(Station station, GeometryImportRequest request)
    {
        EnsureGeometryCanBeAdded(station, request.Name);
        LocalGeometryFile.RequireAvailable(request.File);
        if (!GraphicComponent.CanImport(request.File))
            throw Error("geometry_format_unavailable", "RobotStudio has no available geometry importer for this file type.");

        GraphicComponent? imported;
        try
        {
            // The converter produces a detached component or assembly. Keep the undo
            // transaction closed while the converter is running and the UI can change.
            imported = await GraphicComponent.ImportAsync(request.File, new GraphicImportSettings { AsPart = false }, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Error("geometry_import_failed", "RobotStudio could not import the geometry. Check the file and its installed translator/license. SDK: " + exception.Message);
        }
        EnsureGeometryCanBeAdded(station, request.Name);
        if (imported is null) throw Error("geometry_empty", "The importer returned no geometry component for this file.");
        GeometryBounds(imported, global: false);
        return AddGeometry(station, request, imported, "imported_component_origin", new { file = request.File });
    }

    private static object AddGeometry(Station station, GeometryRequest request, GraphicComponent component, string anchor, object source)
    {
        var pose = request.PlacementFrame == "base"
            ? FrameMath.Compose(ToFrame(RobotBaseWorld(SelectRobot(station, request.Robot))), request.Pose) : request.Pose;
        var placement = MatrixFromFrame(pose);
        component.Name = request.Name;
        EnsureGeometryCanBeAdded(station, request.Name);
        Project.UndoContext.BeginUndoStep("Offline bridge: add geometry");
        try
        {
            station.GraphicComponents.Add(component);
            component.Transform.GlobalMatrix = placement;
            Station.UpdateGraphics();
            var world = GeometryBounds(component, global: true);
            return new
            {
                name = component.Name, id = component.UniqueId, componentType = component.GetType().Name,
                source, anchor, worldPose = Frame(component.Transform.GlobalMatrix),
                worldBounds = new
                {
                    min = Millimetres(world.min), max = Millimetres(world.max),
                    size = Millimetres(world.max - world.min), units = "mm", frame = "station_world", axisAligned = true
                },
                scope = "station_only", undoSteps = 1, stationSaved = false, controllerUpdated = false
            };
        }
        catch
        {
            Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback);
            throw;
        }
        finally { Project.UndoContext.EndUndoStep(); }
    }

    private static void EnsureGeometryCanBeAdded(Station station, string name)
    {
        EnsureCurrent(station);
        RequireStopped();
        if (station.GraphicComponents.Cast<GraphicComponent>().Any(component =>
            string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw Error("geometry_name_exists", $"A top-level station graphic component named '{name}' already exists. Choose another name.");
    }

    private static BoundingBox GeometryBounds(GraphicComponent component, bool global)
    {
        var bounds = component.GetBoundingBox(global);
        if (!bounds.IsValid() || (bounds.max.x == bounds.min.x && bounds.max.y == bounds.min.y && bounds.max.z == bounds.min.z) ||
            !Millimetres(bounds.min).All(double.IsFinite) || !Millimetres(bounds.max).All(double.IsFinite) ||
            !Millimetres(bounds.max - bounds.min).All(double.IsFinite))
            throw Error("geometry_empty", "RobotStudio returned empty or invalid graphical bounds for this component.");
        return bounds;
    }

    private static double[] Millimetres(Vector3 value) => [value.x * 1000, value.y * 1000, value.z * 1000];
}
