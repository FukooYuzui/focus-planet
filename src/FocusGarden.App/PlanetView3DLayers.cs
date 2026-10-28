using System.Windows.Media;
using System.Windows.Media.Media3D;
using FocusGarden.Core;

namespace FocusGarden.App;

internal sealed partial class PlanetView3D
{
    private PlanetScene? naturalScene;
    private Model3DGroup? planetBody;
    private readonly List<SceneActor> naturalActors=[];
    private readonly Dictionary<SceneActor,Model3DGroup> actorWrappers=[];
    private readonly Dictionary<string,PlacedItem> placedItems=new(StringComparer.Ordinal);
    private readonly Dictionary<(int Plot,bool Selected),Model3DGroup> markerCache=[];
    private readonly Dictionary<int,Model3DGroup> displayedMarkers=[];

    private sealed class PlacedItem(PlanetBuildingPlacement placement,Model3DGroup model)
    {
        internal PlanetBuildingPlacement Placement=placement;
        internal readonly Model3DGroup Model=model;
        internal Model3DGroup? StaticWrapper;
        internal SceneActor? Actor;
    }

    /// <summary>Submit a complete planet once, avoiding intermediate states during startup/travel.</summary>
    public void ApplyState(int level,string weather,bool isFocusing,bool isNight,int seed,
        IReadOnlyList<PlanetBuildingPlacement> placements,bool isEditing,int? selectedPlot=null)
    {
        Dispatcher.VerifyAccess();ValidateState(level,weather);ValidatePlot(selectedPlot);
        var snapshot=ValidateBuildings(placements);
        bool layoutChanged=!buildings.SequenceEqual(snapshot),editingChanged=editMode!=isEditing;
        var previousSelection=SelectedPlotId;
        var nextSelection=isEditing?selectedPlot:null;
        bool selectionChanged=SelectedPlotId!=nextSelection;
        bool rebuild=naturalScene is null||currentLevel!=level||currentWeather!=weather||night!=isNight||currentSeed!=seed;
        buildings=snapshot;editMode=isEditing;SelectedPlotId=nextSelection;focusing=isFocusing;
        if(rebuild)RebuildNaturalScene(level,weather,isNight,seed);
        if(rebuild||layoutChanged)UpdatePlacements();
        if(rebuild||layoutChanged||editingChanged)UpdateSceneLayers();
        else if(selectionChanged&&editMode)UpdateSelectedMarkers(previousSelection,nextSelection);
        if(editingChanged&&editMode)ReturnActorsHome();
        UpdateStatus();
    }

    public void SetState(int level,string weather,bool isFocusing,bool isNight,int seed=20261027)
    {
        Dispatcher.VerifyAccess();ValidateState(level,weather);
        focusing=isFocusing;
        if(naturalScene is null||currentLevel!=level||currentWeather!=weather||night!=isNight||currentSeed!=seed)
        {
            RebuildNaturalScene(level,weather,isNight,seed);UpdatePlacements();UpdateSceneLayers();
        }
        UpdateStatus();
    }

    public void SetBuildings(IReadOnlyList<PlanetBuildingPlacement> placements)
    {
        Dispatcher.VerifyAccess();var snapshot=ValidateBuildings(placements);
        if(buildings.SequenceEqual(snapshot))return;
        buildings=snapshot;EnsureNaturalScene();UpdatePlacements();UpdateSceneLayers();UpdateStatus();
    }

    public void SetEditMode(bool enabled)
    {
        Dispatcher.VerifyAccess();if(editMode==enabled)return;
        editMode=enabled;if(!enabled)SelectedPlotId=null;
        EnsureNaturalScene();UpdateSceneLayers();if(enabled)ReturnActorsHome();UpdateStatus();
    }

    public void SelectPlot(int? plotId)
    {
        Dispatcher.VerifyAccess();ValidatePlot(plotId);if(SelectedPlotId==plotId)return;
        var previousSelection=SelectedPlotId;SelectedPlotId=plotId;
        // Only marker references and their hit entries change; the habitat and actors stay intact.
        if(editMode){EnsureNaturalScene();UpdateSelectedMarkers(previousSelection,plotId);}
        UpdateStatus();
    }

    private static void ValidateState(int level,string weather)
    {
        if(level is <0 or >5)throw new ArgumentOutOfRangeException(nameof(level));
        if(weather is not ("clear" or "cloudy" or "rain" or "aurora"))throw new ArgumentException("天气应为 clear、cloudy、rain 或 aurora。",nameof(weather));
    }
    private static void ValidatePlot(int? plotId){if(plotId is <0 or >=32)throw new ArgumentOutOfRangeException(nameof(plotId));}
    private static PlanetBuildingPlacement[] ValidateBuildings(IReadOnlyList<PlanetBuildingPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);var snapshot=placements.ToArray();
        if(snapshot.Any(p=>p is null||string.IsNullOrWhiteSpace(p.Id)||p.PlotId is <0 or >=32||p.QuarterTurns is <0 or >3||!PlanetLayoutCatalog.Entries.Any(d=>d.Kind==p.Kind))||snapshot.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=snapshot.Length||snapshot.Select(p=>p.PlotId).Distinct().Count()!=snapshot.Length||snapshot.Count(p=>PlanetLayoutCatalog.IsAnimal(p.Kind))>PlanetLayoutCatalog.AnimalLimitPerPlanet)
            throw new ArgumentException("内容须使用唯一 ID 和地块，朝向为 0–3，生物最多 12 只。",nameof(placements));
        return snapshot.OrderBy(p=>p.PlotId).ToArray();
    }
    private void EnsureNaturalScene(){if(naturalScene is null)RebuildNaturalScene(0,"clear",false,20261027);}

    private void RebuildNaturalScene(int level,string weather,bool isNight,int seed)
    {
        naturalScene=PlanetView3DScene.Build(level,weather,isNight,seed);
        currentLevel=level;currentWeather=weather;night=isNight;currentSeed=seed;
        naturalActors.Clear();actorWrappers.Clear();placedItems.Clear();
        foreach(var definition in naturalScene.Motions)naturalActors.Add(NewActor(definition));
        var world=new Model3DGroup();
        world.Children.Add(new AmbientLight(isNight?Color.FromRgb(70,86,105):Color.FromRgb(133,143,140)));
        world.Children.Add(new DirectionalLight(isNight?Color.FromRgb(109,149,181):Color.FromRgb(255,245,216),new(-.7,-1,-1.6)));
        world.Children.Add(new DirectionalLight(isNight?Color.FromRgb(62,101,117):Color.FromRgb(82,116,130),new(1,.3,.5)));
        planetBody=new Model3DGroup {Transform=planetTransform};world.Children.Add(planetBody);
        scene.Content=world;SceneBuildCount++;UpdateCamera();
    }

    private SceneActor NewActor(PlanetSceneMotion definition)
    {
        var actor=new SceneActor(definition);var transform=new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(actor.Lean));transform.Children.Add(new RotateTransform3D(actor.Turn));transform.Children.Add(actor.Offset);transform.Children.Add(actor.Surface);
        var wrapper=new Model3DGroup {Transform=transform};wrapper.Children.Add(definition.Model);actorWrappers.Add(actor,wrapper);return actor;
    }

    private void UpdatePlacements()
    {
        var ids=buildings.Select(p=>p.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var id in placedItems.Keys.Where(id=>!ids.Contains(id)).ToArray())RemoveItem(id);
        foreach(var placement in buildings)
        {
            if(placedItems.TryGetValue(placement.Id,out var previous)&&previous.Placement==placement)continue;
            var item=previous;
            if(item is null||item.Placement.Kind!=placement.Kind)
            {
                if(item is not null)RemoveItem(placement.Id);
                item=new(placement,PlanetView3DScene.BuildPlacementModel(placement.Kind));placedItems[placement.Id]=item;
            }
            item.Placement=placement;
            var plot=PlanetView3DScene.Plots[placement.PlotId];var normal=PlanetView3DGeometry.Direction(plot.Latitude,plot.Longitude);
            var surface=PlanetView3DGeometry.SurfaceTransform(plot.Latitude,plot.Longitude,2.69,placement.QuarterTurns*90);
            if(PlanetLayoutCatalog.IsAnimal(placement.Kind))
            {
                var definition=new PlanetSceneMotion(item.Model,surface,"creature",placement.Kind,normal,normal,null,2.69,placement.QuarterTurns*90,0);
                if(item.Actor is null)item.Actor=NewActor(definition);else item.Actor.Definition=definition;
                ReturnActorHome(item.Actor);
            }
            else
            {
                var wrapper=new Model3DGroup {Transform=surface};wrapper.Children.Add(item.Model);wrapper.Freeze();item.StaticWrapper=wrapper;
            }
        }
        motions.Clear();motions.AddRange(naturalActors.Where(a=>a.Definition.Kind=="creature"));
        motions.AddRange(buildings.Select(p=>placedItems[p.Id].Actor).OfType<SceneActor>());
        motions.AddRange(naturalActors.Where(a=>a.Definition.Kind!="creature"));
        var habitats=naturalScene!.Habitats.ToList();
        foreach(var placement in buildings.Where(p=>PlanetView3DScene.IsHabitat(p.Kind)))
        {
            var plot=PlanetView3DScene.Plots[placement.PlotId];habitats.Add((placement.Kind,PlanetView3DGeometry.Direction(plot.Latitude,plot.Longitude)));
        }
        var definitions=motions.Select((a,index)=>a.Definition with {Phase=index*3.71}).ToList();
        PlanetView3DScene.ResolveHabitats(definitions,habitats);
        for(int i=0;i<motions.Count;i++)
        {
            var actor=motions[i];var previous=actor.Definition;var next=definitions[i];actor.Definition=next;
            // A new target can be behind the planet. Clear its old visible pose before
            // culling skips that actor, without replacing any frozen model geometry.
            if(previous.Home!=next.Home||previous.Destination!=next.Destination||previous.Habitat!=next.Habitat||previous.Phase!=next.Phase)
                ReturnActorHome(actor);
        }
        if(editMode)ReturnActorsHome();else AnimateScene(animationSeconds);
        void RemoveItem(string id)
        {
            if(placedItems[id].Actor is {} actor)actorWrappers.Remove(actor);
            placedItems.Remove(id);
        }
    }

    private void UpdateSceneLayers()
    {
        var desired=new List<Model3D> {naturalScene!.Model};
        desired.AddRange(buildings.Select(p=>placedItems[p.Id].StaticWrapper).OfType<Model3DGroup>());
        displayedMarkers.Clear();
        if(editMode)
            for(int id=0;id<PlanetLayoutCatalog.PlotCount;id++)
            {
                var key=(id,SelectedPlotId==id);
                if(!markerCache.TryGetValue(key,out var marker)){marker=PlanetView3DScene.BuildPlotMarker(id,key.Item2);markerCache.Add(key,marker);}
                displayedMarkers.Add(id,marker);desired.Add(marker);
            }
        desired.AddRange(motions.Select(actor=>actorWrappers[actor]));
        // Reuse every unchanged object; collection operations affect only differing positions.
        var children=planetBody!.Children;
        var retained=desired.ToHashSet();
        for(int index=children.Count-1;index>=0;index--)if(!retained.Contains(children[index]))children.RemoveAt(index);
        for(int index=0;index<desired.Count;index++)
        {
            if(index<children.Count&&ReferenceEquals(children[index],desired[index]))continue;
            int existing=children.IndexOf(desired[index]);if(existing>=0)children.RemoveAt(existing);
            children.Insert(index,desired[index]);
        }
        while(children.Count>desired.Count)children.RemoveAt(children.Count-1);
        var hits=new Dictionary<GeometryModel3D,int>();
        if(editMode)
        {
            foreach(var item in placedItems.Values)MapHits(item.Model,item.Placement.PlotId,hits);
            foreach(var (id,marker) in displayedMarkers)MapHits(marker,id,hits);
        }
        plotHits=hits;
        var counts=new Dictionary<string,int>(naturalScene.Features);
        foreach(var placement in buildings){counts[placement.Kind]=counts.GetValueOrDefault(placement.Kind)+1;if(PlanetLayoutCatalog.IsAnimal(placement.Kind))counts["creature"]=counts.GetValueOrDefault("creature")+1;}
        if(editMode)counts["plot"]=PlanetLayoutCatalog.PlotCount;FeatureCounts=counts;
    }
    private void UpdateSelectedMarkers(int? previous,int? next)
    {
        var hits=(Dictionary<GeometryModel3D,int>)plotHits;
        foreach(var plot in new[]{previous,next}.OfType<int>().Distinct())
        {
            var before=displayedMarkers[plot];var key=(plot,next==plot);
            if(!markerCache.TryGetValue(key,out var after)){after=PlanetView3DScene.BuildPlotMarker(plot,key.Item2);markerCache.Add(key,after);}
            int index=planetBody!.Children.IndexOf(before);planetBody.Children[index]=after;displayedMarkers[plot]=after;
            RemoveHits(before);MapHits(after,plot,hits);
        }
        void RemoveHits(Model3D model)
        {
            if(model is GeometryModel3D geometry)hits.Remove(geometry);
            else if(model is Model3DGroup group)foreach(var child in group.Children)RemoveHits(child);
        }
    }
    private static void MapHits(Model3D model,int plot,Dictionary<GeometryModel3D,int> hits)
    {
        if(model is GeometryModel3D geometry)hits.Add(geometry,plot);
        else if(model is Model3DGroup group)foreach(var child in group.Children)MapHits(child,plot,hits);
    }
    private void ReturnActorsHome(){foreach(var actor in motions)ReturnActorHome(actor);}
    private static void ReturnActorHome(SceneActor actor)
    {
        actor.Offset.OffsetX=actor.Offset.OffsetY=actor.Offset.OffsetZ=0;actor.Turn.Angle=actor.Lean.Angle=0;actor.Surface.Matrix=actor.Definition.Surface.Value;
    }
    private void UpdateStatus()
    {
        string state=currentWeather switch {"clear"=>"晴朗","cloudy"=>"云朵漫游","rain"=>"轻雨滋养",_=>"极光之夜"};
        status.Text=$"Lv.{currentLevel}  ·  {state}  ·  {(night?"夜晚":"白昼")}{(editMode?$"  /  布置家园{(SelectedPlotId.HasValue?$" · 地块 {SelectedPlotId+1}":"")}":focusing?"  /  专注中":"")}";UpdateAnimation();
    }
}
