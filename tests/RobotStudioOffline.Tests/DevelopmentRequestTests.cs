using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static DevelopmentRequest Development(string operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        RequestParser.Parse(operation, document.RootElement);
        return DevelopmentRequests.Parse(operation, document.RootElement);
    }

    private static void AddDevelopmentTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("path details validates exact selector fields", () =>
        {
            var request = (PathDetailsRequest)Development("path_details", "{\"robot\":\"TestRobot\",\"path\":\"SamplePath/Inspect\"}");
            Equal("SamplePath/Inspect", request.Path);
            Reject("path_details", "{\"path\":null}");
            Reject("path_details", "{\"extra\":true}");
        }));
        tests.Add(("batch validation merges defaults and preserves item overrides", () =>
        {
            var request = (ValidateTargetsRequest)Development("validate_targets",
                "{\"robot\":\"TestRobot\",\"tool\":\"Fixtures/tProbe\",\"workObject\":\"wobj0\",\"checkCollisions\":true,\"targets\":[{\"id\":\"home\",\"target\":\"pReference\"},{\"tool\":\"tool0\",\"robtarget\":" + Pose + "}]}");
            True(request.CheckCollisions);
            Equal("home", request.Targets[0].Id);
            Equal("TestRobot", request.Targets[1].Request.Robot);
            Equal("Fixtures/tProbe", request.Targets[0].Request.Tool);
            Equal("tool0", request.Targets[1].Request.Tool);
            Equal("wobj0", request.Targets[1].Request.WorkObject);
            Equal("2", request.Targets[1].Id);
        }));
        tests.Add(("batch validation rejects duplicates and invalid nested poses before execution", () =>
        {
            Reject("validate_targets", "{\"targets\":[]}");
            Reject("validate_targets", "{\"tool\":\"tool0\",\"targets\":[{\"id\":\"x\",\"target\":\"a\"},{\"id\":\"x\",\"target\":\"b\"}]}");
            Reject("validate_targets", "{\"tool\":\"tool0\",\"targets\":[{\"target\":\"a\"},{\"target\":\"b\",\"typo\":true}]}");
            Reject("validate_targets", "{\"checkCollisions\":\"false\",\"targets\":[{\"target\":\"a\",\"tool\":\"t\"}]}");
            Reject("validate_targets", "{\"targets\":[{\"target\":\"pReference\"}]}");
            var tooMany = JsonSerializer.Serialize(new { tool = "t", targets = Enumerable.Range(0, 101).Select(x => new { target = "p" + x }) });
            Reject("validate_targets", tooMany);
        }));
        tests.Add(("preview does not require a tool and clear cannot carry mutations", () =>
        {
            var request = (PreviewTargetsRequest)Development("preview_targets", "{\"action\":\"show\",\"workObject\":\"wobj0\",\"targets\":[{\"robtarget\":" + Pose + "},{\"target\":\"pReference\"}]}");
            Equal(2, request.Targets.Length);
            Equal("wobj0", request.Targets[0].WorkObject);
            Equal("clear", ((PreviewTargetsRequest)Development("preview_targets", "{\"action\":\"clear\"}")).Action);
            Reject("preview_targets", "{\"action\":\"clear\",\"targets\":[]}");
            Reject("preview_targets", "{\"action\":\"show\",\"targets\":[{\"robtarget\":" + Pose + "}]}");
            Development("preview_targets", "{\"action\":\"show\",\"targets\":[{\"target\":\"pReference\"}]}");
            Reject("preview_targets", "{\"action\":\"show\",\"targets\":[{\"jointtarget\":{\"robotAxes\":[0,0,0,0,0,0]}}]}");
            Reject("preview_targets", "{\"action\":\"show\",\"targets\":[{\"id\":\"x\",\"target\":\"a\"},{\"id\":\"x\",\"target\":\"b\"}]}");
            Reject("preview_targets", JsonSerializer.Serialize(new { action = "show", targets = Enumerable.Range(0, 101).Select(x => new { target = "p" + x }) }));
        }));
        tests.Add(("target editing distinguishes update and explicit creation", () =>
        {
            var request = (EditTargetsRequest)Development("edit_targets", "{\"workObject\":\"wobj0\",\"changes\":[{\"name\":\"Fixtures/pNew\",\"create\":true,\"robtarget\":" + Pose + "}]}");
            True(request.Changes[0].Create);
            Equal("wobj0", request.Changes[0].WorkObject);
            var existing = (EditTargetsRequest)Development("edit_targets", "{\"changes\":[{\"name\":\"pReference\",\"robtarget\":" + Pose + "}]}");
            True(!existing.Changes[0].Create);
            True(existing.Changes[0].WorkObject is null);
            Reject("edit_targets", "{\"changes\":[{\"name\":\"pNew\",\"create\":true,\"robtarget\":" + Pose + "}]}");
            Reject("edit_targets", "{\"changes\":[{\"name\":\"p\",\"robtarget\":" + Pose + "},{\"name\":\"p\",\"robtarget\":" + Pose + "}]}");
            using var poseDoc = JsonDocument.Parse(Pose);
            Reject("edit_targets", JsonSerializer.Serialize(new { changes = Enumerable.Range(0, 51).Select(x => new { name = "p" + x, robtarget = poseDoc.RootElement }) }));
            Reject("edit_targets", "{\"changes\":[{\"name\":\"p\",\"robtarget\":{\"position\":[1,2,3],\"quaternion\":[0,0,0,0],\"configuration\":[0,0,0,0]}}]}");
        }));
        tests.Add(("selected sync requires an explicit direction and bounded typed data", () =>
        {
            var request = (DataSyncRequest)Development("data_sync", "{\"direction\":\"to_station\",\"data\":[{\"name\":\"Fixtures/pReference\",\"kind\":\"target\"}]}");
            Equal("to_station", request.Direction);
            Equal("Fixtures/pReference", request.Data[0].Name);
            Development("data_compare", "{\"data\":[{\"name\":\"Fixtures/tProbe\",\"kind\":\"tool\"}]}");
            Reject("data_sync", "{\"data\":[{\"name\":\"pReference\",\"kind\":\"target\"}]}");
            Reject("data_sync", "{\"direction\":\"to_real_controller\",\"data\":[]}");
            Reject("data_compare", "{\"data\":[{\"name\":\"pReference\",\"kind\":\"module\"}]}");
            Reject("data_compare", "{\"data\":[{\"name\":\"p\",\"kind\":\"target\"},{\"name\":\"p\",\"kind\":\"target\"}]}");
        }));
        tests.Add(("real-controller parameters require owner permission even within a batch", () =>
        {
            var error = Reject("validate_targets", "{\"targets\":[{\"target\":\"pReference\",\"controller\":\"192.0.2.42\"}]}", "real_controller_permission_required");
            True(error.Message.Contains("explicit permission", StringComparison.Ordinal));
            True(error.Message.Contains("No access was attempted", StringComparison.Ordinal));
        }));
    }
}
