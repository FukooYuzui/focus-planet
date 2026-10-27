using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    private static readonly Dictionary<string,Material> GlossMaterials=[];

    private static Material FinishedMaterial(string color,bool emissive)
    {
        // Only tiny eyes and pond water need a highlight pass. Most of the scene
        // remains matte, retaining both the soft cartoon style and the draw budget.
        bool water=color is "#67BAC8" or "#92D4D6" or "#76C6D1";
        bool eye=color is "#394B42" or "#354C49" or "#263E39";
        if(emissive||(!water&&!eye))return SharedMaterial(color,emissive);
        lock(GlossMaterials)
        {
            if(GlossMaterials.TryGetValue(color,out var cached))return cached;
            var brush=new SolidColorBrush(Color.FromArgb(water?(byte)65:(byte)110,240,250,248));brush.Freeze();
            var material=new MaterialGroup();material.Children.Add(SharedMaterial(color,false));
            material.Children.Add(new SpecularMaterial(brush,water?40:55));material.Freeze();
            GlossMaterials.Add(color,material);return material;
        }
    }
}
