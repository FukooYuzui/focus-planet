using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal sealed partial class PlanetView3D
{
    private sealed class SceneActor(PlanetSceneMotion definition)
    {
        internal PlanetSceneMotion Definition=definition;
        internal readonly TranslateTransform3D Offset=new();
        internal readonly AxisAngleRotation3D Turn=new(new(0,1,0),0);
        internal readonly AxisAngleRotation3D Lean=new(new(0,0,1),0);
        internal readonly MatrixTransform3D Surface=new(definition.Surface.Value);
    }

    private Window? animationHost;
    private ScrollViewer? animationScroll;
    internal int InteractionCount=>motions.Count(m=>m.Definition.Habitat is not null);
    internal int LastAnimatedObjectCount {get;private set;}
    internal int LastCulledObjectCount {get;private set;}
    internal long AnimationTickCount {get;private set;}

    private void AttachVisibilityHost()
    {
        DetachVisibilityHost();animationHost=Window.GetWindow(this);
        if(animationHost is not null)animationHost.StateChanged+=HostStateChanged;
        DependencyObject? parent=this;
        while((parent=VisualTreeHelper.GetParent(parent)) is not null)
            if(parent is ScrollViewer scroll){animationScroll=scroll;scroll.ScrollChanged+=HostScrolled;break;}
    }
    private void DetachVisibilityHost()
    {
        if(animationHost is not null)animationHost.StateChanged-=HostStateChanged;
        if(animationScroll is not null)animationScroll.ScrollChanged-=HostScrolled;
        animationHost=null;animationScroll=null;
    }
    private void HostStateChanged(object? sender,EventArgs e)=>UpdateAnimation();
    private void HostScrolled(object sender,ScrollChangedEventArgs e)=>UpdateAnimation();
    internal bool IsSceneInView()
    {
        if(animationHost?.WindowState==WindowState.Minimized)return false;
        if(animationScroll is not null&&IsLoaded&&ActualWidth>0&&ActualHeight>0)
        {
            var bounds=TransformToAncestor(animationScroll).TransformBounds(new Rect(0,0,ActualWidth,ActualHeight));
            if(!bounds.IntersectsWith(new Rect(0,0,animationScroll.ActualWidth,animationScroll.ActualHeight)))return false;
        }
        return true;
    }

    internal void AnimateScene(double seconds)
    {
        LastAnimatedObjectCount=LastCulledObjectCount=0;
        if(editMode||!double.IsFinite(seconds))return;
        var planet=planetTransform.Value;
        var forward=camera.LookDirection;forward.Normalize();
        var up=camera.UpDirection;up.Normalize();
        var right=Vector3D.CrossProduct(forward,up);right.Normalize();
        double tangent=Math.Tan(camera.FieldOfView*Math.PI/360);
        double aspect=Math.Max(.4,viewport.ActualWidth/Math.Max(1,viewport.ActualHeight));
        for(int i=0;i<motions.Count;i++)
        {
            var actor=motions[i];var m=actor.Definition;
            double cycle=((seconds+m.Phase)%32+32)%32;
            double progress=cycle<9?Ease(cycle/9):cycle<17?1:cycle<26?1-Ease((cycle-17)/9):0;
            bool returning=cycle>=17&&cycle<26,visiting=cycle>=9&&cycle<17;
            var normal=m.Home*(1-progress)+m.Destination*progress;normal.Normalize();
            var point=planet.Transform((Point3D)(normal*(m.Radius+.2)));
            if(!Visible(point)) {LastCulledObjectCount++;continue;}
            LastAnimatedObjectCount++;
            double phase=seconds*.65+m.Phase;
            if(m.Kind=="cloud")
            {
                actor.Offset.OffsetX=Math.Sin(seconds*.22+m.Phase)*.12;
                actor.Offset.OffsetY=Math.Cos(seconds*.154+m.Phase)*.025;
                actor.Turn.Angle=Math.Sin(phase*.2)*3;
                continue;
            }
            var facing=m.Surface.Transform(new Vector3D(1,0,0));
            if(m.Habitat is not null&&progress>0&&progress<1)
                facing=returning?m.Home-m.Destination:m.Destination-m.Home;
            else if(visiting&&m.Habitat is not null)facing=m.Destination-m.Home;
            facing-=normal*Vector3D.DotProduct(facing,normal);
            if(facing.LengthSquared<1e-10){facing=Vector3D.CrossProduct(new(0,0,1),normal);if(facing.LengthSquared<1e-10)facing=new(1,0,0);}
            facing.Normalize();var side=Vector3D.CrossProduct(facing,normal);side.Normalize();
            actor.Surface.Matrix=new Matrix3D(facing.X,facing.Y,facing.Z,0,normal.X,normal.Y,normal.Z,0,side.X,side.Y,side.Z,0,normal.X*m.Radius,normal.Y*m.Radius,normal.Z*m.Radius,1);
            bool flying=m.Species=="butterfly",swimming=m.Species=="duck"&&m.Habitat=="pond"&&visiting;
            bool traveling=m.Habitat is not null&&progress>0&&progress<1;
            actor.Offset.OffsetX=Math.Sin(phase)*(swimming?.045:flying?.035:.012);
            actor.Offset.OffsetZ=swimming?Math.Cos(phase)*.04:0;
            actor.Offset.OffsetY=flying?.13+Math.Sin(phase*3)*.035:swimming?.023:traveling?Math.Pow(Math.Sin(phase*3),2)*(m.Species=="rabbit"?.055:.012):0;
            actor.Turn.Angle=swimming?phase*180/Math.PI:flying?Math.Sin(phase*2)*18:Math.Sin(phase*.55)*6;
            actor.Lean.Angle=flying?Math.Sin(phase*4)*18:visiting&&m.Habitat is not null&&!swimming?-8-Math.Pow(Math.Sin(phase*1.8),2)*7:0;
        }
        static double Ease(double t)=>t*t*(3-2*t);
        bool Visible(Point3D point)
        {
            var delta=point-camera.Position;double depth=Vector3D.DotProduct(delta,forward);
            if(depth<.1)return false;
            // Conservative model-sized margin prevents visible ears/wings popping at edges.
            if(Math.Abs(Vector3D.DotProduct(delta,right))>depth*tangent+.65||Math.Abs(Vector3D.DotProduct(delta,up))>depth*tangent/aspect+.65)return false;
            double distance=delta.Length;if(distance<1e-9)return true;
            var ray=delta/distance;var cameraVector=(Vector3D)camera.Position;
            double along=Vector3D.DotProduct(cameraVector,ray);
            double discriminant=along*along-(cameraVector.LengthSquared-2.67*2.67);
            return discriminant<=0||-along-Math.Sqrt(discriminant)>=distance-.3;
        }
    }
}
