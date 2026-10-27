using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal static partial class PlanetView3DScene
{
    private static readonly Dictionary<(string Color,bool Emissive),Material> Materials=[];

    private static string SurfaceColor(int level,double land,Vector3D n)
    {
        // Broad, quiet gradients replace the old alternating triangle colours.
        if(land<-.07)return Tint("#397E98","#559FAE",Math.Clamp((land+.34)/.27,0,1),8);
        if(land<-.025)return Tint("#60ADB5","#8CC8C2",(land+.07)/.045,6);
        if(land<.012)return Tint("#D0CCAA","#E3D6B3",(land+.025)/.037,3);
        double variation=Math.Clamp(.5+.20*Math.Sin(n.X*2.8+n.Y*3.1)+.13*Math.Cos(n.Z*3.7-n.Y*1.4),0,1);
        if(level==0)return Tint("#AA9C85","#B9AC94",variation,7);
        if(level==1)return Tint("#95AF7C","#ABB987",variation,7);
        return Tint("#85B68B","#9BC499",variation,8);
    }
    private static string Tint(string first,string second,double fraction,int steps)
    {
        double t=Math.Round(Math.Clamp(fraction,0,1)*steps)/steps;
        var a=(Color)ColorConverter.ConvertFromString(first);var b=(Color)ColorConverter.ConvertFromString(second);
        return $"#{(byte)(a.R+(b.R-a.R)*t):X2}{(byte)(a.G+(b.G-a.G)*t):X2}{(byte)(a.B+(b.B-a.B)*t):X2}";
    }
    private static Material SharedMaterial(string color,bool emissive)
    {
        lock(Materials)
        {
            if(Materials.TryGetValue((color,emissive),out var existing))return existing;
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));brush.Freeze();
            Material result=emissive?new EmissiveMaterial(brush):new DiffuseMaterial(brush);result.Freeze();Materials.Add((color,emissive),result);return result;
        }
    }
    private static IReadOnlyList<(double Lat,double Lon)> NatureSites(Random random,int count)
    {
        var candidates=new List<(double Lat,double Lon,Vector3D N,int Order)>();
        for(int i=0;i<2600;i++)
        {
            double lat=Math.Asin(1-2*(i+.5)/2600)*180/Math.PI,lon=(i*137.50776405003785)%360-180;var n=PlanetView3DGeometry.Direction(lat,lon);
            if(Land(n)>.018&&!NearPlot(lat,lon)&&Vector3D.DotProduct(n,PlanetView3DGeometry.Direction(43,-28))<.947)candidates.Add((lat,lon,n,random.Next()));
        }
        var chosen=new List<(double Lat,double Lon,Vector3D N)>();
        foreach(var c in candidates.OrderBy(c=>c.Order))
        {
            if(chosen.All(p=>Vector3D.DotProduct(p.N,c.N)<.993)){chosen.Add((c.Lat,c.Lon,c.N));if(chosen.Count==count)break;}
        }
        return chosen.Select(p=>(p.Lat,p.Lon)).ToArray();
    }
    private static Model3DGroup ScaleGroup(Model3DGroup child,double scale)
    {
        child.Transform=new ScaleTransform3D(scale,scale,scale);var outer=new Model3DGroup();outer.Children.Add(child);return outer;
    }
    private static Model3DGroup RockCluster(double scale)
    {
        var g=new Model3DGroup();
        DetailRound(g,-.07,.055,.01,.10,.075,.075,"#9C9A87");DetailRound(g,.045,.09,-.025,.09,.125,.09,"#B9B39F");DetailRound(g,.11,.028,.055,.055,.045,.045,"#C7BEA8");
        DetailRound(g,-.12,.018,.06,.032,.023,.030,"#D3CBB4");return ScaleGroup(g,scale);
    }
    private static Model3DGroup RockStrata(double scale)
    {
        var g=new Model3DGroup();
        DetailRound(g,0,.02,0,.20,.025,.13,"#9D8B75");
        DetailBox(g,-.15,.03,-.10,.30,.055,.20,"#BB9F80");DetailBox(g,-.13,.083,-.09,.26,.019,.18,"#927F6C");DetailBox(g,-.11,.10,-.08,.25,.06,.18,"#D0B08A");DetailBox(g,-.09,.16,-.07,.18,.025,.15,"#DEBF99");
        DetailRound(g,.16,.04,.04,.07,.05,.065,"#BA9E80");return ScaleGroup(g,scale);
    }
    private static Model3DGroup CrystalCluster(double scale)
    {
        var g=new Model3DGroup();DetailRound(g,0,.012,0,.14,.026,.11,"#9A9693");
        for(int i=0;i<4;i++)
        {
            double x=(i%2-.5)*.095,z=(i/2-.5)*.075,h=.13+(i%3)*.055;
            var crystal=new Model3DGroup();Add(crystal,PlanetView3DGeometry.Frustum(.035,.030,h,5),i%2==0?"#A7B8C1":"#BAC6CF");Add(crystal,PlanetView3DGeometry.Frustum(.030,0,.065,5,h),"#D6E1DC");
            var t=new Transform3DGroup();t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),(i-1)*9)));t.Children.Add(new TranslateTransform3D(x,.015,z));crystal.Transform=t;g.Children.Add(crystal);
        }
        return ScaleGroup(g,scale);
    }
    private static Model3DGroup Crater(double radius)
    {
        var g=new Model3DGroup();DetailRound(g,0,.008,0,radius*.79,.012,radius*.79,"#817769",true);
        double[] ringR=[.60,.76,.93,1.10];double[] ringY=[.014,.036,.032,.002];
        string[] colors=["#A69780","#C2B397","#BCAD92"];
        for(int band=0;band<3;band++)
        {
            var mesh=new MeshGeometry3D();
            for(int i=0;i<32;i++)
            {
                double a=i*2*Math.PI/32,b=(i+1)*2*Math.PI/32;
                Point3D p=new(Math.Cos(a)*radius*ringR[band],ringY[band],Math.Sin(a)*radius*ringR[band]),q=new(Math.Cos(b)*radius*ringR[band],ringY[band],Math.Sin(b)*radius*ringR[band]),r=new(Math.Cos(b)*radius*ringR[band+1],ringY[band+1],Math.Sin(b)*radius*ringR[band+1]),s=new(Math.Cos(a)*radius*ringR[band+1],ringY[band+1],Math.Sin(a)*radius*ringR[band+1]);
                PlanetView3DGeometry.Triangle(mesh,p,q,r);PlanetView3DGeometry.Triangle(mesh,p,r,s);
            }
            mesh.Freeze();Add(g,mesh,colors[band],false,true);
        }
        DetailRound(g,radius*.85,.024,radius*.5,.035,.032,.031,"#C6B89D");DetailRound(g,-radius*.8,.018,-radius*.6,.024,.022,.028,"#978975");return g;
    }
    private static Model3DGroup SoftTree(double scale,int kind)
    {
        var g=new Model3DGroup();
        Add(g,PlanetView3DGeometry.Frustum(.036,.022,.25,8),"#947654");
        DetailBeam(g,new(0,.12,0),new(-.08,.30,.02),.022,"#947654");DetailBeam(g,new(0,.17,0),new(.09,.33,-.03),.018,"#A2865C");
        if(kind==0)
        {
            Add(g,PlanetView3DGeometry.Frustum(.18,.055,.25,12,.14),"#579277");Add(g,PlanetView3DGeometry.Frustum(.15,.025,.24,12,.30),"#70A68A");Add(g,PlanetView3DGeometry.Frustum(.10,0,.21,12,.46),"#81B49A");
        }
        else
        {
            string leaf=kind==1?"#8CB883":"#D3B89A";string light=kind==1?"#A5C795":"#E0C6AC";
            DetailRound(g,-.08,.34,.03,.16,.17,.14,leaf,true);DetailRound(g,.09,.37,-.025,.15,.17,.15,leaf,true);DetailRound(g,0,.48,.005,.17,.17,.15,light,true);
            if(kind==1)foreach(var p in new[]{new Point3D(-.16,.36,.08),new(.16,.37,.03),new(.035,.49,.15)})DetailRound(g,p.X,p.Y,p.Z,.025,.028,.025,"#DEB475");
            else foreach(var p in new[]{new Point3D(-.13,.40,.12),new(.12,.47,.08),new(-.02,.59,.06)})DetailRound(g,p.X,p.Y,p.Z,.032,.024,.030,"#E9C9BA");
        }
        DetailRound(g,-.05,.012,.05,.08,.017,.06,"#7DA079");return ScaleGroup(g,scale);
    }
    private static Model3DGroup ShrubPatch(double scale)
    {
        var g=new Model3DGroup();DetailShrub(g,-.07,.02,.015,.095,"#6B9C79");DetailShrub(g,.055,.015,-.02,.085,"#8EB084");DetailShrub(g,.13,.005,.055,.06,"#A4BA88");
        foreach(var p in new[]{new Point3D(-.12,.10,.06),new(-.04,.125,.075),new(.075,.09,.05)})DetailRound(g,p.X,p.Y,p.Z,.020,.018,.018,"#CDA0A5");return ScaleGroup(g,scale);
    }
    private static Model3DGroup Wildflowers(double scale)
    {
        var g=new Model3DGroup();
        for(int i=0;i<5;i++)
        {
            double angle=i*2.4,x=Math.Cos(angle)*.10,z=Math.Sin(angle)*.07,y=.08+(i%3)*.028;
            DetailBox(g,x-.004,0,z-.004,.008,y,.008,"#6C9262");DetailRound(g,x-.016,.035,z,.027,.010,.013,"#87AA76");
            for(int petal=0;petal<5;petal++){double a=petal*Math.PI*2/5;DetailRound(g,x+Math.Cos(a)*.021,y,z+Math.Sin(a)*.021,.018,.012,.018,i%2==0?"#E3B3B9":"#EBDAAA");}
            DetailRound(g,x,y+.008,z,.014,.010,.014,"#D2AA62");
        }
        return ScaleGroup(g,scale);
    }
    private static Model3DGroup Mushrooms(double scale)
    {
        var g=new Model3DGroup();
        for(int i=0;i<3;i++)
        {
            double x=(i-1)*.07,z=i%2*.06,h=.06+(i%2)*.025;
            DetailRound(g,x,h*.5,z,.018,h*.6,.018,"#D9D1AE");DetailRound(g,x,h+.015,z,.052,.032,.047,i==1?"#C08070":"#BA9A76");
            DetailRound(g,x-.015,h+.042,z,.009,.004,.008,"#EFE0BE");DetailRound(g,x+.021,h+.036,z+.01,.007,.004,.007,"#EFE0BE");
        }
        DetailRound(g,-.10,.012,.02,.035,.018,.04,"#809772");return ScaleGroup(g,scale);
    }
    private static Model3DGroup MeadowHabitat(double scale)
    {
        var g=new Model3DGroup();DetailRound(g,0,.006,0,.16,.015,.11,"#A4BA8B");
        var log=new Model3DGroup();Add(log,PlanetView3DGeometry.Frustum(.036,.032,.23,9),"#967D5A");Add(log,PlanetView3DGeometry.Frustum(.033,.033,.007,9,.229),"#D1B58B");log.Transform=new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),84));g.Children.Add(log);
        DetailShrub(g,.08,.005,-.055,.055,"#759F7C");DetailRound(g,.13,.012,.06,.035,.024,.026,"#C2BA9D");
        for(int i=0;i<3;i++)DetailBeam(g,new(.05+i*.02,0,.07),new(.04+i*.025,.08+i*.008,.08),.007,"#98AA6E");return ScaleGroup(g,scale);
    }
    private static Model3DGroup WaterRipples(double scale)
    {
        var g=new Model3DGroup();
        for(int row=0;row<2;row++)
        {
            var mesh=new MeshGeometry3D();double width=.12-row*.035;
            for(int i=0;i<9;i++){double x=-width+i*width*2/9,next=-width+(i+1)*width*2/9,z=row*.065+Math.Pow(x/width,2)*.016,z2=row*.065+Math.Pow(next/width,2)*.016;PlanetView3DGeometry.Triangle(mesh,new(x,.012,z),new(next,.012,z2),new(next,.012,z2+.007));PlanetView3DGeometry.Triangle(mesh,new(x,.012,z),new(next,.012,z2+.007),new(x,.012,z+.007));}
            mesh.Freeze();Add(g,mesh,row==0?"#A2D1D0":"#91C8CD",false,true);
        }
        return ScaleGroup(g,scale);
    }
    private static Model3DGroup ShorePebbles()
    {
        var g=new Model3DGroup();DetailRound(g,0,.018,0,.045,.025,.035,"#B8BEAB");DetailRound(g,.07,.012,.02,.028,.018,.025,"#D1CEB6");DetailRound(g,-.055,.008,.025,.020,.013,.021,"#A4B1A3");return g;
    }
    private static Model3DGroup SoftSheep()
    {
        var g=new Model3DGroup();foreach(double x in new[]{-.08,.08})foreach(double z in new[]{-.055,.055})DetailBox(g,x-.013,.005,z-.013,.026,.085,.026,"#7C7566");
        DetailRound(g,0,.14,0,.15,.105,.10,"#EFE6CD",true);
        foreach(var p in new[]{new Point3D(-.08,.20,.025),new(.025,.235,0),new(.08,.20,-.015)})DetailRound(g,p.X,p.Y,p.Z,.070,.060,.075,"#F0E9D6");
        DetailRound(g,.15,.16,.005,.065,.07,.062,"#8F8D7B");DetailRound(g,.18,.14,.025,.05,.035,.045,"#9E9A85");
        DetailRound(g,.14,.205,.067,.048,.018,.021,"#B8AD93");DetailRound(g,.14,.205,-.059,.048,.018,.021,"#B8AD93");AnimalEye(g,.185,.18,.055);AnimalEye(g,.185,.18,-.05);DetailRound(g,-.15,.16,0,.044,.043,.04,"#EFE6CD");return g;
    }
    private static Model3DGroup SoftRabbit()
    {
        var g=new Model3DGroup();DetailRound(g,-.025,.09,0,.092,.10,.077,"#D7C8AE",true);DetailRound(g,.065,.16,0,.064,.067,.058,"#E9D8BE",true);
        foreach(double z in new[]{-.032,.032}){DetailRound(g,.054,.255,z,.023,.092,.022,"#D6C5AD");DetailRound(g,.063,.258,z+.009,.011,.065,.012,"#D3A8A6");DetailRound(g,.045,.018,z*1.5,.057,.025,.026,"#E0CFB4");}
        AnimalEye(g,.105,.174,.045);AnimalEye(g,.105,.174,-.045);DetailRound(g,.125,.145,0,.012,.010,.014,"#BE9692");DetailRound(g,-.12,.09,0,.036,.037,.035,"#F0E4D1");return g;
    }
    private static Model3DGroup Fox()
    {
        var g=new Model3DGroup();DetailRound(g,-.01,.105,0,.135,.075,.073,"#CA9470",true);foreach(double x in new[]{-.09,.07})foreach(double z in new[]{-.045,.045})DetailBox(g,x,.005,z,.024,.075,.024,"#8D735D");
        DetailRound(g,.12,.17,0,.068,.072,.063,"#D7A17A");DetailRound(g,.17,.14,0,.06,.030,.042,"#E7CFAC");
        foreach(double z in new[]{-.04,.04}){var ear=new Model3DGroup();Add(ear,PlanetView3DGeometry.Frustum(.034,0,.085,4),"#C58A66");ear.Transform=new TranslateTransform3D(.105,.205,z);g.Children.Add(ear);}
        DetailRound(g,-.17,.13,.015,.14,.052,.058,"#C98F67");DetailRound(g,-.28,.14,.015,.060,.041,.043,"#EAD8B9");AnimalEye(g,.16,.18,.047);AnimalEye(g,.16,.18,-.047);DetailRound(g,.22,.145,0,.012,.011,.017,"#4E5146");return g;
    }
    private static Model3DGroup Duck()
    {
        var g=new Model3DGroup();DetailRound(g,-.015,.075,0,.13,.072,.082,"#E7DFBA",true);DetailRound(g,.10,.15,0,.055,.060,.05,"#6D9D86",true);DetailRound(g,.15,.13,0,.055,.017,.032,"#DDB36E");DetailRound(g,-.03,.09,.068,.075,.042,.020,"#CBBE94");
        AnimalEye(g,.126,.165,.041);AnimalEye(g,.126,.165,-.041);DetailBox(g,-.05,0,-.065,.07,.013,.035,"#CDA165");DetailBox(g,-.05,0,.03,.07,.013,.035,"#CDA165");return g;
    }
    private static Model3DGroup Deer()
    {
        var g=new Model3DGroup();DetailRound(g,-.01,.16,0,.14,.075,.072,"#B89775",true);foreach(double x in new[]{-.095,.07})foreach(double z in new[]{-.045,.045})DetailBox(g,x,.005,z,.018,.14,.018,"#947C61");
        DetailRound(g,.105,.235,0,.044,.11,.045,"#C3A280");DetailRound(g,.145,.31,0,.062,.055,.046,"#CBAE89");DetailRound(g,.19,.29,0,.042,.026,.035,"#D8C3A2");foreach(double z in new[]{-.05,.05})DetailRound(g,.11,.35,z,.045,.018,.025,"#B99B78");
        foreach(double z in new[]{-.028,.028}){DetailBeam(g,new(.12,.35,z),new(.095,.43,z*1.4),.012,"#9D8869");DetailBeam(g,new(.10,.40,z*1.2),new(.065,.42,z*2),.009,"#9D8869");}
        AnimalEye(g,.17,.32,.038);AnimalEye(g,.17,.32,-.038);foreach(double x in new[]{-.08,-.02,.045})DetailRound(g,x,.185,.067,.014,.012,.006,"#E9D9B9");return g;
    }
    private static void AnimalEye(Model3DGroup g,double x,double y,double z){DetailRound(g,x,y,z,.009,.011,.007,"#394B42");DetailRound(g,x+.003,y+.003,z>0?z+.005:z-.005,.003,.004,.003,"#EFE8D2");}
    private static Model3DGroup SoftCloud()
    {
        var g=new Model3DGroup();DetailRound(g,-.23,0,.01,.22,.12,.16,"#DCE8E3",true);DetailRound(g,-.02,.07,0,.27,.19,.21,"#F0F0E4",true);DetailRound(g,.23,.02,0,.21,.14,.17,"#E8ECE2",true);DetailRound(g,.08,-.04,.08,.28,.10,.14,"#DEE7DF",true);return g;
    }
}
