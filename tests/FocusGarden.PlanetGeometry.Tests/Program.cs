using System.Windows.Media.Media3D;
using FocusGarden.App;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var passed = 0;
        var failed = 0;
        void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }

        Test("icosphere detail 0 through 4 has exact bounded topology and outward winding", () =>
        {
            for (var detail = 0; detail <= 4; detail++)
            {
                var faces = PlanetView3DGeometry.SphereFaces(detail);
                Equal(20 * (int)Math.Pow(4, detail), faces.Count);
                foreach (var (a, b, c) in faces)
                {
                    Near(1, ((Vector3D)a).Length); Near(1, ((Vector3D)b).Length); Near(1, ((Vector3D)c).Length);
                    var cross = Vector3D.CrossProduct(b - a, c - a);
                    Assert(cross.Length > 1e-9);
                    Assert(Vector3D.DotProduct(cross, (Vector3D)a + (Vector3D)b + (Vector3D)c) > 0);
                }
            }
        });

        Test("subdivision limits remain 0 to 4 including the shared cache", () =>
        {
            foreach (var detail in new[] { -1, 5, int.MaxValue })
            {
                Throws<ArgumentOutOfRangeException>(() => PlanetView3DGeometry.SphereFaces(detail));
                Throws<ArgumentOutOfRangeException>(() => PlanetView3DGeometry.SmoothEllipsoid(0, 0, 0, 1, 1, 1, detail));
                Throws<ArgumentOutOfRangeException>(() => PlanetView3DGeometry.SmoothUnitSphere(detail));
            }
            Equal(5120 * 3, PlanetView3DGeometry.SmoothUnitSphere(4).TriangleIndices.Count);
        });

        Test("smooth ellipsoid is frozen and matches analytic dimensions and surface", () =>
        {
            var mesh = PlanetView3DGeometry.SmoothEllipsoid(3, -2, 5, 2, .7, 1.3);
            CheckMesh(mesh);
            Equal(320 * 3, mesh.TriangleIndices.Count);
            Near(1, mesh.Bounds.X); Near(-2.7, mesh.Bounds.Y); Near(3.7, mesh.Bounds.Z);
            Near(4, mesh.Bounds.SizeX); Near(1.4, mesh.Bounds.SizeY); Near(2.6, mesh.Bounds.SizeZ);
            foreach (var p in mesh.Positions)
                Near(1, Math.Pow((p.X - 3) / 2, 2) + Math.Pow((p.Y + 2) / .7, 2) + Math.Pow((p.Z - 5) / 1.3, 2));
        });

        Test("nonuniform ellipsoid normals use the analytic gradient rather than radial vectors", () =>
        {
            var mesh = PlanetView3DGeometry.SmoothEllipsoid(3, -2, 5, 2, .7, 1.3, 3);
            var differentFromRadial = false;
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                var offset = mesh.Positions[i] - new Point3D(3, -2, 5);
                var expected = new Vector3D(offset.X / 4, offset.Y / .49, offset.Z / 1.69);
                expected.Normalize();
                Near(0, (expected - mesh.Normals[i]).Length);
                offset.Normalize();
                differentFromRadial |= (offset - expected).Length > .1;
            }
            Assert(differentFromRadial);
        });

        Test("ellipsoid translation leaves normals unchanged and moves all coordinates equally", () =>
        {
            var origin = PlanetView3DGeometry.SmoothEllipsoid(0, 0, 0, .5, 1, 2);
            var shifted = PlanetView3DGeometry.SmoothEllipsoid(7, -4, 9, .5, 1, 2);
            for (var i = 0; i < origin.Positions.Count; i++)
            {
                Near(0, (shifted.Positions[i] - origin.Positions[i] - new Vector3D(7, -4, 9)).Length);
                Near(0, (shifted.Normals[i] - origin.Normals[i]).Length);
            }
        });

        Test("shared vertices have identical smooth normals across triangle boundaries", () =>
        {
            var mesh = PlanetView3DGeometry.SmoothEllipsoid(0, 0, 0, .5, 1, 2, 3);
            var normals = new Dictionary<Point3D, Vector3D>();
            var repeated = 0;
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                if (normals.TryGetValue(mesh.Positions[i], out var previous))
                {
                    Near(0, (previous - mesh.Normals[i]).Length);
                    repeated++;
                }
                else normals.Add(mesh.Positions[i], mesh.Normals[i]);
            }
            Assert(repeated > mesh.Positions.Count / 2);
            Assert((mesh.Normals[0] - mesh.Normals[1]).Length > 1e-3);
        });

        Test("unit sphere cache shares immutable meshes for repeated and parallel callers", () =>
        {
            var first = PlanetView3DGeometry.SmoothUnitSphere(2);
            Assert(ReferenceEquals(first, PlanetView3DGeometry.SmoothUnitSphere(2)));
            Assert(!ReferenceEquals(first, PlanetView3DGeometry.SmoothUnitSphere(1)));
            Parallel.For(0, 16, _ => Assert(ReferenceEquals(first, PlanetView3DGeometry.SmoothUnitSphere(2))));
            CheckMesh(first);
            Throws<InvalidOperationException>(() => first.Positions.Add(new Point3D()));
        });

        Test("smooth spherical triangles default to origin and support a supplied center", () =>
        {
            var mesh = new MeshGeometry3D();
            PlanetView3DGeometry.SmoothTriangle(mesh, new(2, 0, 0), new(0, 2, 0), new(0, 0, 2));
            Assert(!mesh.IsFrozen);
            Near(0, (mesh.Normals[0] - new Vector3D(1, 0, 0)).Length);
            Near(0, (mesh.Normals[1] - new Vector3D(0, 1, 0)).Length);
            Near(0, (mesh.Normals[2] - new Vector3D(0, 0, 1)).Length);
            var shifted = new MeshGeometry3D();
            var center = new Point3D(10, 20, 30);
            PlanetView3DGeometry.SmoothTriangle(shifted, center + new Vector3D(2, 0, 0),
                center + new Vector3D(0, 2, 0), center + new Vector3D(0, 0, 2), center);
            for (var i = 0; i < 3; i++) Near(0, (shifted.Normals[i] - mesh.Normals[i]).Length);
            mesh.Freeze(); shifted.Freeze(); CheckMesh(mesh); CheckMesh(shifted);
        });

        Test("smooth triangle corrects inward winding without changing outward normals", () =>
        {
            var mesh = new MeshGeometry3D();
            PlanetView3DGeometry.SmoothTriangle(mesh, new(1, 0, 0), new(0, 0, 1), new(0, 1, 0));
            mesh.Freeze(); CheckMesh(mesh);
            for (var i = 0; i < 3; i++) Near(0, ((Vector3D)mesh.Positions[i] - mesh.Normals[i]).Length);
        });

        Test("appending smooth triangles preserves previous data and produces valid offset indices", () =>
        {
            var mesh = new MeshGeometry3D();
            PlanetView3DGeometry.Triangle(mesh, new(0, 0, 0), new(1, 0, 0), new(0, 1, 0));
            var firstNormal = mesh.Normals[0];
            PlanetView3DGeometry.SmoothTriangle(mesh, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
            Equal(6, mesh.Positions.Count); Equal(6, mesh.Normals.Count); Equal(6, mesh.TriangleIndices.Count);
            Equal(firstNormal, mesh.Normals[0]); Equal(new Point3D(0, 0, 0), mesh.Positions[0]);
            for (var i = 0; i < 6; i++) Equal(i, mesh.TriangleIndices[i]);
            mesh.Freeze(); CheckMesh(mesh);
        });

        Test("invalid smooth triangles fail before appending any partial mesh data", () =>
        {
            var mesh = new MeshGeometry3D();
            var a = new Point3D(1, 0, 0); var b = new Point3D(0, 1, 0); var c = new Point3D(0, 0, 1);
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothTriangle(mesh, a, a, c));
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothTriangle(mesh, new(), b, c));
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothTriangle(mesh, new(double.NaN, 0, 0), b, c));
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothTriangle(mesh, a, b, c, new(double.PositiveInfinity, 0, 0)));
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothTriangle(mesh, a, new(-1, 0, 0), b));
            Equal(0, mesh.Positions.Count); Equal(0, mesh.Normals.Count); Equal(0, mesh.TriangleIndices.Count);
            mesh.Positions.Add(a);
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothTriangle(mesh, a, b, c));
            Equal(1, mesh.Positions.Count); Equal(0, mesh.Normals.Count);
            var frozen = new MeshGeometry3D(); frozen.Freeze();
            Throws<InvalidOperationException>(() => PlanetView3DGeometry.SmoothTriangle(frozen, a, b, c));
        });

        Test("smooth ellipsoids reject invalid centers radii and overflowing coordinates", () =>
        {
            foreach (var radius in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
                Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothEllipsoid(0, 0, 0, radius, 1, 1));
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothEllipsoid(double.NaN, 0, 0, 1, 1, 1));
            Throws<ArgumentException>(() => PlanetView3DGeometry.SmoothEllipsoid(double.MaxValue, 0, 0, double.MaxValue, 1, 1));
        });

        Test("existing flat ellipsoid and box interfaces retain flat normals and dimensions", () =>
        {
            var ellipsoid = PlanetView3DGeometry.Ellipsoid(0, 0, 0, 1, 2, 3);
            var box = PlanetView3DGeometry.Box(-1, -2, -3, 2, 4, 6);
            CheckMesh(ellipsoid); CheckMesh(box);
            Equal(80 * 3, ellipsoid.TriangleIndices.Count); Equal(12 * 3, box.TriangleIndices.Count);
            Near(2, box.Bounds.SizeX); Near(4, box.Bounds.SizeY); Near(6, box.Bounds.SizeZ);
            foreach (var mesh in new[] { ellipsoid, box })
            for (var i = 0; i < mesh.Normals.Count; i += 3)
            {
                Equal(mesh.Normals[i], mesh.Normals[i + 1]); Equal(mesh.Normals[i], mesh.Normals[i + 2]);
            }
        });

        Test("torus has analytic surface normals dimensions and bounded topology", () =>
        {
            var mesh = PlanetView3DGeometry.Torus(2, 3, 4, 2, .25, 32, 12);
            CheckMesh(mesh);
            Equal(32 * 12 * 2 * 3, mesh.TriangleIndices.Count);
            Near(4.5, mesh.Bounds.SizeX); Near(.5, mesh.Bounds.SizeY); Near(4.5, mesh.Bounds.SizeZ);
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                var p = mesh.Positions[i] - new Point3D(2, 3, 4);
                var radial = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                Near(.25 * .25, Math.Pow(radial - 2, 2) + p.Y * p.Y);
                var expected = new Vector3D(p.X * (radial - 2) / radial, p.Y, p.Z * (radial - 2) / radial);
                expected.Normalize(); Near(0, (mesh.Normals[i] - expected).Length);
            }
        });

        Test("torus seams are numerically closed and have continuous normals", () =>
        {
            var mesh = PlanetView3DGeometry.Torus(0, 0, 0, 1, .2, 24, 8);
            var normals = new Dictionary<Point3D, Vector3D>();
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                if (normals.TryGetValue(mesh.Positions[i], out var n)) Near(0, (mesh.Normals[i] - n).Length);
                else normals.Add(mesh.Positions[i], mesh.Normals[i]);
            }
            Equal(24 * 8, normals.Count);
        });

        Test("torus limits reject unbounded subdivision and self-intersection", () =>
        {
            foreach (var count in new[] { 0, 2, 129, int.MaxValue })
                Throws<ArgumentException>(() => PlanetView3DGeometry.Torus(0, 0, 0, 1, .1, count));
            foreach (var count in new[] { 0, 2, 33, int.MaxValue })
                Throws<ArgumentException>(() => PlanetView3DGeometry.Torus(0, 0, 0, 1, .1, 32, count));
            Throws<ArgumentException>(() => PlanetView3DGeometry.Torus(0, 0, 0, 1, 1));
            Throws<ArgumentException>(() => PlanetView3DGeometry.Torus(0, 0, 0, 1, 2));
            Throws<ArgumentException>(() => PlanetView3DGeometry.Torus(0, 0, 0, 1, 0));
            Throws<ArgumentException>(() => PlanetView3DGeometry.Torus(0, double.NaN, 0, 1, .1));
            var maximum = PlanetView3DGeometry.Torus(0, 0, 0, 1, .1, 128, 32);
            Equal(8192 * 3, maximum.TriangleIndices.Count); CheckMesh(maximum);
            CheckMesh(PlanetView3DGeometry.Torus(0, 0, 0, 1, .1, 3, 3));
        });

        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void CheckMesh(MeshGeometry3D mesh)
    {
        Assert(mesh.IsFrozen);
        Equal(mesh.Positions.Count, mesh.Normals.Count);
        Equal(0, mesh.TriangleIndices.Count % 3);
        foreach (var p in mesh.Positions) Assert(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z));
        foreach (var n in mesh.Normals) Near(1, n.Length);
        foreach (var index in mesh.TriangleIndices) Assert(index >= 0 && index < mesh.Positions.Count);
        for (var i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            var a = mesh.TriangleIndices[i]; var b = mesh.TriangleIndices[i + 1]; var c = mesh.TriangleIndices[i + 2];
            var cross = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            Assert(cross.Length > 1e-9);
            Assert(Vector3D.DotProduct(cross, mesh.Normals[a]) > 0);
            Assert(Vector3D.DotProduct(cross, mesh.Normals[b]) > 0);
            Assert(Vector3D.DotProduct(cross, mesh.Normals[c]) > 0);
        }
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Near(double expected, double actual, double tolerance = 1e-9)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception($"Expected {expected} ± {tolerance}, got {actual}");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
