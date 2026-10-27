using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    // Broad silhouettes share the 320-face sphere. Small accents and curved leaves keep
    // scenery inexpensive; root groups remain mutable for the caller's placement transform.
    private static Model3DGroup SceneryPond()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .012, 0, .215, .012, .165, "#B4AA84", 2);
        EcologyDisk(g, 0, .025, 0, .195, .146, "#67BAC8", 64);
        EcologyDisk(g, -.035, .0255, -.027, .119, .075, "#85CDD0", 32);
        for (int i = 0; i < 5; i++)
        {
            double a = .35 + i * Math.PI * 2 / 5;
            EcologyRound(g, Math.Cos(a) * .188, .022, Math.Sin(a) * .144,
                .023 + i % 2 * .007, .018 + i % 3 * .003, .021 + i % 2 * .004,
                i % 2 == 0 ? "#CFCBB0" : "#9EA99C", i < 2 ? 2 : 1);
        }
        EcologyLilyPad(g, .065, .029, .033, .044, .032, 20, "#73A77C");
        EcologyLilyPad(g, -.025, .029, .070, .035, .027, 125, "#93B97F");
        for (int petal = 0; petal < 5; petal++)
            EcologyLeaf(g, .065, .032, .033, .011, .034, petal * 72, 67,
                petal % 2 == 0 ? "#F0C0C5" : "#F4D9D4");
        EcologyRound(g, .065, .041, .033, .009, .006, .009, "#DAB66C");
        for (int i = 0; i < 3; i++)
        {
            double x = -.145 + i * .025, z = -.087 - i % 2 * .016, height = .13 + i % 3 * .025;
            DetailBeam(g, new(x, .016, z), new(x + .008, height, z), .005, "#709565");
            EcologyRound(g, x + .008, height + .015, z, .008, .028, .008, "#9A7956");
            EcologyLeaf(g, x, .02, z, .010, height * .75, 40 + i * 65, -17 + i * 8, "#91AD72");
        }
        var ripple = new Model3DGroup();
        Add(ripple, PlanetView3DGeometry.Ring(.041, .002, .0265, 24), "#BCE7DF", false, true);
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(1, 1, .63));
        transform.Children.Add(new TranslateTransform3D(-.070, 0, -.017));
        ripple.Transform = transform; g.Children.Add(ripple); return g;
    }

    private static Model3DGroup SceneryMeadow()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, 0, .205, .011, .165, "#91B982", 2);
        for (int tuft = 0; tuft < 7; tuft++)
        {
            double angle = tuft * 2.39996, x = Math.Cos(angle) * .130, z = Math.Sin(angle) * .105;
            for (int blade = 0; blade < 3; blade++)
                EcologyLeaf(g, x, .012, z, .012, .095 + (tuft + blade) % 3 * .025,
                    tuft * 47 + blade * 120, (blade - 1) * 24,
                    blade == 1 ? "#A5C773" : tuft % 2 == 0 ? "#6CA06D" : "#7FB376");
        }
        for (int flower = 0; flower < 2; flower++)
        {
            double x = flower == 0 ? -.045 : .055, z = flower == 0 ? .033 : -.047, height = .11 + flower * .026;
            DetailBeam(g, new(x, .012, z), new(x, height, z), .005, "#5F9468");
            EcologyLeaf(g, x, .039, z, .010, .050, flower * 130, 55, "#7FA56C");
            for (int petal = 0; petal < 5; petal++)
            {
                double angle = petal * Math.PI * 2 / 5;
                EcologyRound(g, x + Math.Cos(angle) * .019, height, z + Math.Sin(angle) * .019,
                    .016, .008, .014, flower == 0 ? "#F2CFD1" : "#E8DCA5");
            }
            EcologyRound(g, x, height + .007, z, .010, .006, .010, "#D8AC60");
        }
        foreach (var p in new[] { new Point3D(-.10, .017, .12), new(.15, .014, .03), new(.06, .015, .13) })
            EcologyRound(g, p.X, p.Y, p.Z, .024, .014, .020, "#CAD0A7", 1);
        return g;
    }

    private static Model3DGroup SceneryOrchard()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, 0, .205, .011, .155, "#93AD7B", 1);
        Add(g, PlanetView3DGeometry.Frustum(.030, .019, .27, 8), "#92704E");
        DetailBeam(g, new(0, .15, 0), new(-.077, .30, .006), .018, "#92704E");
        DetailBeam(g, new(0, .18, 0), new(.075, .33, -.018), .017, "#A18055");
        DetailBeam(g, new(-.035, .225, .003), new(-.062, .29, .078), .011, "#A18055");
        EcologyRound(g, -.075, .325, .006, .105, .125, .105, "#7BA96D", 2);
        EcologyRound(g, .070, .345, -.017, .108, .127, .104, "#8AB376", 2);
        EcologyRound(g, 0, .409, .005, .118, .112, .108, "#A3C483", 2);
        EcologyRound(g, -.022, .294, .069, .080, .068, .063, "#709761", 1);
        var fruit = new[] { new Point3D(-.117, .325, .080), new(-.039, .291, .111), new(.116, .351, .062),
            new(.033, .405, .104), new(-.070, .440, .060), new(.088, .420, -.070), new(-.125, .365, -.055) };
        for (int i = 0; i < fruit.Length; i++)
        {
            var p = fruit[i];
            EcologyRound(g, p.X, p.Y, p.Z, .025, .027, .024, i % 2 == 0 ? "#D9875F" : "#DDA159", i < 2 ? 1 : 0);
            DetailBeam(g, p + new Vector3D(0, .022, 0), p + new Vector3D(.004, .039, 0), .0035, "#81694C");
            if (i < 4) EcologyLeaf(g, p.X + .002, p.Y + .028, p.Z, .007, .027, 65 + i * 87, 53, "#587E51");
        }
        for (int root = 0; root < 3; root++)
        {
            double angle = root * Math.PI * 2 / 3;
            DetailBeam(g, new(0, .045, 0), new(Math.Cos(angle) * .072, .012, Math.Sin(angle) * .064), .016, "#9B815A");
            EcologyLeaf(g, .13, .012, .063, .012, .070 + root * .012, root * 120, (root - 1) * 23, "#89AD70");
        }
        return g;
    }

    private static Model3DGroup SceneryRockGarden()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, 0, .214, .011, .168, "#B9BA9E", 1);
        EcologyRound(g, -.087, .054, -.012, .084, .063, .063, "#9C9A87", 2);
        EcologyRound(g, -.001, .084, -.040, .075, .104, .075, "#B9B39F", 2);
        EcologyRound(g, .052, .032, .025, .046, .038, .038, "#C7BEA8", 1);
        for (int stone = 0; stone < 3; stone++)
        {
            EcologyRound(g, -.11 + stone * .068, .017, .108, .039, .012, .028, stone % 2 == 0 ? "#D4CEB5" : "#B0B49F");
            EcologyRound(g, .130, .029 + stone * .038, -.043, .050 - stone * .011, .025 - stone * .004,
                .038 - stone * .006, stone == 1 ? "#A4AC9A" : "#C5C5AD", 1);
            EcologyRound(g, -.14 + stone * .026, .020, -.105, .019, .014, .017, "#E0D6BA");
            EcologyLeaf(g, .108, .012, .077, .011, .071 + stone * .014, stone * 110, (stone - 1) * 20, "#91AA76");
        }
        for (int moss = 0; moss < 4; moss++)
            EcologyRound(g, -.105 + moss * .044, .018, -.042 + moss % 2 * .040, .039, .009, .025, moss % 2 == 0 ? "#7E9C72" : "#98AD7C");
        return g;
    }

    private static Model3DGroup SceneryBush()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, .010, .205, .011, .145, "#8FA67A", 1);
        DetailBeam(g, new(-.063, .015, .010), new(-.063, .105, .010), .014, "#8B7856");
        DetailBeam(g, new(.061, .015, -.016), new(.061, .104, -.016), .012, "#8B7856");
        EcologyRound(g, -.075, .104, .014, .098, .086, .087, "#6B9C79", 2);
        EcologyRound(g, .049, .112, -.021, .094, .091, .082, "#8EB084", 2);
        EcologyRound(g, .125, .069, .050, .064, .061, .059, "#A4BA88", 2);
        EcologyRound(g, -.020, .144, .027, .059, .060, .053, "#90B58A", 1);
        foreach (var p in new[] { new Point3D(-.117, .146, .066), new(-.074, .160, .054), new(-.023, .189, .054), new(.076, .162, .037), new(.144, .101, .092) })
            EcologyRound(g, p.X, p.Y, p.Z, .011, .012, .011, "#C77C80");
        for (int leaf = 0; leaf < 4; leaf++)
            EcologyLeaf(g, -.12 + leaf * .075, .012, .095, .012, .070 + leaf % 2 * .020,
                leaf * 63, (leaf % 3 - 1) * 18, "#9DBA79");
        EcologyLeaf(g, -.105, .157, .058, .008, .032, 20, 69, "#ADCA8B");
        return g;
    }

    private static Model3DGroup SceneryMushrooms()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, .008, .202, .011, .155, "#89A17A", 1);
        for (int mushroom = 0; mushroom < 3; mushroom++)
        {
            double x = (mushroom - 1) * .092, z = mushroom == 1 ? -.035 : .047, height = mushroom == 1 ? .137 : .079;
            double radius = mushroom == 1 ? .072 : .052;
            var stalk = new Model3DGroup();
            Add(stalk, PlanetView3DGeometry.Frustum(.017, .013, height, 6), "#E6D8B6");
            stalk.Transform = new TranslateTransform3D(x, .013, z); g.Children.Add(stalk);
            EcologyMushroomCap(g, x, height + .025, z, radius, .049,
                mushroom == 1 ? "#CC8173" : "#D3AC7E", mushroom == 1 ? 24 : 16);
            for (int spot = 0; spot < 3; spot++)
            {
                double a = spot * Math.PI * 2 / 3;
                EcologyRound(g, x + Math.Cos(a) * .025, height + .053, z + Math.Sin(a) * .020, .009, .0035, .008, "#F2E4C8");
                double b = .6 + a;
                DetailBeam(g, new(x + Math.Cos(b) * .018, height + .018, z + Math.Sin(b) * .014),
                    new(x + Math.Cos(b) * radius * .82, height + .018, z + Math.Sin(b) * .039), .0027, "#BDAC90");
            }
        }
        DetailBeam(g, new(-.115, .027, -.100), new(.072, .031, -.104), .026, "#A38B63");
        DetailBeam(g, new(-.095, .042, -.103), new(.064, .046, -.106), .005, "#C6AB78");
        for (int leaf = 0; leaf < 5; leaf++)
            EcologyLeaf(g, -.14 + leaf * .070, .012, .098, .011, .052 + leaf % 3 * .013,
                leaf * 71, (leaf % 3 - 1) * 24, leaf % 2 == 0 ? "#A1BC7B" : "#709967");
        return g;
    }

    private static Model3DGroup Butterfly()
    {
        var g = new Model3DGroup();
        for (int side = -1; side <= 1; side += 2)
        {
            Wing(side, .069, .025, .073, .073, "#E6AD70", true);
            Wing(side, .055, -.060, .054, .050, "#D79296", false);
            EcologyRound(g, side * .013, .139, .086, .006, .007, .006, "#354C49");
            DetailBeam(g, new(side * .009, .147, .080), new(side * .028, .185, .100), .0045, "#725B54");
            DetailBeam(g, new(side * .028, .185, .100), new(side * .047, .196, .113), .004, "#725B54");
            EcologyRound(g, side * .047, .198, .113, .006, .007, .006, "#D6B998");
            DetailBeam(g, new(side * .010, .111, -.018), new(side * .034, .078, -.029), .0035, "#725B54");
        }
        EcologyRound(g, 0, .123, -.008, .016, .018, .067, "#745E55", 2);
        EcologyRound(g, 0, .133, .071, .023, .021, .022, "#AD8968", 1);
        return g;

        void Wing(int side, double x, double z, double rx, double rz, string color, bool forewing)
        {
            var wing = new Model3DGroup();
            EcologyWing(wing, side * x, .120, z, rx, rz, side, "#776A68", false);
            EcologyWing(wing, side * x, .120, z, rx, rz, side, color, true);
            EcologyRound(wing, side * (x + .018), .133, z + .012, .017, .0035, .014, forewing ? "#F2D9AC" : "#EACAC3");
            if (forewing)
            {
                EcologyRound(wing, side * (x + .035), .132, z - .018, .008, .003, .009, "#F7E4C5");
                DetailBeam(wing, new(side * (x - .033), .131, z - .024), new(side * (x + .006), .132, z + .025), .002, "#C08D62");
            }
            wing.Transform = new RotateTransform3D(new AxisAngleRotation3D(new(0, 0, 1), side * 12), new Point3D(0, .12, 0));
            g.Children.Add(wing);
        }
    }

    private static void EcologyRound(Model3DGroup group, double x, double y, double z,
        double rx, double ry, double rz, string color, int detail = 0)
    {
        var child = new Model3DGroup(); Add(child, PlanetView3DGeometry.SmoothUnitSphere(detail), color);
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(rx, ry, rz));
        transform.Children.Add(new TranslateTransform3D(x, y, z));
        child.Transform = transform; group.Children.Add(child);
    }

    private static void EcologyDisk(Model3DGroup group, double x, double y, double z,
        double rx, double rz, string color, int segments)
    {
        var mesh = new MeshGeometry3D(); mesh.Positions.Add(new(x, y, z));
        for (int i = 0; i < segments; i++)
        {
            double a = i * 2 * Math.PI / segments;
            mesh.Positions.Add(new(x + rx * Math.Cos(a), y, z + rz * Math.Sin(a)));
        }
        for (int i = 0; i < segments; i++) EcologyFace(mesh, 0, 1 + (i + 1) % segments, 1 + i);
        EcologyFinish(mesh); Add(group, mesh, color, false, true);
    }

    private static void EcologyLilyPad(Model3DGroup group, double x, double y, double z,
        double rx, double rz, double heading, string color)
    {
        // An open wedge gives the pad its recognisable notch without another solid.
        const int segments = 28;
        var mesh = new MeshGeometry3D(); mesh.Positions.Add(new(x, y + .0015, z));
        for (int i = 0; i <= segments; i++)
        {
            double a = heading * Math.PI / 180 + .24 + i * (Math.PI * 2 - .48) / segments;
            mesh.Positions.Add(new(x + rx * Math.Cos(a), y, z + rz * Math.Sin(a)));
        }
        for (int i = 0; i < segments; i++) EcologyFace(mesh, 0, i + 2, i + 1);
        EcologyFinish(mesh); Add(group, mesh, color, false, true);
    }

    private static void EcologyLeaf(Model3DGroup group, double x, double y, double z,
        double width, double height, double heading, double lean, string color)
    {
        // Three diamond sections taper to a curved tip. Shared normals soften the ridge;
        // the closed 24-face leaf stays solid from both sides and grazing camera angles.
        var mesh = new MeshGeometry3D(); mesh.Positions.Add(new(0, 0, 0));
        for (int ring = 1; ring <= 3; ring++)
        {
            double t = ring / 4.0, w = width * Math.Sin(Math.PI * t), bx = height * .10 * t * t, bz = height * .14 * t * t;
            mesh.Positions.Add(new(bx - w, height * t, bz));
            mesh.Positions.Add(new(bx, height * t, bz + w * .24));
            mesh.Positions.Add(new(bx + w, height * t, bz));
            mesh.Positions.Add(new(bx, height * t, bz - w * .24));
        }
        mesh.Positions.Add(new(height * .10, height, height * .14));
        for (int side = 0; side < 4; side++)
        {
            int next = (side + 1) % 4;
            EcologyFace(mesh, 0, 1 + next, 1 + side);
            for (int ring = 0; ring < 2; ring++)
            {
                int a = 1 + ring * 4 + side, b = 1 + ring * 4 + next;
                EcologyFace(mesh, a, b, a + 4); EcologyFace(mesh, b, b + 4, a + 4);
            }
            EcologyFace(mesh, 9 + side, 9 + next, 13);
        }
        // Analytic normals avoid triangulation bias along a very thin leaf's two edges.
        var leafNormals = new Vector3D[14];
        leafNormals[0] = new(-height * .10 / 16, -height / 4, -height * .14 / 16);
        for (int ring = 1; ring <= 3; ring++)
        {
            double t = ring / 4.0, dx = height * .20 * t, dz = height * .28 * t;
            double dw = width * Math.PI * Math.Cos(Math.PI * t);
            int first = 1 + (ring - 1) * 4;
            leafNormals[first] = new(-height, dx - dw, 0);
            leafNormals[first + 1] = new(0, -(dz + dw * .24), height);
            leafNormals[first + 2] = new(height, -(dx + dw), 0);
            leafNormals[first + 3] = new(0, dz - dw * .24, -height);
        }
        leafNormals[13] = new(height * .10 * 7 / 16, height / 4, height * .14 * 7 / 16);
        EcologyFinish(mesh, leafNormals);
        var leaf = new Model3DGroup(); Add(leaf, mesh, color);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 0, 1), lean)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 1, 0), heading)));
        transform.Children.Add(new TranslateTransform3D(x, y, z));
        leaf.Transform = transform; group.Children.Add(leaf);
    }

    private static void EcologyMushroomCap(Model3DGroup group, double x, double y, double z,
        double rx, double rz, string color, int segments)
    {
        // A continuous revolved dome rolls over its rim before meeting a cream underside.
        (double Radius, double Y)[] profile = [(.35, .030), (.68, .022), (.92, .012), (1, .001), (.98, -.004), (.87, -.007)];
        var mesh = new MeshGeometry3D(); mesh.Positions.Add(new(x, y + .033, z));
        foreach (var ring in profile)
            for (int i = 0; i < segments; i++)
            {
                double a = i * 2 * Math.PI / segments;
                mesh.Positions.Add(new(x + rx * ring.Radius * Math.Cos(a), y + ring.Y, z + rz * ring.Radius * Math.Sin(a)));
            }
        int bottom = mesh.Positions.Count; mesh.Positions.Add(new(x, y - .007, z));
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments; EcologyFace(mesh, 0, 1 + next, 1 + i);
            for (int ring = 0; ring < profile.Length - 1; ring++)
            {
                int a = 1 + ring * segments + i, b = 1 + ring * segments + next;
                EcologyFace(mesh, a, b, a + segments); EcologyFace(mesh, b, b + segments, a + segments);
            }
            EcologyFace(mesh, 1 + (profile.Length - 1) * segments + i, 1 + (profile.Length - 1) * segments + next, bottom);
        }
        EcologyFinish(mesh); Add(group, mesh, color);
        EcologyDisk(group, x, y - .0075, z, rx * .86, rz * .86, "#D8C6A6", segments);
    }

    private static void EcologyWing(Model3DGroup group, double x, double y, double z,
        double rx, double rz, int side, string color, bool inset)
    {
        const int segments = 24;
        var mesh = new MeshGeometry3D();
        double innerRadius = inset ? .67 : .74, outerRadius = inset ? .85 : 1;
        mesh.Positions.Add(new(x, y + (inset ? .011 : .008), z));
        Ring(innerRadius, inset ? .009 : .0065); Ring(outerRadius, inset ? .005 : 0);
        int bottom = mesh.Positions.Count;
        if (!inset) { mesh.Positions.Add(new(x, y - .006, z)); Ring(innerRadius, -.005); }
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments, inner = 1 + i, outer = 1 + segments + i;
            int innerNext = 1 + next, outerNext = 1 + segments + next;
            EcologyFace(mesh, 0, innerNext, inner);
            EcologyFace(mesh, inner, innerNext, outer); EcologyFace(mesh, innerNext, outerNext, outer);
            if (!inset)
            {
                int back = bottom + 1 + i, backNext = bottom + 1 + next;
                EcologyFace(mesh, bottom, back, backNext);
                EcologyFace(mesh, back, outer, backNext); EcologyFace(mesh, backNext, outer, outerNext);
            }
        }
        if (side < 0)
            for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                (mesh.TriangleIndices[i + 1], mesh.TriangleIndices[i + 2]) = (mesh.TriangleIndices[i + 2], mesh.TriangleIndices[i + 1]);
        EcologyFinish(mesh); Add(group, mesh, color);

        void Ring(double radius, double elevation)
        {
            for (int i = 0; i < segments; i++)
            {
                double a = i * Math.PI * 2 / segments, edge = 1 + .035 * Math.Cos(a * 6);
                mesh.Positions.Add(new(x + side * rx * radius * Math.Cos(a) * edge,
                    y + elevation, z + rz * radius * (Math.Sin(a) + .10 * Math.Cos(a)) * edge));
            }
        }
    }

    private static void EcologyFace(MeshGeometry3D mesh, int a, int b, int c)
    { mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); }

    private static void EcologyFinish(MeshGeometry3D mesh, Vector3D[]? explicitNormals = null)
    {
        // Area-weighted vertex normals join adjacent sections without hard triangle seams.
        var normals = explicitNormals ?? new Vector3D[mesh.Positions.Count];
        for (int i = 0; explicitNormals is null && i < mesh.TriangleIndices.Count; i += 3)
        {
            int a = mesh.TriangleIndices[i], b = mesh.TriangleIndices[i + 1], c = mesh.TriangleIndices[i + 2];
            var normal = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        foreach (var value in normals) { var normal = value; normal.Normalize(); mesh.Normals.Add(normal); }
        mesh.Freeze();
    }
}
