using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    private static readonly Dictionary<(double Radius,double Height),Model3DGroup> MountainModels=[];

    // A small sculpted ridge replaces the five-sided cone. A shared 108-face peak
    // gives an uneven snow line and bent summit; two smaller copies add shoulders.
    private static Model3DGroup SculptedMountain(double radius,double height)
    {
        lock(MountainModels)
        {
            if(!MountainModels.TryGetValue((radius,height),out var model))
            {
                model=new Model3DGroup();const int sides=12;
                double[] widths=[1,.89,.61,.32,.12];
                double[] levels=[-.025,.15,.39,.68,.87];
                var points=new Point3D[levels.Length,sides];
                for(int ring=0;ring<levels.Length;ring++)
                    for(int side=0;side<sides;side++)
                    {
                        double angle=side*2*Math.PI/sides;
                        double ridge=1+.10*Math.Sin(angle*3+.4)+.06*Math.Cos(angle*5);
                        double wobble=ring==3?.070*Math.Sin(angle*3+.7):ring==0?0:.025*Math.Sin(angle*2+ring);
                        double bend=levels[ring]*levels[ring];
                        points[ring,side]=new(Math.Cos(angle)*radius*widths[ring]*ridge+radius*.14*bend,
                            height*(levels[ring]+wobble),Math.Sin(angle)*radius*widths[ring]*ridge-radius*.10*bend);
                    }
                var stone=new MeshGeometry3D();var snow=new MeshGeometry3D();
                for(int ring=0;ring<levels.Length-1;ring++)
                    for(int side=0;side<sides;side++)
                    {
                        int next=(side+1)%sides;var mesh=ring>=3?snow:stone;
                        // Continuous vertex normals soften the large slopes; the
                        // ring silhouette and irregular snow line remain actual geometry.
                        Face(mesh,points[ring,side],points[ring+1,side],points[ring+1,next]);
                        Face(mesh,points[ring,side],points[ring+1,next],points[ring,next]);
                    }
                var summit=new Point3D(radius*.14,height,radius*-.10);
                for(int side=0;side<sides;side++)Face(snow,points[4,side],summit,points[4,(side+1)%sides]);
                stone.Freeze();snow.Freeze();var peak=new Model3DGroup();Add(peak,stone,"#899A90");Add(peak,snow,"#E6EADC");peak.Freeze();model.Children.Add(peak);
                foreach(var shoulder in new[]{(X:.61,Z:.08,Width:.48,Height:.59),(X:-.49,Z:-.25,Width:.44,Height:.43)})
                {
                    var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(shoulder.Width,shoulder.Height,shoulder.Width));
                    transform.Children.Add(new TranslateTransform3D(shoulder.X*radius,0,shoulder.Z*radius));transform.Freeze();
                    var secondary=new Model3DGroup {Transform=transform};secondary.Children.Add(peak);model.Children.Add(secondary);
                }
                // Exposed rock ribs break up the smooth slope without a texture or shader.
                foreach(int side in new[]{1,5,9})
                {
                    double angle=side*2*Math.PI/sides;
                    var spur=new Model3DGroup();
                    DetailRound(spur,0,0,0,radius*.22,height*.12,radius*.17,"#9AAA98");
                    spur.Transform=new TranslateTransform3D(Math.Cos(angle)*radius*.77,height*.075,Math.Sin(angle)*radius*.77);
                    model.Children.Add(spur);
                }
                model.Freeze();MountainModels.Add((radius,height),model);
            }
            // Mountain locations are not editable; their immutable geometry can be shared.
            var wrapper=new Model3DGroup();wrapper.Children.Add(model);return wrapper;
        }
        void Face(MeshGeometry3D mesh,Point3D a,Point3D b,Point3D c)
        {
            var faceNormal=Vector3D.CrossProduct(b-a,c-a);
            var center=(Point3D)(((Vector3D)a+(Vector3D)b+(Vector3D)c)/3);
            if(Vector3D.DotProduct(faceNormal,new(center.X,0,center.Z))<0)(b,c)=(c,b);
            PlanetView3DGeometry.SmoothTriangle(mesh,a,b,c,new(0,-height*.20,0));
        }
    }
}
