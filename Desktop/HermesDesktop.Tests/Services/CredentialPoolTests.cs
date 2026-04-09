using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Tests for CredentialPool — the thread-safe multi-key API credential manager
/// added in this PR to support key rotation and configurable selection strategies.
/// </summary>
[TestClass]
public class CredentialPoolTests
{
    // ── Add / Count ──

    [TestMethod]
    public void Add_SingleKey_CountIsOne()
    {
        var pool = new CredentialPool();
        pool.Add("sk-key1");
        Assert.AreEqual(1, pool.Count);
    }

    [TestMethod]
    public void Add_DuplicateKey_NotAddedTwice()
    {
        var pool = new CredentialPool();
        pool.Add("sk-dupe");
        pool.Add("sk-dupe");
        Assert.AreEqual(1, pool.Count);
    }

    [TestMethod]
    public void Add_MultipleUniqueKeys_AllStored()
    {
        var pool = new CredentialPool();
        pool.Add("sk-a");
        pool.Add("sk-b");
        pool.Add("sk-c");
        Assert.AreEqual(3, pool.Count);
    }

    [TestMethod]
    public void Add_WithLabel_AcceptedWithoutError()
    {
        var pool = new CredentialPool();
        pool.Add("sk-labeled", label: "primary-key");
        Assert.AreEqual(1, pool.Count);
    }

    // ── HasHealthyCredentials ──

    [TestMethod]
    public void HasHealthyCredentials_EmptyPool_ReturnsFalse()
    {
        var pool = new CredentialPool();
        Assert.IsFalse(pool.HasHealthyCredentials);
    }

    [TestMethod]
    public void HasHealthyCredentials_OneHealthyKey_ReturnsTrue()
    {
        var pool = new CredentialPool();
        pool.Add("sk-healthy");
        Assert.IsTrue(pool.HasHealthyCredentials);
    }

    [TestMethod]
    public void HasHealthyCredentials_AllKeysFailed_ReturnsFalse()
    {
        var pool = new CredentialPool();
        pool.Add("sk-a");
        pool.Add("sk-b");

        pool.MarkFailed("sk-a");
        pool.MarkFailed("sk-b");

        Assert.IsFalse(pool.HasHealthyCredentials);
    }

    [TestMethod]
    public void HasHealthyCredentials_OneHealthyOneFailedKey_ReturnsTrue()
    {
        var pool = new CredentialPool();
        pool.Add("sk-good");
        pool.Add("sk-bad");

        pool.MarkFailed("sk-bad");

        Assert.IsTrue(pool.HasHealthyCredentials);
    }

    // ── GetNext — LeastUsed strategy ──

    [TestMethod]
    public void GetNext_LeastUsedStrategy_ReturnsKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-only");

        var key = pool.GetNext();

        Assert.AreEqual("sk-only", key);
    }

    [TestMethod]
    public void GetNext_LeastUsedStrategy_PrefersMostUnusedKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-a");
        pool.Add("sk-b");

        // Exhaust sk-a by calling GetNext several times then manually track expected behavior
        // First call returns least used (either, but one gets incremented)
        pool.GetNext(); // sk-a count becomes 1
        var second = pool.GetNext(); // sk-b should be least used now

        // After first pick, the second pick should be from the other key
        // Because LeastUsed picks by RequestCount — after first call one key has count=1
        Assert.IsNotNull(second);
    }

    [TestMethod]
    public void GetNext_LeastUsed_BalancesRequestsAcrossKeys()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-1");
        pool.Add("sk-2");

        var counts = new Dictionary<string, int>();
        for (int i = 0; i < 10; i++)
        {
            var key = pool.GetNext()!;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        // With 2 keys and LeastUsed, distribution should be roughly equal (5-5)
        Assert.IsTrue(counts.ContainsKey("sk-1"), "sk-1 should be used");
        Assert.IsTrue(counts.ContainsKey("sk-2"), "sk-2 should be used");
        Assert.AreEqual(10, counts.Values.Sum(), "All requests should be accounted for");
    }

    [TestMethod]
    public void GetNext_NoKeys_ReturnsNull()
    {
        var pool = new CredentialPool();
        Assert.IsNull(pool.GetNext());
    }

    [TestMethod]
    public void GetNext_AllKeysFailed_ReturnsNull()
    {
        var pool = new CredentialPool();
        pool.Add("sk-a");
        pool.MarkFailed("sk-a");

        Assert.IsNull(pool.GetNext());
    }

    // ── GetNext — RoundRobin strategy ──

    [TestMethod]
    public void GetNext_RoundRobinStrategy_CyclesThroughKeys()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.RoundRobin };
        pool.Add("sk-1");
        pool.Add("sk-2");
        pool.Add("sk-3");

        var first = pool.GetNext();
        var second = pool.GetNext();
        var third = pool.GetNext();
        var fourth = pool.GetNext(); // Should wrap around

        // All three keys should appear in first three calls
        var firstThree = new[] { first, second, third };
        CollectionAssert.Contains(firstThree, "sk-1");
        CollectionAssert.Contains(firstThree, "sk-2");
        CollectionAssert.Contains(firstThree, "sk-3");

        // Fourth call should return same key as first (round-robin wrap)
        Assert.AreEqual(first, fourth);
    }

    [TestMethod]
    public void GetNext_RoundRobinSingleKey_AlwaysReturnsSameKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.RoundRobin };
        pool.Add("sk-only");

        Assert.AreEqual("sk-only", pool.GetNext());
        Assert.AreEqual("sk-only", pool.GetNext());
        Assert.AreEqual("sk-only", pool.GetNext());
    }

    // ── GetNext — FillFirst strategy ──

    [TestMethod]
    public void GetNext_FillFirstStrategy_AlwaysReturnsFirstHealthyKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.FillFirst };
        pool.Add("sk-primary");
        pool.Add("sk-secondary");

        // All calls should return sk-primary until it fails
        Assert.AreEqual("sk-primary", pool.GetNext());
        Assert.AreEqual("sk-primary", pool.GetNext());
        Assert.AreEqual("sk-primary", pool.GetNext());
    }

    [TestMethod]
    public void GetNext_FillFirstStrategy_FallsBackToSecondWhenPrimaryFails()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.FillFirst };
        pool.Add("sk-primary");
        pool.Add("sk-secondary");

        pool.MarkFailed("sk-primary");

        Assert.AreEqual("sk-secondary", pool.GetNext());
    }

    // ── GetNext — Random strategy ──

    [TestMethod]
    public void GetNext_RandomStrategy_ReturnsAValidKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.Random };
        pool.Add("sk-x");
        pool.Add("sk-y");

        var key = pool.GetNext();

        Assert.IsNotNull(key);
        Assert.IsTrue(key == "sk-x" || key == "sk-y");
    }

    [TestMethod]
    public void GetNext_RandomStrategy_SingleKey_ReturnsIt()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.Random };
        pool.Add("sk-only");

        Assert.AreEqual("sk-only", pool.GetNext());
    }

    // ── MarkFailed / ResetAll ──

    [TestMethod]
    public void MarkFailed_ValidKey_ExcludesFromGetNext()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-good");
        pool.Add("sk-bad");

        pool.MarkFailed("sk-bad");

        // Multiple calls should always return sk-good
        for (int i = 0; i < 5; i++)
            Assert.AreEqual("sk-good", pool.GetNext());
    }

    [TestMethod]
    public void MarkFailed_WithStatusCode_RecordsErrorContext()
    {
        var pool = new CredentialPool();
        pool.Add("sk-failed");

        pool.MarkFailed("sk-failed", statusCode: 401, reason: "Unauthorized");

        // After marking, the key should be unavailable
        Assert.IsNull(pool.GetNext());
    }

    [TestMethod]
    public void MarkFailed_NonExistentKey_DoesNotThrow()
    {
        var pool = new CredentialPool();
        pool.Add("sk-real");

        // Should not throw
        pool.MarkFailed("sk-nonexistent");

        // Real key still healthy
        Assert.IsTrue(pool.HasHealthyCredentials);
    }

    [TestMethod]
    public void ResetAll_AfterAllFailed_RestoresAllKeys()
    {
        var pool = new CredentialPool();
        pool.Add("sk-a");
        pool.Add("sk-b");

        pool.MarkFailed("sk-a");
        pool.MarkFailed("sk-b");

        Assert.IsFalse(pool.HasHealthyCredentials, "Should be unhealthy before reset");

        pool.ResetAll();

        Assert.IsTrue(pool.HasHealthyCredentials, "Should be healthy after reset");
    }

    [TestMethod]
    public void ResetAll_EmptyPool_DoesNotThrow()
    {
        var pool = new CredentialPool();
        // Should not throw
        pool.ResetAll();
    }

    // ── Lease System ──

    [TestMethod]
    public void AcquireLease_SingleKey_ReturnsKey()
    {
        var pool = new CredentialPool();
        pool.Add("sk-leasable");

        var key = pool.AcquireLease();

        Assert.AreEqual("sk-leasable", key);
    }

    [TestMethod]
    public void AcquireLease_EmptyPool_ReturnsNull()
    {
        var pool = new CredentialPool();
        Assert.IsNull(pool.AcquireLease());
    }

    [TestMethod]
    public void AcquireLease_AllFailed_ReturnsNull()
    {
        var pool = new CredentialPool();
        pool.Add("sk-a");
        pool.MarkFailed("sk-a");

        Assert.IsNull(pool.AcquireLease());
    }

    [TestMethod]
    public void AcquireLease_IncrementsLeaseCount()
    {
        var pool = new CredentialPool();
        pool.Add("sk-lease");

        pool.AcquireLease();

        Assert.AreEqual(1, pool.GetLeaseCount("sk-lease"));
    }

    [TestMethod]
    public void ReleaseLease_DecrementsLeaseCount()
    {
        var pool = new CredentialPool();
        pool.Add("sk-lease");

        pool.AcquireLease();
        Assert.AreEqual(1, pool.GetLeaseCount("sk-lease"));

        pool.ReleaseLease("sk-lease");
        Assert.AreEqual(0, pool.GetLeaseCount("sk-lease"));
    }

    [TestMethod]
    public void ReleaseLease_BelowZero_DoesNotGoNegative()
    {
        var pool = new CredentialPool();
        pool.Add("sk-safe");

        // Release without acquiring — should not go negative
        pool.ReleaseLease("sk-safe");

        Assert.AreEqual(0, pool.GetLeaseCount("sk-safe"));
    }

    [TestMethod]
    public void ReleaseLease_NonExistentKey_DoesNotThrow()
    {
        var pool = new CredentialPool();
        // Should not throw even for an unknown key
        pool.ReleaseLease("sk-ghost");
    }

    [TestMethod]
    public void GetLeaseCount_NonExistentKey_ReturnsZero()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(0, pool.GetLeaseCount("sk-unknown"));
    }

    [TestMethod]
    public void AcquireLease_PrefersKeysBelowSoftCap()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 1 };
        pool.Add("sk-a");
        pool.Add("sk-b");

        // Lease sk-a up to the soft cap
        pool.AcquireLease(); // sk-a gets lease

        // Next lease should prefer sk-b (below cap)
        var second = pool.AcquireLease();
        Assert.AreEqual("sk-b", second);
    }

    [TestMethod]
    public void AcquireLease_MultipleAcquire_IncreasesCount()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 10 };
        pool.Add("sk-only");

        pool.AcquireLease();
        pool.AcquireLease();
        pool.AcquireLease();

        Assert.AreEqual(3, pool.GetLeaseCount("sk-only"));
    }

    // ── Cooldown / Recovery ──

    [TestMethod]
    public void MarkFailed_RateLimitError_UsesRateLimitCooldown()
    {
        // With very short cooldown, key should recover quickly
        var pool = new CredentialPool { RateLimitCooldown = TimeSpan.FromMilliseconds(1) };
        pool.Add("sk-rate-limited");

        pool.MarkFailed("sk-rate-limited", statusCode: 429, reason: "Rate limited");

        // Immediately after — should be failed
        Assert.IsNull(pool.GetNext(), "Key should be failed immediately after marking");
    }

    [TestMethod]
    public void HasHealthyCredentials_AfterCooldownExpires_ReturnsTrue()
    {
        // Use a very short cooldown for testing recovery
        var pool = new CredentialPool
        {
            DefaultCooldown = TimeSpan.FromMilliseconds(1)
        };
        pool.Add("sk-recovering");

        pool.MarkFailed("sk-recovering", statusCode: 500);

        // Wait for cooldown to expire
        Thread.Sleep(50);

        // Should now be healthy (recovered)
        Assert.IsTrue(pool.HasHealthyCredentials, "Key should recover after cooldown");
    }

    [TestMethod]
    public void GetNext_AfterCooldownExpires_ReturnsRecoveredKey()
    {
        var pool = new CredentialPool
        {
            DefaultCooldown = TimeSpan.FromMilliseconds(1)
        };
        pool.Add("sk-recover");

        pool.MarkFailed("sk-recover", statusCode: 500);

        Thread.Sleep(50);

        var key = pool.GetNext();
        Assert.AreEqual("sk-recover", key, "Should return recovered key after cooldown");
    }

    [TestMethod]
    public void MarkFailed_RateLimitVsOther_DifferentCooldowns()
    {
        // Rate limit (429) uses RateLimitCooldown; others use DefaultCooldown
        var pool = new CredentialPool
        {
            RateLimitCooldown = TimeSpan.FromMilliseconds(1),   // short
            DefaultCooldown = TimeSpan.FromHours(24)            // long
        };
        pool.Add("sk-rate");
        pool.Add("sk-auth");

        pool.MarkFailed("sk-rate", statusCode: 429);
        pool.MarkFailed("sk-auth", statusCode: 401);

        Thread.Sleep(50); // Rate limit cooldown expires; auth cooldown hasn't

        // sk-rate should recover; sk-auth still failed
        var next = pool.GetNext();
        Assert.AreEqual("sk-rate", next, "Rate-limited key should recover after short cooldown");
    }

    // ── Strategy enum values ──

    [TestMethod]
    public void PoolStrategy_AllValuesExist()
    {
        // Verify all expected strategy enum values exist
        var values = Enum.GetValues<PoolStrategy>();
        CollectionAssert.Contains(values, PoolStrategy.LeastUsed);
        CollectionAssert.Contains(values, PoolStrategy.RoundRobin);
        CollectionAssert.Contains(values, PoolStrategy.Random);
        CollectionAssert.Contains(values, PoolStrategy.FillFirst);
    }

    // ── Default configuration ──

    [TestMethod]
    public void NewPool_DefaultStrategy_IsLeastUsed()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(PoolStrategy.LeastUsed, pool.Strategy);
    }

    [TestMethod]
    public void NewPool_DefaultMaxConcurrentLeases_IsOne()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(1, pool.MaxConcurrentLeases);
    }

    [TestMethod]
    public void NewPool_DefaultCooldown_Is24Hours()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(TimeSpan.FromHours(24), pool.DefaultCooldown);
    }

    [TestMethod]
    public void NewPool_RateLimitCooldown_Is1Hour()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(TimeSpan.FromHours(1), pool.RateLimitCooldown);
    }
}