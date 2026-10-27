using FocusGarden.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

var passed = 0;
var failed = 0;
var testDirectory = Path.Combine(Path.GetTempPath(), "FocusPlanet-layout-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);

void Run(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
}
void Throws<T>(Action body) where T : Exception
{
    try { body(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

Run("placement position and rotation survive reopening", () =>
{
    var db = Path.Combine(testDirectory, "persistence.db");
    string id;
    using (var store = new PlanetLayoutStore(db))
    {
        var house = store.Place("earth", "house", 31, 3);
        id = house.Id;
        Equal(31, house.PlotId); Equal(0, house.QuarterTurns);
        store.Rotate("earth", id); store.Rotate("earth", id);
        store.Move("earth", id, 3);
    }
    using var reopened = new PlanetLayoutStore(db);
    Equal(new PlanetBuildingPlacement(id, "house", 3, 2), reopened.GetBuildings("earth").Single());
});

Run("eighteen catalog items preserve original kinds and enforce permanent unlock levels", () =>
{
    Equal(18, PlanetLayoutCatalog.Entries.Count);
    Equal("tree,flower,house,windmill,tower,observatory", string.Join(",", PlanetLayoutCatalog.Entries.Take(6).Select(x => x.Kind)));
    Equal("1:12,1:12,3:8,4:3,4:2,5:1", string.Join(",", PlanetLayoutCatalog.Entries.Take(6).Select(x => $"{x.RequiredLevel}:{x.MaxCount}")));
    Equal("建筑", new PlanetBuildingDefinition("compatible", "兼容旧构造器", 1, 1).Category);
    Equal(6, PlanetLayoutCatalog.Entries.Count(x => x.Category == "生物"));
    Equal(8, PlanetLayoutCatalog.Entries.Count(x => x.Category == "景观"));
    Equal(4, PlanetLayoutCatalog.Entries.Count(x => x.Category == "建筑"));
    Equal(false, PlanetLayoutCatalog.Entries.Any(x => string.IsNullOrWhiteSpace(x.Hint)));
    using var store = new PlanetLayoutStore(":memory:");
    var plot = 0;
    foreach (var definition in PlanetLayoutCatalog.Entries)
    {
        if (definition.RequiredLevel > 0)
            Throws<InvalidOperationException>(() => store.Place("p", definition.Kind, plot, definition.RequiredLevel - 1));
        var placed = store.Place("p", definition.Kind, plot, definition.RequiredLevel);
        Equal(definition.Kind, placed.Kind); plot++;
    }
    Equal(18, store.GetBuildings("p").Count);
});

Run("barren level zero can place rock gardens while other items remain locked", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    foreach (var item in PlanetLayoutCatalog.Entries.Where(x => x.Kind != "rockgarden"))
        Throws<InvalidOperationException>(() => store.Place("p", item.Kind, 0, 0));
    Equal("rockgarden", store.Place("p", "rockgarden", 0, 0).Kind);
    Equal(1, store.GetBuildings("p").Count);
});

Run("new animals and scenery match their specific unlocks and inventory caps", () =>
{
    foreach (var (kind, level, cap) in new (string,int,int)[]
    {
        ("rabbit",2,3),("sheep",2,3),("fox",2,2),("duck",2,3),("deer",2,2),("butterfly",2,3),
        ("pond",1,3),("meadow",1,6),("mushroom",1,6),("orchard",1,4),("rockgarden",0,4),("bush",1,6)
    })
    {
        var item=PlanetLayoutCatalog.Get(kind); Equal(level,item.RequiredLevel); Equal(cap,item.MaxCount);
        Equal(level==2,PlanetLayoutCatalog.IsAnimal(kind));
    }
});

Run("animal predicate safely rejects natural scene kinds and unknown values", () =>
{
    Equal(true,PlanetLayoutCatalog.IsAnimal(" RABBIT "));
    foreach(var kind in new string?[]{null,"","  ","mountain","cloud","rocks","shrub","flowers","shore","unknown","house","tree"})
        Equal(false,PlanetLayoutCatalog.IsAnimal(kind));
});

Run("animals share a twelve-per-planet limit while scenery stays independent", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    var plot=0;
    foreach (var (kind,count) in new[] { ("rabbit",3),("sheep",3),("fox",2),("duck",3),("deer",1) })
        for(var i=0;i<count;i++) store.Place("p",kind,plot++,2);
    Equal(12,store.GetBuildings("p").Count);
    Throws<InvalidOperationException>(()=>store.Place("p","deer",12,2));
    Throws<InvalidOperationException>(()=>store.Place("p","butterfly",12,2));
    store.Place("p","pond",12,2);
    store.Place("another","butterfly",12,2);
    var rabbit=store.GetBuildings("p").First(x=>x.Kind=="rabbit");
    Equal(true,store.Remove("p",rabbit.Id));
    store.Place("p","butterfly",rabbit.PlotId,2);
    Equal(12,store.GetBuildings("p").Count(x=>PlanetLayoutCatalog.IsAnimal(x.Kind)));
    Equal(13,store.GetBuildings("p").Count);
});

Run("all new types retain identifiers positions and rotations after moving and reopening", () =>
{
    var db=Path.Combine(testDirectory,"new-types.db");
    var expected=new Dictionary<string,PlanetBuildingPlacement>();
    using(var store=new PlanetLayoutStore(db))
        foreach(var item in PlanetLayoutCatalog.Entries.Skip(6))
        {
            var p=store.Place(item.Kind,item.Kind,0,item.RequiredLevel);
            store.Move(item.Kind,p.Id,31);store.Rotate(item.Kind,p.Id);
            expected[item.Kind]=p with {PlotId=31,QuarterTurns=1};
        }
    using(var store=new PlanetLayoutStore(db))
        foreach(var (planet,p) in expected) Equal(p,store.GetBuildings(planet).Single());
    Equal(PlanetLayoutStore.SchemaVersion,Convert.ToInt32(Scalar(db,"PRAGMA user_version")));
    Equal(false,Directory.Exists(Path.Combine(testDirectory,"backups")));
});

Run("occupied destination rejects placement without changing prior object", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    var tree = store.Place("p", "tree", 0, 1);
    Throws<InvalidOperationException>(() => store.Place("p", "house", 0, 5));
    Equal(tree, store.GetBuildings("p").Single());
});

Run("failed move preserves both objects and a move to the same plot is harmless", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    var tree = store.Place("p", "tree", 0, 1);
    var flower = store.Place("p", "flower", 1, 1);
    Throws<InvalidOperationException>(() => store.Move("p", tree.Id, 1));
    Equal(tree, store.GetBuildings("p")[0]); Equal(flower, store.GetBuildings("p")[1]);
    Equal(tree, store.Move("p", tree.Id, 0));
    Equal(tree with { PlotId = 2 }, store.Move("p", tree.Id, 2));
});

Run("rotation is a four-step cycle and does not change identity or position", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    var house = store.Place("p", "house", 3, 3);
    for (var rotation = 1; rotation <= 12; rotation++)
        Equal(house with { QuarterTurns = rotation % 4 }, store.Rotate("p", house.Id));
    Equal(house, store.GetBuildings("p").Single());
});

Run("same plot on different planets is independent and cannot move foreign IDs", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    var old = store.Place("old", "house", 0, 3);
    var fresh = store.Place("new", "tree", 0, 1);
    Throws<KeyNotFoundException>(() => store.Move("new", old.Id, 1));
    Throws<KeyNotFoundException>(() => store.Rotate("new", old.Id));
    Equal(false, store.Remove("new", old.Id));
    Equal(old, store.GetBuildings("old").Single()); Equal(fresh, store.GetBuildings("new").Single());
});

Run("per-planet inventory limit cannot be bypassed and removal permits replacement", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    for (var plot = 0; plot < 12; plot++) store.Place("p", "tree", plot, 1);
    Throws<InvalidOperationException>(() => store.Place("p", "tree", 12, 5));
    var first = store.GetBuildings("p")[0];
    Equal(true, store.Remove("p", first.Id)); Equal(false, store.Remove("p", first.Id));
    store.Place("p", "tree", 12, 1);
    Equal(12, store.GetBuildings("p").Count);
    store.Place("another", "tree", 12, 1);
    Equal(1, store.GetBuildings("another").Count);
});

Run("all catalog caps are enforced", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    foreach (var item in PlanetLayoutCatalog.Entries)
    {
        for (var plot = 0; plot < item.MaxCount; plot++) store.Place(item.Kind, item.Kind, plot, 5);
        Throws<InvalidOperationException>(() => store.Place(item.Kind, item.Kind, item.MaxCount, 5));
        Equal(item.MaxCount, store.GetBuildings(item.Kind).Count);
    }
});

Run("invalid plots types levels identifiers and unknown objects are rejected", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    Throws<ArgumentOutOfRangeException>(() => store.Place("p", "tree", -1, 1));
    Throws<ArgumentOutOfRangeException>(() => store.Place("p", "tree", 32, 1));
    Throws<ArgumentException>(() => store.Place("p", "shell", 0, 5));
    Throws<ArgumentOutOfRangeException>(() => store.Place("p", "tree", 0, 6));
    Throws<ArgumentOutOfRangeException>(() => store.Place("p", "tree", 0, -1));
    Throws<ArgumentException>(() => store.GetBuildings(""));
    Throws<ArgumentException>(() => store.GetBuildings("\np"));
    Throws<ArgumentException>(() => store.GetBuildings(new string('a', 129)));
    Throws<KeyNotFoundException>(() => store.Move("p", "unknown", 0));
    Throws<KeyNotFoundException>(() => store.Rotate("p", "unknown"));
    Equal(0, store.GetBuildings("p").Count);
});

Run("parameterized identifiers are inert and kind normalization is predictable", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    const string planet = "p'); DROP TABLE planet_buildings; --";
    var tree = store.Place(planet, " TREE ", 0, 1);
    Equal("tree", tree.Kind); Equal(tree, store.GetBuildings(planet).Single());
    Equal(0, store.GetBuildings("p").Count);
    store.Place("p", "house", 0, 3);
});

Run("concurrent connections cannot duplicate a plot or exceed one observatory", () =>
{
    var db = Path.Combine(testDirectory, "concurrency.db");
    using var first = new PlanetLayoutStore(db);
    using var second = new PlanetLayoutStore(db);
    var results = new List<bool>();
    Parallel.ForEach(new[] { first, second }, store =>
    {
        var success = false;
        try { store.Place("p", "house", 0, 5); success = true; }
        catch (InvalidOperationException) { }
        lock (results) results.Add(success);
    });
    Equal(1, results.Count(x => x)); Equal(1, first.GetBuildings("p").Count);
    results.Clear();
    Parallel.For(0, 2, index =>
    {
        var success = false;
        try { (index == 0 ? first : second).Place("p", "observatory", index + 1, 5); success = true; }
        catch (InvalidOperationException) { }
        lock (results) results.Add(success);
    });
    Equal(1, results.Count(x => x)); Equal(1, first.GetBuildings("p").Count(x => x.Kind == "observatory"));
});

Run("database constraints protect plot bounds rotation and occupied plots", () =>
{
    var db = Path.Combine(testDirectory, "constraints.db");
    using var store = new PlanetLayoutStore(db);
    store.Place("p", "tree", 0, 1);
    using var connection = new SqliteConnection($"Data Source={db};Pooling=False"); connection.Open();
    void Insert(string id, int plot, int rotation)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO planet_buildings VALUES('p',$id,'tree',$plot,$rotation)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$plot", plot); command.Parameters.AddWithValue("$rotation", rotation);
        command.ExecuteNonQuery();
    }
    Throws<SqliteException>(() => Insert("duplicate", 0, 0));
    Throws<SqliteException>(() => Insert("bounds", 32, 0));
    Throws<SqliteException>(() => Insert("rotation", 1, 4));
    Equal(1, store.GetBuildings("p").Count);
});

Run("dispose is idempotent and access after disposal is rejected", () =>
{
    var store = new PlanetLayoutStore(":memory:");
    store.Dispose(); store.Dispose();
    Throws<ObjectDisposedException>(() => store.GetBuildings("p"));
    Throws<ObjectDisposedException>(() => store.Place("p", "tree", 0, 1));
});

Run("legacy version zero fixture migrates every old row with an independently readable backup", () =>
{
    var folder=Path.Combine(testDirectory,"legacy");Directory.CreateDirectory(folder);
    var db=Path.Combine(folder,"planet-layout.db");CreateLegacy(db);
    var before=Rows(db);var oldSchema=Convert.ToString(Scalar(db,"SELECT sql FROM sqlite_master WHERE name='planet_buildings'"));
    using(var migrated=new PlanetLayoutStore(db))
    {
        Equal(before,Rows(db));
        Equal(1,Convert.ToInt32(Scalar(db,"PRAGMA user_version")));
        Equal("rabbit",migrated.Place("legacy-a","rabbit",12,2).Kind);
    }
    var backup=Directory.GetFiles(Path.Combine(folder,"backups"),"*.db").Single();
    Equal(before,Rows(backup));Equal(0,Convert.ToInt32(Scalar(backup,"PRAGMA user_version")));
    Equal(oldSchema,Convert.ToString(Scalar(backup,"SELECT sql FROM sqlite_master WHERE name='planet_buildings'")));
    Equal("delete",Convert.ToString(Scalar(backup,"PRAGMA journal_mode")));
    Equal(false,File.Exists(backup+"-wal"));Equal(false,File.Exists(backup+"-shm"));
    using(var reopened=new PlanetLayoutStore(db)) Equal(4,reopened.GetBuildings("legacy-a").Count);
    Equal(1,Directory.GetFiles(Path.Combine(folder,"backups"),"*.db").Length);
});

Run("legacy WAL-only committed rows are copied into the layout migration backup", () =>
{
    var folder=Path.Combine(testDirectory,"legacy-wal");Directory.CreateDirectory(folder);
    var db=Path.Combine(folder,"planet-layout.db");CreateLegacy(db);
    using var writer=Open(db);
    Execute(writer,"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE);");
    Execute(writer,"INSERT INTO planet_buildings VALUES('wal-only','stable-wal-id','tree',30,3)");
    Equal(true,File.Exists(db+"-wal"));
    var before=Rows(db);
    using(var store=new PlanetLayoutStore(db)) Equal(before,Rows(db));
    var backup=Directory.GetFiles(Path.Combine(folder,"backups"),"*.db").Single();
    Equal(before,Rows(backup));
    Equal(1L,(long)Scalar(backup,"SELECT COUNT(*) FROM planet_buildings WHERE id='stable-wal-id'")!);
});

Run("layout migration failure after copying rolls back schema rows and version", () =>
{
    var folder=Path.Combine(testDirectory,"rollback");Directory.CreateDirectory(folder);
    var db=Path.Combine(folder,"planet-layout.db");CreateLegacy(db);
    // SQLite validates dependent schema on ALTER TABLE. This intentionally broken view
    // makes the rename fail after the new table was filled and the old table was dropped.
    using(var c=Open(db)) Execute(c,"CREATE VIEW broken_fixture_view AS SELECT * FROM deliberately_missing_table;");
    var before=Rows(db);var schema=Convert.ToString(Scalar(db,"SELECT sql FROM sqlite_master WHERE name='planet_buildings'"));
    Throws<SqliteException>(()=>{using var store=new PlanetLayoutStore(db);});
    Equal(before,Rows(db));Equal(0,Convert.ToInt32(Scalar(db,"PRAGMA user_version")));
    Equal(schema,Convert.ToString(Scalar(db,"SELECT sql FROM sqlite_master WHERE name='planet_buildings'")));
    Equal(0L,(long)Scalar(db,"SELECT COUNT(*) FROM sqlite_master WHERE name='planet_buildings_v1_migration'")!);
    Equal(before,Rows(Directory.GetFiles(Path.Combine(folder,"backups"),"*.db").Single()));
});

Run("failed backup leaves legacy rows and schema version unchanged", () =>
{
    var folder=Path.Combine(testDirectory,"backup-failure");Directory.CreateDirectory(folder);
    var db=Path.Combine(folder,"planet-layout.db");CreateLegacy(db);
    File.WriteAllText(Path.Combine(folder,"backups"),"synthetic path conflict");
    var before=Rows(db);
    Throws<IOException>(()=>{using var store=new PlanetLayoutStore(db);});
    Equal(before,Rows(db));Equal(0,Convert.ToInt32(Scalar(db,"PRAGMA user_version")));
});

Run("unknown newer layout versions are rejected without touching rows or version", () =>
{
    var folder=Path.Combine(testDirectory,"unknown-version");Directory.CreateDirectory(folder);
    var db=Path.Combine(folder,"planet-layout.db");CreateLegacy(db);
    using(var c=Open(db)) Execute(c,"PRAGMA user_version=99;");
    var before=Rows(db);var bytes=File.ReadAllBytes(db);
    Throws<InvalidOperationException>(()=>{using var store=new PlanetLayoutStore(db);});
    Equal(before,Rows(db));Equal(99,Convert.ToInt32(Scalar(db,"PRAGMA user_version")));
    Equal(true,bytes.SequenceEqual(File.ReadAllBytes(db)));
    Equal(false,Directory.Exists(Path.Combine(folder,"backups")));
});

Run("layout store refuses a focus database filename without opening or changing it", () =>
{
    var folder=Path.Combine(testDirectory,"focus-guard");Directory.CreateDirectory(folder);
    var db=Path.Combine(folder,"focus.db");var bytes=new byte[]{70,111,99,117,115};File.WriteAllBytes(db,bytes);
    Throws<ArgumentException>(()=>{using var store=new PlanetLayoutStore(db);});
    Equal(true,bytes.SequenceEqual(File.ReadAllBytes(db)));
});

Run("concurrent connections enforce the shared animal cap atomically", () =>
{
    var db=Path.Combine(testDirectory,"animal-concurrency.db");
    using var first=new PlanetLayoutStore(db);using var second=new PlanetLayoutStore(db);
    var plot=0;
    foreach(var(kind,count) in new[]{("rabbit",3),("sheep",3),("fox",2),("duck",3)})
        for(var i=0;i<count;i++) first.Place("p",kind,plot++,2);
    var success=0;
    Parallel.For(0,2,index=>
    {
        try{(index==0?first:second).Place("p",index==0?"deer":"butterfly",11+index,2);Interlocked.Increment(ref success);}
        catch(InvalidOperationException){}
    });
    Equal(1,success);Equal(12,first.GetBuildings("p").Count);
});

static SqliteConnection Open(string path,bool readOnly=false)
{
    var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate}.ToString());
    c.Open();return c;
}
static void Execute(SqliteConnection c,string sql){using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
static object? Scalar(string path,string sql){using var c=Open(path,true);using var command=c.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
static string Rows(string path)
{
    using var c=Open(path,true);using var command=c.CreateCommand();command.CommandText="SELECT planet_id,id,kind,plot_id,quarter_turns FROM planet_buildings ORDER BY planet_id,id";
    using var reader=command.ExecuteReader();var rows=new List<object[]>();
    while(reader.Read()){var row=new object[reader.FieldCount];reader.GetValues(row);rows.Add(row);}
    return JsonSerializer.Serialize(rows);
}
static void CreateLegacy(string path)
{
    // Frozen original 0.4–0.6 table definition: only the six legacy kinds, user_version=0.
    using var c=Open(path);Execute(c,"""
        CREATE TABLE planet_buildings (
            planet_id TEXT NOT NULL,
            id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('tree','flower','house','windmill','tower','observatory')),
            plot_id INTEGER NOT NULL CHECK(plot_id >= 0 AND plot_id < 32),
            quarter_turns INTEGER NOT NULL CHECK(quarter_turns >= 0 AND quarter_turns < 4),
            PRIMARY KEY(planet_id,id),
            UNIQUE(planet_id,plot_id)
        );
        INSERT INTO planet_buildings VALUES
            ('legacy-a','fixed-tree-id','tree',0,0),
            ('legacy-a','fixed-flower-id','flower',1,3),
            ('legacy-a','fixed-house-id','house',31,2),
            ('legacy-b','fixed-windmill-id','windmill',3,1),
            ('legacy-b','fixed-tower-id','tower',7,2),
            ('legacy-b','fixed-observatory-id','observatory',12,3);
        PRAGMA user_version=0;
        """);
}

Console.WriteLine($"Planet layout: {passed} passed, {failed} failed.");
var safeRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
var resolved=Path.GetFullPath(testDirectory);
if(!resolved.StartsWith(safeRoot+"FocusPlanet-layout-tests-",StringComparison.OrdinalIgnoreCase)||Directory.GetParent(resolved)?.FullName!=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
    throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
try { Directory.Delete(resolved, recursive: true); } catch { }
Environment.ExitCode = failed == 0 ? 0 : 1;
