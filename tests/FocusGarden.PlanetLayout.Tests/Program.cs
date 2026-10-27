using FocusGarden.Core;
using Microsoft.Data.Sqlite;

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

Run("six catalog items have meaningful permanent unlock levels", () =>
{
    Equal(6, PlanetLayoutCatalog.Entries.Count);
    using var store = new PlanetLayoutStore(":memory:");
    var plot = 0;
    foreach (var definition in PlanetLayoutCatalog.Entries)
    {
        Throws<InvalidOperationException>(() => store.Place("p", definition.Kind, plot, definition.RequiredLevel - 1));
        var placed = store.Place("p", definition.Kind, plot, definition.RequiredLevel);
        Equal(definition.Kind, placed.Kind); plot++;
    }
    Equal(6, store.GetBuildings("p").Count);
});

Run("barren level zero cannot place scenery or buildings", () =>
{
    using var store = new PlanetLayoutStore(":memory:");
    foreach (var item in PlanetLayoutCatalog.Entries)
        Throws<InvalidOperationException>(() => store.Place("p", item.Kind, 0, 0));
    Equal(0, store.GetBuildings("p").Count);
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

Console.WriteLine($"Planet layout: {passed} passed, {failed} failed.");
try { Directory.Delete(testDirectory, recursive: true); } catch { }
Environment.ExitCode = failed == 0 ? 0 : 1;
