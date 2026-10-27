using System.Windows.Media.Media3D;

namespace FocusGarden.App;

/// <summary>Flat-shaded, outward-facing primitives used by the native WPF planet.</summary>
internal static class PlanetView3DGeometry
{
    internal static Vector3D Direction(double latitude, double longitude)
    {
        double lat = latitude * Math.PI / 180, lon = longitude * Math.PI / 180;
        return new(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    internal static Transform3D SurfaceTransform(double latitude, double longitude, double radius = 2.69, double heading = 0)
    {
        var normal = Direction(latitude, longitude);
        var axis = Vector3D.CrossProduct(new(0, 1, 0), normal);
        if (axis.LengthSquared < 1e-12) axis = new(1, 0, 0);
        var result = new Transform3DGroup();
        result.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 1, 0), heading)));
        result.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, Vector3D.AngleBetween(new(0, 1, 0), normal))));
        result.Children.Add(new TranslateTransform3D(normal * radius)); result.Freeze(); return result;
    }

    internal static IReadOnlyList<(Point3D A, Point3D B, Point3D C)> SphereFaces(int subdivisions = 2)
    {
        if (subdivisions is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(subdivisions));
        double t = (1 + Math.Sqrt(5)) / 2;
        var points = new[] { new Point3D(-1,t,0),new(1,t,0),new(-1,-t,0),new(1,-t,0),new(0,-1,t),new(0,1,t),new(0,-1,-t),new(0,1,-t),new(t,0,-1),new(t,0,1),new(-t,0,-1),new(-t,0,1) }.Select(Unit).ToArray();
        int[] indices = [0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8, 3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1];
        var faces = new List<(Point3D, Point3D, Point3D)>();
        for (int i = 0; i < indices.Length; i += 3) faces.Add((points[indices[i]], points[indices[i+1]], points[indices[i+2]]));
        for (int j = 0; j < subdivisions; j++)
        {
            var next = new List<(Point3D, Point3D, Point3D)>();
            foreach (var (a,b,c) in faces) { var ab = Mid(a,b); var bc = Mid(b,c); var ca = Mid(c,a); next.Add((a,ab,ca)); next.Add((b,bc,ab)); next.Add((c,ca,bc)); next.Add((ab,bc,ca)); }
            faces = next;
        }
        return faces;
    }

    internal static MeshGeometry3D Ellipsoid(double x, double y, double z, double rx, double ry, double rz, int detail = 1)
    {
        CheckDimensions(rx, ry, rz); var mesh = new MeshGeometry3D();
        foreach (var (a,b,c) in SphereFaces(detail)) Triangle(mesh, Stretch(a), Stretch(b), Stretch(c));
        mesh.Freeze(); return mesh;
        Point3D Stretch(Point3D p) => new(x+p.X*rx,y+p.Y*ry,z+p.Z*rz);
    }

    internal static MeshGeometry3D Frustum(double bottomRadius, double topRadius, double height, int sides = 7, double y = 0)
    {
        CheckDimensions(bottomRadius,height); if (!double.IsFinite(topRadius) || topRadius < 0 || sides is < 3 or > 64) throw new ArgumentOutOfRangeException(nameof(topRadius));
        var mesh = new MeshGeometry3D();
        for(int i=0;i<sides;i++)
        {
            double a = i*2*Math.PI/sides, b = (i+1)*2*Math.PI/sides;
            Point3D p = new(Math.Cos(a)*bottomRadius,y,Math.Sin(a)*bottomRadius), q = new(Math.Cos(b)*bottomRadius,y,Math.Sin(b)*bottomRadius);
            Point3D u = new(Math.Cos(a)*topRadius,y+height,Math.Sin(a)*topRadius), v = new(Math.Cos(b)*topRadius,y+height,Math.Sin(b)*topRadius);
            Triangle(mesh,p,u,q); if(topRadius>0) Triangle(mesh,q,u,v);
            Triangle(mesh,new(0,y,0),p,q); if(topRadius>0) Triangle(mesh,new(0,y+height,0),v,u);
        }
        mesh.Freeze(); return mesh;
    }

    internal static MeshGeometry3D Box(double x, double y, double z, double width, double height, double depth)
    {
        CheckDimensions(width,height,depth); var mesh = new MeshGeometry3D();
        Point3D a = new(x,y,z), b = new(x+width,y,z), c = new(x+width,y+height,z), d = new(x,y+height,z), e = new(x,y,z+depth), f = new(x+width,y,z+depth), g = new(x+width,y+height,z+depth), h = new(x,y+height,z+depth);
        Quad(a,d,c,b); Quad(e,f,g,h); Quad(a,e,h,d); Quad(b,c,g,f); Quad(d,h,g,c); Quad(a,b,f,e); mesh.Freeze(); return mesh;
        void Quad(Point3D p,Point3D q,Point3D r,Point3D s) { Triangle(mesh,p,q,r); Triangle(mesh,p,r,s); }
    }

    internal static MeshGeometry3D Roof(double width, double depth, double y, double height)
    {
        CheckDimensions(width,depth,height); var m = new MeshGeometry3D();
        Point3D a=new(-width/2,y,-depth/2), b=new(width/2,y,-depth/2), c=new(width/2,y,depth/2), d=new(-width/2,y,depth/2), e=new(0,y+height,-depth/2), f=new(0,y+height,depth/2);
        Triangle(m,a,e,b); Triangle(m,d,c,f); Triangle(m,a,d,f); Triangle(m,a,f,e); Triangle(m,b,e,f); Triangle(m,b,f,c); Triangle(m,a,b,c); Triangle(m,a,c,d); m.Freeze(); return m;
    }

    internal static MeshGeometry3D Ring(double radius, double width, double y, int segments = 100)
    {
        CheckDimensions(radius,width); var mesh=new MeshGeometry3D();
        for(int i=0;i<segments;i++) { double a=i*2*Math.PI/segments,b=(i+1)*2*Math.PI/segments; Point3D p=new(Math.Cos(a)*radius,y,Math.Sin(a)*radius),q=new(Math.Cos(b)*radius,y,Math.Sin(b)*radius),r=new(Math.Cos(b)*(radius+width),y,Math.Sin(b)*(radius+width)),s=new(Math.Cos(a)*(radius+width),y,Math.Sin(a)*(radius+width)); Triangle(mesh,p,q,r);Triangle(mesh,p,r,s); }
        mesh.Freeze();return mesh;
    }

    internal static void Triangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    {
        var n=Vector3D.CrossProduct(b-a,c-a); if(n.LengthSquared<1e-20) throw new ArgumentException("Degenerate triangle."); n.Normalize();
        int start=mesh.Positions.Count; foreach(var p in new[]{a,b,c}) {mesh.Positions.Add(p);mesh.Normals.Add(n);mesh.TriangleIndices.Add(start++);}
    }
    private static Point3D Unit(Point3D p) {var v=(Vector3D)p;v.Normalize();return (Point3D)v;}
    private static Point3D Mid(Point3D a,Point3D b)=>Unit((Point3D)(((Vector3D)a+(Vector3D)b)/2));
    private static void CheckDimensions(params double[] values) {if(values.Any(v=>!double.IsFinite(v)||v<=0))throw new ArgumentOutOfRangeException(nameof(values));}
}
