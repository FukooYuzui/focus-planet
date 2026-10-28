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
        if(args.Length==2&&args[0]=="--render-ecology-only"){RenderEcology(args[1]);return 0;}
        if(args.Length==2&&args[0]=="--render-models-only"){RenderModelSheet(args[1]);return 0;}
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
        Test("selection changes only the old and new markers while keeping the same natural world",()=>
        {
            var c=new PlanetView3D();c.ApplyState(5,"rain",true,false,42,[new("house","house",0,1),new("rabbit","rabbit",8,0)],true);
            var body=Field<Model3DGroup>(c,"planetBody");var originalWorld=body.Children[0];var content=Scene(c).Content;int builds=c.SceneBuildCount;
            var before=body.Children.ToArray();c.SelectPlot(0);var selected=body.Children.ToArray();
            Assert(before.Length==selected.Length&&before.Where((model,index)=>!ReferenceEquals(model,selected[index])).Count()==1,"Selecting an empty selection changed more than its marker");
            c.SelectPlot(8);var changed=body.Children.ToArray();
            Assert(selected.Where((model,index)=>!ReferenceEquals(model,changed[index])).Count()==2,"Selection rebuilt a habitat, actor, building, or unrelated marker");
            for(int i=0;i<96;i++)c.SelectPlot(i%32);
            Assert(c.SceneBuildCount==builds&&ReferenceEquals(content,Scene(c).Content)&&ReferenceEquals(originalWorld,body.Children[0]));
            var cached=(System.Collections.IDictionary)typeof(PlanetView3D).GetField("markerCache",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(c)!;
            Assert(cached.Count<=64,"Selection marker cache grows without a bound");
        });
        Test("moving and rotating one placement retains every unrelated model and remaps its hit identity",()=>
        {
            var c=new PlanetView3D();var original=new[]{new PlanetBuildingPlacement("house","house",0,0),new PlanetBuildingPlacement("tree","tree",2,0)};
            c.ApplyState(3,"cloudy",false,false,42,original,true);var body=Field<Model3DGroup>(c,"planetBody");
            var before=body.Children.ToArray();var originalLeaves=GeometryLeaves(body).ToArray();int builds=c.SceneBuildCount;
            c.SetBuildings([original[0] with {PlotId=5,QuarterTurns=3},original[1]]);var after=body.Children.ToArray();
            Assert(before.Length==after.Length&&before.Except(after).Count()==1&&after.Except(before).Count()==1,"A local placement edit replaced unrelated scene wrappers");
            Assert(GeometryLeaves(body).ToHashSet().SetEquals(originalLeaves),"A move/rotation replaced immutable geometry wrappers");
            var changed=after.Except(before).Single();
            var hits=Field<IReadOnlyDictionary<GeometryModel3D,int>>(c,"plotHits");
            Assert(GeometryLeaves(changed).All(model=>hits.TryGetValue(model,out int plot)&&plot==5),"Moved building kept its previous hit-test plot");
            c.SetBuildings([original[1]]);
            Assert(c.SceneBuildCount==builds&&!c.FeatureCounts.ContainsKey("house")&&ReferenceEquals(before[0],body.Children[0]));
        });
        Test("editing mode only changes plot overlays and moving poses without rebuilding the habitat",()=>
        {
            var c=new PlanetView3D();c.ApplyState(5,"cloudy",false,false,42,MaximumMovingLayout(),false);
            var content=Scene(c).Content;var terrain=Field<Model3DGroup>(c,"planetBody").Children[0];int builds=c.SceneBuildCount;
            var originals=GeometryLeaves(content).ToHashSet();c.SetEditMode(true);
            Assert(originals.IsSubsetOf(GeometryLeaves(Scene(c).Content).ToHashSet())&&c.FeatureCounts["plot"]==32);
            c.SetEditMode(false);
            Assert(originals.SetEquals(GeometryLeaves(Scene(c).Content))&&ReferenceEquals(terrain,Field<Model3DGroup>(c,"planetBody").Children[0]));
            Assert(ReferenceEquals(content,Scene(c).Content)&&c.SceneBuildCount==builds);
        });
        Test("combined state starts and travels with one final scene and rejects invalid input atomically",()=>
        {
            var c=new PlanetView3D(deferInitialScene:true);Assert(c.SceneBuildCount==0);
            var layout=new[]{new PlanetBuildingPlacement("home","house",0,2)};
            c.ApplyState(5,"rain",true,true,43,layout,true,0);Assert(c.SceneBuildCount==1&&c.FeatureCounts["house"]==1&&c.SelectedPlotId==0);
            c.ApplyState(3,"clear",false,false,44,[new("new-tree","tree",3,1)],false);
            Assert(c.SceneBuildCount==2&&!c.FeatureCounts.ContainsKey("house")&&!c.FeatureCounts.ContainsKey("plot")&&c.SelectedPlotId is null);
            var content=Scene(c).Content;var signature=Signature((Model3DGroup)content);
            Throws(()=>c.ApplyState(4,"rain",false,false,42,[new("invalid","house",32,0)],true));
            Assert(ReferenceEquals(content,Scene(c).Content)&&signature==Signature((Model3DGroup)content)&&c.SceneBuildCount==2);
        });
        Test("local habitat replacement updates creature destinations without stale targets or new models",()=>
        {
            var pair=NearbyPlotPairs().First(p=>PlanetView3DScene.Build(0,"clear",false,42,[new("duck","duck",p.First,0),new("pond","pond",p.Second,0)]).Motions.Single().Habitat=="pond");
            var animal=new PlanetBuildingPlacement("duck","duck",pair.First,0);var pond=new PlanetBuildingPlacement("pond","pond",pair.Second,0);
            var c=new PlanetView3D();c.ApplyState(0,"clear",false,false,42,[animal,pond],false);int builds=c.SceneBuildCount;
            Assert(c.InteractionCount==1);var before=LiveMotions(c).Single();
            Assert(Angle(before.Destination,PlotDirection(pair.Second))<1e-6);
            c.SetBuildings([animal]);var removed=LiveMotions(c).Single();
            Assert(c.InteractionCount==0&&removed.Habitat is null&&Angle(removed.Home,removed.Destination)<1e-6,"Removed pond remained an interaction target");
            c.SetBuildings([animal,pond]);var restored=LiveMotions(c).Single();
            Assert(c.InteractionCount==1&&ReferenceEquals(before.Model,restored.Model)&&c.SceneBuildCount==builds);
            Assert(Angle(restored.Destination,PlotDirection(pair.Second))<1e-6);
        });
        Test("removing a visible habitat clears the old creature pose even when its new home is culled",()=>
        {
            var pair=NearbyPlotPairs().First(p=>PlanetView3DScene.Build(0,"clear",false,42,[new("duck","duck",p.First,0),new("pond","pond",p.Second,0)]).Motions.Single().Habitat=="pond");
            var animal=new PlanetBuildingPlacement("duck","duck",pair.First,0);var pond=new PlanetBuildingPlacement("pond","pond",pair.Second,0);
            var c=ArrangedPlanet(new Size(1000,700));c.ApplyState(0,"clear",false,false,42,[animal,pond],false);
            var actors=(System.Collections.IEnumerable)typeof(PlanetView3D).GetField("motions",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(c)!;
            var actor=actors.Cast<object>().Single();var surface=(MatrixTransform3D)actor.GetType().GetField("Surface",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(actor)!;
            bool found=false;
            foreach(var pitch in new[]{-60.0,-30,0,30,60})
            {
                for(int yaw=0;yaw<360&&!found;yaw+=5)
                {
                    c.ResetView();c.RotateView(yaw,pitch);c.AnimateScene(0);bool hiddenHome=c.LastCulledObjectCount==1;
                    c.AnimateScene(12);found=hiddenHome&&c.LastAnimatedObjectCount==1;
                }
                if(found)break;
            }
            Assert(found,"The synthetic camera must see the pond while the creature's home is occluded");
            var original=LiveMotions(c).Single();var previous=surface.Matrix;int builds=c.SceneBuildCount;
            Assert(!FullMatrixClose(previous,original.Surface.Value),"The creature never reached the pond");
            typeof(PlanetView3D).GetField("animationSeconds",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(c,12.0);
            c.SetBuildings([animal]);
            Assert(c.LastCulledObjectCount==1,"The new home must remain culled for this regression");
            Assert(FullMatrixClose(surface.Matrix,LiveMotions(c).Single().Surface.Value),"A culled creature retained its pose at the removed pond");
            Assert(ReferenceEquals(original.Model,LiveMotions(c).Single().Model)&&c.SceneBuildCount==builds,"Clearing a stale pose rebuilt frozen geometry");
        });
        Test("incremental full layouts keep equivalent visual budgets and bounded local edit cost",()=>
        {
            var layout=MaximumMovingLayout();var c=new PlanetView3D();c.ApplyState(5,"rain",false,false,42,layout,true);
            var expected=PlanetView3DScene.Build(5,"rain",false,42,layout,true);var expectedMetrics=SceneMetrics(expected);var actual=Metrics(GeometryLeaves(Scene(c).Content));
            Assert(expectedMetrics.Triangles==actual.Triangles&&expectedMetrics.GeometryModels==actual.GeometryModels&&actual.Triangles<=220000);
            Assert(c.FeatureCounts.Count==expected.Features.Count&&c.FeatureCounts.All(entry=>expected.Features.TryGetValue(entry.Key,out var count)&&count==entry.Value));
            int builds=c.SceneBuildCount;var natural=Field<Model3DGroup>(c,"planetBody").Children[0];var samples=new double[80];
            for(int index=0;index<samples.Length;index++)
            {
                var changed=layout.ToArray();changed[0]=changed[0] with {QuarterTurns=index%4};
                var watch=System.Diagnostics.Stopwatch.StartNew();c.SetBuildings(changed);watch.Stop();samples[index]=watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);Assert(c.SceneBuildCount==builds&&ReferenceEquals(natural,Field<Model3DGroup>(c,"planetBody").Children[0]));
            Console.WriteLine($"  Local full-layout edits: p95 {samples[75]:0.0000} ms, max {samples[^1]:0.0000} ms, zero natural-scene rebuilds. This measures UI model updates, not GPU presentation.");
        });
        Test("rendered surface hit testing finds front plots and respects occlusion",()=>{var c=new PlanetView3D {Width=700,Height=500};c.SetState(3,"clear",false,false);c.SetEditMode(true);c.Measure(new Size(700,500));c.Arrange(new Rect(0,0,700,500));c.UpdateLayout();var hits=new HashSet<int>();for(int y=20;y<450;y+=7)for(int x=70;x<630;x+=7){var hit=c.HitTestPlot(new Point(x,y));if(hit.HasValue)hits.Add(hit.Value);}Assert(hits.Count>=4&&hits.Count<20);Assert(c.HitTestPlot(new Point(0,0))==null);Console.WriteLine($"  {hits.Count} visible plots respond to hit testing");});
        Test("animals and clouds move without rebuilding frozen meshes",()=>{var c=new PlanetView3D();c.SetState(3,"cloudy",false,false);Assert(c.MovingObjectCount==c.FeatureCounts["creature"]+c.FeatureCounts["cloud"]&&c.FeatureCounts["creature"]>=10);int built=c.SceneBuildCount;c.AnimateScene(0);double before=c.MotionOffsetTotal;c.AnimateScene(3);Assert(c.MotionOffsetTotal!=before&&c.SceneBuildCount==built);Assert(!c.AnimationRunning);c.SetEditMode(true);Assert(!c.AnimationRunning);c.SetState(0,"clear",false,false);Assert(c.MovingObjectCount==0);});
        Test("detailed worlds remain bounded and barren worlds have geological variety",()=>
        {
            foreach(int level in new[]{0,5})
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();var s=PlanetView3DScene.Build(level,"cloudy",false,42);watch.Stop();
                int triangles=Triangles(s.Model)+s.Motions.Sum(m=>Triangles(m.Model));Assert(triangles<220000);
                if(level==0){Assert(s.Features["crater"]>=5&&s.Features["crystal"]>=5&&s.Features["rocks"]>=20);}
                else Assert(s.Features["tree"]>=25&&s.Features.ContainsKey("mushrooms")&&s.Features.ContainsKey("meadow"));
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
        Test("all 18 catalog kinds create finite frozen scenery and respect scene budgets",()=>
        {
            Assert(PlanetLayoutCatalog.Entries.Count==18,"The expanded placement catalog should expose 18 supported kinds");
            foreach(var definition in PlanetLayoutCatalog.Entries)
            {
                var layout=Enumerable.Range(0,definition.MaxCount).Select(i=>new PlanetBuildingPlacement($"kind-{definition.Kind}-{i}",definition.Kind,i,i%4)).ToArray();
                var scene=PlanetView3DScene.Build(5,"cloudy",false,42,layout,true,0);
                CheckGroup(scene.Model);foreach(var motion in scene.Motions)CheckGroup(motion.Model);
                Assert(scene.Model.IsFrozen&&scene.Motions.All(m=>m.Model.IsFrozen&&m.Surface.IsFrozen));
                Assert(scene.PlotHits.Values.Distinct().Count()==PlanetLayoutCatalog.PlotCount);
                if(PlanetLayoutCatalog.IsAnimal(definition.Kind))
                    foreach(var placement in layout)Assert(FindPlacedMotion(scene,placement).Species==definition.Kind);
                var metrics=SceneMetrics(scene);Assert(metrics.Triangles<=220000,$"{definition.Kind} maximum count exceeded 220,000 instance triangles: {metrics.Triangles}");
                Assert(scene.Motions.Count<=27,$"{definition.Kind} exceeded the total moving-object budget");
                Console.WriteLine($"  Catalog {definition.Kind} × {definition.MaxCount}: {metrics.Triangles:N0} instance triangles, {metrics.GeometryModels:N0} geometry models, {metrics.UniqueMeshes:N0} unique meshes, {scene.Motions.Count} moving objects");
            }
        });
        Test("every animal can find a real nearby preferred habitat instead of a phantom target",()=>
        {
            var preferences=new[]{("rabbit","meadow"),("sheep","meadow"),("fox","bush"),("deer","tree"),("duck","pond"),("butterfly","flower")};
            var pairs=NearbyPlotPairs();Assert(pairs.Length>0,"No nearby plot fixtures exist");
            foreach(var (species,habitat) in preferences)
            {
                bool found=false;
                // Level zero has no naturally generated preferred vegetation, so a
                // successful destination must correspond to our actual placed target.
                // Some pairs cross the sea; the renderer may correctly reject those.
                foreach(var pair in pairs)
                {
                    var animal=new PlanetBuildingPlacement("preferred-animal",species,pair.First,1);
                    var target=new PlanetBuildingPlacement("actual-habitat",habitat,pair.Second,0);
                    var scene=PlanetView3DScene.Build(0,"clear",false,42,[animal,target]);
                    var motion=FindPlacedMotion(scene,animal);
                    CheckMotion(motion);
                    if(motion.Habitat is null)continue;
                    Assert(motion.Habitat==habitat,$"{species} reported a habitat that does not exist in the fixture");
                    var targetDirection=PlotDirection(target.PlotId);
                    double targetDistance=Angle(motion.Destination,targetDirection);
                    Assert(Angle(motion.Home,targetDirection)<=.680001,"Animal chose a target outside the bounded search radius");
                    Assert(targetDistance<.15,$"{species} destination is not near the actual {habitat}: {targetDistance}");
                    if(species=="duck")Assert(targetDistance<1e-6,"Duck should reach the real pond center");
                    Assert(Angle(motion.Home,motion.Destination)>.005,"A reported habitat interaction never leaves the home plot");
                    Console.WriteLine($"  {species} → {habitat}: plots {pair.First}/{pair.Second}, destination is {targetDistance:0.0000} rad from real target");
                    found=true;break;
                }
                Assert(found,$"{species} never found its real preferred {habitat} among nearby plot fixtures");
            }
        });
        Test("animals without a preferred habitat use bounded local fallback",()=>
        {
            foreach(var definition in PlanetLayoutCatalog.Entries.Where(d=>PlanetLayoutCatalog.IsAnimal(d.Kind)))
            {
                var placement=new PlanetBuildingPlacement("no-target-"+definition.Kind,definition.Kind,0,2);
                var scene=PlanetView3DScene.Build(0,"clear",false,42,[placement,new("unrelated-scenery","rockgarden",1,0)]);
                var motion=FindPlacedMotion(scene,placement);CheckMotion(motion);
                Assert(motion.Habitat is null,$"{definition.Kind} invented a preferred habitat in a barren scene");
                Assert(Angle(motion.Home,motion.Destination)<=.15,$"{definition.Kind} fallback travels too far from home");
            }
        });
        Test("placed instances have distinct hit wrappers but share frozen meshes and materials",()=>
        {
            foreach(string kind in new[]{"rabbit","flower","pond","house"})
            {
                var scene=PlanetView3DScene.Build(5,"clear",false,42,[new("duplicate-a",kind,0,0),new("duplicate-b",kind,1,2)],true,0);
                GeometryModel3D[] first,second;
                if(PlanetLayoutCatalog.IsAnimal(kind))
                {
                    first=GeometryLeaves(FindPlacedMotion(scene,new("duplicate-a",kind,0,0)).Model).ToArray();
                    second=GeometryLeaves(FindPlacedMotion(scene,new("duplicate-b",kind,1,2)).Model).ToArray();
                    Assert(first.All(model=>scene.PlotHits.TryGetValue(model,out var plot)&&plot==0),"First animal lost its editable hit identity");
                    Assert(second.All(model=>scene.PlotHits.TryGetValue(model,out var plot)&&plot==1),"Second animal overwrote the first animal's hit identity");
                }
                else
                {
                    first=scene.PlotHits.Where(kv=>kv.Value==0).Select(kv=>kv.Key).ToArray();
                    second=scene.PlotHits.Where(kv=>kv.Value==1).Select(kv=>kv.Key).ToArray();
                }
                Assert(first.Length>0&&second.Length>0);
                Assert(!first.Any(a=>second.Any(b=>ReferenceEquals(a,b))),$"{kind} reused geometry wrappers across independently selectable objects");
                Assert(first.Any(a=>second.Any(b=>ReferenceEquals(a.Geometry,b.Geometry))),$"{kind} did not reuse any mesh between repeated instances");
                Assert(first.Any(a=>second.Any(b=>ReferenceEquals(a.Material,b.Material))),$"{kind} did not reuse materials");
                Assert(first.Concat(second).All(model=>model.IsFrozen&&model.Geometry.IsFrozen&&model.Material.IsFrozen));
            }
        });
        Test("maximum legal layouts stay within 32 plots 12 placed animals and 220000 triangles",()=>
        {
            var baseline=PlanetView3DScene.Build(5,"rain",false,42);
            int baselineTriangles=SceneMetrics(baseline).Triangles;
            var weights=PlanetLayoutCatalog.Entries.Select(d=>(Definition:d,Weight:SceneMetrics(PlanetView3DScene.Build(5,"rain",false,42,[new("weight",d.Kind,0,0)])).Triangles-baselineTriangles)).ToArray();
            var heaviest=new List<PlanetBuildingPlacement>();int animals=0;
            foreach(var entry in weights.OrderByDescending(x=>x.Weight))
                for(int n=0;n<entry.Definition.MaxCount&&heaviest.Count<32;n++)
                {
                    bool animal=PlanetLayoutCatalog.IsAnimal(entry.Definition.Kind);if(animal&&animals>=12)break;
                    heaviest.Add(new("heavy-"+heaviest.Count,entry.Definition.Kind,heaviest.Count,n%4));if(animal)animals++;
                }
            Assert(heaviest.Count==32,"The worst-case fixture did not fill every plot");
            foreach(var layout in new[]{heaviest.ToArray(),MaximumMovingLayout()})
            foreach(string weather in new[]{"clear","cloudy","rain","aurora"})
            {
                var scene=PlanetView3DScene.Build(5,weather,weather=="aurora",42,layout,true,0);
                var metrics=SceneMetrics(scene);
                Assert(metrics.Triangles<=220000,$"Full layout with {weather} has {metrics.Triangles} triangles");
                Assert(scene.Motions.Count<=27,"Natural animals, placed animals and clouds exceed the cap");
                int placedAnimals=layout.Count(p=>PlanetLayoutCatalog.IsAnimal(p.Kind));
                Assert(scene.Motions.Count(m=>m.Kind=="creature")-placedAnimals<=12,"Natural animal count exceeds 12");
                Console.WriteLine($"  Maximum layout ({placedAnimals} placed animals, {weather}): {metrics.Triangles:N0} triangles, {metrics.GeometryModels:N0} geometry models, {metrics.UniqueMeshes:N0} unique meshes, {scene.Motions.Count} moving objects");
            }
        });
        Test("continuous ecology animation preserves the scene and frozen mesh references",()=>
        {
            var control=ArrangedPlanet(new Size(1000,700));control.SetState(5,"cloudy",false,false,42);control.SetBuildings(MaximumMovingLayout());
            var content=Scene(control).Content;int builds=control.SceneBuildCount;
            var before=GeometryLeaves(content).ToArray();var meshes=before.Select(model=>model.Geometry).ToArray();var materials=before.Select(model=>model.Material).ToArray();
            Assert(control.MovingObjectCount<=27&&control.InteractionCount>0,"The bounded fixture must contain real habitat interactions");
            double initialMotion=control.MotionOffsetTotal;
            for(int tick=1;tick<=360;tick++)control.AnimateScene(tick*.125);
            var after=GeometryLeaves(Scene(control).Content).ToArray();
            Assert(ReferenceEquals(content,Scene(control).Content)&&control.SceneBuildCount==builds,"Animation rebuilt the scene");
            Assert(before.Length==after.Length&&before.Where((model,index)=>!ReferenceEquals(model,after[index])||!ReferenceEquals(meshes[index],after[index].Geometry)||!ReferenceEquals(materials[index],after[index].Material)).Count()==0,"Animation replaced a geometry wrapper, mesh or material");
            Assert(after.All(model=>model.Geometry.IsFrozen&&model.Material.IsFrozen));
            Assert(control.MotionOffsetTotal!=initialMotion,"No visible creature or cloud changed its transform");
        });
        Test("editing returns all moving objects home and direct animation calls keep them there",()=>
        {
            var layout=MaximumMovingLayout();var control=ArrangedPlanet(new Size(1000,700));control.SetState(5,"cloudy",false,false,42);control.SetBuildings(layout);
            control.AnimateScene(17);control.SetEditMode(true);
            var expected=PlanetView3DScene.Build(5,"cloudy",false,42,layout,true,-1);
            var body=((Model3DGroup)Scene(control).Content).Children.OfType<Model3DGroup>().Single(group=>ReferenceEquals(group.Transform,Field<Transform3DGroup>(control,"planetTransform")));
            var movingGroups=body.Children.OfType<Model3DGroup>().Where(group=>!group.IsFrozen).ToArray();
            Assert(movingGroups.Length==expected.Motions.Count,"Cannot locate the editable motion wrappers");
            for(int i=0;i<movingGroups.Length;i++)
                Assert(FullMatrixClose(movingGroups[i].Transform.Value,expected.Motions[i].Surface.Value),$"Moving object {i} did not return to its home surface/heading");
            var frozenPose=Signature((Model3DGroup)Scene(control).Content);int builds=control.SceneBuildCount;
            foreach(double seconds in new[]{0.0,1.0,12.0,500.0})control.AnimateScene(seconds);
            Assert(frozenPose==Signature((Model3DGroup)Scene(control).Content)&&control.SceneBuildCount==builds,"Direct animation moved editable objects or rebuilt their home pose");
            Assert(!control.AnimationRunning);
        });
        Test("animation culls back-side and out-of-view objects",()=>
        {
            var control=ArrangedPlanet(new Size(1000,700));control.SetState(5,"cloudy",false,false,42);control.SetBuildings(MaximumMovingLayout());control.AnimateScene(9);
            Assert(control.LastAnimatedObjectCount>0&&control.LastCulledObjectCount>0,"Overview should update front objects while skipping hidden ones");
            Assert(control.LastAnimatedObjectCount+control.LastCulledObjectCount==control.MovingObjectCount);
            var camera=(PerspectiveCamera)Viewport(control).Camera;var originalLook=camera.LookDirection;
            camera.LookDirection=(Vector3D)camera.Position;control.AnimateScene(10);
            Assert(control.LastAnimatedObjectCount==0&&control.LastCulledObjectCount==control.MovingObjectCount,"Objects behind a camera looking away were still animated");
            camera.LookDirection=originalLook;control.AnimateScene(11);Assert(control.LastAnimatedObjectCount>0,"Objects did not resume when camera returned");
        });
        Test("animation tick cost is measured without claiming a hardware-independent deadline",()=>
        {
            var control=ArrangedPlanet(new Size(1000,700));control.SetState(5,"cloudy",false,false,42);control.SetBuildings(MaximumMovingLayout());
            var timer=Field<DispatcherTimer>(control,"animation");Assert(timer.Interval.TotalMilliseconds>=120&&timer.Interval.TotalMilliseconds<=130,"Scene timer should run at approximately 8 Hz");
            foreach(bool surface in new[]{false,true})
            {
                control.SetSurfaceView(surface);for(int i=0;i<80;i++)control.AnimateScene(i*.125);
                var samples=new double[600];long bytesBefore=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<samples.Length;i++){long start=System.Diagnostics.Stopwatch.GetTimestamp();control.AnimateScene(10+i*.125);samples[i]=System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;}
                long allocated=GC.GetAllocatedBytesForCurrentThread()-bytesBefore;Array.Sort(samples);
                Assert(samples.All(double.IsFinite));
                var metrics=Metrics(GeometryLeaves(Scene(control).Content));
                Console.WriteLine($"  Tick {(surface?"surface":"overview")}: median {samples[300]:0.0000} ms, p95 {samples[569]:0.0000} ms, max {samples[^1]:0.0000} ms, {allocated/(double)samples.Length:0.0} managed bytes/tick; {control.LastAnimatedObjectCount} updated/{control.LastCulledObjectCount} culled; {metrics.Triangles:N0} triangles, {metrics.GeometryModels:N0} models, {metrics.UniqueMeshes:N0} unique meshes. This times managed transform updates, not GPU rendering.");
            }
        });
        Test("real offscreen hosts pause and resume animation on minimize scroll and reattachment",()=>
        {
            var control=ArrangedPlanet(new Size(1000,700));control.SetState(3,"cloudy",true,false,42);
            var stack=new StackPanel();stack.Children.Add(control);stack.Children.Add(new Border {Height=2200});
            var scroll=new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            var host=OffscreenHost(scroll);Window? nextHost=null;
            try
            {
                host.Show();PumpUntil(()=>control.IsLoaded&&control.AnimationRunning,"Initial offscreen host did not start animation");
                var timer=Field<DispatcherTimer>(control,"animation");
                Assert(ReferenceEquals(Field<Window?>(control,"animationHost"),host)&&ReferenceEquals(Field<ScrollViewer?>(control,"animationScroll"),scroll));
                host.WindowState=WindowState.Minimized;PumpUntil(()=>!control.AnimationRunning&&!control.IsSceneInView(),"Minimized host did not stop animation");
                long ticks=control.AnimationTickCount;double motion=control.MotionOffsetTotal;var orientation=Orientation(control);
                PumpFor(TimeSpan.FromMilliseconds(360));
                Assert(control.AnimationTickCount==ticks&&control.MotionOffsetTotal==motion&&MatrixClose(orientation,Orientation(control)),"Minimized host still advanced the scene timer");
                host.WindowState=WindowState.Normal;PumpUntil(()=>control.AnimationRunning&&control.AnimationTickCount>ticks,"Restored host did not resume animation");
                control.Visibility=Visibility.Collapsed;PumpUntil(()=>!control.AnimationRunning,"Collapsed page kept animating");ticks=control.AnimationTickCount;PumpFor(TimeSpan.FromMilliseconds(300));Assert(control.AnimationTickCount==ticks);
                control.Visibility=Visibility.Visible;PumpUntil(()=>control.AnimationRunning&&control.AnimationTickCount>ticks,"Visible page did not resume animation");
                scroll.ScrollToVerticalOffset(control.ActualHeight+100);scroll.UpdateLayout();
                PumpUntil(()=>!control.IsSceneInView()&&!control.AnimationRunning,"Scrolling the scene out of view did not pause it");ticks=control.AnimationTickCount;PumpFor(TimeSpan.FromMilliseconds(300));Assert(control.AnimationTickCount==ticks);
                scroll.ScrollToTop();scroll.UpdateLayout();PumpUntil(()=>control.IsSceneInView()&&control.AnimationRunning&&control.AnimationTickCount>ticks,"Scrolling back did not resume animation");
                stack.Children.Remove(control);PumpUntil(()=>!control.IsLoaded&&!control.AnimationRunning,"Unloaded control kept animating");
                Assert(Field<Window?>(control,"animationHost") is null&&Field<ScrollViewer?>(control,"animationScroll") is null,"Old host event references survived unload");
                nextHost=OffscreenHost(control);nextHost.Left=-22000;nextHost.Show();
                PumpUntil(()=>control.IsLoaded&&control.AnimationRunning,"Reattached control did not restart");
                Assert(ReferenceEquals(timer,Field<DispatcherTimer>(control,"animation")),"Reattachment replaced the scene timer");
                Assert(ReferenceEquals(Field<Window?>(control,"animationHost"),nextHost)&&Field<ScrollViewer?>(control,"animationScroll") is null,"Reattachment still refers to the old window or scroll container");
                ticks=control.AnimationTickCount;host.WindowState=WindowState.Minimized;
                PumpUntil(()=>control.AnimationRunning&&control.AnimationTickCount>ticks,"Changing the detached old window affected the new animation host");
                nextHost.Content=null;PumpUntil(()=>!control.AnimationRunning,"Final detachment did not stop the timer");
            }
            finally{if(nextHost is not null){nextHost.Content=null;nextHost.Close();}host.Content=null;host.Close();PumpFor(TimeSpan.FromMilliseconds(40));}
        });
        Test("refined models keep finite plot-sized bounds and healthy surface geometry",()=>
        {
            foreach(string kind in new[]{"rabbit","sheep","fox","duck","deer","butterfly","pond","meadow","mushroom","orchard","rockgarden","bush","tree","mountain"})
            {
                var model=ModelForInspection(kind);CheckGroup(model);var bounds=model.Bounds;
                Assert(!bounds.IsEmpty&&new[]{bounds.X,bounds.Y,bounds.Z,bounds.SizeX,bounds.SizeY,bounds.SizeZ}.All(double.IsFinite),$"{kind} has invalid bounds");
                Assert(bounds.SizeX>0&&bounds.SizeY>0&&bounds.SizeZ>0,$"{kind} lost a dimension");
                Assert(bounds.SizeX<=1.25&&bounds.SizeY<=1.25&&bounds.SizeZ<=1.25,$"{kind} grew outside a reasonable plot footprint");
                Assert(bounds.Y>=-.08&&Math.Abs(bounds.X+bounds.SizeX/2)<.45&&Math.Abs(bounds.Z+bounds.SizeZ/2)<.45,$"{kind} is displaced from its surface anchor");
                Console.WriteLine($"  Model {kind}: {Triangles(model):N0} triangles, {GeometryLeaves(model).Count()} parts, bounds {bounds.SizeX:0.000} × {bounds.SizeY:0.000} × {bounds.SizeZ:0.000}");
            }
        });
        Test("animals expose dark facial details from both sides rather than burying one eye",()=>
        {
            foreach(string species in new[]{"rabbit","sheep","fox","duck","deer","butterfly"})
            {
                var model=ModelForInspection(species);var overall=model.Bounds;bool butterfly=species=="butterfly";
                var parts=GeometryBounds(model,Matrix3D.Identity).Where(part=>
                {
                    var color=DiffuseColor(part.Model.Material);var bounds=part.Bounds;double longest=Math.Max(bounds.SizeX,Math.Max(bounds.SizeY,bounds.SizeZ)),shortest=Math.Min(bounds.SizeX,Math.Min(bounds.SizeY,bounds.SizeZ));
                    var center=BoundsCenter(bounds);double forward=butterfly?center.Z:center.X;
                    return color.HasValue&&.2126*color.Value.R+.7152*color.Value.G+.0722*color.Value.B<105&&longest<=.045&&shortest/longest>.25&&center.Y>overall.Y+overall.SizeY*.30&&forward>0;
                }).ToArray();
                var visual=new ModelVisual3D {Content=model};
                foreach(int side in new[]{-1,1})
                {
                    var candidates=parts.Where(part=>(butterfly?BoundsCenter(part.Bounds).X:BoundsCenter(part.Bounds).Z)*side>.006).ToArray();
                    Assert(candidates.Length>0,$"{species} lacks a separate dark facial detail on side {side}");
                    Assert(candidates.Any(part=>VisibleFromSide(visual,part.Model,BoundsCenter(part.Bounds),side,butterfly)),$"{species} facial detail on side {side} is hidden behind the head or another large part");
                }
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
    private static void RenderModelSheet(string path)
    {
        const int width=1600,height=1600;
        var background=new SolidColorBrush(Color.FromRgb(14,23,30));
        var root=new Grid {Width=width,Height=height,Background=background};
        root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(105)});root.RowDefinitions.Add(new RowDefinition());
        var title=new StackPanel {Margin=new Thickness(28,20,28,12)};
        title.Children.Add(new TextBlock {Text="专注星球 · 真实模型接触表",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=30,Foreground=Brushes.White});
        title.Children.Add(new TextBlock {Text="六种生物 · 六种自然景观 · 树木与山体 · 固定三分之四视角，末两格查看另一侧五官",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=16,Foreground=new SolidColorBrush(Color.FromRgb(178,199,193)),Margin=new Thickness(0,8,0,0)});
        root.Children.Add(title);
        var cards=new System.Windows.Controls.Primitives.UniformGrid {Rows=4,Columns=4,Margin=new Thickness(16,0,16,16)};Grid.SetRow(cards,1);root.Children.Add(cards);
        var items=new[]{("rabbit","兔子",false),("sheep","绵羊",false),("fox","狐狸",false),("duck","鸭子",false),("deer","鹿",false),("butterfly","蝴蝶",false),("pond","池塘",false),("meadow","草地",false),("mushroom","蘑菇丛",false),("orchard","果树",false),("rockgarden","岩石庭园",false),("bush","灌木",false),("tree","树木",false),("mountain","山体",false),("rabbit","兔子 · 另一侧",true),("fox","狐狸 · 另一侧",true)};
        foreach(var (kind,label,opposite) in items)
        {
            var model=ModelForInspection(kind);var bounds=model.Bounds;int triangles=Triangles(model);
            double longest=Math.Max(bounds.SizeX,Math.Max(bounds.SizeY,bounds.SizeZ));Assert(longest>0&&double.IsFinite(longest));
            double scale=1.9/longest;var center=new Point3D(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
            var subject=new Model3DGroup();subject.Children.Add(model);var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(scale,scale,scale));transform.Children.Add(new TranslateTransform3D(-center.X*scale,-center.Y*scale,-center.Z*scale));subject.Transform=transform;subject.Freeze();
            var scene=new Model3DGroup();scene.Children.Add(new AmbientLight(Color.FromRgb(132,143,141)));scene.Children.Add(new DirectionalLight(Color.FromRgb(255,245,222),new Vector3D(-.7,-1,-1.6)));scene.Children.Add(new DirectionalLight(Color.FromRgb(105,135,151),new Vector3D(1,.2,.8)));scene.Children.Add(subject);
            double z=opposite?-4:4;var camera=new OrthographicCamera(new Point3D(3.2,2.2,z),new Vector3D(-3.2,-2.2,-z),new Vector3D(0,1,0),3.0);
            var viewport=new Viewport3D {Camera=camera,ClipToBounds=true};viewport.Children.Add(new ModelVisual3D {Content=scene});
            var content=new Grid {Margin=new Thickness(12)};content.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});content.RowDefinitions.Add(new RowDefinition());content.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            content.Children.Add(new TextBlock {Text=label,FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=19,Foreground=Brushes.White,Margin=new Thickness(2,0,0,5)});
            Grid.SetRow(viewport,1);content.Children.Add(viewport);
            var metrics=new TextBlock {Text=$"{triangles:N0} 三角形 · {GeometryLeaves(model).Count()} 个几何部件",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(159,182,174)),Margin=new Thickness(2,6,0,0)};Grid.SetRow(metrics,2);content.Children.Add(metrics);
            cards.Children.Add(new Border {Background=new SolidColorBrush(Color.FromRgb(27,39,47)),BorderBrush=new SolidColorBrush(Color.FromRgb(49,66,73)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Margin=new Thickness(6),Child=content});
        }
        root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var absolute=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);using var stream=File.Create(absolute);encoder.Save(stream);
        Console.WriteLine("Real WPF model contact sheet: "+absolute);
    }
    private static Model3DGroup ModelForInspection(string kind)
    {
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
        return kind=="mountain"?(Model3DGroup)typeof(PlanetView3DScene).GetMethod("Mountain",flags)!.Invoke(null,[.38,.58])!:(Model3DGroup)typeof(PlanetView3DScene).GetMethod("PlaceableModel",flags)!.Invoke(null,[kind])!;
    }
    private static void RenderEcology(string path)
    {
        const int width=1280,height=900;
        var layout=new Grid {Width=width,Height=height,Background=new SolidColorBrush(Color.FromRgb(12,22,28))};
        layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());
        var heading=new StackPanel {Margin=new Thickness(30,22,30,10)};
        heading.Children.Add(new TextBlock {Text="专注星球 · 生机相伴",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=27,Foreground=Brushes.White});
        heading.Children.Add(new TextBlock {Text="合成生态布局 · 地表近景 · 鸭子与池塘、兔子与草地、鹿与果树、蝴蝶与花丛",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(190,207,200)),Margin=new Thickness(0,7,0,0),TextWrapping=TextWrapping.Wrap});
        heading.Children.Add(new TextBlock {Text="仅为功能演示，不代表真实专注时长或成长记录。",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(142,163,158)),Margin=new Thickness(0,5,0,0)});layout.Children.Add(heading);
        var preferredCenter=PlanetView3DGeometry.Direction(20,0);
        var pairs=NearbyPlotPairs().OrderBy(pair=>Angle(PlotDirection(pair.First)+PlotDirection(pair.Second),preferredCenter)).ToArray();
        var placements=new List<PlanetBuildingPlacement>();var used=new HashSet<int>();
        foreach(var (species,habitat) in new[]{("duck","pond"),("rabbit","meadow"),("deer","orchard"),("butterfly","flower")})
        {
            bool placed=false;
            foreach(var pair in pairs)
            {
                if(used.Contains(pair.First)||used.Contains(pair.Second))continue;
                var animal=new PlanetBuildingPlacement("demo-"+species,species,pair.First,0);
                var target=new PlanetBuildingPlacement("demo-"+habitat,habitat,pair.Second,0);
                var probe=PlanetView3DScene.Build(2,"clear",false,42,[animal,target]);
                var candidateMotion=FindPlacedMotion(probe,animal);
                if(candidateMotion.Habitat!=habitat||Angle(candidateMotion.Destination,PlotDirection(target.PlotId))>(species=="duck"?1e-6:.15))continue;
                placements.Add(animal);placements.Add(target);used.Add(pair.First);used.Add(pair.Second);placed=true;break;
            }
            Assert(placed,"Cannot construct a real habitat pair for ecology preview: "+species);
        }
        var control=new PlanetView3D();control.SetState(2,"clear",false,false,42);control.SetBuildings(placements);control.SetSurfaceView(true);control.SetZoom(1.08);
        var dominantTarget=placements.Single(p=>p.Kind=="pond");var center=PlanetView3DScene.Plots[dominantTarget.PlotId];
        Grid.SetRow(control,1);layout.Children.Add(control);layout.Measure(new Size(width,height));layout.Arrange(new Rect(0,0,width,height));layout.UpdateLayout();
        // Compose around the actual pond location in the existing 3D camera,
        // rather than guessing a geographic latitude that may clip the subject.
        double bestScore=double.PositiveInfinity,bestYaw=0,bestPitch=0;
        for(int yaw=-15;yaw<=15;yaw+=3)for(int pitch=-15;pitch<=15;pitch+=3)
        {
            double candidateYaw=6-center.Longitude+yaw,candidatePitch=center.Latitude-35+pitch;
            control.ResetView();control.SetZoom(1.08);control.RotateView(candidateYaw,candidatePitch);
            var camera=(PerspectiveCamera)Viewport(control).Camera;
            var point=Orientation(control).Transform((Point3D)(PlotDirection(dominantTarget.PlotId)*2.72));
            var delta=point-camera.Position;var forward=camera.LookDirection;forward.Normalize();var up=camera.UpDirection;up.Normalize();var right=Vector3D.CrossProduct(forward,up);right.Normalize();
            double depth=Vector3D.DotProduct(delta,forward);if(depth<=0)continue;
            double tangent=Math.Tan(camera.FieldOfView*Math.PI/360),aspect=Viewport(control).ActualWidth/Viewport(control).ActualHeight;
            double x=.5+Vector3D.DotProduct(delta,right)/(2*depth*tangent),y=.5-Vector3D.DotProduct(delta,up)*aspect/(2*depth*tangent);
            var ray=delta/delta.Length;double along=Vector3D.DotProduct((Vector3D)camera.Position,ray),discriminant=along*along-(((Vector3D)camera.Position).LengthSquared-2.7*2.7);
            if(discriminant>0&&-along-Math.Sqrt(discriminant)<delta.Length-.08)continue;
            double score=Math.Pow(x-.5,2)+Math.Pow(y-.75,2);
            if(score<bestScore){bestScore=score;bestYaw=candidateYaw;bestPitch=candidatePitch;}
        }
        Assert(double.IsFinite(bestScore),"Cannot frame the real ecology target in surface mode");
        control.ResetView();control.SetZoom(1.08);control.RotateView(bestYaw,bestPitch);
        var ecology=PlanetView3DScene.Build(2,"clear",false,42,placements);
        var duck=FindPlacedMotion(ecology,placements.Single(p=>p.Kind=="duck"));
        Assert(Angle(duck.Destination,PlotDirection(dominantTarget.PlotId))<1e-6,"The preview duck selected another pond instead of the framed target");
        double seconds=((12-duck.Phase)%32+32)%32;control.AnimateScene(seconds);layout.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(layout);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var absolute=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);using var stream=File.Create(absolute);encoder.Save(stream);
        Console.WriteLine($"Synthetic ecology preview: {absolute}; {control.InteractionCount} actual habitat interactions; animated {control.LastAnimatedObjectCount}, culled {control.LastCulledObjectCount}; placements {string.Join(", ",placements.Select(p=>$"{p.Kind}@{p.PlotId}"))}");
    }
    private static void CheckGroup(Model3DGroup group){foreach(var child in group.Children)if(child is GeometryModel3D g)CheckMesh((MeshGeometry3D)g.Geometry);else if(child is Model3DGroup nested)CheckGroup(nested);}
    private static int Triangles(Model3DGroup group)=>group.Children.Sum(m=>m is GeometryModel3D g?((MeshGeometry3D)g.Geometry).TriangleIndices.Count/3:m is Model3DGroup nested?Triangles(nested):0);
    private static IEnumerable<GeometryModel3D> GeometryLeaves(Model3D model)
    {
        if(model is GeometryModel3D geometry)yield return geometry;
        else if(model is Model3DGroup group)foreach(var child in group.Children)foreach(var leaf in GeometryLeaves(child))yield return leaf;
    }
    private static IEnumerable<(GeometryModel3D Model,Rect3D Bounds)> GeometryBounds(Model3D model,Matrix3D parent)
    {
        var current=model.Transform.Value;current.Append(parent);
        if(model is GeometryModel3D geometry)yield return(geometry,new MatrixTransform3D(current).TransformBounds(geometry.Geometry.Bounds));
        else if(model is Model3DGroup group)foreach(var child in group.Children)foreach(var leaf in GeometryBounds(child,current))yield return leaf;
    }
    private static Point3D BoundsCenter(Rect3D bounds)=>new(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
    private static Color? DiffuseColor(Material material)
    {
        if(material is DiffuseMaterial diffuse&&diffuse.Brush is SolidColorBrush solid)return solid.Color;
        if(material is MaterialGroup group)foreach(var child in group.Children)if(DiffuseColor(child) is {} color)return color;
        return null;
    }
    private static bool VisibleFromSide(ModelVisual3D visual,GeometryModel3D expected,Point3D center,int side,bool butterfly)
    {
        foreach(double horizontal in new[]{0.0,-.002,.002})foreach(double vertical in new[]{0.0,-.002,.002})
        {
            var point=butterfly?new Point3D(center.X,center.Y+vertical,center.Z+horizontal):new Point3D(center.X+horizontal,center.Y+vertical,center.Z);
            var outward=butterfly?new Vector3D(side,0,0):new Vector3D(0,0,side);GeometryModel3D? first=null;
            VisualTreeHelper.HitTest(visual,null,result=>{if(result is RayMeshGeometry3DHitTestResult hit){first=hit.ModelHit as GeometryModel3D;return HitTestResultBehavior.Stop;}return HitTestResultBehavior.Continue;},new RayHitTestParameters(point+outward*2,-outward));
            if(ReferenceEquals(first,expected))return true;
        }
        return false;
    }
    private static (int Triangles,int GeometryModels,int UniqueMeshes) SceneMetrics(PlanetScene scene)=>Metrics(GeometryLeaves(scene.Model).Concat(scene.Motions.SelectMany(m=>GeometryLeaves(m.Model))));
    private static (int Triangles,int GeometryModels,int UniqueMeshes) Metrics(IEnumerable<GeometryModel3D> geometry)
    {
        var models=geometry.ToArray();return(models.Sum(model=>((MeshGeometry3D)model.Geometry).TriangleIndices.Count/3),models.Length,models.Select(model=>model.Geometry).Distinct(ReferenceEqualityComparer.Instance).Count());
    }
    private static Vector3D PlotDirection(int plot){var p=PlanetView3DScene.Plots[plot];return PlanetView3DGeometry.Direction(p.Latitude,p.Longitude);}
    private static double Angle(Vector3D first,Vector3D second){first.Normalize();second.Normalize();return Math.Acos(Math.Clamp(Vector3D.DotProduct(first,second),-1,1));}
    private static PlanetSceneMotion FindPlacedMotion(PlanetScene scene,PlanetBuildingPlacement placement)
        =>scene.Motions.Single(m=>m.Kind=="creature"&&m.Species==placement.Kind&&Angle(m.Home,PlotDirection(placement.PlotId))<1e-6);
    private static void CheckMotion(PlanetSceneMotion motion)
    {
        Assert(new[]{motion.Home.X,motion.Home.Y,motion.Home.Z,motion.Destination.X,motion.Destination.Y,motion.Destination.Z,motion.Radius,motion.Heading,motion.Phase}.All(double.IsFinite),"Motion contains non-finite state");
        Assert(motion.Home.Length>0&&motion.Destination.Length>0&&motion.Radius>=2.69);
    }
    private static (int First,int Second)[] NearbyPlotPairs()
        =>Enumerable.Range(0,PlanetLayoutCatalog.PlotCount).SelectMany(a=>Enumerable.Range(a+1,PlanetLayoutCatalog.PlotCount-a-1).Select(b=>(First:a,Second:b,Distance:Angle(PlotDirection(a),PlotDirection(b))))).Where(p=>p.Distance>.15&&p.Distance<.66).OrderBy(p=>p.Distance).Select(p=>(p.First,p.Second)).ToArray();
    private static PlanetBuildingPlacement[] MaximumMovingLayout()
    {
        // Six species, two of each: 12 manually placed animals plus 20 real
        // landscapes/buildings, all within the catalog's individual limits.
        string[] kinds=["rabbit","rabbit","sheep","sheep","fox","fox","duck","duck","deer","deer","butterfly","butterfly","pond","pond","flower","flower","meadow","meadow","bush","bush","tree","tree","orchard","orchard","mushroom","mushroom","rockgarden","rockgarden","house","windmill","tower","observatory"];
        return kinds.Select((kind,index)=>new PlanetBuildingPlacement("maximum-"+index,kind,index,index%4)).ToArray();
    }
    private static bool FullMatrixClose(Matrix3D a,Matrix3D b)=>MatrixClose(a,b)&&Math.Abs(a.OffsetX-b.OffsetX)<1e-9&&Math.Abs(a.OffsetY-b.OffsetY)<1e-9&&Math.Abs(a.OffsetZ-b.OffsetZ)<1e-9&&Math.Abs(a.M44-b.M44)<1e-9;
    private static void CheckMesh(MeshGeometry3D mesh){Assert(mesh.IsFrozen);Assert(mesh.Positions.Count==mesh.Normals.Count);Assert(mesh.Positions.All(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.Z)));for(int i=0;i<mesh.TriangleIndices.Count;i+=3){int a=mesh.TriangleIndices[i],b=mesh.TriangleIndices[i+1],c=mesh.TriangleIndices[i+2];var cross=Vector3D.CrossProduct(mesh.Positions[b]-mesh.Positions[a],mesh.Positions[c]-mesh.Positions[a]);Assert(cross.Length>1e-9&&Vector3D.DotProduct(cross,mesh.Normals[a])>0);}}
    private static string Signature(Model3DGroup group){var text=new System.Text.StringBuilder();void Walk(Model3D m){text.Append(m.Transform.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));if(m is GeometryModel3D g)foreach(var p in ((MeshGeometry3D)g.Geometry).Positions)text.Append(p.ToString(System.Globalization.CultureInfo.InvariantCulture));else if(m is Model3DGroup inner)foreach(var c in inner.Children)Walk(c);}Walk(group);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));}
    private static PlanetView3D ArrangedPlanet(Size size){var c=new PlanetView3D();Arrange(c,size);return c;}
    private static void Arrange(PlanetView3D control,Size size){control.Width=size.Width;control.Height=size.Height;control.Measure(size);control.Arrange(new Rect(new Point(),size));control.UpdateLayout();}
    private static T Field<T>(PlanetView3D control,string name)=>(T)typeof(PlanetView3D).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
    private static Window OffscreenHost(UIElement content)=>new() {Title="Synthetic planet animation test",Left=-20000,Top=-20000,Width=1100,Height=700,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false,Content=content};
    private static void PumpUntil(Func<bool> condition,string failure)
    {
        if(condition())return;
        var frame=new DispatcherFrame();var clock=System.Diagnostics.Stopwatch.StartNew();bool succeeded=false;
        var probe=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(15)};
        probe.Tick+=(_,_)=>{if(condition()){succeeded=true;frame.Continue=false;}else if(clock.Elapsed>TimeSpan.FromSeconds(3))frame.Continue=false;};
        try{probe.Start();Dispatcher.PushFrame(frame);}finally{probe.Stop();}
        Assert(succeeded,failure);
    }
    private static void PumpFor(TimeSpan duration)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=duration};timer.Tick+=(_,_)=>frame.Continue=false;
        try{timer.Start();Dispatcher.PushFrame(frame);}finally{timer.Stop();}
    }
    private static PlanetSceneMotion[] LiveMotions(PlanetView3D control)
    {
        var actors=(System.Collections.IEnumerable)typeof(PlanetView3D).GetField("motions",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
        return actors.Cast<object>().Select(actor=>(PlanetSceneMotion)actor.GetType().GetField("Definition",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public)!.GetValue(actor)!).ToArray();
    }
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
