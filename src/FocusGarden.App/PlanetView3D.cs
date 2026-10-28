using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using FocusGarden.Core;

namespace FocusGarden.App;

/// <summary>A genuine 3D spherical habitat; it renders only the progression state supplied by its caller.</summary>
internal sealed partial class PlanetView3D : UserControl
{
    private readonly Viewport3D viewport=new() { ClipToBounds=true,Focusable=true };
    private readonly PerspectiveCamera camera=new() { FieldOfView=42,NearPlaneDistance=.1,FarPlaneDistance=100 };
    private readonly ModelVisual3D scene=new();
    private readonly AxisAngleRotation3D yawRotation=new(new(0,1,0),-6);
    private readonly AxisAngleRotation3D pitchRotation=new(new(1,0,0),12);
    private readonly TextBlock status=new() { Foreground=Brush("#BDD1C8"),FontSize=12,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock instruction=new() {Foreground=Brush("#A9BCB8"),FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(10,0,10,12),TextWrapping=TextWrapping.Wrap};
    private readonly Button orbitButton,surfaceButton;
    private readonly DispatcherTimer animation=new() { Interval=TimeSpan.FromMilliseconds(120) };
    private bool dragging, focusing,night;
    private int currentLevel=-1,currentSeed;
    private string currentWeather="";
    private Point pointer;
    private Point pressPointer;
    private bool editMode;
    private PlanetBuildingPlacement[] buildings=[];
    private IReadOnlyDictionary<GeometryModel3D,int> plotHits=new Dictionary<GeometryModel3D,int>();
    private readonly List<SceneActor> motions=[];
    private double animationSeconds;
    private double zoom=1;
    private double orbitZoom=1,surfaceZoom=1,pitchAngle=12;
    private bool surfaceView;
    internal int SceneBuildCount {get;private set;}
    internal IReadOnlyDictionary<string,int> FeatureCounts {get;private set;}=new Dictionary<string,int>();
    internal double Zoom=>zoom;
    internal bool IsSurfaceView=>surfaceView;
    internal bool AnimationRunning=>animation.IsEnabled;
    internal int MovingObjectCount=>motions.Count;
    internal double MotionOffsetTotal=>motions.Sum(m=>m.Offset.OffsetX+m.Offset.OffsetY+m.Turn.Angle+m.Surface.Matrix.OffsetX+m.Surface.Matrix.OffsetY+m.Lean.Angle);
    public int? SelectedPlotId {get;private set;}
    public event Action<int>? PlotClicked;

    public PlanetView3D(bool deferInitialScene=false)
    {
        MinHeight=300;Focusable=false;Foreground=Brush("#F2F6F5");
        var root=new Grid { Background=StarBrush(),ClipToBounds=true };
        root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        var toolbar=new StackPanel {Margin=new Thickness(16,12,14,8)};
        toolbar.Children.Add(status);
        var actions=new WrapPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,7,0,0)};
        Button CameraButton(string label,Action action,string hint){var b=new Button {Content=label,Foreground=Brush("#E5ECFF"),Background=Brush("#263853"),BorderBrush=Brush("#4B6385"),BorderThickness=new Thickness(1),Padding=new Thickness(10,5,10,5),FontSize=11,ToolTip=hint,Margin=new Thickness(0,0,7,0)};b.Click+=(_,_)=>action();return b;}
        orbitButton=CameraButton("全星球",()=>SetSurfaceView(false),"从太空查看完整星球，保留当前观察方向。");
        surfaceButton=CameraButton("地表近景",()=>SetSurfaceView(true),"靠近地表，查看弧形地平线、建筑和生物；拖动可探索其他区域。");
        actions.Children.Add(orbitButton);actions.Children.Add(surfaceButton);
        actions.Children.Add(CameraButton("−",()=>SetZoom(zoom/(surfaceView?.96:.85)),"拉远，查看更多景物"));
        actions.Children.Add(CameraButton("＋",()=>SetZoom(zoom*(surfaceView?.96:.85)),"靠近，观察细节"));
        actions.Children.Add(CameraButton("重置视角",ResetView,"重置当前模式的朝向和远近，也可双击星球或按 Home。"));toolbar.Children.Add(actions);root.Children.Add(toolbar);
        var surface=new Border {Background=Brush("#01000000"),Child=viewport,ClipToBounds=true};Grid.SetRow(surface,1);root.Children.Add(surface);
        Grid.SetRow(instruction,2);root.Children.Add(instruction);Content=root;UpdateViewLabels();
        viewport.Camera=camera;viewport.Children.Add(scene);
        var transform=new Transform3DGroup();transform.Children.Add(new RotateTransform3D(yawRotation));transform.Children.Add(new RotateTransform3D(pitchRotation));
        // Lights remain outside this transform; the star keeps a consistent external light source.
        planetTransform=transform;
        viewport.SizeChanged+=(_,_)=>UpdateCamera();
        surface.MouseLeftButtonDown+=(_,e)=>{viewport.Focus();if(e.ClickCount==2){ResetView();e.Handled=true;return;}dragging=true;pointer=pressPointer=e.GetPosition(viewport);viewport.CaptureMouse();Cursor=Cursors.SizeAll;UpdateAnimation();e.Handled=true;};
        surface.MouseMove+=(_,e)=>{if(!dragging)return;var next=e.GetPosition(viewport);RotateView((next.X-pointer.X)*(surfaceView?.16:.45),(next.Y-pointer.Y)*(surfaceView?.13:.35));pointer=next;e.Handled=true;};
        surface.MouseLeftButtonUp+=(_,e)=>{if(!dragging)return;var at=e.GetPosition(viewport);bool click=(at-pressPointer).Length<5;viewport.ReleaseMouseCapture();StopDrag();if(click&&editMode)ClickPlot(at);e.Handled=true;};
        viewport.LostMouseCapture+=(_,_)=>StopDrag();
        surface.PreviewMouseWheel+=(_,e)=>{SetZoom(zoom*Math.Pow(surfaceView?.96:.88,e.Delta/120.0));e.Handled=true;};
        viewport.KeyDown+=(_,e)=>{switch(e.Key){case Key.Left:RotateView(surfaceView?-3:-8,0);break;case Key.Right:RotateView(surfaceView?3:8,0);break;case Key.Up:RotateView(0,surfaceView?-2.5:-6);break;case Key.Down:RotateView(0,surfaceView?2.5:6);break;case Key.Home:ResetView();break;case Key.Add:case Key.OemPlus:SetZoom(zoom*(surfaceView?.96:.88));break;case Key.Subtract:case Key.OemMinus:SetZoom(zoom/(surfaceView?.96:.88));break;default:return;}e.Handled=true;};
        animation.Tick+=(_,_)=>{if(!IsSceneInView()){animation.Stop();return;}AnimationTickCount++;animationSeconds+=.12;AnimateScene(animationSeconds);if(focusing&&!surfaceView)yawRotation.Angle=(yawRotation.Angle+.045)%360;};
        Loaded+=(_,_)=>{AttachVisibilityHost();UpdateAnimation();};IsVisibleChanged+=(_,_)=>UpdateAnimation();
        Unloaded+=(_,_)=>{animation.Stop();DetachVisibilityHost();if(viewport.IsMouseCaptured)viewport.ReleaseMouseCapture();};
        if(!deferInitialScene)SetState(0,"clear",false,false);
    }

    private readonly Transform3DGroup planetTransform;

    private void ClickPlot(Point point)
    {
        var found=HitTestPlot(point);
        if(found.HasValue){SelectPlot(found);PlotClicked?.Invoke(found.Value);}
    }
    internal int? HitTestPlot(Point point)
    {
        int? found=null;VisualTreeHelper.HitTest(viewport,null,result=>{if(result is RayMeshGeometry3DHitTestResult hit){if(hit.ModelHit is GeometryModel3D model&&plotHits.TryGetValue(model,out var plot))found=plot;return HitTestResultBehavior.Stop;}return HitTestResultBehavior.Continue;},new PointHitTestParameters(point));return found;
    }

    public void SetSurfaceView(bool enabled)
    {
        Dispatcher.VerifyAccess();if(surfaceView==enabled)return;
        if(surfaceView)surfaceZoom=zoom;else orbitZoom=zoom;
        surfaceView=enabled;zoom=surfaceView?surfaceZoom:orbitZoom;
        pitchRotation.Angle=pitchAngle-(surfaceView?55:0);
        UpdateViewLabels();UpdateCamera();UpdateAnimation();
    }
    internal void RotateView(double yawDelta,double pitchDelta)
    {
        if(!double.IsFinite(yawDelta)||!double.IsFinite(pitchDelta))return;
        yawRotation.Angle=(yawRotation.Angle+yawDelta)%360;
        pitchAngle=Math.Clamp(pitchAngle+pitchDelta,-85,85);
        pitchRotation.Angle=pitchAngle-(surfaceView?55:0);
    }
    internal void SetZoom(double value) {zoom=Math.Clamp(double.IsFinite(value)?value:1,surfaceView?.86:.45,surfaceView?1.18:1.7);UpdateCamera();}
    public void ResetView(){yawRotation.Angle=-6;pitchAngle=12;pitchRotation.Angle=pitchAngle-(surfaceView?55:0);SetZoom(1);}
    private void UpdateViewLabels()
    {
        orbitButton.Background=Brush(surfaceView?"#263853":"#486588");surfaceButton.Background=Brush(surfaceView?"#486588":"#263853");
        orbitButton.FontWeight=surfaceView?FontWeights.Normal:FontWeights.SemiBold;surfaceButton.FontWeight=surfaceView?FontWeights.SemiBold:FontWeights.Normal;
        instruction.Text=surfaceView?"地表近景  ·  拖动环顾四周  ·  滚轮调整远近  ·  双击重置":"拖动探索星球  ·  滚轮缩放  ·  双击重置";
        System.Windows.Automation.AutomationProperties.SetItemStatus(orbitButton,surfaceView?"未选择":"已选择");
        System.Windows.Automation.AutomationProperties.SetItemStatus(surfaceButton,surfaceView?"已选择":"未选择");
    }
    private void StopDrag(){dragging=false;Cursor=Cursors.Arrow;UpdateAnimation();}
    private void UpdateAnimation(){if(IsLoaded&&IsVisible&&IsSceneInView()&&((focusing&&!surfaceView)||motions.Count>0)&&!dragging&&!editMode)animation.Start();else animation.Stop();}
    private void UpdateCamera()
    {
        double aspect=Math.Max(.4,viewport.ActualWidth/Math.Max(1,viewport.ActualHeight));
        if(surfaceView)
        {
            // Look just above the true tangent of the sphere: the horizon stays 56% down
            // the viewport while the near surface extends beyond its sides and bottom.
            // Keep the camera outside even the tallest landmarks and limit the close-up
            // field of view so a resize never reveals a detached, complete globe.
            double surfaceDistance=Math.Sqrt(3.2*3.2+4*4)*zoom;
            double elevation=Math.Atan2(3.2,4);
            double horizontalTangent=Math.Min(Math.Tan(25*Math.PI/180),Math.Tan(19*Math.PI/180)*aspect);
            double limbElevation=Math.Asin(2.7/surfaceDistance)-elevation;
            camera.Position=new(0,Math.Sin(elevation)*surfaceDistance,Math.Cos(elevation)*surfaceDistance);
            // A very wide viewport otherwise reveals the globe's lower side edges.
            // Narrow the lens until a ray just outside each side, at 90% height,
            // intersects the real sphere, leaving a margin for its tessellated rim.
            bool CoversLowerSides(double tangent)
            {
                double angle=limbElevation+Math.Atan(.12*tangent/aspect);
                var ray=new Vector3D(1.05*tangent,Math.Sin(angle)-.8*tangent/aspect*Math.Cos(angle),-Math.Cos(angle)-.8*tangent/aspect*Math.Sin(angle));ray.Normalize();
                double toward=Vector3D.DotProduct((Vector3D)camera.Position,ray);
                return toward<0&&surfaceDistance*surfaceDistance-toward*toward<2.7*2.7;
            }
            if(!CoversLowerSides(horizontalTangent))
            {
                double low=0,high=horizontalTangent;
                for(int i=0;i<24;i++){double middle=(low+high)/2;if(CoversLowerSides(middle))low=middle;else high=middle;}
                horizontalTangent=low*.98;
            }
            camera.FieldOfView=2*Math.Atan(horizontalTangent)*180/Math.PI;
            double lookElevation=limbElevation+Math.Atan(.12*horizontalTangent/aspect);
            camera.LookDirection=new(0,Math.Sin(lookElevation),-Math.Cos(lookElevation));
            camera.UpDirection=new(0,Math.Cos(lookElevation),Math.Sin(lookElevation));
            return;
        }
        camera.FieldOfView=42;
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
