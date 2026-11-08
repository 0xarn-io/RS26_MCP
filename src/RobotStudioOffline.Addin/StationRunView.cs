using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio.Stations;
using ABB.Robotics.RobotStudio.Stations.Forms;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private object RunView(Station station, RunViewRequest request)
    {
        var snapshot = CaptureStore.Snapshot(request.RunId);
        if (snapshot.Status.StationId != station.UniqueId ||
            !string.Equals(snapshot.Status.StationFile, station.FileInfo?.FullName, StringComparison.OrdinalIgnoreCase))
            throw Error("run_station_mismatch", "Open the station used to record this run before rendering its geometry.");
        if (snapshot.Samples.Length == 0) throw Error("empty_trace", "The run has no TCP samples to draw.");
        var rules = request.Assertions.Length != 0 ? request.Assertions : CaptureStore.ReadReview(request.RunId)?.Rules ?? snapshot.Request.Assertions;
        const int width = 1200, height = 800;
        // A separate native graphics control keeps the owner's view and camera intact.
        using var view = new GraphicControl { ClientSize = new Size(width, height) };
        view.CreateControl();
        view.RootObject = station;
        view.Projection = GraphicProjection.Orthographic;
        view.ViewAll(request.View == "top" ? new Vector3(0, 0, -1) : new Vector3(0, 1, 0), 0);
        // Some hosts interpret ViewAll's direction as a camera offset. Verify the actual camera side.
        if ((request.View == "top" && view.LookFrom.z < view.LookAt.z) ||
            (request.View == "side" && view.LookFrom.y > view.LookAt.y))
            view.ViewAll(request.View == "top" ? new Vector3(0, 0, 1) : new Vector3(0, -1, 0), 0);
        view.Update();
        using var bitmap = view.ScreenShot(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var trace = new Pen(Color.DeepSkyBlue, 3);
            using var zone = new Pen(Color.OrangeRed, 2);
            using var waypoint = new Pen(Color.LimeGreen, 2);
            using var heightPen = new Pen(Color.Gold, 2);
            Vector3 Metres(Vector3D p) => new(p.X / 1000, p.Y / 1000, p.Z / 1000);
            Point Project(Vector3D p) => view.ProjectPointToScreen(Metres(p));
            var points = view.ProjectPointsToScreen(snapshot.Samples.Select(s => Metres(s.WorldTcp.Position))).ToArray();
            var allowedGap = 1.5 * 1000 / snapshot.Status.RateHz;
            for (int i = 0; i < points.Length; i++)
            {
                if (i > 0 && snapshot.Samples[i].TimeMs - snapshot.Samples[i - 1].TimeMs <= allowedGap)
                    graphics.DrawLine(trace, points[i - 1], points[i]);
                else graphics.FillEllipse(Brushes.DeepSkyBlue, points[i].X - 2, points[i].Y - 2, 4, 4);
            }

            foreach (var rule in rules)
            {
                var sample = snapshot.Samples.FirstOrDefault(s => rule.Phase == null || s.Phase == rule.Phase || s.ManualPhase == rule.Phase);
                if (sample is null) continue;
                var origin = rule.Frame switch
                {
                    "base" => sample.WorldFromBase ?? FrameMath.Compose(sample.WorldTcp, FrameMath.Inverse(sample.BaseTcp)),
                    "workObject" => sample.WorldFromWorkObject ?? FrameMath.Compose(sample.WorldTcp, FrameMath.Inverse(sample.WorkObjectTcp)),
                    _ => RigidFrame.Identity
                };
                Point Local(double x, double y, double z) => Project(origin.Position + FrameMath.Rotate(origin.Orientation, new(x,y,z)));
                if (rule.Type == "waypoint")
                {
                    var pos = rule.Position!;
                    var center = Local(pos[0],pos[1],pos[2]);
                    // Orthographic projection of a tolerance sphere is a circle in each view.
                    var right = request.View == "top" ? new Vector3D(1,0,0) : new Vector3D(0,0,1);
                    var world = origin.Position + FrameMath.Rotate(origin.Orientation, new(pos[0],pos[1],pos[2]));
                    var r = rule.Tolerance!.Value;
                    var edge = Project(world + new Vector3D(right.X*r,right.Y*r,right.Z*r));
                    var radius = (float)Math.Max(2, Math.Sqrt(Math.Pow((double)edge.X-center.X,2) + Math.Pow((double)edge.Y-center.Y,2)));
                    graphics.DrawEllipse(waypoint, center.X-radius, center.Y-radius, 2*radius, 2*radius);
                    graphics.DrawString(rule.Id, SystemFonts.DefaultFont, Brushes.LimeGreen, center);
                    continue;
                }
                var lo = rule.Min!;
                var hi = rule.Max!;
                var floor = rule.Type == "min_height";
                var z0 = floor ? rule.MinZ!.Value : lo[2];
                Point[] Rectangle(double z) => [Local(lo[0],lo[1],z), Local(hi[0],lo[1],z), Local(hi[0],hi[1],z), Local(lo[0],hi[1],z)];
                var bottom = Rectangle(z0);
                graphics.DrawPolygon(floor ? heightPen : zone, bottom);
                if (!floor)
                {
                    var top = Rectangle(hi[2]);
                    graphics.DrawPolygon(zone, top);
                    for (int i = 0; i < 4; i++) graphics.DrawLine(zone,bottom[i],top[i]);
                }
                graphics.DrawString(rule.Id, SystemFonts.DefaultFont, floor ? Brushes.Gold : Brushes.OrangeRed, bottom[0]);
            }
            var label = $"{request.View.ToUpperInvariant()}   {snapshot.Status.Station}   |   {points.Length} TCP samples   |   {request.RunId}";
            var size = graphics.MeasureString(label, SystemFonts.DefaultFont);
            graphics.FillRectangle(Brushes.Black, 4, 4, size.Width + 10, size.Height + 8);
            graphics.DrawString(label, SystemFonts.DefaultFont, Brushes.White, 9, 8);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        var file = CaptureStore.SaveView(request.RunId, request.View, bytes);
        return new { mimeType = "image/png", data = Convert.ToBase64String(bytes), width, height, file, request.RunId, request.View };
    }
}
