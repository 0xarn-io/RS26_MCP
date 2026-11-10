using System.Text.Json;
using System.Xml.Linq;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private const string SafeMoveExample = """
        <SimplifiedSafetyConfiguration version="1.01.00" xmlns="urn:abb-robotics-simplified-safety-controller-configuration" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <DriveModuleConfiguration driveModuleId="1">
            <Robot name="Arm" startSpeedOffset="0.1" />
            <Tool id="1" name="SampleTool">
              <TCP x="0" y="0" z="0.150" />
              <ToolOrientation q1="1" q2="0" q3="0" q4="0"/>
              <ToolGeometry xsi:type="Sphere" name="Envelope" radius="0.2"><Center x="0" y="0" z="0.1" /></ToolGeometry>
            </Tool>
            <SafeZone id="1" name="Zone_A" top="2.5" bottom="-0.5" speedLimitPriority="NORMAL">
              <Point x="0" y="0" /><Point x="2" y="0" /><Point x="2" y="2" /><Point x="0" y="2" />
            </SafeZone>
          </DriveModuleConfiguration>
        </SimplifiedSafetyConfiguration>
        """;

    private static void AddSafeMoveInspectionTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("draft SafeMove polygon accepts either winding and concave vertices", () =>
        {
            double[][] fixture = [[0, 0], [4, 0], [4, 3], [2, 2], [0, 3]];
            SafeMovePolygon.Check(fixture);
            SafeMovePolygon.Check(fixture.Reverse().ToArray());
        }));
        tests.Add(("draft SafeMove polygon rejects bad coordinates, crossing, touching, short edges and acute corners", () =>
        {
            double[][][] invalid =
            [
                [[0, 0], [2, 0], [1, 2]], [[0, 0], [1, 0], [1, 1], [0, double.PositiveInfinity]],
                [[0, 0], [2, 0], [2, 1], [0, 1001]], [[0, 0], [2, 2], [0, 2], [2, 0]],
                [[0, 0], [2, 0], [2, 2], [1, 0], [0, 2]], [[0, 0], [2, 0], [2, 2], [0, 0.05]],
                [[0, 0], [4, 0], [1, 0.1], [0, 1]], [[0, 0], [2, 0], [2, 2], [0, 0]],
                [[0, 0], [2, 0], [2, 0.1], [0, 0.1]]
            ];
            foreach (var polygon in invalid) Equal("invalid_request", Throws<BridgeException>(() => SafeMovePolygon.Check(polygon)).Code);
        }));
        if (!OperatingSystem.IsWindows()) return; // Drive-letter file tools are Windows-only.

        tests.Add(("SafeMove XML rejects wrong namespace, DTD, stylesheets, excessive nesting and oversized files", () =>
            WithGeometryExport((input, _) =>
            {
                void RejectDocument(string text, string code)
                {
                    File.WriteAllText(input, text);
                    Equal(code, Throws<BridgeException>(() => SafeMoveInspection.Read(input, 0, 1)).Code);
                }
                RejectDocument("<SimplifiedSafetyConfiguration/>", "safemove_format_unsupported");
                RejectDocument("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///missing'>]>" + SafeMoveExample, "invalid_request");
                RejectDocument("<?xml-stylesheet href='https://example.invalid/a'?>" + SafeMoveExample, "invalid_request");
                RejectDocument(SafeMoveExample.Replace("<Robot ", string.Concat(Enumerable.Repeat("<a>", 20)) +
                    string.Concat(Enumerable.Repeat("</a>", 20)) + "<Robot "), "invalid_request");
                RejectDocument(new string('x', 2 * 1024 * 1024 + 1), "invalid_request");
            })));
        tests.Add(("SafeMove edits require an XML sibling and preserve existing files", () =>
            WithGeometryExport((input, directory) =>
            {
                foreach (var other in new[] { "../draft.xml", @"folder\draft.xml", "C:other.xml", "report.txt", "NUL.xml" })
                    Equal("invalid_request", Throws<BridgeException>(() => SafeMoveInspection.EditZoneCopy(input, other, 1, 1, -1, null, null)).Code);
                File.WriteAllText(Path.Combine(directory, "used.xml"), "preserve me");
                foreach (var existing in new[] { "used.xml", Path.GetFileName(input) })
                    Equal("safemove_output_exists", Throws<BridgeException>(() => SafeMoveInspection.EditZoneCopy(input, existing, 1, 1, -1, null, null)).Code);
                Equal("preserve me", File.ReadAllText(Path.Combine(directory, "used.xml")));
                Equal(SafeMoveExample, File.ReadAllText(input));
            })));

        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ABB", "RobotWare");
        var hasGeometrySchema = Directory.Exists(packages) && Directory.EnumerateDirectories(packages, "RobotControl_8.*")
            .Any(d => File.Exists(Path.Combine(d, "utility", "SafeMove2", "SimplifiedSafetyControllerSchema.xsd")));
        if (!hasGeometrySchema)
        {
            tests.Add(("SafeMove reports the missing RobotWare geometry schema", () => WithGeometryExport((input, _) =>
                Equal("safemove_schema_unavailable", Throws<BridgeException>(() => SafeMoveInspection.Read(input, 0, 1)).Code))));
            return;
        }
        tests.Add(("ABB XSD checked geometry inventory includes units, frame, nested tool geometry and pagination", () =>
            WithGeometryExport((input, _) =>
            {
                var json = JsonSerializer.SerializeToElement(SafeMoveInspection.Read(input, 2, 1));
                Equal(4, json.GetProperty("total").GetInt32());
                Equal(3, json.GetProperty("nextOffset").GetInt32());
                Equal("controllerWorld", json.GetProperty("zoneFrame").GetString());
                True(json.GetProperty("schemaChecked").GetBoolean() && !json.GetProperty("activeConfigurationVerified").GetBoolean());
                var tool = json.GetProperty("items")[0].GetProperty("geometry");
                Equal("Tool", tool.GetProperty("kind").GetString());
                Equal("Sphere", tool.GetProperty("children")[2].GetProperty("attributes").GetProperty("xsi:type").GetString());
                Near(0.15, tool.GetProperty("children")[0].GetProperty("attributes").GetProperty("z").GetDouble());
            })));
        tests.Add(("ABB geometry edit produces a re-readable draft, preserving unrelated tool and source", () =>
            WithGeometryExport((input, directory) =>
            {
                var result = JsonSerializer.SerializeToElement(SafeMoveInspection.EditZoneCopy(input, "candidate.xml", 1, 1, -0.75, null,
                    [[-1, -1], [4, -1], [4, 1], [-1, 1]]));
                True(!result.GetProperty("appliedToController").GetBoolean());
                True(!result.GetProperty("safetyValidationVerified").GetBoolean());
                var output = Path.Combine(directory, "candidate.xml");
                var page = JsonSerializer.SerializeToElement(SafeMoveInspection.Read(output, 3, 1));
                var zone = page.GetProperty("items")[0].GetProperty("geometry");
                Near(-0.75, zone.GetProperty("attributes").GetProperty("bottom").GetDouble());
                Near(2.5, zone.GetProperty("attributes").GetProperty("top").GetDouble());
                Near(4, zone.GetProperty("children")[1].GetProperty("attributes").GetProperty("x").GetDouble());
                Equal(SafeMoveExample, File.ReadAllText(input));
                XNamespace ns = "urn:abb-robotics-simplified-safety-controller-configuration";
                True(XNode.DeepEquals(XDocument.Load(input).Descendants(ns + "Tool").Single(), XDocument.Load(output).Descendants(ns + "Tool").Single()));
            })));
        tests.Add(("nonfinite measurements outside the selected zone fail inspection and draft creation", () =>
            WithGeometryExport((input, directory) =>
            {
                File.WriteAllText(input, SafeMoveExample.Replace("startSpeedOffset=\"0.1\"", "startSpeedOffset=\"NaN\""));
                Throws<BridgeException>(() => SafeMoveInspection.Read(input, 3, 1));
                Throws<BridgeException>(() => SafeMoveInspection.EditZoneCopy(input, "preview.xml", 1, 1, null, 2, null));
                True(!File.Exists(Path.Combine(directory, "preview.xml")));
            })));
        tests.Add(("SafeMove rejects missing, duplicate and malformed zones without writing a draft", () =>
            WithGeometryExport((input, directory) =>
            {
                void RejectEdit(int id, double bottom, string code)
                {
                    Equal(code, Throws<BridgeException>(() => SafeMoveInspection.EditZoneCopy(input, "new.xml", 1, id, bottom, null, null)).Code);
                    True(!File.Exists(Path.Combine(directory, "new.xml")));
                }
                RejectEdit(2, 0, "safemove_zone_missing");
                RejectEdit(1, 3, "invalid_request");
                var doc = XDocument.Parse(SafeMoveExample);
                var zone = doc.Root!.Elements().Single().Elements().Last();
                zone.AddAfterSelf(new XElement(zone));
                doc.Save(input);
                RejectEdit(1, 0, "invalid_request");
                File.WriteAllText(input, SafeMoveExample.Replace("top=\"2.5\"", "top=\"bad\""));
                RejectEdit(1, 0, "safemove_schema_mismatch");
            })));
    }

    private static void WithGeometryExport(Action<string, string> test)
    {
        var directory = Directory.CreateTempSubdirectory("RobotStudioGeometryXml-");
        try
        {
            var input = Path.Combine(directory.FullName, "source.xml");
            File.WriteAllText(input, SafeMoveExample);
            test(input, directory.FullName);
        }
        finally { directory.Delete(recursive: true); }
    }
}
