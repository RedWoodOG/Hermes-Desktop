using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.LLM;

/// <summary>
/// Bundle E.7 tests for the <see cref="SavedModelStore"/> JSON CRUD lifecycle:
/// list ordering, upsert/delete, reload-from-disk fidelity, and validation guards.
/// </summary>
[TestClass]
public class SavedModelStoreTests
{
    private string _tempDir = "";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-models-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SavedModelStore NewStore() =>
        new(_tempDir, NullLogger<SavedModelStore>.Instance);

    [TestMethod]
    public void List_EmptyStore_ReturnsEmpty()
    {
        var store = NewStore();
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public async Task Upsert_RoundTripsAcrossReload_PreservesProfile()
    {
        var store = NewStore();
        var profile = SavedModelProfile.Create("My GPT", "openai", "gpt-5.4", contextLength: 128_000);
        await store.UpsertAsync(profile);

        var reloaded = NewStore();
        var list = reloaded.List();
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("My GPT", list[0].Name);
        Assert.AreEqual("openai", list[0].Provider);
        Assert.AreEqual("gpt-5.4", list[0].ModelId);
        Assert.AreEqual(128_000, list[0].ContextLength);
        Assert.IsFalse(list[0].IsFavorite);
    }

    [TestMethod]
    public async Task List_WithFavorites_OrdersFavoritesFirst()
    {
        var store = NewStore();
        var a = SavedModelProfile.Create("Apple", "openai", "gpt-5.4", isFavorite: false);
        var b = SavedModelProfile.Create("Banana", "openai", "gpt-5.4", isFavorite: true);
        var c = SavedModelProfile.Create("Cherry", "openai", "gpt-5.4", isFavorite: false);
        await store.UpsertAsync(a);
        await store.UpsertAsync(b);
        await store.UpsertAsync(c);

        var list = store.List();
        Assert.AreEqual(3, list.Count);
        Assert.AreEqual("Banana", list[0].Name);
        Assert.AreEqual("Apple", list[1].Name);
        Assert.AreEqual("Cherry", list[2].Name);
    }

    [TestMethod]
    public async Task Upsert_SameId_ReplacesExistingEntry()
    {
        var store = NewStore();
        var p = SavedModelProfile.Create("Original", "openai", "gpt-5.4");
        await store.UpsertAsync(p);

        var updated = p with { Name = "Renamed", ModelId = "gpt-5.4-mini" };
        await store.UpsertAsync(updated);

        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual("Renamed", store.Get(p.Id)!.Name);
        Assert.AreEqual("gpt-5.4-mini", store.Get(p.Id)!.ModelId);
    }

    [TestMethod]
    public async Task Delete_ExistingProfile_RemovesAndPersists()
    {
        var store = NewStore();
        var p = SavedModelProfile.Create("Doomed", "openai", "gpt-5.4");
        await store.UpsertAsync(p);

        Assert.IsTrue(await store.DeleteAsync(p.Id));

        var reloaded = NewStore();
        Assert.AreEqual(0, reloaded.List().Count);
        Assert.IsNull(reloaded.Get(p.Id));
    }

    [TestMethod]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        var store = NewStore();
        Assert.IsFalse(await store.DeleteAsync(Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task Upsert_EmptyName_ThrowsArgument()
    {
        var store = NewStore();
        var bad = SavedModelProfile.Create("", "openai", "gpt-5.4");
        await store.UpsertAsync(bad);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task Upsert_EmptyProvider_ThrowsArgument()
    {
        var store = NewStore();
        var bad = SavedModelProfile.Create("X", "", "gpt-5.4");
        await store.UpsertAsync(bad);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task Upsert_EmptyModelId_ThrowsArgument()
    {
        var store = NewStore();
        var bad = SavedModelProfile.Create("X", "openai", "");
        await store.UpsertAsync(bad);
    }

    [TestMethod]
    public async Task Upsert_OnDisk_WritesValidJsonFile()
    {
        var store = NewStore();
        var p = SavedModelProfile.Create("Pretty", "anthropic", "claude-haiku-4.5", isFavorite: true);
        await store.UpsertAsync(p);

        var path = Path.Combine(_tempDir, "saved-models.json");
        Assert.IsTrue(File.Exists(path));
        var raw = File.ReadAllText(path);
        Assert.IsTrue(raw.Contains("\"profiles\""));
        Assert.IsTrue(raw.Contains("\"Pretty\""));
    }
}
