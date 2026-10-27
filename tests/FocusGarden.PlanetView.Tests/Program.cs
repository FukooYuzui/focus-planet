using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using FocusGarden.App;
using FocusGarden.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==4&&args[0] is "--render-only" or "--render-edit-only" or "--render-surface-only"){Render(args[1],int.Parse(args[2]),args[3],args[0]=="--render-edit-only",args[0]=="--render-surface-only");return 0;}
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
        Test("surface framing keeps real terrain below the sky and beyond three viewport edges",()=>
        {
            var framingFailures=new List<string>();
            foreach(var size in new[]{new Size(900,600),new Size(1400,500),new Size(500,760)})
            {
                var c=ArrangedPlanet(size);c.SetSurfaceView(true);var view=Viewport(c);
                foreach(double x in new[]{.1,.5,.9})foreach(double y in new[]{.12,.30})
                    Assert(!BodyHit(view,new Point(view.ActualWidth*x,view.ActualHeight*y)), $"{size}: terrain intrudes into upper sky");
                var rows=new List<int>();
                for(int y=0;y<view.ActualHeight;y+=4)if(BodyHit(view,new Point(view.ActualWidth/2,y)))rows.Add(y);
                Assert(rows.Count>0,$"{size}: terrain is missing");
                double horizon=rows.Min()/view.ActualHeight;
                Assert(horizon>=.42&&horizon<=.68,$"{size}: central horizon at {horizon:P1}, expected lower-half framing");
                bool left=BodyHit(view,new Point(1,view.ActualHeight-2)),right=BodyHit(view,new Point(view.ActualWidth-2,view.ActualHeight-2)),bottom=BodyHit(view,new Point(view.ActualWidth/2,view.ActualHeight-2));
                Console.WriteLine($"  Surface {size.Width:0}×{size.Height:0}: horizon {horizon:P1}; body at left/right/bottom = {left}/{right}/{bottom}");
                if(!left||!right||!bottom)framingFailures.Add($"{size}: terrain must leave both side edges and the bottom (left={left}, right={right}, bottom={bottom})");
            }
            Assert(framingFailures.Count==0,string.Join("; ",framingFailures));
        });
        Test("surface camera stays outside the actual sphere across zoom bounds and aspect ratios",()=>
        {
            foreach(var size in new[]{new Size(900,600),new Size(1400,500),new Size(500,760)})
            {
                var c=ArrangedPlanet(size);c.SetSurfaceView(true);
                foreach(double value in new[]{-100.0,.86,1,1.18,100,double.NaN,double.PositiveInfinity})
                {
                    c.SetZoom(value);c.RotateView(37,-11);
                    var camera=(PerspectiveCamera)Viewport(c).Camera;
                    double distance=((Vector3D)camera.Position).Length;
                    Assert(double.IsFinite(distance)&&distance>2.7+camera.NearPlaneDistance,$"{size}: camera enters body or near plane clips it");
                    Assert(c.Zoom>=.86&&c.Zoom<=1.18&&camera.LookDirection.Length>0&&camera.FarPlaneDistance>distance+2.7);
                }
                c.SetZoom(-100);Assert(c.Zoom==.86);c.SetZoom(100);Assert(c.Zoom==1.18);
            }
        });
        Test("view modes restore independent zoom and preserve the same logical direction",()=>
        {
            var c=ArrangedPlanet(new Size(900,600));c.RotateView(27,9);c.SetZoom(.67);
            var orbitDirection=Orientation(c);int builds=c.SceneBuildCount;var scene=Scene(c);
            c.SetSurfaceView(true);Assert(c.IsSurfaceView&&c.Zoom==1);c.SetZoom(1.12);
            c.SetSurfaceView(false);Assert(!c.IsSurfaceView&&c.Zoom==.67);Assert(MatrixClose(orbitDirection,Orientation(c)));
            c.SetZoom(1.24);c.SetSurfaceView(true);Assert(c.Zoom==1.12);
            c.SetSurfaceView(true);Assert(c.Zoom==1.12);c.RotateView(15,-6);c.SetSurfaceView(false);
            var expected=ArrangedPlanet(new Size(900,600));expected.RotateView(42,3);
            Assert(c.Zoom==1.24&&MatrixClose(Orientation(c),Orientation(expected)));
            Assert(c.SceneBuildCount==builds&&ReferenceEquals(scene,Scene(c)),"Mode changes rebuilt the scene");
        });
        Test("surface wheel zoom preserves buildings selected plots and the existing scene",()=>
        {
            var c=ArrangedPlanet(new Size(900,600));c.SetState(4,"clear",false,false,42);
            var buildings=new[]{new PlanetBuildingPlacement("fixture-house","house",0,2),new PlanetBuildingPlacement("fixture-tree","tree",7,1)};
            c.SetBuildings(buildings);c.SetEditMode(true);c.SelectPlot(0);
            int builds=c.SceneBuildCount;var scene=Scene(c);var direction=Orientation(c);var signature=Signature((Model3DGroup)scene.Content);
            c.SetSurfaceView(true);var surfaceDirection=Orientation(c);
            RaiseWheel(c,120);Assert(c.Zoom<1&&c.Zoom>.86,"One surface wheel notch should adjust gently without hitting its near limit");RaiseWheel(c,-120);
            for(int n=0;n<8;n++)RaiseWheel(c,120);
            Assert(c.Zoom==.86&&c.SelectedPlotId==0&&MatrixClose(surfaceDirection,Orientation(c)));
            c.SetSurfaceView(false);
            Assert(c.Zoom==1&&c.SelectedPlotId==0&&c.FeatureCounts["house"]==1);
            Assert(c.SceneBuildCount==builds&&ReferenceEquals(scene,Scene(c))&&MatrixClose(direction,Orientation(c)));
            Assert(signature==Signature((Model3DGroup)scene.Content),"Wheel or mode switch mutated stored world geometry");
            c.SetBuildings(buildings);Assert(c.SceneBuildCount==builds,"Existing building data changed during navigation");
        });
        Test("surface mode exposes real fixed front plots while empty sky remains unselectable",()=>
        {
            var c=ArrangedPlanet(new Size(900,600));c.SetState(3,"clear",false,false,42);c.SetEditMode(true);c.SetSurfaceView(true);
            var view=Viewport(c);var found=new HashSet<int>();
            for(int y=(int)(view.ActualHeight*.42);y<view.ActualHeight-3;y+=10)
                for(int x=8;x<view.ActualWidth-8;x+=10)if(c.HitTestPlot(new Point(x,y)) is int plot)found.Add(plot);
            Assert(found.Count>=1,"No real fixed plot responds in surface view");
            Assert(found.All(p=>p>=0&&p<PlanetView3DScene.Plots.Count));
            Assert(c.HitTestPlot(new Point(view.ActualWidth/2,view.ActualHeight*.12))==null);
            int selected=found.Min();c.SelectPlot(selected);Assert(c.SelectedPlotId==selected);
            Console.WriteLine($"  Surface picking reaches {found.Count} fixed plots: {string.Join(", ",found.Order())}");
        });
        Test("reset retains surface mode and resets only its own zoom and current orientation",()=>
        {
            var c=ArrangedPlanet(new Size(900,600));c.SetZoom(.72);c.SetSurfaceView(true);c.SetZoom(1.15);c.RotateView(50,-30);
            int builds=c.SceneBuildCount;c.ResetView();
            var expected=ArrangedPlanet(new Size(900,600));expected.SetSurfaceView(true);
            Assert(c.IsSurfaceView&&c.Zoom==1&&MatrixClose(Orientation(c),Orientation(expected)));
            Assert(c.SceneBuildCount==builds);
            c.SetSurfaceView(false);expected.SetSurfaceView(false);
            Assert(c.Zoom==.72&&MatrixClose(Orientation(c),Orientation(expected)));
            c.SetSurfaceView(true);Assert(c.Zoom==1);
        });
        Test("surface animation keeps creatures and clouds moving without automatic globe rotation",()=>
        {
            var c=ArrangedPlanet(new Size(900,600));c.SetState(3,"cloudy",true,false,42);c.SetSurfaceView(true);
            Assert(c.FeatureCounts["creature"]>0&&c.FeatureCounts["cloud"]>0&&c.MovingObjectCount>0);
            var direction=Orientation(c);double before=c.MotionOffsetTotal;int builds=c.SceneBuildCount;
            AdvanceAnimationTick(c);
            Assert(c.MotionOffsetTotal!=before,"Surface mode stopped creature/cloud animation");
            Assert(MatrixClose(direction,Orientation(c)),"Surface mode automatically rotated the globe");
            Assert(c.SceneBuildCount==builds);
            c.SetSurfaceView(false);direction=Orientation(c);AdvanceAnimationTick(c);
            Assert(!MatrixClose(direction,Orientation(c)),"Control check: focusing overview did not advance automatic rotation");
        });
        Test("surface navigation remains stable when resized without changing layout or mode",()=>
        {
            var c=ArrangedPlanet(new Size(900,600));c.SetState(3,"clear",false,false,42);c.SetBuildings([new("resize-house","house",0,0)]);
            c.SetEditMode(true);c.SelectPlot(0);c.SetSurfaceView(true);c.SetZoom(1.08);c.RotateView(-18,7);
            int builds=c.SceneBuildCount;var direction=Orientation(c);var scene=Scene(c);
            foreach(var size in new[]{new Size(500,760),new Size(1400,500),new Size(900,600)})
            {
                Arrange(c,size);Assert(c.IsSurfaceView&&c.Zoom==1.08&&c.SelectedPlotId==0);
                Assert(c.SceneBuildCount==builds&&ReferenceEquals(scene,Scene(c))&&MatrixClose(direction,Orientation(c)));
                var camera=(PerspectiveCamera)Viewport(c).Camera;Assert(((Vector3D)camera.Position).Length>2.7+camera.NearPlaneDistance);
            }
        });
        Console.WriteLine($"{pass} passed / {fail} failed");
        if(args.Length==4&&args[0] is "--render" or "--render-edit")Render(args[1],int.Parse(args[2]),args[3],args[0]=="--render-edit");
        return fail==0?0:1;
    }
    private static void Render(string path,int level,string weather,bool edit=false,bool surface=false)
    {
        const int width=1100,height=760;
        var layout=new Grid {Width=width,Height=height,Background=new SolidColorBrush(Color.FromRgb(12,22,28))};layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());
        var heading=new StackPanel {Margin=new Thickness(28,20,28,6)};heading.Children.Add(new TextBlock {Text=surface?"专注星球 · 地表近景演示":"专注星球 · 成长场景演示",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=26,Foreground=Brushes.White});heading.Children.Add(new TextBlock {Text=$"演示等级 Lv.{level} · 合成状态，仅用于展示景观；不代表真实专注或 Token 进度",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(190,207,200)),Margin=new Thickness(0,7,0,0)});layout.Children.Add(heading);
        var control=new PlanetView3D();control.SetState(level,weather,false,weather=="aurora");control.SetSurfaceView(surface);
        var frontPlots=PlanetView3DScene.Plots.Select((p,i)=>(p,i)).Where(p=>PlanetView3DGeometry.Direction(p.p.Latitude,p.p.Longitude).Z>.4).OrderBy(p=>surface?Math.Abs(p.p.Latitude-20):p.p.Latitude).ToArray();var demoBuildings=new List<PlanetBuildingPlacement>();if(level>=3)for(int i=0;i<Math.Min(3,frontPlots.Length);i++)demoBuildings.Add(new("demo"+i,"house",frontPlots[i].i,i%4));if(level>=4&&frontPlots.Length>5){demoBuildings.Add(new("demoWindmill","windmill",frontPlots[4].i,0));demoBuildings.Add(new("demoTower","tower",frontPlots[5].i,0));}if(level>=5&&frontPlots.Length>6)demoBuildings.Add(new("demoObservatory","observatory",frontPlots[^1].i,0));control.SetBuildings(demoBuildings);
        Grid.SetRow(control,1);layout.Children.Add(control);layout.Measure(new Size(width,height));layout.Arrange(new Rect(0,0,width,height));layout.UpdateLayout();
        if(edit){control.SetEditMode(true);control.SelectPlot(0);layout.UpdateLayout();}
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(layout);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var absolute=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);using var stream=File.Create(absolute);encoder.Save(stream);Console.WriteLine("Synthetic demonstration preview: "+absolute);
    }
    private static void CheckGroup(Model3DGroup group){foreach(var child in group.Children)if(child is GeometryModel3D g)CheckMesh((MeshGeometry3D)g.Geometry);else if(child is Model3DGroup nested)CheckGroup(nested);}
    private static int Triangles(Model3DGroup group)=>group.Children.Sum(m=>m is GeometryModel3D g?((MeshGeometry3D)g.Geometry).TriangleIndices.Count/3:m is Model3DGroup nested?Triangles(nested):0);
    private static void CheckMesh(MeshGeometry3D mesh){Assert(mesh.IsFrozen);Assert(mesh.Positions.Count==mesh.Normals.Count);Assert(mesh.Positions.All(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.Z)));for(int i=0;i<mesh.TriangleIndices.Count;i+=3){int a=mesh.TriangleIndices[i],b=mesh.TriangleIndices[i+1],c=mesh.TriangleIndices[i+2];var cross=Vector3D.CrossProduct(mesh.Positions[b]-mesh.Positions[a],mesh.Positions[c]-mesh.Positions[a]);Assert(cross.Length>1e-9&&Vector3D.DotProduct(cross,mesh.Normals[a])>0);}}
    private static string Signature(Model3DGroup group){var text=new System.Text.StringBuilder();void Walk(Model3D m){text.Append(m.Transform.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));if(m is GeometryModel3D g)foreach(var p in ((MeshGeometry3D)g.Geometry).Positions)text.Append(p.ToString(System.Globalization.CultureInfo.InvariantCulture));else if(m is Model3DGroup inner)foreach(var c in inner.Children)Walk(c);}Walk(group);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));}
    private static PlanetView3D ArrangedPlanet(Size size){var c=new PlanetView3D();Arrange(c,size);return c;}
    private static void Arrange(PlanetView3D control,Size size){control.Width=size.Width;control.Height=size.Height;control.Measure(size);control.Arrange(new Rect(new Point(),size));control.UpdateLayout();}
    private static T Field<T>(PlanetView3D control,string name)=>(T)typeof(PlanetView3D).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
    private static Viewport3D Viewport(PlanetView3D control)=>Field<Viewport3D>(control,"viewport");
    private static ModelVisual3D Scene(PlanetView3D control)=>Field<ModelVisual3D>(control,"scene");
    private static Matrix3D Orientation(PlanetView3D control)=>Field<Transform3DGroup>(control,"planetTransform").Value;
    private static bool MatrixClose(Matrix3D a,Matrix3D b)=>new[]{Math.Abs(a.M11-b.M11),Math.Abs(a.M12-b.M12),Math.Abs(a.M13-b.M13),Math.Abs(a.M21-b.M21),Math.Abs(a.M22-b.M22),Math.Abs(a.M23-b.M23),Math.Abs(a.M31-b.M31),Math.Abs(a.M32-b.M32),Math.Abs(a.M33-b.M33)}.All(v=>v<1e-9);
    private static bool BodyHit(Viewport3D view,Point point)
    {
        bool found=false;
        VisualTreeHelper.HitTest(view,null,result=>
        {
            if(result is RayMeshGeometry3DHitTestResult hit&&hit.MeshHit.Positions.Count>1000&&hit.MeshHit.Positions.Take(3).All(p=>Math.Abs(((Vector3D)p).Length-2.7)<.001))
            {found=true;return HitTestResultBehavior.Stop;}
            return HitTestResultBehavior.Continue;
        },new PointHitTestParameters(point));
        return found;
    }
    private static void RaiseWheel(PlanetView3D control,int delta)
    {
        var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent};
        Viewport(control).RaiseEvent(wheel);Assert(wheel.Handled,"Routed mouse-wheel input was not handled by the scene");
    }
    private static void AdvanceAnimationTick(PlanetView3D control)
    {
        // Exercise the real registered timer callback in a bounded offscreen dispatcher frame.
        var timer=Field<DispatcherTimer>(control,"animation");var frame=new DispatcherFrame();bool ticked=false;
        var timeout=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};
        EventHandler finish=(_,_)=>{ticked=true;frame.Continue=false;};
        EventHandler timedOut=(_,_)=>frame.Continue=false;
        timer.Tick+=finish;timeout.Tick+=timedOut;
        try{timeout.Start();timer.Start();Dispatcher.PushFrame(frame);}
        finally{timer.Stop();timeout.Stop();timer.Tick-=finish;timeout.Tick-=timedOut;}
        Assert(ticked,"Animation timer did not tick within the bounded offscreen check");
    }
    private static void Assert(bool value,string message="Assertion failed."){if(!value)throw new Exception(message);}
    private static void Throws(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Expected argument validation.");}
}
