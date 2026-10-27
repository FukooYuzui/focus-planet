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
        NatureStem(g,new(0,.012,0),new(.012,.29,-.006),.035,"#947654");
        NatureStem(g,new(.005,.15,0),new(-.10,.33,.025),.021,"#947654");
        NatureStem(g,new(.008,.19,-.004),new(.10,.37,-.045),.018,"#A2865C");
        NatureStem(g,new(.006,.22,0),new(-.025,.40,.085),.014,"#A2865C");
        for(int root=0;root<3;root++)
        {
            double a=root*2*Math.PI/3+.25;
            NatureStem(g,new(0,.055,0),new(Math.Cos(a)*.075,.008,Math.Sin(a)*.065),.018,"#8B7154");
        }
        if(kind==0)
        {
            NatureNeedleTier(g,.18,.26,.14,-.012,"#579277");
            NatureNeedleTier(g,.145,.24,.30,.012,"#70A68A");
            NatureNeedleTier(g,.095,.21,.46,.018,"#81B49A");
        }
        else
        {
            string leaf=kind==1?"#8CB883":"#D3B89A";string light=kind==1?"#A5C795":"#E0C6AC";
            NatureCanopy(g,new(-.10,.32,-.025),new(.14,.135,.12),leaf);
            NatureCanopy(g,new(.10,.35,-.035),new(.13,.14,.125),leaf);
            NatureCanopy(g,new(-.045,.405,.085),new(.13,.12,.11),light);
            NatureCanopy(g,new(.045,.435,-.085),new(.115,.13,.105),leaf);
            NatureCanopy(g,new(0,.515,.015),new(.12,.11,.11),light);
            foreach(var p in new[]{new Point3D(-.145,.35,.08),new(.15,.38,.045),new(.025,.51,.12)})
                NatureRound(g,p.X,p.Y,p.Z,.025,.026,.025,kind==1?"#DEB475":"#E9C9BA");
        }
        NatureRound(g,-.05,.012,.05,.08,.017,.06,"#7DA079");return ScaleGroup(g,scale);
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
        var g=new Model3DGroup();
        foreach(double x in new[]{-.085,.078})foreach(double z in new[]{-.052,.052})
        {
            NatureStem(g,new(x,.095,z),new(x+.007,.020,z),.016,"#9D937A");
            DetailBox(g,x-.013,.004,z-.015,.033,.025,.030,"#665E50");
        }
        DetailRound(g,-.008,.14,0,.145,.094,.096,"#DED9C3",true);
        // A close scalloped fleece layer follows the torso, instead of raising three large balls above it.
        foreach(var p in new[]{new Point3D(-.095,.185,-.035),new(-.030,.215,-.035),new(.045,.208,-.025),
            new(-.100,.145,.067),new(-.022,.168,.085),new(.060,.163,.073),new(-.040,.165,-.080),new(.070,.150,-.067)})
            DetailRound(g,p.X,p.Y,p.Z,.062,.049,.056,p.Z>0?"#F1EAD7":"#E8E1CB");
        DetailRound(g,.145,.167,0,.061,.065,.054,"#8F8D7B");
        NatureRound(g,.190,.142,0,.044,.030,.039,"#B3A990",1);
        foreach(int side in new[]{-1,1})
        {
            NatureBlade(g,new(.127,.207,side*.045),new(.139,.213,side*.101),new(.025,0,0),.008,"#B8AD93");
            NatureBlade(g,new(.129,.215,side*.055),new(.138,.218,side*.091),new(.014,0,0),.003,"#D8C4AD");
            NatureRound(g,.223,.148,side*.017,.006,.005,.005,"#756E5D");
        }
        DetailBeam(g,new(.226,.133,-.014),new(.226,.132,.014),.003,"#80715E");
        AnimalEye(g,.184,.185,.048);AnimalEye(g,.184,.185,-.048);
        DetailRound(g,-.153,.155,0,.038,.041,.037,"#EFE6CD");return g;
    }
    private static Model3DGroup SoftRabbit()
    {
        var g=new Model3DGroup();DetailRound(g,-.035,.092,0,.093,.098,.077,"#D7C8AE",true);
        DetailRound(g,.063,.164,0,.061,.065,.054,"#E9D8BE",true);
        foreach(int side in new[]{-1,1})
        {
            double z=side*.031;
            double earLean=side>0?8:13;
            NatureTiltRound(g,new(.035,.282,z),new(.023,.086,.021),"#D6C5AD",new(0,0,1),earLean);
            NatureTiltRound(g,new(.052,.290,z),new(.0085,.062,.0125),"#D5AAA6",new(0,0,1),earLean);
            DetailRound(g,-.060,.067,side*.046,.055,.060,.043,"#CCB99E");
            DetailRound(g,.008,.019,side*.056,.062,.024,.030,"#E1D0B7");
            for(int toe=0;toe<2;toe++)DetailBeam(g,new(.043,.037,side*.056+(toe-.5)*.015),new(.060,.031,side*.056+(toe-.5)*.015),.0028,"#BDA68E");
            NatureStem(g,new(.063,.103,side*.028),new(.075,.025,side*.032),.013,"#D9C8AE");
            NatureRound(g,.085,.019,side*.031,.027,.016,.019,"#EADAC1",1);
            NatureRound(g,.115,.141,side*.017,.024,.018,.021,"#F0E1CD");
        }
        AnimalEye(g,.104,.180,.043);AnimalEye(g,.104,.180,-.043);
        NatureRound(g,.136,.150,0,.010,.008,.011,"#BE9692");
        DetailBeam(g,new(.136,.143,0),new(.137,.131,0),.003,"#9E817A");
        foreach(int side in new[]{-1,1})DetailBeam(g,new(.137,.131,0),new(.132,.128,side*.014),.0028,"#9E817A");
        DetailRound(g,-.126,.100,0,.032,.034,.032,"#F2E8D8");return g;
    }
    private static Model3DGroup Fox()
    {
        var g=new Model3DGroup();DetailRound(g,-.012,.109,0,.135,.073,.071,"#CA9470",true);
        foreach(double x in new[]{-.09,.070})foreach(double z in new[]{-.045,.045})
        {
            NatureStem(g,new(x,.110,z),new(x+.005,.025,z),.015,"#A17A5D");
            NatureRound(g,x+.014,.017,z,.028,.018,.019,"#655749");
        }
        DetailRound(g,.115,.170,0,.064,.069,.060,"#D7A17A",true);
        NatureStem(g,new(.151,.148,0),new(.229,.145,0),.026,"#E9D6B8",.039);
        NatureBlade(g,new(.057,.095,0),new(.108,.186,0),new(0,0,.045),.021,"#E9DCC0");
        foreach(int side in new[]{-1,1})
        {
            NatureFoxEar(g,side);
            NatureBlade(g,new(.126,.137,side*.047),new(.172,.153,side*.035),new(0,.019,0),.006,"#E9D5B6");
        }
        DetailRound(g,-.176,.143,.020,.135,.051,.056,"#C98F67");
        NatureTiltRound(g,new(-.294,.170,.032),new(.073,.029,.034),"#EBDCC0",new(0,0,1),-15);
        AnimalEye(g,.158,.184,.046);AnimalEye(g,.158,.184,-.046);
        NatureRound(g,.229,.145,0,.011,.010,.015,"#4E5146");
        DetailBeam(g,new(.200,.133,-.015),new(.225,.134,0),.003,"#8A6754");
        DetailBeam(g,new(.200,.133,.015),new(.225,.134,0),.003,"#8A6754");return g;
    }
    private static Model3DGroup Duck()
    {
        var g=new Model3DGroup();DetailRound(g,-.020,.077,0,.127,.071,.080,"#E7DFBA",true);
        NatureStem(g,new(.063,.092,0),new(.090,.151,0),.035,"#CFD8B8");
        DetailRound(g,.099,.154,0,.052,.057,.047,"#6D9D86",true);
        NatureBlade(g,new(.136,.137,0),new(.207,.132,0),new(0,0,.029),.009,"#DDB36E");
        foreach(int side in new[]{-1,1})
        {
            DetailRound(g,-.034,.091,side*.066,.076,.041,.022,"#C6B992");
            for(int feather=0;feather<3;feather++)
                NatureBlade(g,new(.010-feather*.013,.108-feather*.010,side*.081),new(-.109+feather*.008,.088-feather*.010,side*.084),new(0,.010,0),.004,feather%2==0?"#E2D5AF":"#B5AA84");
            NatureRound(g,.173,.141,side*.012,.0045,.003,.0035,"#8F714E");
            NatureWebFoot(g,-.018,side*.042);
            NatureBlade(g,new(-.110,.083,side*.014),new(-.174,.105,side*.027),new(0,.012,0),.011,"#D7C9A3");
        }
        DetailBeam(g,new(.159,.126,-.024),new(.198,.127,-.012),.0028,"#B88E56");
        DetailBeam(g,new(.159,.126,.024),new(.198,.127,.012),.0028,"#B88E56");
        AnimalEye(g,.126,.169,.039);AnimalEye(g,.126,.169,-.039);return g;
    }
    private static Model3DGroup Deer()
    {
        var g=new Model3DGroup();DetailRound(g,-.014,.166,0,.139,.073,.070,"#B89775",true);
        foreach(double x in new[]{-.095,.070})foreach(double z in new[]{-.045,.045})
        {
            NatureStem(g,new(x,.164,z),new(x+.008,.023,z),.013,"#A58A6C");
            DetailBox(g,x-.004,.004,z-.012,.028,.023,.024,"#655E51");
        }
        DetailRound(g,.103,.237,0,.044,.107,.043,"#C3A280");
        DetailRound(g,.146,.312,0,.060,.054,.044,"#CBAE89",true);
        NatureStem(g,new(.173,.296,0),new(.224,.285,0),.023,"#DFCAAA",.032);
        NatureRound(g,.224,.285,0,.011,.010,.018,"#615748");
        NatureBlade(g,new(.080,.164,0),new(.110,.270,0),new(0,0,.026),.012,"#E3D3B3");
        foreach(int side in new[]{-1,1})
        {
            NatureBlade(g,new(.105,.344,side*.028),new(.086,.380,side*.098),new(.030,0,0),.009,"#AD8E6B");
            NatureBlade(g,new(.108,.352,side*.040),new(.093,.374,side*.084),new(.017,0,0),.003,"#DCC6A7");
            NatureStem(g,new(.120,.352,side*.025),new(.108,.410,side*.037),.010,"#9D8869");
            NatureStem(g,new(.108,.410,side*.037),new(.077,.466,side*.047),.007,"#B3A082");
            NatureStem(g,new(.109,.403,side*.035),new(.060,.430,side*.073),.006,"#A59174");
            NatureStem(g,new(.096,.433,side*.041),new(.126,.461,side*.071),.005,"#BAA88A");
            foreach(var p in new[]{new Point3D(-.088,.186,side*.064),new(-.035,.194,side*.068),new(.020,.184,side*.067),new(-.061,.154,side*.071)})
                NatureRound(g,p.X,p.Y,p.Z,.013,.010,.005,"#EADBBE");
        }
        NatureBlade(g,new(-.143,.176,0),new(-.188,.204,0),new(0,0,.017),.009,"#E3D5B9");
        AnimalEye(g,.172,.325,.037);AnimalEye(g,.172,.325,-.037);
        DetailBeam(g,new(.205,.274,-.013),new(.224,.277,0),.0028,"#90785E");
        DetailBeam(g,new(.205,.274,.013),new(.224,.277,0),.0028,"#90785E");return g;
    }
    private static void AnimalEye(Model3DGroup g,double x,double y,double z)
    {
        DetailRound(g,x,y,z,.009,.011,.007,"#394B42");
        NatureRound(g,x+.003,y+.003,z>0?z+.005:z-.005,.003,.004,.003,"#EFE8D2");
    }

    // Small highlights and markings share the existing 20-face sphere; silhouettes use the larger meshes.
    private static void NatureRound(Model3DGroup group,double x,double y,double z,double rx,double ry,double rz,string color,int detail=0)
    {
        var child=new Model3DGroup();Add(child,PlanetView3DGeometry.SmoothUnitSphere(detail),color);
        var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(rx,ry,rz));transform.Children.Add(new TranslateTransform3D(x,y,z));
        transform.Freeze();child.Transform=transform;group.Children.Add(child);
    }

    private static void NatureTiltRound(Model3DGroup group,Point3D center,Vector3D size,string color,Vector3D axis,double angle)
    {
        var child=new Model3DGroup();Add(child,PlanetView3DGeometry.SmoothUnitSphere(1),color);
        var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(size));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis,angle)));
        transform.Children.Add(new TranslateTransform3D((Vector3D)center));transform.Freeze();child.Transform=transform;group.Children.Add(child);
    }

    private static readonly MeshGeometry3D NatureFoxEarMesh=CreateNatureFoxEarMesh();
    private static MeshGeometry3D CreateNatureFoxEarMesh()
    {
        // Keep an ellipsoid's rounded volume, tapering the upper half into a soft triangular ear.
        var mesh=PlanetView3DGeometry.SmoothUnitSphere(1).Clone();
        for(int i=0;i<mesh.Positions.Count;i++)
        {
            var p=mesh.Positions[i];mesh.Positions[i]=new(p.X*(.78-.28*p.Y),p.Y,p.Z*(.92-.15*p.Y));
        }
        mesh.Freeze();return mesh;
    }
    private static void NatureFoxEar(Model3DGroup group,int side)
    {
        var child=new Model3DGroup();Add(child,NatureFoxEarMesh,"#C68D67");
        var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(.040,.046,.021));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new(1,0,0),side*12)));
        transform.Children.Add(new TranslateTransform3D(.100,.247,side*.044));transform.Freeze();child.Transform=transform;group.Children.Add(child);
        NatureTiltRound(group,new(.106,.251,side*.060),new(.015,.030,.005),"#8F6A59",new(1,0,0),side*12);
    }

    private static readonly MeshGeometry3D NatureTaperMesh=PlanetView3DGeometry.Frustum(1,.42,1,6);
    private static void NatureStem(Model3DGroup group,Point3D start,Point3D end,double radius,string color,double? secondRadius=null)
    {
        var direction=end-start;double length=direction.Length;direction.Normalize();
        var axis=Vector3D.CrossProduct(new(0,1,0),direction);if(axis.LengthSquared<1e-12)axis=new(1,0,0);
        var child=new Model3DGroup();Add(child,NatureTaperMesh,color);
        var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(radius,length,secondRadius??radius));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis,Vector3D.AngleBetween(new(0,1,0),direction))));
        transform.Children.Add(new TranslateTransform3D((Vector3D)start));transform.Freeze();child.Transform=transform;group.Children.Add(child);
    }

    // Twelve closed faces form an ear, feather or fur tip with a pointed silhouette and a shallow central ridge.
    private static void NatureBlade(Model3DGroup group,Point3D root,Point3D tip,Vector3D width,double depth,string color)
    {
        var direction=tip-root;var normal=Vector3D.CrossProduct(direction,width);normal.Normalize();
        Point3D[] edge=[root,root+direction*.28+width*.82,root+direction*.63+width,tip,
            root+direction*.63-width,root+direction*.28-width*.82];
        var center=root+direction*.47;var front=center+normal*depth;var back=center-normal*depth;var mesh=new MeshGeometry3D();
        for(int i=0;i<edge.Length;i++)
        {
            NatureFace(mesh,edge[i],front,edge[(i+1)%edge.Length],center);
            NatureFace(mesh,edge[i],edge[(i+1)%edge.Length],back,center);
        }
        mesh.Freeze();Add(group,mesh,color);
    }
    private static void NatureFace(MeshGeometry3D mesh,Point3D a,Point3D b,Point3D c,Point3D center)
    {
        var midpoint=new Point3D((a.X+b.X+c.X)/3,(a.Y+b.Y+c.Y)/3,(a.Z+b.Z+c.Z)/3);
        if(Vector3D.DotProduct(Vector3D.CrossProduct(b-a,c-a),midpoint-center)<0)(b,c)=(c,b);
        PlanetView3DGeometry.Triangle(mesh,a,b,c);
    }
    private static void NatureWebFoot(Model3DGroup group,double x,double z)
    {
        (double X,double Z)[] outline=[(-.018,-.012),(.030,-.033),(.062,-.029),(.049,-.009),(.068,0),(.049,.009),(.062,.029),(.030,.033),(-.018,.012)];
        var mesh=new MeshGeometry3D();var center=new Point3D(x+.02,.007,z);
        for(int i=0;i<outline.Length;i++)
        {
            var a=outline[i];var b=outline[(i+1)%outline.Length];
            Point3D p=new(x+a.X,.012,z+a.Z),q=new(x+b.X,.012,z+b.Z),r=new(x+a.X,.002,z+a.Z),s=new(x+b.X,.002,z+b.Z);
            NatureFace(mesh,new(x+.02,.012,z),p,q,center);NatureFace(mesh,new(x+.02,.002,z),s,r,center);
            NatureFace(mesh,p,r,s,center);NatureFace(mesh,p,s,q,center);
        }
        mesh.Freeze();Add(group,mesh,"#CDA165");
    }

    private static readonly MeshGeometry3D NatureCanopyMesh=CreateNatureCanopyMesh();
    private static MeshGeometry3D CreateNatureCanopyMesh()
    {
        var mesh=PlanetView3DGeometry.SmoothUnitSphere(1).Clone();
        for(int i=0;i<mesh.Positions.Count;i++)
        {
            var p=mesh.Positions[i];double ripple=1+.075*Math.Sin(p.X*5+p.Z*2)*Math.Cos(p.Y*4-p.Z*3);
            mesh.Positions[i]=new(p.X*ripple,p.Y*(1+.035*Math.Sin(p.X*4-p.Z*3)),p.Z*ripple);
        }
        mesh.Freeze();return mesh;
    }
    private static void NatureCanopy(Model3DGroup group,Point3D center,Vector3D size,string color)
    {
        var child=new Model3DGroup();Add(child,NatureCanopyMesh,color);
        var transform=new Transform3DGroup();transform.Children.Add(new ScaleTransform3D(size));transform.Children.Add(new TranslateTransform3D((Vector3D)center));
        transform.Freeze();child.Transform=transform;group.Children.Add(child);
    }
    private static void NatureNeedleTier(Model3DGroup group,double radius,double height,double y,double lean,string color)
    {
        var mesh=new MeshGeometry3D();double[] levels=[0,.12,.46,.78];double[] radii=[.88,1,.64,.30];const int sides=10;
        Point3D Ring(int ring,int side)
        {
            double a=(side%sides)*2*Math.PI/sides;double ripple=1+.06*Math.Sin(a*3+.8);
            return new(Math.Cos(a)*radius*radii[ring]*ripple+lean*levels[ring]*levels[ring],y+height*levels[ring],Math.Sin(a)*radius*radii[ring]*ripple);
        }
        for(int ring=0;ring<levels.Length-1;ring++)for(int i=0;i<sides;i++)
        {
            var p=Ring(ring,i);var q=Ring(ring,i+1);var r=Ring(ring+1,i);var s=Ring(ring+1,i+1);
            NatureFace(mesh,p,r,q,new(0,y+height*.3,0));NatureFace(mesh,q,r,s,new(0,y+height*.3,0));
        }
        for(int i=0;i<sides;i++)
        {
            NatureFace(mesh,Ring(0,i),new(0,y,0),Ring(0,i+1),new(0,y+height*.3,0));
            NatureFace(mesh,Ring(3,i),new(lean,y+height,0),Ring(3,i+1),new(0,y+height*.3,0));
        }
        mesh.Freeze();Add(group,mesh,color);
    }
    private static Model3DGroup SoftCloud()
    {
        var g=new Model3DGroup();DetailRound(g,-.23,0,.01,.22,.12,.16,"#DCE8E3",true);DetailRound(g,-.02,.07,0,.27,.19,.21,"#F0F0E4",true);DetailRound(g,.23,.02,0,.21,.14,.17,"#E8ECE2",true);DetailRound(g,.08,-.04,.08,.28,.10,.14,"#DEE7DF",true);return g;
    }
}
