using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FocusGarden.App;
using FocusGarden.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==4&&args[0] is "--render-only" or "--render-edit-only"){Render(args[1],int.Parse(args[2]),args[3],args[0]=="--render-edit-only");return 0;}
        int pass=0,fail=0;
        void Test(string name,Action action){try{action();Console.WriteLine("PASS "+name);pass++;}catch(Exception ex){Console.WriteLine("FAIL "+name+": "+ex);fail++;}}
        Test("sphere has outward, finite, nondegenerate faces",()=>{var mesh=PlanetView3DGeometry.Ellipsoid(0,0,0,2.7,2.7,2.7,3);CheckMesh(mesh);Assert(mesh.TriangleIndices.Count==1280*3);for(int i=0;i<mesh.Positions.Count;i+=3)Assert(Vector3D.DotProduct((Vector3D)mesh.Positions[i],mesh.Normals[i])>0);});
        Test("boxes and tapered cones have outward normals",()=>{var box=PlanetView3DGeometry.Box(-1,-1,-1,2,2,2);CheckMesh(box);for(int i=0;i<box.Positions.Count;i+=3)Assert(Vector3D.DotProduct((Vector3D)box.Positions[i],box.Normals[i])>0);var cone=PlanetView3DGeometry.Frustum(1,.3,2,8);CheckMesh(cone);for(int i=0;i<cone.Positions.Count;i+=3){var center=(cone.Positions[i]+(Vector3D)cone.Positions[i+1]+(Vector3D)cone.Positions[i+2]);Assert(Vector3D.DotProduct((Vector3D)center/3-new Vector3D(0,1,0),cone.Normals[i])>0);}Throws(()=>PlanetView3DGeometry.Ellipsoid(0,0,0,0,1,1));});
        Test("landmarks stand along the sphere normal at every latitude",()=>{foreach(double lat in new[]{-90.0,-45,0,35,90})foreach(double lon in new[]{-180.0,-30,80,180}){var transform=PlanetView3DGeometry.SurfaceTransform(lat,lon);var origin=transform.Transform(new Point3D());var up=transform.Transform(new Vector3D(0,1,0));var n=PlanetView3DGeometry.Direction(lat,lon);Assert(Math.Abs(((Vector3D)origin).Length-2.69)<1e-9);Assert(Vector3D.DotProduct(up,n)>.999999);}});
        Test("natural growth reveals vegetation and life but never auto-builds homes",()=>{for(int level=0;level<=5;level++){var s=PlanetView3DScene.Build(level,"clear",false,42);Assert(s.Model.IsFrozen);Assert(s.Features.ContainsKey("terrain"));Assert(s.Features.ContainsKey("tree")== (level>=1));Assert(s.Features.ContainsKey("creature")== (level>=2));Assert(!s.Features.ContainsKey("house")&&!s.Features.ContainsKey("windmill")&&!s.Features.ContainsKey("observatory"));Assert(s.Features.ContainsKey("ring")== (level>=5));CheckGroup(s.Model);}});
        Test("weather has distinct visible scene geometry",()=>{var cloud=PlanetView3DScene.Build(3,"cloudy",false,42);var rain=PlanetView3DScene.Build(3,"rain",false,42);var aurora=PlanetView3DScene.Build(5,"aurora",true,42);Assert(cloud.Features["cloud"]==3&&!cloud.Features.ContainsKey("rain"));Assert(rain.Features["rain"]==2);Assert(aurora.Features["aurora"]==3);CheckGroup(rain.Model);CheckGroup(aurora.Model);foreach(var motion in rain.Motions)CheckGroup(motion.Model);});
        Test("seed fixes geometry and alternate seeds produce variants",()=>{var a=Signature(PlanetView3DScene.Build(5,"clear",false,42).Model);var b=Signature(PlanetView3DScene.Build(5,"clear",false,42).Model);var c=Signature(PlanetView3DScene.Build(5,"clear",false,43).Model);Assert(a==b&&a!=c);});
        Test("control validates states and skips redundant rebuilds",()=>{var c=new PlanetView3D();c.SetState(3,"clear",false,false);int built=c.SceneBuildCount;c.SetState(3,"clear",false,false);Assert(c.SceneBuildCount==built);c.SetState(3,"clear",true,false);Assert(c.SceneBuildCount==built&&!c.AnimationRunning);c.SetState(3,"rain",true,false);Assert(c.SceneBuildCount==built+1);Throws(()=>c.SetState(-1,"clear",false,false));Throws(()=>c.SetState(6,"clear",false,false));Throws(()=>c.SetState(0,"unknown",false,false));c.SetZoom(.1);Assert(c.Zoom==.45);c.SetZoom(100);Assert(c.Zoom==1.7);c.ResetView();Assert(c.Zoom==1);});
        Test("32 stable spherical plots expose editable hit geometry",()=>{Assert(PlanetView3DScene.Plots.Count==32);var s=PlanetView3DScene.Build(4,"clear",false,42,[new("a","house",0,1),new("b","windmill",9,3)],true,0);Assert(s.Features["plot"]==32&&s.Features["house"]==1&&s.Features["windmill"]==1);Assert(s.PlotHits.Values.Distinct().Count()==32);CheckGroup(s.Model);var normal=PlanetView3DScene.Build(4,"clear",false,42);Assert(normal.PlotHits.Count==0);});
        Test("layout inputs are snapshotted, validated, and rebuild only on change",()=>{var c=new PlanetView3D();var list=new List<PlanetBuildingPlacement>{new("a","house",0,1)};c.SetBuildings(list);int built=c.SceneBuildCount;c.SetBuildings(list);Assert(c.SceneBuildCount==built);list.Add(new("b","tree",1,0));Assert(!c.FeatureCounts.ContainsKey("tree"));c.SetBuildings(list);Assert(c.FeatureCounts["tree"]==1);c.SetEditMode(true);c.SelectPlot(1);Assert(c.SelectedPlotId==1&&c.FeatureCounts["plot"]==32);c.SetEditMode(false);Assert(c.SelectedPlotId==null&&!c.FeatureCounts.ContainsKey("plot"));Throws(()=>c.SetBuildings([new("a","house",32,0)]));Throws(()=>c.SetBuildings([new("a","house",0,0),new("b","tree",0,0)]));Throws(()=>c.SetBuildings([new("a","invalid",0,0)]));});
        Test("rendered surface hit testing finds front plots and respects occlusion",()=>{var c=new PlanetView3D {Width=700,Height=500};c.SetState(3,"clear",false,false);c.SetEditMode(true);c.Measure(new Size(700,500));c.Arrange(new Rect(0,0,700,500));c.UpdateLayout();var hits=new HashSet<int>();for(int y=20;y<450;y+=7)for(int x=70;x<630;x+=7){var hit=c.HitTestPlot(new Point(x,y));if(hit.HasValue)hits.Add(hit.Value);}Assert(hits.Count>=4&&hits.Count<20);Assert(c.HitTestPlot(new Point(0,0))==null);Console.WriteLine($"  {hits.Count} visible plots respond to hit testing");});
        Test("animals and clouds move without rebuilding frozen meshes",()=>{var c=new PlanetView3D();c.SetState(3,"cloudy",false,false);Assert(c.MovingObjectCount==c.FeatureCounts["creature"]+c.FeatureCounts["cloud"]&&c.FeatureCounts["creature"]>=10);int built=c.SceneBuildCount;c.AnimateScene(0);double before=c.MotionOffsetTotal;c.AnimateScene(3);Assert(c.MotionOffsetTotal!=before&&c.SceneBuildCount==built);Assert(!c.AnimationRunning);c.SetEditMode(true);Assert(!c.AnimationRunning);c.SetState(0,"clear",false,false);Assert(c.MovingObjectCount==0);});
        Test("detailed worlds remain bounded and barren worlds have geological variety",()=>
        {
            foreach(int level in new[]{0,5})
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();var s=PlanetView3DScene.Build(level,"cloudy",false,42);watch.Stop();
                int triangles=Triangles(s.Model)+s.Motions.Sum(m=>Triangles(m.Model));Assert(triangles<220000);
                if(level==0){Assert(s.Features["crater"]>=5&&s.Features["crystal"]>=5&&s.Features["rocks"]>=20);}
                else Assert(s.Features["tree"]>=25&&s.Features.ContainsKey("mushrooms")&&s.Features.ContainsKey("habitat"));
                Console.WriteLine($"  Lv.{level}: {triangles:N0} triangles, {watch.ElapsedMilliseconds} ms scene build");
            }
        });
        Test("default camera makes the body prominent in a wide viewport",()=>
        {
            var c=new PlanetView3D {Width=1100,Height=650};c.Measure(new Size(1100,650));c.Arrange(new Rect(0,0,1100,650));c.UpdateLayout();
            var view=(Viewport3D)typeof(PlanetView3D).GetField("viewport",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(c)!;
            var rows=new List<int>();
            for(int y=0;y<view.ActualHeight;y+=5){bool body=false;VisualTreeHelper.HitTest(view,null,r=>{if(r is RayMeshGeometry3DHitTestResult hit&&hit.MeshHit.Positions.Take(3).All(p=>Math.Abs(((Vector3D)p).Length-2.7)<.001)){body=true;return HitTestResultBehavior.Stop;}return HitTestResultBehavior.Continue;},new PointHitTestParameters(new Point(view.ActualWidth/2,y)));if(body)rows.Add(y);}
            double fill=(rows.Max()-rows.Min())/view.ActualHeight;Console.WriteLine($"  Planet body fills {fill:P0} of viewport height");Assert(fill>.78&&fill<.98);
        });
        Test("continuous surface map closes its seam and keeps poles inside texture rows",()=>
        {
            var s=PlanetView3DScene.Build(5,"clear",false,42);var surface=(GeometryModel3D)s.Model.Children[0];
            var mesh=(MeshGeometry3D)surface.Geometry;var brush=(ImageBrush)((DiffuseMaterial)surface.Material).Brush;var map=(BitmapSource)brush.ImageSource;
            Assert(mesh.TextureCoordinates.Count==mesh.Positions.Count&&mesh.TriangleIndices.Count/3==5120&&map.IsFrozen);
            for(int i=0;i<mesh.TextureCoordinates.Count;i+=3){var uv=mesh.TextureCoordinates.Skip(i).Take(3).ToArray();Assert(uv.Max(p=>p.X)-uv.Min(p=>p.X)<=.500001);Assert(uv.All(p=>p.Y>=.5/map.PixelHeight&&p.Y<=1-.5/map.PixelHeight));}
            var pixels=new byte[map.PixelWidth*map.PixelHeight*4];map.CopyPixels(pixels,map.PixelWidth*4,0);
            for(int y=0;y<map.PixelHeight;y++)for(int channel=0;channel<4;channel++)Assert(pixels[(y*map.PixelWidth)*4+channel]==pixels[(y*map.PixelWidth+map.PixelWidth-1)*4+channel]);
            Assert(ReferenceEquals(surface,PlanetView3DScene.Build(5,"cloudy",true,43).Model.Children[0]));
        });
        Console.WriteLine($"{pass} passed / {fail} failed");
        if(args.Length==4&&args[0] is "--render" or "--render-edit")Render(args[1],int.Parse(args[2]),args[3],args[0]=="--render-edit");
        return fail==0?0:1;
    }
    private static void Render(string path,int level,string weather,bool edit=false)
    {
        const int width=1100,height=760;
        var layout=new Grid {Width=width,Height=height,Background=new SolidColorBrush(Color.FromRgb(12,22,28))};layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());
        var heading=new StackPanel {Margin=new Thickness(28,20,28,6)};heading.Children.Add(new TextBlock {Text="专注星球 · 成长场景演示",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=26,Foreground=Brushes.White});heading.Children.Add(new TextBlock {Text=$"演示等级 Lv.{level} · 合成状态，仅用于展示景观；不代表真实专注或 Token 进度",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(190,207,200)),Margin=new Thickness(0,7,0,0)});layout.Children.Add(heading);
        var control=new PlanetView3D();control.SetState(level,weather,false,weather=="aurora");
        var frontPlots=PlanetView3DScene.Plots.Select((p,i)=>(p,i)).Where(p=>PlanetView3DGeometry.Direction(p.p.Latitude,p.p.Longitude).Z>.4).OrderBy(p=>p.p.Latitude).ToArray();var demoBuildings=new List<PlanetBuildingPlacement>();if(level>=3)for(int i=0;i<Math.Min(3,frontPlots.Length);i++)demoBuildings.Add(new("demo"+i,"house",frontPlots[i].i,i%4));if(level>=4&&frontPlots.Length>5){demoBuildings.Add(new("demoWindmill","windmill",frontPlots[4].i,0));demoBuildings.Add(new("demoTower","tower",frontPlots[5].i,0));}if(level>=5&&frontPlots.Length>6)demoBuildings.Add(new("demoObservatory","observatory",frontPlots[^1].i,0));control.SetBuildings(demoBuildings);
        Grid.SetRow(control,1);layout.Children.Add(control);layout.Measure(new Size(width,height));layout.Arrange(new Rect(0,0,width,height));layout.UpdateLayout();
        if(edit){control.SetEditMode(true);control.SelectPlot(0);layout.UpdateLayout();}
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(layout);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var absolute=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);using var stream=File.Create(absolute);encoder.Save(stream);Console.WriteLine("Synthetic demonstration preview: "+absolute);
    }
    private static void CheckGroup(Model3DGroup group){foreach(var child in group.Children)if(child is GeometryModel3D g)CheckMesh((MeshGeometry3D)g.Geometry);else if(child is Model3DGroup nested)CheckGroup(nested);}
    private static int Triangles(Model3DGroup group)=>group.Children.Sum(m=>m is GeometryModel3D g?((MeshGeometry3D)g.Geometry).TriangleIndices.Count/3:m is Model3DGroup nested?Triangles(nested):0);
    private static void CheckMesh(MeshGeometry3D mesh){Assert(mesh.IsFrozen);Assert(mesh.Positions.Count==mesh.Normals.Count);Assert(mesh.Positions.All(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.Z)));for(int i=0;i<mesh.TriangleIndices.Count;i+=3){int a=mesh.TriangleIndices[i],b=mesh.TriangleIndices[i+1],c=mesh.TriangleIndices[i+2];var cross=Vector3D.CrossProduct(mesh.Positions[b]-mesh.Positions[a],mesh.Positions[c]-mesh.Positions[a]);Assert(cross.Length>1e-9&&Vector3D.DotProduct(cross,mesh.Normals[a])>0);}}
    private static string Signature(Model3DGroup group){var text=new System.Text.StringBuilder();void Walk(Model3D m){text.Append(m.Transform.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));if(m is GeometryModel3D g)foreach(var p in ((MeshGeometry3D)g.Geometry).Positions)text.Append(p.ToString(System.Globalization.CultureInfo.InvariantCulture));else if(m is Model3DGroup inner)foreach(var c in inner.Children)Walk(c);}Walk(group);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));}
    private static void Assert(bool value){if(!value)throw new Exception("Assertion failed.");}
    private static void Throws(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Expected argument validation.");}
}
