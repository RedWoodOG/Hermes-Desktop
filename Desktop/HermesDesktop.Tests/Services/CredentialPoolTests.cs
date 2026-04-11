using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Tests for CredentialPool — the thread-safe multi-key API credential manager
/// supporting key rotation and configurable selection strategies.
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

    [TestMethod]
    public void Add_SameKeyAfterFailure_StillCountedOnce()
    {
        var pool = new CredentialPool();
        pool.Add("sk-once");
        pool.MarkFailed("sk-once");
        pool.Add("sk-once"); // attempt to re-add should be no-op
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

        Assert.IsTrue(counts.ContainsKey("sk-1"), "sk-1 should be used");
        Assert.IsTrue(counts.ContainsKey("sk-2"), "sk-2 should be used");
        Assert.AreEqual(10, counts.Values.Sum(), "All requests should be accounted for");
    }

    [TestMethod]
    public void GetNext_LeastUsedStrategy_PrefersMostUnusedKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-a");
        pool.Add("sk-b");

        // First call — pick one (any), second call should pick the other
        var first = pool.GetNext();
        var second = pool.GetNext();

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first, second, "Second call should pick the other (least-used) key");
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

        var firstThree = new[] { first, second, third };
        CollectionAssert.Contains(firstThree, "sk-1");
        CollectionAssert.Contains(firstThree, "sk-2");
        CollectionAssert.Contains(firstThree, "sk-3");

        Assert.AreEqual(first, fourth, "Fourth call should wrap around to the first key");
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

    [TestMethod]
    public void GetNext_RoundRobin_SkipsFailedKeys()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.RoundRobin };
        pool.Add("sk-good");
        pool.Add("sk-bad");
        pool.MarkFailed("sk-bad");

        for (int i = 0; i < 5; i++)
            Assert.AreEqual("sk-good", pool.GetNext(), "Round-robin should skip failed keys");
    }

    // ── GetNext — FillFirst strategy ──

    [TestMethod]
    public void GetNext_FillFirstStrategy_AlwaysReturnsFirstHealthyKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.FillFirst };
        pool.Add("sk-primary");
        pool.Add("sk-secondary");

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

    [TestMethod]
    public void GetNext_FillFirst_EmptyPool_ReturnsNull()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.FillFirst };
        Assert.IsNull(pool.GetNext());
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

    [TestMethod]
    public void GetNext_RandomStrategy_NeverReturnsFailedKey()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.Random };
        pool.Add("sk-alive");
        pool.Add("sk-dead");
        pool.MarkFailed("sk-dead");

        for (int i = 0; i < 20; i++)
            Assert.AreEqual("sk-alive", pool.GetNext(), "Random should never return a failed key");
    }

    // ── MarkFailed / ResetAll ──

    [TestMethod]
    public void MarkFailed_ValidKey_ExcludesFromGetNext()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-good");
        pool.Add("sk-bad");

        pool.MarkFailed("sk-bad");

        for (int i = 0; i < 5; i++)
            Assert.AreEqual("sk-good", pool.GetNext());
    }

    [TestMethod]
    public void MarkFailed_WithStatusCode_RecordsErrorContext()
    {
        var pool = new CredentialPool();
        pool.Add("sk-failed");

        pool.MarkFailed("sk-failed", statusCode: 401, reason: "Unauthorized");

        Assert.IsNull(pool.GetNext(), "Key marked failed with 401 should be unavailable");
    }

    [TestMethod]
    public void MarkFailed_With429StatusCode_UsesRateLimitCooldown()
    {
        var pool = new CredentialPool { RateLimitCooldown = TimeSpan.FromMilliseconds(1) };
        pool.Add("sk-rate-limited");

        pool.MarkFailed("sk-rate-limited", statusCode: 429, reason: "Rate limited");

        Assert.IsNull(pool.GetNext(), "Key should be failed immediately after marking with 429");
    }

    [TestMethod]
    public void MarkFailed_NonExistentKey_DoesNotThrow()
    {
        var pool = new CredentialPool();
        pool.Add("sk-real");

        pool.MarkFailed("sk-nonexistent");

        Assert.IsTrue(pool.HasHealthyCredentials, "Real key should remain healthy");
    }

    [TestMethod]
    public void MarkFailed_AllKeys_HasHealthyCredentialsFalse()
    {
        var pool = new CredentialPool();
        pool.Add("sk-1");
        pool.Add("sk-2");
        pool.Add("sk-3");

        pool.MarkFailed("sk-1");
        pool.MarkFailed("sk-2");
        pool.MarkFailed("sk-3");

        Assert.IsFalse(pool.HasHealthyCredentials);
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
    public void ResetAll_MakesKeysAvailableInGetNext()
    {
        var pool = new CredentialPool();
        pool.Add("sk-recover");
        pool.MarkFailed("sk-recover");

        Assert.IsNull(pool.GetNext(), "Should be unavailable before reset");

        pool.ResetAll();

        Assert.AreEqual("sk-recover", pool.GetNext(), "Should be available after reset");
    }

    [TestMethod]
    public void ResetAll_EmptyPool_DoesNotThrow()
    {
        var pool = new CredentialPool();
        pool.ResetAll();
    }

    [TestMethod]
    public void ResetAll_NeverFailed_DoesNotThrow()
    {
        var pool = new CredentialPool();
        pool.Add("sk-fine");
        pool.ResetAll(); // idempotent — no-op on healthy pool
        Assert.IsTrue(pool.HasHealthyCredentials);
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
    public void AcquireLease_MultipleAcquire_IncreasesCount()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 10 };
        pool.Add("sk-only");

        pool.AcquireLease();
        pool.AcquireLease();
        pool.AcquireLease();

        Assert.AreEqual(3, pool.GetLeaseCount("sk-only"));
    }

    [TestMethod]
    public void AcquireLease_PrefersKeysBelowSoftCap()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 1 };
        pool.Add("sk-a");
        pool.Add("sk-b");

        pool.AcquireLease(); // sk-a gets lease (index 0, least used)

        // Next lease should prefer sk-b (below cap)
        var second = pool.AcquireLease();
        Assert.AreEqual("sk-b", second);
    }

    [TestMethod]
    public void AcquireLease_WhenAllAtCap_StillReturnsKey()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 1 };
        pool.Add("sk-capped");

        pool.AcquireLease(); // fills cap
        var second = pool.AcquireLease(); // above cap — still returns key

        Assert.AreEqual("sk-capped", second,
            "AcquireLease should still return a key when all credentials are at cap");
    }

    // ── ReleaseLease ──

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

        pool.ReleaseLease("sk-safe"); // release without acquiring

        Assert.AreEqual(0, pool.GetLeaseCount("sk-safe"));
    }

    [TestMethod]
    public void ReleaseLease_NonExistentKey_DoesNotThrow()
    {
        var pool = new CredentialPool();
        pool.ReleaseLease("sk-ghost");
    }

    [TestMethod]
    public void ReleaseLease_MultipleAcquireThenRelease_CountMatchesExpected()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 5 };
        pool.Add("sk-multi");

        pool.AcquireLease();
        pool.AcquireLease();
        pool.AcquireLease();

        pool.ReleaseLease("sk-multi");
        pool.ReleaseLease("sk-multi");

        Assert.AreEqual(1, pool.GetLeaseCount("sk-multi"));
    }

    // ── GetLeaseCount ──

    [TestMethod]
    public void GetLeaseCount_NonExistentKey_ReturnsZero()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(0, pool.GetLeaseCount("sk-unknown"));
    }

    [TestMethod]
    public void GetLeaseCount_BeforeAcquire_IsZero()
    {
        var pool = new CredentialPool();
        pool.Add("sk-new");
        Assert.AreEqual(0, pool.GetLeaseCount("sk-new"));
    }

    // ── Cooldown / Recovery ──

    [TestMethod]
    public void MarkFailed_RateLimitError_KeyUnavailableImmediately()
    {
        var pool = new CredentialPool { RateLimitCooldown = TimeSpan.FromMilliseconds(1) };
        pool.Add("sk-rate-limited");

        pool.MarkFailed("sk-rate-limited", statusCode: 429, reason: "Rate limited");

        Assert.IsNull(pool.GetNext(), "Key should be failed immediately after marking");
    }

    [TestMethod]
    public void HasHealthyCredentials_AfterCooldownExpires_ReturnsTrue()
    {
        var pool = new CredentialPool
        {
            DefaultCooldown = TimeSpan.FromMilliseconds(1)
        };
        pool.Add("sk-recovering");

        pool.MarkFailed("sk-recovering", statusCode: 500);

        Thread.Sleep(50);

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
        var pool = new CredentialPool
        {
            RateLimitCooldown = TimeSpan.FromMilliseconds(1),
            DefaultCooldown = TimeSpan.FromHours(24)
        };
        pool.Add("sk-rate");
        pool.Add("sk-auth");

        pool.MarkFailed("sk-rate", statusCode: 429);
        pool.MarkFailed("sk-auth", statusCode: 401);

        Thread.Sleep(50);

        var next = pool.GetNext();
        Assert.AreEqual("sk-rate", next, "Rate-limited key should recover after short cooldown");
    }

    [TestMethod]
    public void MarkFailed_DefaultCooldown_KeyStillFailedBeforeExpiry()
    {
        // Very long cooldown — key should not recover
        var pool = new CredentialPool
        {
            DefaultCooldown = TimeSpan.FromHours(24)
        };
        pool.Add("sk-stuck");

        pool.MarkFailed("sk-stuck", statusCode: 500);

        // No sleep — cooldown has not expired
        Assert.IsNull(pool.GetNext(), "Key should remain failed within cooldown window");
    }

    // ── Strategy enum values ──

    [TestMethod]
    public void PoolStrategy_AllValuesExist()
    {
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

    [TestMethod]
    public void NewPool_Count_IsZero()
    {
        var pool = new CredentialPool();
        Assert.AreEqual(0, pool.Count);
    }

    // ── Thread safety boundary test ──

    [TestMethod]
    public void GetNext_ConcurrentCalls_DoNotThrow()
    {
        var pool = new CredentialPool { Strategy = PoolStrategy.LeastUsed };
        pool.Add("sk-concurrent-1");
        pool.Add("sk-concurrent-2");

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = Enumerable.Range(0, 20).Select(_ => new Thread(() =>
        {
            try { pool.GetNext(); }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.AreEqual(0, exceptions.Count, "Concurrent GetNext calls should not throw");
    }

    [TestMethod]
    public void AcquireAndReleaseLease_ConcurrentCalls_DoNotThrow()
    {
        var pool = new CredentialPool { MaxConcurrentLeases = 10 };
        pool.Add("sk-thread-safe");

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            try
            {
                var key = pool.AcquireLease();
                if (key is not null)
                    pool.ReleaseLease(key);
            }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.AreEqual(0, exceptions.Count, "Concurrent acquire/release should not throw");
    }
}