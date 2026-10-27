using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    private const int SurfaceTextureWidth=1024,SurfaceTextureHeight=512;
    private static readonly object SurfaceCacheLock=new();
    private static readonly GeometryModel3D?[] SurfaceLevels=new GeometryModel3D?[6];
    private static MeshGeometry3D? smoothSurface;

    /// <summary>One shared spherical mesh and one frozen procedural colour map per growth level.</summary>
    private static GeometryModel3D PlanetSurface(int level)
    {
        lock(SurfaceCacheLock)
        {
            if(SurfaceLevels[level] is { } cached)return cached;
            smoothSurface??=CreateSurfaceMesh();
            var texture=CreateSurfaceTexture(level);
            var brush=new ImageBrush(texture)
            {
                ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1),
                ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,Viewbox=new Rect(0,0,1,1),
                Stretch=Stretch.Fill,TileMode=TileMode.Tile
            };
            RenderOptions.SetBitmapScalingMode(brush,BitmapScalingMode.HighQuality);
            brush.Freeze();var material=new DiffuseMaterial(brush);material.Freeze();
            var result=new GeometryModel3D(smoothSurface,material);result.Freeze();SurfaceLevels[level]=result;return result;
        }
    }

    private static MeshGeometry3D CreateSurfaceMesh()
    {
        var mesh=new MeshGeometry3D();
        foreach(var(a,b,c) in PlanetView3DGeometry.SphereFaces(4))
        {
            int first=mesh.Positions.Count;
            PlanetView3DGeometry.SmoothTriangle(mesh,(Point3D)((Vector3D)a*Radius),(Point3D)((Vector3D)b*Radius),(Point3D)((Vector3D)c*Radius));
            // Read the emitted vertices because SmoothTriangle may correct winding.
            var uv=new Point[3];var polar=new bool[3];double minU=double.PositiveInfinity,maxU=double.NegativeInfinity;
            for(int i=0;i<3;i++)
            {
                var n=(Vector3D)mesh.Positions[first+i];n.Normalize();
                polar[i]=n.X*n.X+n.Z*n.Z<1e-14;
                double u=(Math.Atan2(n.X,n.Z)+Math.PI)/(2*Math.PI),v=.5-Math.Asin(Math.Clamp(n.Y,-1,1))/Math.PI;
                // TileMode repeats both axes. Sample the pole-row pixel centres so V never wraps
                // across the north/south edge while U can still repeat across longitude +/-pi.
                v=.5/SurfaceTextureHeight+(1-1.0/SurfaceTextureHeight)*v;
                uv[i]=new Point(u,v);
                if(!polar[i]){minU=Math.Min(minU,u);maxU=Math.Max(maxU,u);}
            }
            // A triangle crossing longitude +/-pi must interpolate through u=1, never through u=.5.
            if(maxU-minU>.5)for(int i=0;i<3;i++)if(!polar[i]&&uv[i].X<.5)uv[i].X+=1;
            for(int i=0;i<3;i++)
            {
                if(polar[i])
                {
                    double sum=0;int count=0;for(int j=0;j<3;j++)if(!polar[j]){sum+=uv[j].X;count++;}
                    uv[i].X=count==0?.5:sum/count;
                }
                mesh.TextureCoordinates.Add(uv[i]);
            }
        }
        mesh.Freeze();return mesh;
    }

    private static BitmapSource CreateSurfaceTexture(int level)
    {
        const int stride=SurfaceTextureWidth*4;
        var pixels=new byte[SurfaceTextureHeight*stride];
        var sinLongitude=new double[SurfaceTextureWidth];var cosLongitude=new double[SurfaceTextureWidth];
        for(int x=0;x<SurfaceTextureWidth;x++)
        {
            double longitude=((double)x/(SurfaceTextureWidth-1)-.5)*2*Math.PI;
            sinLongitude[x]=Math.Sin(longitude);cosLongitude[x]=Math.Cos(longitude);
        }
        for(int y=0;y<SurfaceTextureHeight;y++)
        {
            double latitude=(.5-(double)y/(SurfaceTextureHeight-1))*Math.PI;
            double sinLatitude=Math.Sin(latitude),cosLatitude=Math.Cos(latitude);
            for(int x=0;x<SurfaceTextureWidth;x++)
            {
                var normal=new Vector3D(cosLatitude*sinLongitude[x],sinLatitude,cosLatitude*cosLongitude[x]);
                double land=Land(normal)+(level==0?.18:level==1?.08:0);
                var color=ContinuousSurfaceColor(level,land,normal);
                int offset=y*stride+x*4;pixels[offset]=color.B;pixels[offset+1]=color.G;pixels[offset+2]=color.R;pixels[offset+3]=255;
            }
            // Pixel-identical edges guarantee a colour-continuous repeated texture seam.
            Buffer.BlockCopy(pixels,y*stride,pixels,y*stride+(SurfaceTextureWidth-1)*4,4);
        }
        var bitmap=BitmapSource.Create(SurfaceTextureWidth,SurfaceTextureHeight,96,96,PixelFormats.Bgra32,null,pixels,stride);bitmap.Freeze();return bitmap;
    }

    private static Color ContinuousSurfaceColor(int level,double land,Vector3D n)
    {
        // Colour transitions use the same geographic field as the stable land plots, with no per-face bands.
        double variation=Math.Clamp(.5+.20*Math.Sin(n.X*2.8+n.Y*3.1)+.13*Math.Cos(n.Z*3.7-n.Y*1.4),0,1);
        var deep=Color.FromRgb(53,119,150);var shelf=Color.FromRgb(78,158,174);var shallows=Color.FromRgb(137,198,192);
        var beach=Color.FromRgb(221,211,170);
        Color inland=level switch
        {
            0=>BlendSurface(Color.FromRgb(167,151,128),Color.FromRgb(188,171,145),variation),
            1=>BlendSurface(Color.FromRgb(143,170,113),Color.FromRgb(169,187,132),variation),
            _=>BlendSurface(Color.FromRgb(114,168,112),Color.FromRgb(148,189,132),variation)
        };
        if(land<-.09)return BlendSurface(deep,shelf,SurfaceSmoothStep(-.34,-.09,land));
        if(land<-.027)return BlendSurface(shelf,shallows,SurfaceSmoothStep(-.09,-.027,land));
        if(land<.002)return BlendSurface(shallows,beach,SurfaceSmoothStep(-.027,.002,land));
        return BlendSurface(beach,inland,SurfaceSmoothStep(.002,.045,land));
    }

    private static double SurfaceSmoothStep(double low,double high,double value)
    {
        double t=Math.Clamp((value-low)/(high-low),0,1);return t*t*(3-2*t);
    }
    private static Color BlendSurface(Color a,Color b,double amount)
    {
        double t=Math.Clamp(amount,0,1);return Color.FromRgb((byte)Math.Round(a.R+(b.R-a.R)*t),(byte)Math.Round(a.G+(b.G-a.G)*t),(byte)Math.Round(a.B+(b.B-a.B)*t));
    }
}
