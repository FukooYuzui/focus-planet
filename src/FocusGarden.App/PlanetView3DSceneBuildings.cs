using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    // Shared frozen primitives keep repeated window frames, plants and ornaments inexpensive.
    private static readonly MeshGeometry3D DetailUnitBox = PlanetView3DGeometry.Box(-.5, 0, -.5, 1, 1, 1);
    private static readonly MeshGeometry3D DetailUnitRound = PlanetView3DGeometry.SmoothEllipsoid(0, 0, 0, 1, 1, 1, 1);
    private static readonly MeshGeometry3D DetailUnitDome = PlanetView3DGeometry.SmoothEllipsoid(0, 0, 0, 1, 1, 1, 2);

    private static Model3DGroup DetailedHouse(string wall, string roof)
    {
        var g = new Model3DGroup();
        DetailBox(g, -.255, -.015, -.205, .51, .06, .41, "#B7A88E");
        DetailBox(g, -.23, .035, -.18, .46, .335, .36, wall);
        DetailBox(g, -.245, .33, -.195, .49, .035, .39, "#C9AE85");
        Add(g, PlanetView3DGeometry.Roof(.59, .49, .365, .235), roof);
        // Eaves and roof courses give a small house a readable silhouette at planet scale.
        DetailBeam(g, new(-.30, .365, -.25), new(-.30, .365, .25), .027, "#884F44");
        DetailBeam(g, new(.30, .365, -.25), new(.30, .365, .25), .027, "#884F44");
        DetailBeam(g, new(0, .605, -.255), new(0, .605, .255), .026, "#D9A17A");
        for (int side = -1; side <= 1; side += 2)
        {
            for (int course = 1; course <= 3; course++)
            {
                double x = side * course * .072, y = .60 - course * .057;
                DetailBeam(g, new(x, y, -.247), new(x, y, .247), .009, "#C78D70");
            }
            DetailBeam(g, new(0, .606, .253), new(side * .296, .365, .253), .021, "#E8D3A6");
        }
        DetailBox(g, -.063, .036, .184, .126, .205, .021, "#A98461");
        DetailBox(g, -.049, .042, .207, .098, .178, .012, "#6F5747");
        DetailBox(g, -.006, .046, .222, .011, .17, .007, "#9E7A55");
        DetailRound(g, .030, .128, .229, .011, .011, .009, "#E4C078");
        DetailBox(g, -.09, .018, .205, .18, .027, .065, "#C7BA99");
        DetailBox(g, -.11, -.005, .254, .22, .025, .065, "#AEA78C");
        foreach (double x in new[] { -.151, .151 })
        {
            DetailedWindow(g, x, .24, .187, .082, .086);
            DetailFlowerBox(g, x, .158, .218, x < 0 ? "#D69EAE" : "#E3BC72");
        }
        // The side window remains visible when the house is rotated on its plot.
        var sideWindow = new Model3DGroup();
        DetailedWindow(sideWindow, 0, .235, 0, .105, .10);
        var sideTransform = new Transform3DGroup();
        sideTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 1, 0), 90)));
        sideTransform.Children.Add(new TranslateTransform3D(.233, 0, -.005));
        sideWindow.Transform = sideTransform; g.Children.Add(sideWindow);
        DetailBox(g, .13, .43, -.12, .075, .19, .078, "#C3AA8B");
        DetailBox(g, .12, .606, -.13, .095, .027, .098, "#E0D1B2");
        DetailBox(g, .14, .633, -.11, .055, .006, .058, "#725F53");
        DetailShrub(g, -.285, .045, -.105, .075, "#6B9570");
        DetailShrub(g, .285, .046, .11, .067, "#759F71");
        return g;
    }

    private static Model3DGroup DetailedWindmill()
    {
        var g = new Model3DGroup();
        Add(g, PlanetView3DGeometry.Frustum(.22, .21, .055, 12, -.015), "#B4AC91");
        Add(g, PlanetView3DGeometry.Frustum(.18, .125, .63, 16, .025), "#E6D6AC");
        Add(g, PlanetView3DGeometry.Frustum(.183, .177, .03, 16, .09), "#CBB88E");
        Add(g, PlanetView3DGeometry.Frustum(.163, .158, .025, 16, .31), "#CDBB95");
        Add(g, PlanetView3DGeometry.Frustum(.146, .140, .025, 16, .50), "#D2BE97");
        Add(g, PlanetView3DGeometry.Frustum(.235, 0, .225, 12, .645), "#688D84");
        Add(g, PlanetView3DGeometry.Frustum(.245, .235, .024, 12, .637), "#466D68");
        DetailBox(g, -.045, .025, .175, .09, .165, .018, "#866B50");
        DetailBox(g, -.008, .032, .195, .009, .15, .007, "#B39265");
        DetailBox(g, -.08, -.003, .17, .16, .033, .11, "#C9BFA1");
        DetailedWindow(g, 0, .365, .157, .072, .082);
        var blades = new Model3DGroup();
        for (int arm = 0; arm < 4; arm++)
        {
            var blade = new Model3DGroup();
            DetailBox(blade, -.012, .035, -.012, .024, .415, .024, "#8E7557");
            DetailBox(blade, -.045, .17, -.005, .09, .28, .016, "#E7D8AD");
            DetailBox(blade, -.049, .16, .013, .012, .30, .014, "#B4996C");
            DetailBox(blade, .037, .16, .013, .012, .30, .014, "#B4996C");
            for (int rung = 0; rung < 4; rung++) DetailBox(blade, -.047, .19 + rung * .072, .027, .094, .010, .009, "#C7AF81");
            blade.Transform = new RotateTransform3D(new AxisAngleRotation3D(new(0, 0, 1), arm * 90));
            blades.Children.Add(blade);
        }
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0, 0, 1), 35)));
        transform.Children.Add(new TranslateTransform3D(0, .555, .191));
        blades.Transform = transform; g.Children.Add(blades);
        DetailRound(g, 0, .555, .225, .055, .055, .037, "#8E7859");
        DetailRound(g, 0, .555, .258, .021, .021, .009, "#DDC58C");
        DetailShrub(g, -.225, .035, -.08, .064, "#779872");
        DetailFlowerBox(g, .202, .04, .03, "#E5C179");
        return g;
    }

    private static Model3DGroup DetailedTower()
    {
        var g = new Model3DGroup();
        foreach (double x in new[] { -.125, .125 }) foreach (double z in new[] { -.125, .125 })
        {
            DetailBox(g, x - .039, -.01, z - .039, .078, .045, .078, "#AAA98F");
            DetailBox(g, x - .023, .025, z - .023, .046, .62, .046, "#9E825D");
        }
        foreach (double z in new[] { -.125, .125 })
        {
            DetailBeam(g, new(-.125, .13, z), new(.125, .48, z), .024, "#B49B71");
            DetailBeam(g, new(.125, .13, z), new(-.125, .48, z), .024, "#B49B71");
        }
        DetailBox(g, -.225, .605, -.225, .45, .045, .45, "#C5AE80");
        for (int plank = 0; plank < 6; plank++) DetailBox(g, -.208 + plank * .078, .652, -.215, .009, .007, .43, "#9C845C");
        foreach (double x in new[] { -.19, .19 }) foreach (double z in new[] { -.19, .19 }) DetailBox(g, x - .014, .65, z - .014, .028, .22, .028, "#B9A47A");
        foreach (double z in new[] { -.20, .20 })
        {
            DetailBox(g, -.20, .78, z - .014, .40, .027, .028, "#D6C193");
            DetailBox(g, -.20, .70, z - .010, .40, .017, .020, "#A48E65");
        }
        foreach (double x in new[] { -.20, .20 }) DetailBox(g, x - .014, .78, -.20, .028, .027, .40, "#D6C193");
        Add(g, PlanetView3DGeometry.Frustum(.34, 0, .19, 4, .85), "#608F89");
        Add(g, PlanetView3DGeometry.Frustum(.355, .34, .025, 4, .825), "#426F6C");
        // A ladder and a small warm lantern make the lookout visibly inhabitable.
        foreach (double x in new[] { -.065, .065 }) DetailBeam(g, new(x, .01, .20), new(x, .61, .17), .018, "#C8AD7E");
        for (int rung = 0; rung < 7; rung++) DetailBeam(g, new(-.065, .07 + rung * .078, .195), new(.065, .07 + rung * .078, .195), .014, "#B09669");
        DetailBox(g, .125, .69, .225, .048, .069, .042, "#DABC7B");
        DetailBox(g, .118, .759, .218, .062, .013, .056, "#7A725C");
        DetailShrub(g, -.21, .035, -.20, .065, "#729373");
        return g;
    }

    private static Model3DGroup DetailedObservatory()
    {
        var g = new Model3DGroup();
        Add(g, PlanetView3DGeometry.Frustum(.325, .325, .045, 20, -.012), "#B9B6A0");
        Add(g, PlanetView3DGeometry.Frustum(.285, .285, .25, 20, .03), "#DDD7BB");
        Add(g, PlanetView3DGeometry.Frustum(.299, .299, .028, 20, .254), "#9FB5AB");
        DetailRound(g, 0, .28, 0, .302, .235, .302, "#7FA9AB", true);
        // Three slender ribs break the dome's silhouette without a large mesh budget.
        foreach (double azimuth in new[] { 0.0, 60, 120 })
        {
            var rib = new Model3DGroup();
            for (int part = 0; part < 8; part++)
            {
                double a = part * Math.PI / 8, b = (part + 1) * Math.PI / 8;
                DetailBeam(rib, new(Math.Cos(a) * .306, .28 + Math.Sin(a) * .238, 0), new(Math.Cos(b) * .306, .28 + Math.Sin(b) * .238, 0), .009, "#A7C3BF");
            }
            rib.Transform = new RotateTransform3D(new AxisAngleRotation3D(new(0, 1, 0), azimuth)); g.Children.Add(rib);
        }
        DetailBox(g, -.065, .034, .283, .13, .186, .017, "#738E88");
        DetailBox(g, -.047, .045, .303, .094, .157, .011, "#ADCAC2");
        DetailBox(g, -.10, .012, .292, .20, .027, .079, "#C8C3A8");
        DetailBox(g, -.12, -.008, .347, .24, .023, .069, "#A9AB99");
        foreach (double x in new[] { -.176, .176 }) DetailedWindow(g, x, .145, .234, .052, .077);
        var telescope = new Model3DGroup();
        Add(telescope, PlanetView3DGeometry.Frustum(.054, .072, .29, 12), "#D5E0CF");
        Add(telescope, PlanetView3DGeometry.Frustum(.076, .076, .035, 12, .28), "#8AABA6");
        Add(telescope, PlanetView3DGeometry.Frustum(.062, .062, .008, 12, .316), "#315467");
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(1, 0, 0), 52)));
        transform.Children.Add(new TranslateTransform3D(0, .438, .092)); telescope.Transform = transform; g.Children.Add(telescope);
        // A compact solar collector supplies another readable science-themed detail.
        DetailBox(g, -.405, .005, -.12, .035, .145, .035, "#A4B7A5");
        var panel = new Model3DGroup();
        DetailBox(panel, -.105, 0, -.085, .21, .018, .17, "#436C80");
        foreach (double x in new[] { -.105, 0, .105 }) DetailBox(panel, x - .004, .019, -.085, .008, .008, .17, "#9BB8BF");
        foreach (double z in new[] { -.085, 0, .085 }) DetailBox(panel, -.105, .019, z - .004, .21, .008, .008, "#9BB8BF");
        var panelTransform = new Transform3DGroup();
        panelTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(1, 0, 0), 18)));
        panelTransform.Children.Add(new TranslateTransform3D(-.39, .15, -.10)); panel.Transform = panelTransform; g.Children.Add(panel);
        DetailShrub(g, .315, .035, -.06, .062, "#719D83");
        return g;
    }

    private static void DetailedWindow(Model3DGroup group, double x, double y, double z, double width, double height)
    {
        DetailBox(group, x - width / 2 - .012, y - height / 2 - .012, z, width + .024, height + .024, .014, "#B4A17C");
        DetailBox(group, x - width / 2, y - height / 2, z + .015, width, height, .009, "#C9D9B4");
        DetailBox(group, x - .006, y - height / 2, z + .025, .012, height, .009, "#F0DDB1");
        DetailBox(group, x - width / 2, y - .005, z + .025, width, .010, .009, "#F0DDB1");
        DetailBox(group, x - width / 2 - .018, y - height / 2 - .018, z + .006, width + .036, .016, .039, "#CCB68B");
    }

    private static void DetailFlowerBox(Model3DGroup group, double x, double y, double z, string blossom)
    {
        DetailBox(group, x - .061, y, z, .122, .034, .056, "#AA8262");
        DetailBox(group, x - .067, y + .028, z - .004, .134, .014, .064, "#C8A67A");
        for (int i = 0; i < 3; i++)
        {
            double dx = x + (i - 1) * .039;
            DetailRound(group, dx, y + .055, z + .025, .031, .029, .029, "#729664");
            DetailRound(group, dx, y + .078 + (i % 2) * .012, z + .031, .019, .017, .019, blossom);
        }
    }

    private static void DetailShrub(Model3DGroup group, double x, double y, double z, double radius, string color)
    {
        DetailRound(group, x, y + radius * .45, z, radius, radius * .8, radius * .85, color);
        DetailRound(group, x - radius * .5, y + radius * .25, z + radius * .24, radius * .55, radius * .58, radius * .58, "#88A37A");
    }

    private static void DetailBox(Model3DGroup group, double x, double y, double z, double width, double height, double depth, string color)
    {
        var child = new Model3DGroup(); Add(child, DetailUnitBox, color);
        var transform = new Transform3DGroup(); transform.Children.Add(new ScaleTransform3D(width, height, depth)); transform.Children.Add(new TranslateTransform3D(x + width / 2, y, z + depth / 2)); transform.Freeze(); child.Transform = transform; group.Children.Add(child);
    }

    private static void DetailRound(Model3DGroup group, double x, double y, double z, double rx, double ry, double rz, string color, bool dome = false)
    {
        var child = new Model3DGroup(); Add(child, dome ? DetailUnitDome : DetailUnitRound, color);
        var transform = new Transform3DGroup(); transform.Children.Add(new ScaleTransform3D(rx, ry, rz)); transform.Children.Add(new TranslateTransform3D(x, y, z)); transform.Freeze(); child.Transform = transform; group.Children.Add(child);
    }

    private static void DetailBeam(Model3DGroup group, Point3D start, Point3D end, double thickness, string color)
    {
        var direction = end - start; double length = direction.Length; direction.Normalize(); var axis = Vector3D.CrossProduct(new(0, 1, 0), direction); if (axis.LengthSquared < 1e-12) axis = new(1, 0, 0);
        var child = new Model3DGroup(); Add(child, DetailUnitBox, color);
        var transform = new Transform3DGroup(); transform.Children.Add(new ScaleTransform3D(thickness, length, thickness)); transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, Vector3D.AngleBetween(new(0, 1, 0), direction)))); transform.Children.Add(new TranslateTransform3D((Vector3D)start)); transform.Freeze(); child.Transform = transform; group.Children.Add(child);
    }
}
