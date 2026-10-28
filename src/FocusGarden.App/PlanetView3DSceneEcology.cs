using System.Windows.Media.Media3D;
using FocusGarden.Core;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    private static readonly Dictionary<string,Model3DGroup> PlaceableModels=[];
    private static readonly Dictionary<int,IReadOnlyList<(double Lat,double Lon)>> SiteCache=[];

    private static IReadOnlyList<(double Lat,double Lon)> CachedNatureSites(int seed)
    {
        lock(SiteCache)
        {
            if(SiteCache.TryGetValue(seed,out var sites))return sites;
            sites=NatureSites(new Random(seed),125);
            if(SiteCache.Count>=4)SiteCache.Remove(SiteCache.Keys.First());
            SiteCache.Add(seed,sites);return sites;
        }
    }

    // Cache the expensive geometry once, but keep wrapper identity unique per placement:
    // hit testing maps GeometryModel3D instances to persistent plot IDs.
    private static Model3DGroup PlaceableModel(string kind)
    {
        lock(PlaceableModels)
        {
            if(!PlaceableModels.TryGetValue(kind,out var prototype))
            {
                prototype=kind switch
                {
                    "tree"=>SoftTree(1,1),"flower"=>Wildflowers(1.2),
                    "house"=>DetailedHouse("#F0DEB8","#BD7963"),"windmill"=>DetailedWindmill(),
                    "tower"=>DetailedTower(),"observatory"=>DetailedObservatory(),
                    "rabbit"=>SoftRabbit(),"sheep"=>SoftSheep(),"fox"=>Fox(),"duck"=>Duck(),"deer"=>Deer(),"butterfly"=>Butterfly(),
                    "pond"=>SceneryPond(),"meadow"=>SceneryMeadow(),"mushroom"=>SceneryMushrooms(),
                    "orchard"=>SceneryOrchard(),"rockgarden"=>SceneryRockGarden(),"bush"=>SceneryBush(),
                    _=>throw new ArgumentException("Unknown building kind.",nameof(kind))
                };
                prototype.Freeze();PlaceableModels.Add(kind,prototype);
            }
            return (Model3DGroup)Wrap(prototype);
        }
        static Model3D Wrap(Model3D source)
        {
            if(source is GeometryModel3D g)return new GeometryModel3D(g.Geometry,g.Material){BackMaterial=g.BackMaterial,Transform=g.Transform};
            var result=new Model3DGroup {Transform=source.Transform};
            foreach(var child in ((Model3DGroup)source).Children)result.Children.Add(Wrap(child));
            return result;
        }
    }

    private static bool Likes(string species,string habitat)=>species switch
    {
        "rabbit"=>habitat is "meadow" or "flower" or "bush",
        "sheep"=>habitat is "meadow" or "orchard" or "tree",
        "fox"=>habitat is "bush" or "tree" or "mushroom",
        "deer"=>habitat is "pond" or "tree" or "orchard",
        "duck"=>habitat=="pond",
        "butterfly"=>habitat is "flower" or "orchard" or "mushroom",
        _=>false
    };

    internal static void ResolveHabitats(List<PlanetSceneMotion> motions,List<(string Kind,Vector3D Normal)> habitats)
    {
        for(int i=0;i<motions.Count;i++)
        {
            var motion=motions[i];if(motion.Kind!="creature")continue;
            // A moved or removed habitat must not leave an animal visiting its old location.
            motion=motion with {Destination=motion.Home,Habitat=null};motions[i]=motion;
            double closest=Math.Cos(.68);(string Kind,Vector3D Normal)? target=null;
            foreach(var habitat in habitats)
            {
                double dot=Vector3D.DotProduct(motion.Home,habitat.Normal);
                if(!Likes(motion.Species,habitat.Kind)||dot<=closest)continue;
                // No pathfinding engine: only accept a short, dry, sampled arc.
                bool dry=true;
                if(motion.Species!="butterfly")
                    for(int step=1;step<8;step++)
                    {
                        var n=motion.Home*(1-step/8.0)+habitat.Normal*(step/8.0);n.Normalize();
                        if(Land(n)<-.018){dry=false;break;}
                    }
                if(dry){closest=dot;target=habitat;}
            }
            if(target is not {} found)continue;
            double angle=Math.Acos(Math.Clamp(closest,-1,1));
            double stop=motion.Species=="duck"?0:found.Kind is "tree" or "orchard" or "bush"?.13:.06;
            double fraction=angle>1e-6?Math.Clamp(1-stop/angle,0,1):0;
            var destination=motion.Home*(1-fraction)+found.Normal*fraction;destination.Normalize();
            motions[i]=motion with {Destination=destination,Habitat=found.Kind};
        }
    }
}
