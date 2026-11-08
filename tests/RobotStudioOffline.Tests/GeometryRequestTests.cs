using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddGeometryRequestTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("box dimensions remain in millimetres and placement defaults to world identity", () =>
        {
            var request = (GeometryCreateRequest)ParseGeometry("geometry_create",
                """{"name":"Test spacer","shape":"box","size":[600,125.5,30]}""");
            Equal("Test spacer", request.Name);
            Equal("box", request.Shape);
            True(request.Size!.SequenceEqual(new[] { 600.0, 125.5, 30.0 }));
            True(request.Radius is null && request.Height is null);
            Equal(RigidFrame.Identity, request.Pose);
        }));
        tests.Add(("cylinder and sphere parse their own dimensions and world pose independently", () =>
        {
            var cylinder = (GeometryCreateRequest)ParseGeometry("geometry_create",
                """{"name":"Post","shape":"cylinder","radius":25,"height":400,"position":[-12.5,0,340]}""");
            True(cylinder.Size is null);
            Near(25, cylinder.Radius!.Value);
            Near(400, cylinder.Height!.Value);
            Equal(new Vector3D(-12.5, 0, 340), cylinder.Pose.Position);
            Equal(QuaternionD.Identity, cylinder.Pose.Orientation);
            var sphere = (GeometryCreateRequest)ParseGeometry("geometry_create",
                """{"name":"Marker","shape":"sphere","radius":7.5,"quaternion":[0,0,0,-1.0000001]}""");
            True(sphere.Size is null && sphere.Height is null);
            Near(7.5, sphere.Radius!.Value);
            Equal(new Vector3D(), sphere.Pose.Position);
            Equal(new QuaternionD(0, 0, 0, -1), sphere.Pose.Orientation);
        }));
        tests.Add(("geometry shapes reject missing, mixed, nonpositive and nonfinite dimensions", () =>
        {
            foreach (var fields in new[]
            {
                """ "shape":"box" """, """ "shape":"box","size":[1,3] """, """ "shape":"box","size":[1,2,0] """,
                """ "shape":"box","size":[1,-1,2] """, """ "shape":"box","size":[1,2,1e400] """,
                """ "shape":"box","size":[1,2,3],"radius":4 """, """ "shape":"cylinder","height":10 """,
                """ "shape":"cylinder","radius":1 """, """ "shape":"cylinder","radius":1,"height":0 """,
                """ "shape":"sphere","radius":1,"height":2 """, """ "shape":"sphere","radius":-2 """,
                """ "shape":"sphere","radius":1e400 """, """ "shape":"sphere","radius":"10" """,
                """ "shape":"cube","size":[1,2,3] """
            }) Reject("geometry_create", """{"name":"test",""" + fields + "}");
        }));
        tests.Add(("geometry imports preserve world placement and canonicalize ordinary Windows paths", () =>
        {
            foreach (var extension in new[] { "SAT", "step", "StP", "STL" })
            {
                var path = "d:/CAD/Cell layout/Préparation.v2." + extension;
                var request = (GeometryImportRequest)ParseGeometry("geometry_import", JsonSerializer.Serialize(new
                {
                    name = "Imported tool", file = path, position = new[] { 200, 0, -10 },
                    quaternion = new[] { 0, 1, 0, 0 }
                }));
                Equal(@"D:\CAD\Cell layout\Préparation.v2." + extension, request.File);
                Equal(new Vector3D(200, 0, -10), request.Pose.Position);
                Equal(new QuaternionD(0, 1, 0, 0), request.Pose.Orientation);
            }
            var identity = (GeometryImportRequest)ParseGeometry("geometry_import", GeometryFileJson(@"C:\absent.step"));
            Equal(RigidFrame.Identity, identity.Pose);
        }));
        tests.Add(("geometry pose validation shares the strict robtarget quaternion contract", () =>
        {
            foreach (var field in new[]
            {
                """ "position":[] """, """ "position":[1,2,1e400] """, """ "position":[0,"1",2] """,
                """ "quaternion":[0,0,0,0] """, """ "quaternion":[0,1,0] """,
                """ "quaternion":[2,0,0,0] """, """ "quaternion":[1e400,0,0,0] """, """ "quaternion":null """
            })
            {
                Reject("geometry_create", """{"name":"s","shape":"sphere","radius":1,""" + field + "}");
                Reject("geometry_import", """{"name":"p","file":"C:\\box.stl",""" + field + "}");
            }
        }));
        tests.Add(("CAD import rejects network, relative, traversal, stream, device and non-CAD paths", () =>
        {
            foreach (var path in new[]
            {
                @"\\server\share\part.stp", @"\\?\C:\part.stp", @"\\.\C:\part.stp", @"\\?\UNC\server\part.stp",
                "file:///C:/cad/part.stp", "http://example.test/part.stp", @"part.stp", @"C:part.stp", @"\cad\part.stp",
                @"C:\cad\..\secret.step", @"C:\cad\.\part.step", @"C:\cad\\part.stl", @"C:\cad\part.stp:alternate.stp",
                @"C:\cad\bad.\part.stp", @"C:\cad\bad \part.stp", @"C:\CON.stl", @"C:\cad\NuL.abc.stl",
                @"C:\AUX .stl", @"C:\LPT²\part.sat", @"C:\com1.part.step", @"C:\cad\CONOUT$.stl",
                @"C:\cad\*.sat", @"C:\cad\p?rt.stp", "C:\\cad\\bad\npart.step",
                @"C:\cad\part.stp ", @"C:\cad\part.stp.", @"C:\cad\part.stp.exe", @"C:\cad\part"
            }) Reject("geometry_import", GeometryFileJson(path));
        }));
        tests.Add(("geometry tools enforce exact names, duplicate-key and controller checks on both entry points", () =>
        {
            Reject("geometry_create", """{"name":"s","shape":"sphere","radius":1,"radius":2}""");
            Reject("geometry_create", """{"name":"s","shape":"sphere","radius":1,"color":"red"}""");
            Reject("geometry_create", """{"name":"  ","shape":"sphere","radius":1}""");
            Reject("geometry_import", """{"name":"p","file":"C:\\p.stl","file":"C:\\q.stl"}""");
            Reject("geometry_import", """{"file":"C:\\p.stl"}""");
            Reject("geometry_import", """{"name":"p","file":"C:\\p.stl","robot":"TestRobot"}""");
            Reject("geometry_import", """{"name":"p","file":"C:\\p.stl","controller":"192.0.2.42"}""", "real_controller_permission_required");
            Equal("real_controller_permission_required", Throws<BridgeException>(() => ParseGeometry("geometry_create",
                """{"shape":"box","size":[],"nested":{"controllerIp":"192.0.2.42"}}""")).Code);
            Equal("geometry_create", RequestParser.ParseJson("geometry_create",
                """{"name":"s","shape":"sphere","radius":1}""").Operation);
            Equal("geometry_import", RequestParser.ParseJson("geometry_import", GeometryFileJson(@"C:\p.stp")).Operation);
        }));
        if (OperatingSystem.IsWindows())
            tests.Add(("local CAD file guard distinguishes a regular file, empty file, missing file and folder", CheckLocalGeometryFile));
    }

    private static string GeometryFileJson(string file) => JsonSerializer.Serialize(new { name = "fixture", file });

    private static GeometryRequest ParseGeometry(string operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        return GeometryRequests.Parse(operation, document.RootElement);
    }

    private static void CheckLocalGeometryFile()
    {
        var fixture = Directory.CreateTempSubdirectory("RobotStudioGeometry-");
        var regular = Path.Combine(fixture.FullName, "fixture.stl");
        var empty = Path.Combine(fixture.FullName, "empty.stl");
        var missing = Path.Combine(fixture.FullName, "missing.stl");
        var folder = Path.Combine(fixture.FullName, "directory.stl");
        try
        {
            File.WriteAllText(regular, "solid fixture\nendsolid fixture");
            File.WriteAllBytes(empty, []);
            Directory.CreateDirectory(folder);
            LocalGeometryFile.RequireAvailable(regular);
            foreach (var (path, code, status) in new[]
            {
                (empty, "geometry_file_empty", 400), (missing, "geometry_file_missing", 404),
                (folder, "geometry_file_not_regular", 400)
            })
            {
                var error = Throws<BridgeException>(() => LocalGeometryFile.RequireAvailable(path));
                Equal(code, error.Code);
                Equal(status, error.StatusCode);
            }
        }
        finally
        {
            File.Delete(regular);
            File.Delete(empty);
            if (Directory.Exists(folder)) Directory.Delete(folder);
            fixture.Delete();
        }
    }
}
