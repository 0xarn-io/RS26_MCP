using System.IO.Compression;
using System.Xml.Linq;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddLogicTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("station logic accepts object bindings, virtual signal connections and component inputs", () =>
        {
            LogicRequest Parse(string json) => RequestParser.ParseJson("station_logic", json).Logic!;
            Equal(50, Parse("{}").Limit);
            var binding = Parse("""{"action":"connect","kind":"property","source":{"component":"Eye","member":"SensedPart"},"target":{"component":"Grip","member":"Child"}}""");
            Equal("SensedPart", binding.Source!.Member);
            Equal("Child", binding.Target!.Member);
            var wire = Parse("""{"action":"connect","source":{"robot":"Arm","member":"doGrip"},"target":{"component":"Grip","member":"Execute"}}""");
            Equal("signal", wire.Kind!);
            Equal("Arm", wire.Source!.Robot!);
            Equal(0, Parse("""{"action":"set_input","target":{"component":"Eye","member":"Active"},"value":0}""").Value!.Value);
            True(Parse("""{"action":"pulse","target":{"component":"Grip","member":"Execute"}}""").Value == null);
        }));
        tests.Add(("station logic rejects ambiguous endpoints, physical selectors and invalid writes before dispatch", () =>
        {
            foreach (var json in new[]
            {
                """{"action":"list","target":{}}""", """{"action":"list","limit":201}""",
                """{"action":"set_input","target":{"component":"a","member":"Execute"}}""",
                """{"action":"pulse","target":{"robot":"r","member":"s"}}""",
                """{"action":"pulse","target":{"component":"a","member":"s"},"value":0}""",
                """{"action":"set_input","target":{"component":"a","member":"s"},"value":2}""",
                """{"action":"connect","source":{"robot":"r","component":"a","member":"s"},"target":{"component":"b","member":"t"}}""",
                """{"action":"connect","source":{"component":"a","member":"s"},"target":{"component":"a","member":"s"}}""",
                """{"action":"connect","source":{"robot":"r","member":"s"},"target":{"robot":"r","member":"t"}}""",
                """{"action":"disconnect","kind":"property","source":{"component":"a","member":"s"},"target":{"robot":"r","member":"t"}}"""
            }) Reject("station_logic", json);
            Reject("station_logic", """{"action":"pulse","target":{"controller":"hardware","member":"x"}}""", "real_controller_permission_required");
        }));

        var installation = Environment.GetEnvironmentVariable("RS_OFFLINE_TEST_SDK");
        if (installation == null) return;
        tests.Add(("installed ABB templates expose the object and trigger endpoints in the pick example", () =>
        {
            XElement[] Definitions(string template)
            {
                using var library = ZipFile.OpenRead(Path.Combine(installation, "ABB Library", "Components", template + ".rslib"));
                using var stream = library.GetEntry("PIM.xml")!.Open();
                return XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "Element").ToArray();
            }
            static string? Field(XElement element, string name) => (string?)element.Element(element.Name.Namespace + name)?.Attribute("Value");
            var sensor = Definitions("LineSensor");
            True(sensor.Any(e => Field(e, "Id") == "SensedPart" && Field(e, "Type")?.EndsWith(".Part") == true));
            True(sensor.Any(e => Field(e, "Name") == "SensorOut"));
            foreach (var name in new[] { "Attacher", "Detacher" })
            {
                var component = Definitions(name);
                True(component.Any(e => Field(e, "Id") == "Child" && Field(e, "Type")?.EndsWith(".IAttachableChild") == true));
                True(component.Any(e => Field(e, "Name") == "Execute"));
            }
        }));
    }
}
