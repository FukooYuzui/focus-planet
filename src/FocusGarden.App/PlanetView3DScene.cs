using System.Windows.Media;
using System.Windows.Media.Media3D;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed record PlanetSceneMotion(Model3DGroup Model,Transform3D Surface,string Kind);
internal sealed record PlanetScene(Model3DGroup Model, IReadOnlyDictionary<string, int> Features, IReadOnlyDictionary<GeometryModel3D,int> PlotHits,IReadOnlyList<PlanetSceneMotion> Motions);

internal static class PlanetView3DScene
{
    private const double Radius=2.7;
    internal static IReadOnlyList<(double Latitude,double Longitude)> Plots {get;}=CreatePlots();
    internal static PlanetScene Build(int level,string weather,bool night,int seed,IReadOnlyList<PlanetBuildingPlacement>? buildings=null,bool editMode=false,int selectedPlot=-1)
    {
        if(level is <0 or >5)throw new ArgumentOutOfRangeException(nameof(level));
        if(weather is not ("clear" or "cloudy" or "rain" or "aurora"))throw new ArgumentException("Unknown weather.",nameof(weather));
        var world=new Model3DGroup(); var features=new Dictionary<string,int>(); var random=new Random(seed);var hitPlots=new Dictionary<GeometryModel3D,int>();var motions=new List<PlanetSceneMotion>();
        void Count(string name) => features[name]=features.GetValueOrDefault(name)+1;
        var terrain=new Dictionary<string,MeshGeometry3D>();
        string[] seas=["#389AA3","#42A4AB","#4CAFB1","#3DA0AA","#55B6B7"];
        string[] grasses=level switch {0=>["#928D7C","#A19A86","#ACA490","#999381","#878777"],1=>["#81976B","#98AA76","#A0AF7F","#AAA881","#8B9F75"],_=>["#73AD6D","#85BA78","#8AC17E","#9CC989","#75B47C"]};
        foreach(var(a,b,c) in PlanetView3DGeometry.SphereFaces(3))
        {
            var n=((Vector3D)a+(Vector3D)b+(Vector3D)c)/3;n.Normalize();
            double land=Land(n)+(level==0?.18:level==1?.08:0), pattern=Math.Sin(n.X*27+n.Z*13)*Math.Cos(n.Y*19);
            int tone=Math.Clamp((int)((pattern+1)*2.49),0,4);
            string color=land>.018?grasses[tone]:land>-.035?"#C8C79A":seas[tone];
            if(!terrain.TryGetValue(color,out var mesh))terrain.Add(color,mesh=new MeshGeometry3D());
            PlanetView3DGeometry.Triangle(mesh,(Point3D)((Vector3D)a*Radius),(Point3D)((Vector3D)b*Radius),(Point3D)((Vector3D)c*Radius));
        }
        foreach(var(color,mesh) in terrain) {mesh.Freeze();Add(world,mesh,color);}
        Count("terrain");
        // Landmarks use local Y as their up direction, rotated into the actual surface normal.
        Place(43,-31,Mountain(.42,.74),"mountain"); Place(46,-17,Mountain(.31,.48),"mountain"); Place(34,-39,Mountain(.24,.40),"mountain");
        Place(-26,125,Mountain(.39,.70),"mountain");Place(6,-115,Mountain(.32,.50),"mountain");
        for(int i=0;i<16;i++)
        {
            double lat=-65+random.NextDouble()*130,lon=-180+random.NextDouble()*360;
            if(Land(PlanetView3DGeometry.Direction(lat,lon))+(level==0?.18:level==1?.08:0)<-.07) {var foam=new Model3DGroup();Add(foam,PlanetView3DGeometry.Box(-.09,0,-.012,.18,.008,.025),"#A3DECE"); Place(lat,lon,foam,"wave",radius:2.705);}
        }
        if(level>=1)
        {
            (double Lat,double Lon)[] trees=[(15,-39),(6,-43),(-5,-28),(29,-45),(53,-1),(51,22),(24,5),(-10,-4),(0,18),(37,5),(36,75),(14,95),(-4,107),(-27,146),(-38,159),(8,-93),(-17,-78),(44,131),(11,152)];
            foreach(var(lat,lon) in trees) if(!NearPlot(lat,lon)) Place(lat,lon,Tree(.75+random.NextDouble()*.38,random.Next(3)==0),"tree",heading:random.Next(360));
            foreach(var(lat,lon) in new[]{(-2.0,-13.0),(17.0,13.0),(20.0,-24.0),(-12.0,-21.0),(30.0,96.0)})Place(lat,lon,Flowers(),"flowers");
        }
        if(level>=2)
        {
            Place(11,19,Sheep(),"creature",heading:-20);Place(4,31,Sheep(),"creature",heading:35);Place(-6,-22,Rabbit(),"creature",heading:-40);Place(23,90,Rabbit(),"creature",heading:55);
        }
        if(level>=5)
        {
            var ring=new Model3DGroup();Add(ring,PlanetView3DGeometry.Ring(3.35,.065,0),"#C8D7B0",true,true);Add(ring,PlanetView3DGeometry.Ring(3.49,.018,0),"#6BAEA7",true,true); ring.Transform=new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),-24));world.Children.Add(ring);Count("ring");
            Place(-18,1,Rabbit(),"creature",heading:35);Place(36,152,Sheep(),"creature");
        }
        foreach(var building in buildings??[])
        {
            if(building.PlotId is <0 or >=32||building.QuarterTurns is <0 or >3)throw new ArgumentException("Invalid building placement.",nameof(buildings));
            Model3DGroup obj=building.Kind switch {"tree"=>Tree(1,true),"flower"=>Flowers(),"house"=>House("#E4D5A8","#B9705C"),"windmill"=>Windmill(),"tower"=>Tower(),"observatory"=>Observatory(),_=>throw new ArgumentException("Unknown building kind.",nameof(buildings))};
            var plot=Plots[building.PlotId];Place(plot.Latitude,plot.Longitude,obj,building.Kind,heading:building.QuarterTurns*90);
            if(editMode)MapHits(obj,building.PlotId);
        }
        if(editMode)
        {
            for(int plotId=0;plotId<Plots.Count;plotId++)
            {
                var p=Plots[plotId];bool selected=plotId==selectedPlot;var marker=new Model3DGroup();
                Add(marker,PlanetView3DGeometry.Ring(selected?.23:.20,selected?.035:.018,.035,32),selected?"#FFE2A0":"#91D7BE",false,true);
                // A low disc supplies a generous clickable surface without a floating board.
                Add(marker,PlanetView3DGeometry.Frustum(.20,.20,.009,16,.006),selected?"#C3B77C":"#6F9E82");
                Place(p.Latitude,p.Longitude,marker,"plot",radius:2.705);MapHits(marker,plotId);
            }
        }
        if(weather is "cloudy" or "rain")
        {
            Place(39,-52,Cloud(),"cloud",radius:3.48);Place(18,50,Cloud(),"cloud",radius:3.42);Place(62,80,Cloud(),"cloud",radius:3.46);
            if(weather=="rain")foreach(var(lat,lon) in new[]{(39.0,-52.0),(18.0,50.0)}) {var drops=new Model3DGroup();for(int i=0;i<7;i++) {double x=(i%4-.5)*.13,z=(i/4-.5)*.16;Add(drops,PlanetView3DGeometry.Ellipsoid(x,-.12-(i%3)*.11,z,.012,.10,.012,0),"#91DCE7",true);}Place(lat,lon,drops,"rain",radius:3.36);}
        }
        if(weather=="aurora")
        {
            for(int band=0;band<3;band++)
            {
                var mesh=new MeshGeometry3D();
                for(int i=0;i<34;i++) {double a=(-153+i*3.8)*Math.PI/180,b=(-153+(i+1)*3.8)*Math.PI/180;double r=3.16+band*.11; Point3D p=new(Math.Cos(a)*r,1.8+Math.Sin(a*4)*.13,Math.Sin(a)*r),q=new(Math.Cos(b)*r,1.8+Math.Sin(b*4)*.13,Math.Sin(b)*r);var u=p+new Vector3D(0,.35+Math.Sin(a*5)*.13,0);var v=q+new Vector3D(0,.35+Math.Sin(b*5)*.13,0);PlanetView3DGeometry.Triangle(mesh,p,q,v);PlanetView3DGeometry.Triangle(mesh,p,v,u);}
                mesh.Freeze();Add(world,mesh,new[]{"#9061BAA7","#805B8DAB","#806A6298"}[band],false,true);Count("aurora");
            }
        }
        // Local window light is subtle during the day and brighter against the night lighting.
        world.Freeze();return new(world,features,hitPlots,motions);
        void Place(double lat,double lon,Model3DGroup obj,string type,double radius=2.69,double heading=0)
        {
            var surface=PlanetView3DGeometry.SurfaceTransform(lat,lon,radius,heading);
            if(type is "creature" or "cloud"){obj.Freeze();motions.Add(new(obj,surface,type));}
            else {obj.Transform=surface;world.Children.Add(obj);}Count(type);
        }
        void MapHits(Model3D model,int plotId){if(model is GeometryModel3D geometry)hitPlots[geometry]=plotId;else if(model is Model3DGroup group)foreach(var child in group.Children)MapHits(child,plotId);}
    }

    private static bool NearPlot(double lat,double lon)=>Plots.Any(p=>Vector3D.DotProduct(PlanetView3DGeometry.Direction(lat,lon),PlanetView3DGeometry.Direction(p.Latitude,p.Longitude))>.989);
    private static IReadOnlyList<(double Latitude,double Longitude)> CreatePlots()
    {
        // Fixed farthest-point sampling keeps 32 land plots evenly spaced and their IDs stable.
        var candidates=new List<(double Lat,double Lon,Vector3D Normal)>();
        for(int i=0;i<720;i++) {double lat=Math.Asin(1-2*(i+.5)/720)*180/Math.PI,lon=(i*137.50776405003785)%360-180;var n=PlanetView3DGeometry.Direction(lat,lon);if(Land(n)>.055&&Vector3D.DotProduct(n,PlanetView3DGeometry.Direction(43,-28))<.960)candidates.Add((lat,lon,n));}
        var chosen=new List<(double Lat,double Lon,Vector3D Normal)>{(9,-10,PlanetView3DGeometry.Direction(9,-10))};
        while(chosen.Count<32){var next=candidates.OrderBy(c=>chosen.Max(p=>Vector3D.DotProduct(c.Normal,p.Normal))).First();chosen.Add(next);candidates.Remove(next);}
        return chosen.Select(p=>(p.Lat,p.Lon)).ToArray();
    }

    private static double Land(Vector3D n)
    {
        double score=Math.Max(Vector3D.DotProduct(n,PlanetView3DGeometry.Direction(25,-17))-.70,Math.Max(Vector3D.DotProduct(n,PlanetView3DGeometry.Direction(17,112))-.80,Math.Max(Vector3D.DotProduct(n,PlanetView3DGeometry.Direction(-35,160))-.87,Vector3D.DotProduct(n,PlanetView3DGeometry.Direction(-2,-94))-.86)));
        return score+.038*Math.Sin(n.X*14+n.Y*4)*Math.Cos(n.Z*12-n.Y*8);
    }
    private static Model3DGroup Mountain(double radius,double height)
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Frustum(radius,0,height,5,-.035),"#8B9B8C");Add(g,PlanetView3DGeometry.Frustum(radius*.34,0,height*.34,5,height*.66-.035),"#E2E5D6");return g;
    }
    private static Model3DGroup Tree(double scale,bool round)
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Frustum(.038,.028,.23,5),"#876A4E");
        if(round) {Add(g,PlanetView3DGeometry.Ellipsoid(0,.36,0,.19,.25,.18,1),"#A8C777");Add(g,PlanetView3DGeometry.Ellipsoid(.08,.49,.03,.13,.15,.12,0),"#B4D284");}
        else {Add(g,PlanetView3DGeometry.Frustum(.22,0,.42,6,.15),"#3E8669");Add(g,PlanetView3DGeometry.Frustum(.17,0,.36,6,.34),"#5A9D75");}
        // Scale meshes in an inner group so the caller can independently orient the outer group.
        var outer=new Model3DGroup();g.Transform=new ScaleTransform3D(scale,scale,scale);outer.Children.Add(g);return outer;
    }
    private static Model3DGroup Flowers()
    {
        var g=new Model3DGroup();for(int i=0;i<3;i++){double x=(i-1)*.08,z=(i%2)*.07;Add(g,PlanetView3DGeometry.Box(x,.0,z,.012,.10,.012),"#47754C");Add(g,PlanetView3DGeometry.Ellipsoid(x,.10,z,.042,.028,.042,0),i==1?"#E7C679":"#D9A7AE");}return g;
    }
    private static Model3DGroup Sheep()
    {
        var g=new Model3DGroup();foreach(double x in new[]{-.10,.10})foreach(double z in new[]{-.06,.06})Add(g,PlanetView3DGeometry.Box(x-.018,0,z-.018,.035,.10,.035),"#786A57");Add(g,PlanetView3DGeometry.Ellipsoid(0,.17,0,.19,.13,.13,1),"#E9E6D5");Add(g,PlanetView3DGeometry.Ellipsoid(.18,.18,.01,.075,.09,.07,1),"#7C7667");Add(g,PlanetView3DGeometry.Ellipsoid(.2,.205,.066,.012,.014,.008,0),"#1E302E");Add(g,PlanetView3DGeometry.Ellipsoid(.15,.245,.04,.055,.025,.025,0),"#A49B87");return g;
    }
    private static Model3DGroup Rabbit()
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Ellipsoid(0,.11,0,.105,.12,.085,1),"#DCCAB9");Add(g,PlanetView3DGeometry.Ellipsoid(.085,.18,.015,.07,.075,.064,1),"#E7D8C4");foreach(double z in new[]{-.018,.045})Add(g,PlanetView3DGeometry.Ellipsoid(.08,.29,z,.023,.10,.022,0),"#DBBFC1");Add(g,PlanetView3DGeometry.Ellipsoid(.12,.19,.069,.009,.011,.009,0),"#243A32");Add(g,PlanetView3DGeometry.Ellipsoid(-.095,.12,0,.045,.045,.045,0),"#F2E7D5");return g;
    }
    private static Model3DGroup House(string wall,string roof)
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Box(-.23,0,-.18,.46,.33,.36),wall);Add(g,PlanetView3DGeometry.Roof(.57,.47,.33,.24),roof);Add(g,PlanetView3DGeometry.Box(-.045,0,.182,.09,.18,.015),"#80634C");foreach(double x in new[]{-.16,.1})Add(g,PlanetView3DGeometry.Box(x,.17,.183,.07,.075,.014),"#F8E1A3",true);Add(g,PlanetView3DGeometry.Box(.12,.39,-.08,.065,.17,.065),"#B59C83");return g;
    }
    private static Model3DGroup Windmill()
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Frustum(.18,.12,.66,8),"#E4D6AF");Add(g,PlanetView3DGeometry.Frustum(.23,0,.24,7,.64),"#AE7560");
        var blades=new Model3DGroup();Add(blades,PlanetView3DGeometry.Box(-.04,-.46,0,.08,.92,.035),"#EEE4C3");Add(blades,PlanetView3DGeometry.Box(-.46,-.04,0,.92,.08,.035),"#EEE4C3");var t=new Transform3DGroup();t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),35)));t.Children.Add(new TranslateTransform3D(0,.56,.17));blades.Transform=t;g.Children.Add(blades);Add(g,PlanetView3DGeometry.Ellipsoid(0,.56,.205,.065,.065,.04,1),"#7B7060");return g;
    }
    private static Model3DGroup Tower()
    {
        var g=new Model3DGroup();foreach(double x in new[]{-.11,.11})foreach(double z in new[]{-.11,.11})Add(g,PlanetView3DGeometry.Box(x-.02,0,z-.02,.04,.64,.04),"#887A61");Add(g,PlanetView3DGeometry.Box(-.21,.57,-.21,.42,.07,.42),"#CDBE95");Add(g,PlanetView3DGeometry.Frustum(.33,0,.22,4,.78),"#6B9690");foreach(double x in new[]{-.16,.14})Add(g,PlanetView3DGeometry.Box(x,.63,-.16,.025,.18,.32),"#A29575");return g;
    }
    private static Model3DGroup Observatory()
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Frustum(.29,.29,.26,12),"#DDD7BA");Add(g,PlanetView3DGeometry.Ellipsoid(0,.27,0,.31,.23,.31,2),"#799F9C");Add(g,PlanetView3DGeometry.Box(-.065,.18,.28,.13,.2,.015),"#F4DBA0",true);
        var telescope=new Model3DGroup();Add(telescope,PlanetView3DGeometry.Frustum(.055,.07,.35,8),"#D7E4D9");Add(telescope,PlanetView3DGeometry.Frustum(.071,.071,.018,8,.35),"#243D49");var t=new Transform3DGroup();t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(1,0,0),55)));t.Children.Add(new TranslateTransform3D(0,.44,.08));telescope.Transform=t;g.Children.Add(telescope);return g;
    }
    private static Model3DGroup Cloud()
    {
        var g=new Model3DGroup();Add(g,PlanetView3DGeometry.Ellipsoid(-.24,0,0,.25,.13,.17,1),"#D1DFDB");Add(g,PlanetView3DGeometry.Ellipsoid(0,.07,0,.31,.20,.22,1),"#E8ECE2");Add(g,PlanetView3DGeometry.Ellipsoid(.28,.02,0,.24,.14,.18,1),"#DCE7E0");return g;
    }
    private static void Add(Model3DGroup group,MeshGeometry3D mesh,string color,bool emissive=false,bool doubleSided=false)
    {
        var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));brush.Freeze();Material material=emissive?new EmissiveMaterial(brush):new DiffuseMaterial(brush);material.Freeze();var model=new GeometryModel3D(mesh,material);if(doubleSided)model.BackMaterial=material;group.Children.Add(model);
    }
}
