using System.Windows.Media;
using System.Windows.Media.Media3D;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed record PlanetSceneMotion(Model3DGroup Model,Transform3D Surface,string Kind,
    string Species,Vector3D Home,Vector3D Destination,string? Habitat,double Radius,double Heading,double Phase);
internal sealed record PlanetScene(Model3DGroup Model, IReadOnlyDictionary<string, int> Features, IReadOnlyDictionary<GeometryModel3D,int> PlotHits,IReadOnlyList<PlanetSceneMotion> Motions)
{
    internal IReadOnlyList<(string Kind,Vector3D Normal)> Habitats {get;init;}=[];
}

internal static partial class PlanetView3DScene
{
    private const double Radius=2.7;
    internal static IReadOnlyList<(double Latitude,double Longitude)> Plots {get;}=CreatePlots();
    internal static PlanetScene Build(int level,string weather,bool night,int seed,IReadOnlyList<PlanetBuildingPlacement>? buildings=null,bool editMode=false,int selectedPlot=-1)
    {
        if(level is <0 or >5)throw new ArgumentOutOfRangeException(nameof(level));
        if(weather is not ("clear" or "cloudy" or "rain" or "aurora"))throw new ArgumentException("Unknown weather.",nameof(weather));
        if(buildings is not null&&(buildings.Count>32||buildings.Count(p=>PlanetLayoutCatalog.IsAnimal(p.Kind))>PlanetLayoutCatalog.AnimalLimitPerPlanet))throw new ArgumentException("Too many placed objects.",nameof(buildings));
        var world=new Model3DGroup(); var features=new Dictionary<string,int>(); var random=new Random(seed);var hitPlots=new Dictionary<GeometryModel3D,int>();var motions=new List<PlanetSceneMotion>();
        var habitats=new List<(string Kind,Vector3D Normal)>();
        void Count(string name) => features[name]=features.GetValueOrDefault(name)+1;
        world.Children.Add(PlanetSurface(level));
        Count("terrain");
        // Landmarks use local Y as their up direction, rotated into the actual surface normal.
        Place(43,-31,Mountain(.38,.58),"mountain"); Place(46,-17,Mountain(.28,.39),"mountain"); Place(34,-39,Mountain(.23,.32),"mountain");
        if(!NearPlot(-26,125))Place(-26,125,Mountain(.31,.48),"mountain");if(!NearPlot(6,-115))Place(6,-115,Mountain(.25,.35),"mountain");
        var sites=CachedNatureSites(seed);
        for(int i=0;i<Math.Min(sites.Count,level==0?74:90);i++)
        {
            var site=sites[i];double size=.68+random.NextDouble()*.30;
            if(level==0)
            {
                var piece=(i%7) switch {0=>Crater(.15+random.NextDouble()*.06),1=>CrystalCluster(size),2=>RockStrata(size),_=>RockCluster(size)};
                Place(site.Lat,site.Lon,piece,i%7==0?"crater":i%7==1?"crystal":"rocks",heading:random.Next(360));
            }
            else
            {
                Model3DGroup piece;string key;
                if(i%9<4){piece=SoftTree(size,i%3);key="tree";}
                else if(i%9==4){piece=ShrubPatch(size);key="shrub";}
                else if(i%9==5){piece=Wildflowers(size);key="flowers";}
                else if(i%9==6){piece=Mushrooms(size);key="mushrooms";}
                else if(i%9==7){piece=i%18==7?PlaceableModel("pond"):RockCluster(size*.8);key=i%18==7?"pond":"rocks";}
                else {piece=level>=2?PlaceableModel("meadow"):CrystalCluster(size*.7);key=level>=2?"meadow":"crystal";}
                Place(site.Lat,site.Lon,piece,key,heading:random.Next(360));
            }
        }
        for(int i=0;i<90;i++)
        {
            double lat=-70+random.NextDouble()*140,lon=-180+random.NextDouble()*360;double coast=Land(PlanetView3DGeometry.Direction(lat,lon))+(level==0?.18:level==1?.08:0);
            if(coast<-.025&&coast>-.27) Place(lat,lon,WaterRipples(.6+random.NextDouble()*.6),"wave",radius:2.704,heading:random.Next(25)-12);
            else if(coast is >-.017 and <.017&&!NearPlot(lat,lon))Place(lat,lon,ShorePebbles(),"shore",radius:2.704,heading:random.Next(360));
        }
        if(level>=2)
        {
            for(int i=90;i<Math.Min(sites.Count,102);i++)
            {
                var site=sites[i];string species=(i%6) switch {0=>"sheep",1=>"rabbit",2=>"fox",3=>"duck",4=>"deer",_=>"butterfly"};
                Place(site.Lat,site.Lon,PlaceableModel(species),species,heading:random.Next(360));
            }
        }
        if(level>=5)
        {
            var ring=new Model3DGroup();Add(ring,PlanetView3DGeometry.Ring(3.35,.065,0),"#C8D7B0",true,true);Add(ring,PlanetView3DGeometry.Ring(3.49,.018,0),"#6BAEA7",true,true); ring.Transform=new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),-24));world.Children.Add(ring);Count("ring");
        }
        foreach(var building in buildings??[])
        {
            if(building.PlotId is <0 or >=32||building.QuarterTurns is <0 or >3)throw new ArgumentException("Invalid building placement.",nameof(buildings));
            Model3DGroup obj=PlaceableModel(building.Kind);
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
            Place(39,-52,SoftCloud(),"cloud",radius:3.48);Place(18,50,SoftCloud(),"cloud",radius:3.42);Place(62,80,SoftCloud(),"cloud",radius:3.46);
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
        ResolveHabitats(motions,habitats);
        world.Freeze();return new(world,features,hitPlots,motions){Habitats=habitats.ToArray()};
        void Place(double lat,double lon,Model3DGroup obj,string type,double radius=2.69,double heading=0)
        {
            var surface=PlanetView3DGeometry.SurfaceTransform(lat,lon,radius,heading);
            bool animal=PlanetLayoutCatalog.IsAnimal(type);
            var normal=PlanetView3DGeometry.Direction(lat,lon);
            if(animal||type=="cloud")
            {
                obj.Freeze();motions.Add(new(obj,surface,animal?"creature":"cloud",animal?type:"",normal,normal,null,radius,heading,motions.Count*3.71));
                if(animal)Count("creature");
            }
            else {obj.Transform=surface;world.Children.Add(obj);}Count(type);
            string habitat=type switch {"flowers"=>"flower","shrub"=>"bush","mushrooms"=>"mushroom","habitat"=>"meadow",_=>type};
            if(habitat is "tree" or "flower" or "pond" or "meadow" or "mushroom" or "orchard" or "bush")habitats.Add((habitat,normal));
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
        return SculptedMountain(radius,height);
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
        var material=FinishedMaterial(color,emissive);var model=new GeometryModel3D(mesh,material);if(doubleSided)model.BackMaterial=material;group.Children.Add(model);
    }
}
