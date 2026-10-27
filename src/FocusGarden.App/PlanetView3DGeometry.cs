using System.Collections.Concurrent;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

/// <summary>Outward-facing flat and smooth primitives used by the native WPF planet.</summary>
internal static class PlanetView3DGeometry
{
    private static readonly ConcurrentDictionary<int, MeshGeometry3D> SmoothSphereCache = new();

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

    /// <summary>A frozen shared unit sphere. Reuse it with model transforms for small rounded scenery.</summary>
    internal static MeshGeometry3D SmoothUnitSphere(int detail = 2)
    {
        if (detail is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(detail));
        return SmoothSphereCache.GetOrAdd(detail, d => SmoothEllipsoid(0, 0, 0, 1, 1, 1, d));
    }

    /// <summary>Smooth ellipsoid with analytic gradient normals, including nonuniform axis scaling.</summary>
    internal static MeshGeometry3D SmoothEllipsoid(double x, double y, double z,
        double rx, double ry, double rz, int detail = 2)
    {
        CheckDimensions(rx, ry, rz);
        CheckFinite(new Point3D(x, y, z));
        var mesh = new MeshGeometry3D();
        foreach (var (a, b, c) in SphereFaces(detail))
            AppendSmoothTriangle(mesh, Stretch(a), Stretch(b), Stretch(c), Normal(a), Normal(b), Normal(c));
        mesh.Freeze();
        return mesh;

        Point3D Stretch(Point3D p) => new(x + p.X * rx, y + p.Y * ry, z + p.Z * rz);
        // For q=(rx*u.x, ry*u.y, rz*u.z), grad(sum(q_i/r_i)^2) is proportional to u_i/r_i.
        Vector3D Normal(Point3D unit) => NormalizeChecked(new Vector3D(unit.X / rx, unit.Y / ry, unit.Z / rz));
    }

    /// <summary>
    /// Append a spherical triangle with radial vertex normals. The sphere is centered at the origin
    /// unless center is supplied. Inward input winding is corrected; the caller freezes the combined mesh.
    /// </summary>
    internal static void SmoothTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c,
        Point3D? center = null)
    {
        var origin = center ?? new Point3D();
        CheckFinite(origin);
        AppendSmoothTriangle(mesh, a, b, c, NormalizeChecked(a - origin), NormalizeChecked(b - origin),
            NormalizeChecked(c - origin));
    }

    /// <summary>A smooth ring tube around the local Y axis. The major radius exceeds the tube radius.</summary>
    internal static MeshGeometry3D Torus(double x, double y, double z, double radius, double tubeRadius,
        int segments = 32, int tubeSegments = 10)
    {
        CheckFinite(new Point3D(x, y, z));
        CheckDimensions(radius, tubeRadius);
        if (tubeRadius >= radius) throw new ArgumentOutOfRangeException(nameof(tubeRadius));
        if (segments is < 3 or > 128) throw new ArgumentOutOfRangeException(nameof(segments));
        if (tubeSegments is < 3 or > 32) throw new ArgumentOutOfRangeException(nameof(tubeSegments));
        var mesh = new MeshGeometry3D();
        for (var i = 0; i < segments; i++)
        for (var j = 0; j < tubeSegments; j++)
        {
            var a = Vertex(i, j);
            var b = Vertex(i, j + 1);
            var c = Vertex(i + 1, j + 1);
            var d = Vertex(i + 1, j);
            AppendSmoothTriangle(mesh, a.Position, b.Position, c.Position, a.Normal, b.Normal, c.Normal);
            AppendSmoothTriangle(mesh, a.Position, c.Position, d.Position, a.Normal, c.Normal, d.Normal);
        }
        mesh.Freeze();
        return mesh;

        (Point3D Position, Vector3D Normal) Vertex(int i, int j)
        {
            // Modulo closes the seam with numerically identical positions and normals.
            var around = (i % segments) * 2 * Math.PI / segments;
            var tube = (j % tubeSegments) * 2 * Math.PI / tubeSegments;
            var ca = Math.Cos(around); var sa = Math.Sin(around);
            var ct = Math.Cos(tube); var st = Math.Sin(tube);
            return (new Point3D(x + (radius + tubeRadius * ct) * ca, y + tubeRadius * st,
                    z + (radius + tubeRadius * ct) * sa), new Vector3D(ct * ca, st, ct * sa));
        }
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

    private static void AppendSmoothTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c,
        Vector3D na, Vector3D nb, Vector3D nc)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.IsFrozen) throw new InvalidOperationException("Cannot append to a frozen mesh.");
        if (mesh.Positions.Count != mesh.Normals.Count)
            throw new ArgumentException("Existing positions and normals must have matching counts.", nameof(mesh));
        CheckFinite(a); CheckFinite(b); CheckFinite(c);
        var face = Vector3D.CrossProduct(b - a, c - a);
        if (!double.IsFinite(face.LengthSquared) || face.LengthSquared < 1e-20)
            throw new ArgumentException("Degenerate or non-finite triangle.");
        na = NormalizeChecked(na); nb = NormalizeChecked(nb); nc = NormalizeChecked(nc);
        var facing = Vector3D.DotProduct(face, na + nb + nc);
        if (!double.IsFinite(facing) || Math.Abs(facing) < 1e-15)
            throw new ArgumentException("Triangle has no consistent outward direction.");
        if (facing < 0) { (b, c) = (c, b); (nb, nc) = (nc, nb); }
        var start = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        mesh.Normals.Add(na); mesh.Normals.Add(nb); mesh.Normals.Add(nc);
        mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 2);
    }

    private static void CheckFinite(Point3D point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw new ArgumentOutOfRangeException(nameof(point), "Coordinates must be finite.");
    }

    private static Vector3D NormalizeChecked(Vector3D vector)
    {
        if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z) ||
            (vector.X == 0 && vector.Y == 0 && vector.Z == 0))
            throw new ArgumentException("A surface normal must be finite and nonzero.");
        vector.Normalize();
        if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z))
            throw new ArgumentException("A surface normal could not be normalized.");
        return vector;
    }
    private static Point3D Unit(Point3D p) {var v=(Vector3D)p;v.Normalize();return (Point3D)v;}
    private static Point3D Mid(Point3D a,Point3D b)=>Unit((Point3D)(((Vector3D)a+(Vector3D)b)/2));
    private static void CheckDimensions(params double[] values) {if(values.Any(v=>!double.IsFinite(v)||v<=0))throw new ArgumentOutOfRangeException(nameof(values));}
}
