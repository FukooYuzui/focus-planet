using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    // Small plot decorations use shared low-detail meshes. Their root groups stay mutable
    // until the caller assigns a surface transform and freezes the complete scene.
    private static Model3DGroup SceneryPond()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .012, 0, .215, .012, .165, "#B4AA84", 1);
        EcologyRound(g, 0, .024, 0, .194, .009, .145, "#67BAC8", 1);
        EcologyRound(g, -.028, .034, -.018, .117, .0015, .071, "#92D4D6");
        for (int i = 0; i < 6; i++)
        {
            double angle = .35 + i * Math.PI * 2 / 6;
            EcologyRound(g, Math.Cos(angle) * .190, .022, Math.Sin(angle) * .145,
                .023 + i % 2 * .003, .018 + i % 3 * .003, .021,
                i % 2 == 0 ? "#CFCBB0" : "#9EA99C");
        }

        EcologyRound(g, .065, .037, .033, .043, .004, .031, "#73A77C", 1);
        EcologyRound(g, -.025, .037, .070, .034, .004, .026, "#93B97F", 1);
        for (int petal = 0; petal < 4; petal++)
        {
            double angle = petal * Math.PI / 2;
            EcologyRound(g, .065 + Math.Cos(angle) * .013, .049, .033 + Math.Sin(angle) * .013,
                .016, .011, .012, petal % 2 == 0 ? "#F0C0C5" : "#F4D9D4");
        }
        EcologyRound(g, .065, .056, .033, .009, .006, .009, "#DAB66C");

        for (int i = 0; i < 4; i++)
        {
            double x = -.145 + i * .022, z = -.087 - i % 2 * .016, height = .13 + i % 3 * .025;
            DetailBeam(g, new(x, .016, z), new(x + .008, height, z), .006, "#709565");
            EcologyRound(g, x + .008, height + .015, z, .009, .029, .009, "#9A7956");
            EcologyLeaf(g, x, .02, z, .012, height * .75, 40 + i * 65, -17 + i * 8, "#91AD72");
        }
        var ripple = new Model3DGroup();
        Add(ripple, PlanetView3DGeometry.Ring(.041, .003, .036, 16), "#BCE7DF", false, true);
        var rippleTransform = new Transform3DGroup();
        rippleTransform.Children.Add(new ScaleTransform3D(1, 1, .63));
        rippleTransform.Children.Add(new TranslateTransform3D(-.070, 0, -.017));
        ripple.Transform = rippleTransform;
        g.Children.Add(ripple);
        return g;
    }

    private static Model3DGroup SceneryMeadow()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, 0, .205, .011, .165, "#91B982", 1);
        for (int tuft = 0; tuft < 7; tuft++)
        {
            double angle = tuft * 2.39996, x = Math.Cos(angle) * .130, z = Math.Sin(angle) * .105;
            for (int blade = 0; blade < 3; blade++)
                EcologyLeaf(g, x, .012, z, .014, .095 + (tuft + blade) % 3 * .025,
                    tuft * 47 + blade * 120, (blade - 1) * 24,
                    blade == 1 ? "#A5C773" : tuft % 2 == 0 ? "#6CA06D" : "#7FB376");
        }
        for (int flower = 0; flower < 2; flower++)
        {
            double x = flower == 0 ? -.045 : .055, z = flower == 0 ? .033 : -.047, height = .11 + flower * .026;
            DetailBeam(g, new(x, .012, z), new(x, height, z), .006, "#5F9468");
            for (int petal = 0; petal < 5; petal++)
            {
                double angle = petal * Math.PI * 2 / 5;
                EcologyRound(g, x + Math.Cos(angle) * .019, height, z + Math.Sin(angle) * .019,
                    .016, .008, .014, flower == 0 ? "#F2CFD1" : "#E8DCA5");
            }
            EcologyRound(g, x, height + .007, z, .010, .006, .010, "#D8AC60");
        }
        foreach (var p in new[] { new Point3D(-.10, .017, .12), new(.15, .014, .03), new(.06, .015, .13) })
            EcologyRound(g, p.X, p.Y, p.Z, .024, .014, .020, "#CAD0A7");
        return g;
    }

    private static Model3DGroup SceneryOrchard()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, 0, .205, .011, .155, "#93AD7B", 1);
        Add(g, PlanetView3DGeometry.Frustum(.030, .019, .27, 8), "#92704E");
        DetailBeam(g, new(0, .15, 0), new(-.077, .30, .006), .018, "#92704E");
        DetailBeam(g, new(0, .18, 0), new(.075, .33, -.018), .017, "#A18055");
        EcologyRound(g, -.075, .325, .006, .105, .125, .105, "#7BA96D", 1);
        EcologyRound(g, .070, .345, -.017, .108, .127, .104, "#8AB376", 1);
        EcologyRound(g, 0, .409, .005, .118, .112, .108, "#A3C483", 1);
        var fruit = new[]
        {
            new Point3D(-.117, .325, .080), new(-.039, .291, .091), new(.116, .351, .062),
            new(.033, .405, .104), new(-.070, .440, .060), new(.088, .420, -.070), new(-.125, .365, -.055)
        };
        for (int i = 0; i < fruit.Length; i++)
        {
            var p = fruit[i];
            EcologyRound(g, p.X, p.Y, p.Z, .026, .028, .025, i % 2 == 0 ? "#D9875F" : "#DDA159");
        }
        EcologyLeaf(g, -.117, .349, .080, .010, .027, 70, 42, "#587E51");
        EcologyLeaf(g, .116, .375, .062, .010, .027, -35, -42, "#638A51");
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
        var cluster = RockCluster(.82);
        cluster.Transform = new TranslateTransform3D(-.038, .010, -.020);
        g.Children.Add(cluster);
        for (int stone = 0; stone < 3; stone++)
        {
            EcologyRound(g, -.11 + stone * .068, .017, .108, .039, .012, .028,
                stone % 2 == 0 ? "#D4CEB5" : "#B0B49F");
            EcologyRound(g, .130, .029 + stone * .038, -.043, .050 - stone * .011, .025 - stone * .004,
                .038 - stone * .006, stone == 1 ? "#A4AC9A" : "#C5C5AD");
            EcologyRound(g, -.14 + stone * .026, .020, -.105, .019, .014, .017, "#E0D6BA");
            EcologyLeaf(g, .108, .012, .077, .011, .071 + stone * .014, stone * 110, (stone - 1) * 20, "#91AA76");
        }
        for (int moss = 0; moss < 4; moss++)
            EcologyRound(g, -.105 + moss * .044, .018, -.042 + moss % 2 * .040, .039, .009, .025,
                moss % 2 == 0 ? "#7E9C72" : "#98AD7C");
        return g;
    }

    private static Model3DGroup SceneryBush()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, .010, .205, .011, .145, "#8FA67A", 1);
        var shrubs = ShrubPatch(.9);
        shrubs.Transform = new TranslateTransform3D(0, .025, 0);
        g.Children.Add(shrubs);
        foreach (var p in new[] { new Point3D(-.085, .112, .075), new(-.023, .132, .058), new(.080, .102, .058) })
            EcologyRound(g, p.X, p.Y, p.Z, .013, .014, .013, "#C77C80");
        for (int leaf = 0; leaf < 4; leaf++)
            EcologyLeaf(g, -.12 + leaf * .075, .012, .095, .014, .070 + leaf % 2 * .020,
                leaf * 63, (leaf % 3 - 1) * 18, "#9DBA79");
        return g;
    }

    private static Model3DGroup SceneryMushrooms()
    {
        var g = new Model3DGroup();
        EcologyRound(g, 0, .011, .008, .202, .011, .155, "#89A17A", 1);
        for (int mushroom = 0; mushroom < 3; mushroom++)
        {
            double x = (mushroom - 1) * .092, z = mushroom == 1 ? -.035 : .047, height = mushroom == 1 ? .137 : .079;
            var stalk = new Model3DGroup();
            Add(stalk, PlanetView3DGeometry.Frustum(.017, .013, height, 6), "#E6D8B6");
            stalk.Transform = new TranslateTransform3D(x, .013, z);
            g.Children.Add(stalk);
            EcologyRound(g, x, height + .025, z, mushroom == 1 ? .072 : .052, .030, .049,
                mushroom == 1 ? "#CC8173" : "#D3AC7E", 1);
            for (int spot = 0; spot < 3; spot++)
            {
                double angle = spot * Math.PI * 2 / 3;
                EcologyRound(g, x + Math.Cos(angle) * .025, height + .050, z + Math.Sin(angle) * .020,
                    .009, .004, .008, "#F2E4C8");
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
            Wing(side, .069, .025, .071, .073, "#E6AD70");
            Wing(side, .055, -.060, .054, .050, "#D79296");
            EcologyRound(g, side * .013, .139, .086, .006, .007, .006, "#354C49");
            DetailBeam(g, new(side * .009, .147, .080), new(side * .028, .185, .100), .0045, "#725B54");
            DetailBeam(g, new(side * .028, .185, .100), new(side * .047, .196, .113), .004, "#725B54");
            EcologyRound(g, side * .047, .198, .113, .006, .007, .006, "#D6B998");
            DetailBeam(g, new(side * .010, .111, -.018), new(side * .034, .078, -.029), .0035, "#725B54");
        }
        EcologyRound(g, 0, .123, -.008, .016, .018, .067, "#745E55", 1);
        EcologyRound(g, 0, .133, .071, .023, .021, .022, "#AD8968");
        return g;

        void Wing(int side, double x, double z, double rx, double rz, string color)
        {
            var wing = new Model3DGroup();
            EcologyRound(wing, side * x, .120, z, rx, .009, rz, "#776A68", 1);
            EcologyRound(wing, side * x, .128, z, rx * .85, .006, rz * .85, color, 1);
            EcologyRound(wing, side * (x + .018), .135, z + .012, .018, .004, .015,
                z > 0 ? "#F2D9AC" : "#EACAC3");
            wing.Transform = new RotateTransform3D(new AxisAngleRotation3D(new(0, 0, 1), side * 12), new Point3D(0, .12, 0));
            g.Children.Add(wing);
        }
    }

    private static void EcologyRound(Model3DGroup group, double x, double y, double z,
        double rx, double ry, double rz, string color, int detail = 0)
    {
        var child = new Model3DGroup();
        Add(child, PlanetView3DGeometry.SmoothUnitSphere(detail), color);
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(rx, ry, rz));
        transform.Children.Add(new TranslateTransform3D(x, y, z));
        child.Transform = transform;
        group.Children.Add(child);
    }

    private static void EcologyLeaf(Model3DGroup group, double x, double y, double z,
        double width, double height, double heading, double lean, string color)
    {
        // Eight faces form a pointed, gently ridged leaf that remains visible from both sides.
        var mesh = new MeshGeometry3D();
        Point3D[] border = [new(0, 0, 0), new(-width, height * .38, 0),
            new(height * .16, height, 0), new(width, height * .48, 0)];
        var front = new Point3D(0, height * .43, width * .20);
        var back = new Point3D(0, height * .43, -width * .20);
        for (int i = 0; i < border.Length; i++)
        {
            var next = border[(i + 1) % border.Length];
            PlanetView3DGeometry.Triangle(mesh, border[i], front, next);
            PlanetView3DGeometry.Triangle(mesh, border[i], next, back);
        }
        mesh.Freeze();
        var leaf = new Model3DGroup();
        Add(leaf, mesh, color);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 0, 1), lean)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 1, 0), heading)));
        transform.Children.Add(new TranslateTransform3D(x, y, z));
        leaf.Transform = transform;
        group.Children.Add(leaf);
    }
}
