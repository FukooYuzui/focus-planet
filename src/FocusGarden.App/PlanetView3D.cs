using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using FocusGarden.Core;

namespace FocusGarden.App;

/// <summary>A genuine 3D spherical habitat; it renders only the progression state supplied by its caller.</summary>
internal sealed class PlanetView3D : UserControl
{
    private readonly Viewport3D viewport=new() { ClipToBounds=true,Focusable=true };
    private readonly PerspectiveCamera camera=new() { FieldOfView=42,NearPlaneDistance=.1,FarPlaneDistance=100 };
    private readonly ModelVisual3D scene=new();
    private readonly AxisAngleRotation3D yawRotation=new(new(0,1,0),-6);
    private readonly AxisAngleRotation3D pitchRotation=new(new(1,0,0),12);
    private readonly TextBlock status=new() { Foreground=Brush("#BDD1C8"),FontSize=12,VerticalAlignment=VerticalAlignment.Center };
    private readonly DispatcherTimer animation=new() { Interval=TimeSpan.FromMilliseconds(120) };
    private bool dragging, focusing,night;
    private int currentLevel=-1,currentSeed;
    private string currentWeather="";
    private Point pointer;
    private Point pressPointer;
    private bool editMode;
    private int layoutRevision,renderedLayoutRevision=-1;
    private PlanetBuildingPlacement[] buildings=[];
    private IReadOnlyDictionary<GeometryModel3D,int> plotHits=new Dictionary<GeometryModel3D,int>();
    private readonly List<(TranslateTransform3D Offset,AxisAngleRotation3D Turn,string Kind)> motions=[];
    private double animationSeconds;
    private double zoom=1;
    internal int SceneBuildCount {get;private set;}
    internal IReadOnlyDictionary<string,int> FeatureCounts {get;private set;}=new Dictionary<string,int>();
    internal double Zoom=>zoom;
    internal bool AnimationRunning=>animation.IsEnabled;
    internal int MovingObjectCount=>motions.Count;
    internal double MotionOffsetTotal=>motions.Sum(m=>m.Offset.OffsetX+m.Offset.OffsetY+m.Turn.Angle);
    public int? SelectedPlotId {get;private set;}
    public event Action<int>? PlotClicked;

    public PlanetView3D()
    {
        MinHeight=300;Focusable=false;Foreground=Brush("#F2F6F5");
        var root=new Grid { Background=StarBrush(),ClipToBounds=true };
        root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        var toolbar=new Grid {Margin=new Thickness(16,12,14,0)};
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());toolbar.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        toolbar.Children.Add(status);
        var actions=new StackPanel {Orientation=Orientation.Horizontal};
        Button CameraButton(string label,Action action,string hint){var b=new Button {Content=label,Foreground=Brush("#E5ECFF"),Background=Brush("#263853"),BorderBrush=Brush("#4B6385"),BorderThickness=new Thickness(1),Padding=new Thickness(10,5,10,5),FontSize=11,ToolTip=hint,Margin=new Thickness(0,0,7,0)};b.Click+=(_,_)=>action();return b;}
        actions.Children.Add(CameraButton("−",()=>SetZoom(zoom/ .85),"缩小，查看整体"));
        actions.Children.Add(CameraButton("＋",()=>SetZoom(zoom*.85),"放大，观察细节"));
        actions.Children.Add(CameraButton("重置视角",ResetView,"恢复默认朝向和大小，也可双击星球或按 Home。"));Grid.SetColumn(actions,1);toolbar.Children.Add(actions);root.Children.Add(toolbar);
        var surface=new Border {Background=Brush("#01000000"),Child=viewport,ClipToBounds=true};Grid.SetRow(surface,1);root.Children.Add(surface);
        var instruction=new TextBlock {Text="拖动探索星球  ·  滚轮缩放  ·  双击重置",Foreground=Brush("#A9BCB8"),FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(10,0,10,12),TextWrapping=TextWrapping.Wrap};Grid.SetRow(instruction,2);root.Children.Add(instruction);Content=root;
        viewport.Camera=camera;viewport.Children.Add(scene);
        var transform=new Transform3DGroup();transform.Children.Add(new RotateTransform3D(yawRotation));transform.Children.Add(new RotateTransform3D(pitchRotation));
        // Lights remain outside this transform; the star keeps a consistent external light source.
        planetTransform=transform;
        viewport.SizeChanged+=(_,_)=>UpdateCamera();
        surface.MouseLeftButtonDown+=(_,e)=>{viewport.Focus();if(e.ClickCount==2){ResetView();e.Handled=true;return;}dragging=true;pointer=pressPointer=e.GetPosition(viewport);viewport.CaptureMouse();Cursor=Cursors.SizeAll;UpdateAnimation();e.Handled=true;};
        surface.MouseMove+=(_,e)=>{if(!dragging)return;var next=e.GetPosition(viewport);yawRotation.Angle+=(next.X-pointer.X)*.45;pitchRotation.Angle=Math.Clamp(pitchRotation.Angle+(next.Y-pointer.Y)*.35,-85,85);pointer=next;e.Handled=true;};
        surface.MouseLeftButtonUp+=(_,e)=>{if(!dragging)return;var at=e.GetPosition(viewport);bool click=(at-pressPointer).Length<5;viewport.ReleaseMouseCapture();StopDrag();if(click&&editMode)ClickPlot(at);e.Handled=true;};
        viewport.LostMouseCapture+=(_,_)=>StopDrag();
        surface.PreviewMouseWheel+=(_,e)=>{SetZoom(zoom*Math.Pow(.88,e.Delta/120.0));e.Handled=true;};
        viewport.KeyDown+=(_,e)=>{switch(e.Key){case Key.Left:yawRotation.Angle-=8;break;case Key.Right:yawRotation.Angle+=8;break;case Key.Up:pitchRotation.Angle=Math.Max(-85,pitchRotation.Angle-6);break;case Key.Down:pitchRotation.Angle=Math.Min(85,pitchRotation.Angle+6);break;case Key.Home:ResetView();break;case Key.Add:case Key.OemPlus:SetZoom(zoom*.88);break;case Key.Subtract:case Key.OemMinus:SetZoom(zoom/.88);break;default:return;}e.Handled=true;};
        animation.Tick+=(_,_)=>{animationSeconds+=.12;AnimateScene(animationSeconds);if(focusing)yawRotation.Angle=(yawRotation.Angle+.045)%360;};
        Loaded+=(_,_)=>UpdateAnimation();IsVisibleChanged+=(_,_)=>UpdateAnimation();
        Unloaded+=(_,_)=>{animation.Stop();if(viewport.IsMouseCaptured)viewport.ReleaseMouseCapture();};
        SetState(0,"clear",false,false);
    }

    private readonly Transform3DGroup planetTransform;

    public void SetState(int level,string weather,bool isFocusing,bool isNight,int seed=20261027)
    {
        Dispatcher.VerifyAccess();
        if(level is <0 or >5)throw new ArgumentOutOfRangeException(nameof(level));
        if(weather is not ("clear" or "cloudy" or "rain" or "aurora"))throw new ArgumentException("天气应为 clear、cloudy、rain 或 aurora。",nameof(weather));
        focusing=isFocusing;
        if(currentLevel!=level||currentWeather!=weather||night!=isNight||currentSeed!=seed||layoutRevision!=renderedLayoutRevision)
        {
            var built=PlanetView3DScene.Build(level,weather,isNight,seed,buildings,editMode,SelectedPlotId??-1);FeatureCounts=built.Features;plotHits=built.PlotHits;
            var world=new Model3DGroup();
            world.Children.Add(new AmbientLight(isNight?Color.FromRgb(70,86,105):Color.FromRgb(133,143,140)));
            world.Children.Add(new DirectionalLight(isNight?Color.FromRgb(109,149,181):Color.FromRgb(255,245,216),new(-.7,-1,-1.6)));
            world.Children.Add(new DirectionalLight(isNight?Color.FromRgb(62,101,117):Color.FromRgb(82,116,130),new(1,.3,.5)));
            var body=new Model3DGroup {Transform=planetTransform};body.Children.Add(built.Model);motions.Clear();
            foreach(var moving in built.Motions)
            {
                var offset=new TranslateTransform3D();var turn=new AxisAngleRotation3D(new(0,1,0),0);var transform=new Transform3DGroup();transform.Children.Add(new RotateTransform3D(turn));transform.Children.Add(offset);transform.Children.Add(moving.Surface);var group=new Model3DGroup {Transform=transform};group.Children.Add(moving.Model);body.Children.Add(group);motions.Add((offset,turn,moving.Kind));
            }
            AnimateScene(animationSeconds);world.Children.Add(body);scene.Content=world;
            currentLevel=level;currentWeather=weather;night=isNight;currentSeed=seed;renderedLayoutRevision=layoutRevision;SceneBuildCount++;UpdateCamera();
        }
        string state=weather switch {"clear"=>"晴朗","cloudy"=>"云朵漫游","rain"=>"轻雨滋养",_=>"极光之夜"};
        status.Text=$"Lv.{level}  ·  {state}  ·  {(isNight?"夜晚":"白昼")}{(editMode?$"  /  布置家园{(SelectedPlotId.HasValue?$" · 地块 {SelectedPlotId+1}":"")}":isFocusing?"  /  专注中":"")}";UpdateAnimation();
    }

    public void SetBuildings(IReadOnlyList<PlanetBuildingPlacement> placements)
    {
        Dispatcher.VerifyAccess();ArgumentNullException.ThrowIfNull(placements);
        var snapshot=placements.ToArray();
        if(snapshot.Any(p=>p is null||string.IsNullOrWhiteSpace(p.Id)||p.PlotId is <0 or >=32||p.QuarterTurns is <0 or >3||p.Kind is not ("tree" or "flower" or "house" or "windmill" or "tower" or "observatory"))||snapshot.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=snapshot.Length||snapshot.Select(p=>p.PlotId).Distinct().Count()!=snapshot.Length)throw new ArgumentException("建筑须使用唯一 ID 和地块，朝向为 0–3。",nameof(placements));
        snapshot=snapshot.OrderBy(p=>p.PlotId).ToArray();if(buildings.SequenceEqual(snapshot))return;buildings=snapshot;layoutRevision++;RefreshState();
    }
    public void SetEditMode(bool enabled){Dispatcher.VerifyAccess();if(editMode==enabled)return;editMode=enabled;if(!enabled)SelectedPlotId=null;layoutRevision++;RefreshState();}
    public void SelectPlot(int? plotId){Dispatcher.VerifyAccess();if(plotId is <0 or >=32)throw new ArgumentOutOfRangeException(nameof(plotId));if(SelectedPlotId==plotId)return;SelectedPlotId=plotId;layoutRevision++;RefreshState();}
    private void RefreshState()=>SetState(currentLevel,currentWeather,focusing,night,currentSeed);
    private void ClickPlot(Point point)
    {
        var found=HitTestPlot(point);
        if(found.HasValue){SelectPlot(found);PlotClicked?.Invoke(found.Value);}
    }
    internal int? HitTestPlot(Point point)
    {
        int? found=null;VisualTreeHelper.HitTest(viewport,null,result=>{if(result is RayMeshGeometry3DHitTestResult hit){if(hit.ModelHit is GeometryModel3D model&&plotHits.TryGetValue(model,out var plot))found=plot;return HitTestResultBehavior.Stop;}return HitTestResultBehavior.Continue;},new PointHitTestParameters(point));return found;
    }

    internal void SetZoom(double value) {zoom=Math.Clamp(double.IsFinite(value)?value:1,.45,1.7);UpdateCamera();}
    public void ResetView(){yawRotation.Angle=-6;pitchRotation.Angle=12;SetZoom(1);}
    private void StopDrag(){dragging=false;Cursor=Cursors.Arrow;UpdateAnimation();}
    private void UpdateAnimation(){if(IsLoaded&&IsVisible&&(focusing||motions.Count>0)&&!dragging&&!editMode)animation.Start();else animation.Stop();}
    internal void AnimateScene(double seconds)
    {
        for(int i=0;i<motions.Count;i++)
        {
            var motion=motions[i];double phase=seconds*(motion.Kind=="cloud"?.22:.65)+i*1.9;
            motion.Offset.OffsetX=Math.Sin(phase)*(motion.Kind=="cloud"?.12:.035);
            motion.Offset.OffsetY=motion.Kind=="cloud"?Math.Cos(phase*.7)*.025:Math.Pow(Math.Sin(phase*2),2)*.018;
            motion.Turn.Angle=Math.Sin(phase*.55)*(motion.Kind=="cloud"?3:9);
        }
    }
    private void UpdateCamera()
    {
        double aspect=Math.Max(.4,viewport.ActualWidth/Math.Max(1,viewport.ActualHeight));
        // WPF FieldOfView is horizontal. Fit against the tighter axis so large and narrow
        // windows keep the planet prominent; zoom remains relative when the viewport resizes.
        double tangent=Math.Tan(camera.FieldOfView*Math.PI/360)/Math.Max(1,aspect);
        double distance=Math.Max(4.05,3.15/Math.Sin(Math.Atan(tangent))*zoom);
        camera.Position=new(0,.18,distance);camera.LookDirection=new(0,-.18,-distance);camera.UpDirection=new(0,1,0);
    }
    private static SolidColorBrush Brush(string hex) {var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));b.Freeze();return b;}
    private static DrawingBrush StarBrush()
    {
        var drawing=new DrawingGroup();using(var dc=drawing.Open())
        {
            var background=new RadialGradientBrush {Center=new(.5,.46),GradientOrigin=new(.5,.45),RadiusX=.66,RadiusY=.8};background.GradientStops.Add(new(Color.FromRgb(22,42,69),0));background.GradientStops.Add(new(Color.FromRgb(12,19,38),.57));background.GradientStops.Add(new(Color.FromRgb(5,9,22),1));
            dc.DrawRectangle(background,null,new Rect(0,0,1200,750));var random=new Random(7241);
            foreach(var cloud in new[]{(X:180.0,Y:190.0,R:320.0,Color:"#304A3477"),(X:1040.0,Y:560.0,R:370.0,Color:"#2434678B")})
            {var haze=new RadialGradientBrush((Color)ColorConverter.ConvertFromString(cloud.Color),Colors.Transparent);dc.DrawEllipse(haze,null,new(cloud.X,cloud.Y),cloud.R,cloud.R*.7);}
            for(int i=0;i<310;i++){double x=random.NextDouble()*1200,y=random.NextDouble()*750,r=i%19==0?1.7:.35+random.NextDouble()*.65;var color=Brush(i%19==0?"#CCDDF8":i%7==0?"#9C92C5":"#637694");dc.DrawEllipse(color,null,new(x,y),r,r);if(i%53==0){var p=new Pen(Brush("#8A99B9"),.7);dc.DrawLine(p,new(x-4,y),new(x+4,y));dc.DrawLine(p,new(x,y-4),new(x,y+4));}}
        }
        drawing.ClipGeometry=new RectangleGeometry(new Rect(0,0,1200,750));
        drawing.Freeze();var result=new DrawingBrush(drawing){Stretch=Stretch.Fill};result.Freeze();return result;
    }
}
