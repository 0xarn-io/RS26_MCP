using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddSmartTests(List<(string Name, Action Run)> tests)
    {
        SmartRequest Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return SmartRequests.Parse("smart_component", doc.RootElement);
        }
        tests.Add(("smart component catalog uses named ABB templates; listing accepts bounded paging", () =>
        {
            Equal("catalog", Parse("{}").Action);
            Equal("Detacher", Parse("""{"action":"catalog","template":"Detacher"}""").Template!);
            Equal(200, Parse("""{"action":"list","limit":200}""").Limit);
            Equal("trial", Parse("""{"action":"delete","component":"trial"}""").Component!);
            foreach (var json in new[] { """{"action":"catalog","template":"ExecuteCommand"}""",
                """{"action":"catalog","template":"../Attacher.rslib"}""", """{"action":"list","limit":201}""",
                """{"action":"inspect","component":"sense","properties":[]}""" })
                Reject("smart_component", json);
        }));
        tests.Add(("smart component creation retains property order, native values and station object references", () =>
        {
            var mover = Parse("""{"action":"create","template":"LinearMover","name":"feed","properties":[{"name":"Object","value":{"object":"unit"}},{"name":"Direction","value":[1,0,0]},{"name":"Speed","value":0.35}],"position":[123,0,0],"quaternion":[1,0,0,0]}""");
            Equal("unit", mover.Properties![0].Value.GetProperty("object").GetString()!);
            Equal(1.0, mover.Properties[1].Value[0].GetDouble());
            Equal(0.35, mover.Properties[2].Value.GetDouble());
            Equal(123.0, mover.Position!.Value.X);
            var change = Parse("""{"action":"configure","component":"pick","properties":[{"name":"Child","value":null},{"name":"Mount","value":false}]}""");
            True(change.Properties![0].Value.ValueKind == JsonValueKind.Null && !change.Properties[1].Value.GetBoolean());
        }));
        tests.Add(("smart edits reject duplicate keys, invalid references, invalid vectors and controller selectors", () =>
        {
            foreach (var values in new[] { """[]""", """[{"name":"Speed","value":1},{"name":"Speed","value":2}]""",
                """[{"name":"Speed","value":1e999}]""", """[{"name":"Child","value":{"object":"a","extra":1}}]""",
                """[{"name":"Direction","value":[1,0]}]""", """[{"name":"Object"}]""" })
                Reject("smart_component", """{"action":"configure","component":"item","properties":""" + values + "}");
            Reject("smart_component", """{"action":"configure","component":"x"}""");
            Reject("smart_component", """{"action":"configure","component":"x","quaternion":[2,0,0,0]}""");
            Reject("smart_component", """{"action":"create","template":"Attacher","name":"pick","controller":"PLC"}""", "real_controller_permission_required");
        }));
        var installation = Environment.GetEnvironmentVariable("RS_OFFLINE_TEST_SDK");
        if (installation == null) return;
        tests.Add(("catalog matches the installed ABB component libraries and built-in code-behind identities", () =>
        {
            foreach (var template in SmartRequests.Templates)
            {
                using var library = ZipFile.OpenRead(Path.Combine(installation, "ABB Library", "Components", template + ".rslib"));
                using var stream = library.GetEntry("PIM.xml")!.Open();
                var document = XDocument.Load(stream);
                var code = document.Descendants().Single(e => e.Name.LocalName == "CodeBehindLocation");
                Equal($"RobotStudio.Services.Modeling.BuiltIn.{template},RobotStudio.Services.SmartComponents.dll", (string)code.Attribute("Value")!);
                if (template is "LineSensor" or "PlaneSensor" or "VolumeSensor")
                {
                    var sensor = document.Descendants().Single(e => e.Name.LocalName == "SmartComponentInstance");
                    var ns = sensor.Name.Namespace;
                    var glyph = document.Descendants(ns + "ComponentInstance").Single(e =>
                        (string?)e.Element(ns + "Parent")?.Attribute("ID") == (string?)sensor.Attribute("ID"));
                    var name = (string)glyph.Element(ns + "Name")!.Attribute("Value")!;
                    var visible = (bool)glyph.Element(ns + "UIVisible")!.Attribute("Value")!;
                    True(SmartRequests.IsSensorGraphic(template, name, visible));
                    True(!SmartRequests.IsSensorGraphic(template, name, true));
                    True(!SmartRequests.IsSensorGraphic(template, "user_part", false));
                }
            }
        }));
    }
}
