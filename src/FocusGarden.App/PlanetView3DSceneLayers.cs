using System.Windows.Media.Media3D;
using FocusGarden.Core;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    internal static Model3DGroup BuildPlacementModel(string kind)
    {
        var model=PlaceableModel(kind);model.Freeze();return model;
    }

    internal static Model3DGroup BuildPlotMarker(int plotId,bool selected)
    {
        var marker=new Model3DGroup();
        Add(marker,PlanetView3DGeometry.Ring(selected?.23:.20,selected?.035:.018,.035,32),selected?"#FFE2A0":"#91D7BE",false,true);
        Add(marker,PlanetView3DGeometry.Frustum(.20,.20,.009,16,.006),selected?"#C3B77C":"#6F9E82");
        var plot=Plots[plotId];marker.Transform=PlanetView3DGeometry.SurfaceTransform(plot.Latitude,plot.Longitude,2.705);
        marker.Freeze();return marker;
    }

    internal static bool IsHabitat(string kind)=>kind is "tree" or "flower" or "pond" or "meadow" or "mushroom" or "orchard" or "bush";
}
