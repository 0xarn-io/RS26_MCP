using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private const string CarryPhysicsKey = "OfflineStationBridge.OriginalCarryPhysics";
    private static readonly Guid CarryAttributeOwner = new("b02eb8c3-2daa-47d2-b3bc-72ca8f95a147");

    private object BodyEdit(Station station, BodyRequest request)
    {
        EnsureCurrent(station);
        if (request.Action != "state" && station.ReadOnly) throw Error("station_read_only", "The open station is read-only.");
        if (request.Action == "delete_collision_set") return DeleteCollisionSet(station, request.Selector);
        var selected = ResolveSceneObject(station, request.Selector);
        var body = selected as Body;
        var part = selected as Part ?? body?.Parent as Part ??
            throw Error("body_selection", "Select a CAD Body or Part by its geometry ID.");
        if (request.Action == "state") return BodyState(station, selected, part);
        if (HasActiveBodyMotion(station, part))
            throw Error("body_motion_active", "Stop the bridge's motion for this part before editing or transferring it.");
        // A link in a mechanism and a part built into a smart component are structural geometry.
        // Only independent parts at station level or in plain CAD groups are editable.
        var owner = EditablePartOwner(station, part);
        var carrying = request.Action is "attach" or "release";
        if (body != null && carrying && part.Bodies.Count != 1)
            throw Error("body_selection", "Attachments act on an entire Part. Select the Part explicitly, or use an item with one body.");
        if (body != null && !carrying && (part.Library != null || part.Bodies.IsReadOnly))
            throw Error("body_read_only", "Select the Part to edit the placed instance. Its individual CAD bodies are linked or read-only.");

        object result;
        var changed = true;
        switch (request.Action)
        {
            case "move":
                if (CurrentBodyAttachment(station, part) != null)
                    throw Error("body_attached", "Release this part before moving it independently.");
                var reference = request.Frame == "base" ? ToFrame(RobotBaseWorld(SelectRobot(station, request.Robot))) : RigidFrame.Identity;
                var transform = ((IHasTransform)selected).Transform;
                var destination = MatrixFromFrame(request.MovePose(reference, ToFrame(transform.GlobalMatrix)));
                var verifyMove = body == null ? null : WatchOtherBodyInstances(station, part, body);
                result = UndoBodyChange("move geometry", () =>
                {
                    transform.GlobalMatrix = destination;
                    verifyMove?.Invoke();
                    Station.UpdateGraphics();
                    if (!transform.GlobalMatrix.AlmostEquals(destination, 1e-6))
                        throw Error("body_move_failed", "RobotStudio did not apply the requested world pose.");
                    return BodyState(station, selected, part);
                });
                break;
            case "delete":
                RequireStopped();
                CheckBodyLogicReferences(station, selected);
                var set = station.CollisionSets.FirstOrDefault(s => s.FirstGroup.Contains(part) || s.SecondGroup.Contains(part));
                if (body == null && set != null)
                    throw Error("body_in_collision_set", $"Part belongs to collision set '{set.Name}'. Remove that membership or set before deleting the part.");
                if (station.Attachments.Any(a => a.AttachmentChild == part || a.AttachmentParent == part) ||
                    station.PhysicsConstraints.Any(c => c.FirstComponent == part || c.SecondComponent == part))
                    throw Error("body_in_use", "This part participates in an attachment or physics constraint. Remove that relation before deleting geometry.");
                var removed = new { id = selected.UniqueId, name = selected.Name, type = selected.GetType().Name, removed = true };
                var verifyRemoval = body == null ? null : WatchOtherBodyInstances(station, part, body);
                result = UndoBodyChange("remove geometry", () =>
                {
                    // ABB's permanent Delete() cannot be undone. Collection removal participates in Undo.
                    var success = body != null ? part.Bodies.Remove(body) : owner.Remove(part, true);
                    if (!success) throw Error("body_remove_failed", "RobotStudio did not remove the selected geometry.");
                    verifyRemoval?.Invoke();
                    Station.UpdateGraphics();
                    return removed;
                });
                break;
            case "attach":
            case "release":
                result = TransferBody(station, selected, part, request, out changed);
                break;
            default:
                throw Error("invalid_request", "Unsupported body edit action.");
        }
        if (changed) ForgetBodyMotion(station, part);
        return result;
    }

    private static GraphicComponentCollection EditablePartOwner(Station station, Part part)
    {
        if (station.GraphicComponents.Contains(part)) return station.GraphicComponents;
        if (part.Parent is GraphicComponentGroup group && group.GraphicComponents.Contains(part))
        {
            var current = group;
            while (current.Library == null)
            {
                if (station.GraphicComponents.Contains(current)) return group.GraphicComponents;
                if (current.Parent is not GraphicComponentGroup parent) break;
                current = parent;
            }
        }
        throw Error("body_structural_geometry", "Select an independent station Part or its CAD body. Mechanism links and geometry built into other components are protected.");
    }

    private static Attachment? CurrentBodyAttachment(Station station, ProjectObject child) =>
        station.Attachments.SingleOrDefault(a => a.AttachmentChild == child);

    private static Action WatchOtherBodyInstances(Station station, Part part, Body body)
    {
        // ABB CopyInstance() can share a CAD definition without a library. The public SDK does
        // not expose definition identity. Shared definitions have the same indexed body list.
        // Detect a change to any sibling instance inside the undo transaction and roll it back.
        int index = part.Bodies.ToList().IndexOf(body);
        if (index < 0) throw Error("body_selection", "The selected body is no longer in its Part.");
        int count = part.Bodies.Count;
        var others = station.GetAllObjects().OfType<Part>().Where(p => p != part && p.Bodies.Count == count)
            .Select(p => (Part: p, Pose: p.Bodies[index].Transform.Matrix)).ToArray();
        return () =>
        {
            if (others.Any(row => row.Part.Bodies.Count != count || !row.Part.Bodies[index].Transform.Matrix.AlmostEquals(row.Pose, 1e-12)))
                throw Error("body_shared_definition", "Another placed Part shares the edited CAD geometry. Move or remove the Part instance, or make the geometry independent.");
        };
    }

    private static void CheckBodyLogicReferences(Station station, ProjectObject selected)
    {
        bool WithinSelection(ProjectObject? obj)
        {
            for (; obj != null && obj != station; obj = obj.Parent)
                if (obj == selected) return true;
            return false;
        }
        void CheckBindings(PropertyBindingCollection bindings)
        {
            foreach (var binding in bindings)
            {
                if (WithinSelection(binding.SourceObject) || WithinSelection(binding.TargetObject))
                    throw Error("body_in_use", $"Disconnect the binding from '{binding.SourceObjectName}.{binding.SourceProperty}' to '{binding.TargetObjectName}.{binding.TargetProperty}' before deleting geometry.");
            }
        }
        CheckBindings(station.Bindings);
        foreach (var component in station.GetAllObjects().OfType<SmartComponent>())
        {
            if (WithinSelection(component.RoleObject))
                throw Error("body_in_use", $"Smart Component '{component.Name}' uses this geometry as its role object.");
            CheckBindings(component.Bindings);
            foreach (var property in component.Properties)
            {
                var value = property.Value;
                if (value is ProjectObject obj && WithinSelection(obj) ||
                    value is System.Collections.IEnumerable list && value is not string &&
                    value.GetType().GetElementType()?.IsValueType != true && list.OfType<ProjectObject>().Any(WithinSelection))
                    throw Error("body_in_use", $"Clear or reassign Smart Component property '{component.Name}.{property.Name}' before deleting its referenced geometry.");
            }
        }
    }

    private static object BodyState(Station station, ProjectObject selected, Part part)
    {
        var attached = CurrentBodyAttachment(station, part);
        var parent = attached?.AttachmentParent;
        return new
        {
            id = selected.UniqueId, name = selected.Name, type = selected.GetType().Name, partId = part.UniqueId,
            worldPose = Frame(((IHasTransform)selected).Transform.GlobalMatrix),
            partWorldPose = Frame(part.Transform.GlobalMatrix), physics = part.PhysicsMotionControl.ToString(),
            attached = parent != null, attachmentParentId = parent?.UniqueId, attachmentParent = parent?.Name,
            restorePhysicsOnWorldRelease = StoredCarryPhysics(attached)?.ToString(),
            carried = station.Attachments.Where(a => a.AttachmentParent == part)
                .Select(a => new { id = a.AttachmentChild.UniqueId, name = a.AttachmentChild.Name }).ToArray()
        };
    }

    private static PhysicsMotionControl? StoredCarryPhysics(Attachment? attachment)
    {
        if (attachment != null && attachment.Attributes.TryGetValue<string>(CarryPhysicsKey, out var text) &&
            Enum.TryParse<PhysicsMotionControl>(text, out var prior) && prior is PhysicsMotionControl.Static or PhysicsMotionControl.Dynamic)
            return prior;
        return null;
    }

    private static object TransferBody(Station station, ProjectObject selected, Part part, BodyRequest request, out bool changed)
    {
        var previous = CurrentBodyAttachment(station, part);
        GraphicComponent? parent = null;
        if (request.Parent != null)
        {
            parent = (GraphicComponent)ResolveSceneObject(station, request.Parent, typeof(GraphicComponent));
            if (parent is Mechanism mechanism)
            {
                var flanges = mechanism.GetFlanges();
                if (flanges.Length != 1) throw Error("body_parent", "Select the exact gripper or link when a mechanism has multiple or no flanges.");
                parent = flanges[0].Link;
            }
            if (parent is not IAttachableParent)
                throw Error("body_parent", "Select an attachable gripper, mechanism flange link, Part or smart component.");
            PreventCarryCycle(station, part, parent);
        }
        changed = previous?.AttachmentParent != parent;
        if (!changed) return BodyState(station, selected, part);
        if (request.Action == "release" && previous == null)
            throw Error("body_not_attached", "The part is free. Use attach to place a free part on a carrier.");
        var pose = part.Transform.GlobalMatrix;
        var priorPhysics = StoredCarryPhysics(previous);
        return UndoBodyChange("transfer part", () =>
        {
            if (previous != null)
            {
                if (previous.AttachmentParent is not IAttachableParent oldParent || !oldParent.Detach(part))
                    throw Error("body_detach_failed", "RobotStudio did not detach the part from its current parent.");
                // Detach can send externally mounted parts back to their saved mounting pose.
                part.Transform.GlobalMatrix = pose;
            }
            if (parent is IAttachableParent receiver)
            {
                if (part.PhysicsMotionControl is PhysicsMotionControl.Dynamic or PhysicsMotionControl.Static)
                {
                    priorPhysics = part.PhysicsMotionControl;
                    part.PhysicsMotionControl = PhysicsMotionControl.Kinematic;
                }
                if (!receiver.CanAttachChild(part) || !receiver.Attach(part, false, Matrix4.Identity))
                    throw Error("body_attach_failed", "RobotStudio could not attach the part to the requested parent.");
                var created = CurrentBodyAttachment(station, part);
                if (created == null || created.AttachmentParent != parent)
                    throw Error("body_attach_failed", "The requested attachment could not be verified.");
                if (priorPhysics.HasValue)
                    created.Attributes.Add(CarryAttributeOwner, CarryPhysicsKey, priorPhysics.Value.ToString());
            }
            else if (priorPhysics.HasValue && part.PhysicsMotionControl == PhysicsMotionControl.Kinematic)
                part.PhysicsMotionControl = priorPhysics.Value;
            Station.UpdateGraphics();
            if (!part.Transform.GlobalMatrix.AlmostEquals(pose, 1e-6))
                throw Error("body_transfer_failed", "RobotStudio could not retain the item's world pose during transfer.");
            return BodyState(station, selected, part);
        });
    }

    private static void PreventCarryCycle(Station station, Part child, GraphicComponent parent)
    {
        var pending = new Stack<ProjectObject>();
        var visited = new HashSet<ProjectObject>();
        pending.Push(parent);
        while (pending.TryPop(out var current))
        {
            if (current == child) throw Error("attachment_cycle", "A part cannot carry itself or an ancestor.");
            if (!visited.Add(current) || current == station) continue;
            if (current.Parent != null) pending.Push(current.Parent);
            var attachmentParent = CurrentBodyAttachment(station, current)?.AttachmentParent;
            if (attachmentParent != null) pending.Push(attachmentParent);
        }
    }

    private static object DeleteCollisionSet(Station station, string selector)
    {
        RequireStopped();
        var sets = station.CollisionSets;
        var set = sets.FirstOrDefault(s => s.UniqueId == selector);
        if (set == null)
        {
            var named = sets.Where(s => s.Name == selector).Take(2).ToArray();
            if (named.Length != 1) throw Error("collision_set_selection", "Select an existing collision set by its exact ID or unique name.");
            set = named[0];
        }
        var result = new { id = set.UniqueId, name = set.Name, removed = true };
        return UndoBodyChange("remove collision set", () =>
        {
            if (!sets.Remove(set)) throw Error("collision_set_remove_failed", "RobotStudio did not remove the selected collision set.");
            return result;
        });
    }

    private static object UndoBodyChange(string description, Func<object> edit)
    {
        Project.UndoContext.BeginUndoStep("Offline bridge: " + description);
        try { return edit(); }
        catch { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); throw; }
        finally { Project.UndoContext.EndUndoStep(); }
    }
}
