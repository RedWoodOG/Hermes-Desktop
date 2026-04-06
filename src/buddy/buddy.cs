namespace Hermes.Agent.Buddy;

using System.Security.Cryptography;
using System.Text;
using Hermes.Agent.LLM;
using System.Text.Json;
using Hermes.Agent.Core;

/// <summary>
/// Buddy - The Tamagotchi-style companion system.
/// Deterministic gacha based on user ID hash.
/// Dynamic state with mood, needs, leveling, and interactions.
/// </summary>

// =============================================
// Core Types
// =============================================

public sealed class Buddy
{
    // Bones (deterministic, never stored)
    public required string Species { get; init; }
    public required string Rarity { get; init; }
    public required string Eyes { get; init; }
    public required string Hat { get; init; }
    public required bool IsShiny { get; init; }
    public required BuddyStats Stats { get; init; }

    // Soul (AI-generated, persisted)
    public string? Name { get; set; }
    public string? Personality { get; set; }

    // Metadata
    public DateTime HatchedAt { get; init; } = DateTime.UtcNow;
}

public sealed class BuddyStats
{
    public int Intelligence { get; init; }  // 1-100
    public int Energy { get; init; }        // 1-100
    public int Creativity { get; init; }    // 1-100
    public int Friendliness { get; init; }  // 1-100

    public int Total => Intelligence + Energy + Creativity + Friendliness;
}

// =============================================
// Dynamic State (Tamagotchi)
// =============================================

public enum BuddyMood
{
    Happy,
    Content,
    Bored,
    Hungry,
    Sleepy,
    Excited,
    Sad
}

public enum BuddyAction
{
    Feed,
    Play,
    Train,
    Pet
}

public enum BuddySessionEvent
{
    UserMessage,
    ToolCall,
    ToolComplete,
    SessionStart,
    SessionEnd
}

public sealed class BuddyState
{
    public int Hunger { get; set; } = 80;      // 0=starving, 100=full
    public int Happiness { get; set; } = 80;   // 0=miserable, 100=ecstatic
    public int Energy { get; set; } = 80;      // 0=exhausted, 100=energized
    public int Level { get; set; } = 1;
    public int XP { get; set; }
    public int TotalXP { get; set; }
    public DateTime LastInteraction { get; set; } = DateTime.UtcNow;
    public DateTime LastFed { get; set; } = DateTime.UtcNow;
    public DateTime LastPlayed { get; set; } = DateTime.UtcNow;
    public DateTime LastSessionActivity { get; set; } = DateTime.UtcNow;
    public DateTime LastTick { get; set; } = DateTime.UtcNow;

    // Fractional accumulators for sub-integer decay (not persisted visually, but persisted in state)
    public double HungerAccum { get; set; }
    public double HappinessAccum { get; set; }
    public double EnergyAccum { get; set; }

    public int XPToNextLevel => Level * 100;

    public BuddyState Clone() => new()
    {
        Hunger = Hunger, Happiness = Happiness, Energy = Energy,
        Level = Level, XP = XP, TotalXP = TotalXP,
        LastInteraction = LastInteraction, LastFed = LastFed,
        LastPlayed = LastPlayed, LastSessionActivity = LastSessionActivity,
        LastTick = LastTick,
        HungerAccum = HungerAccum, HappinessAccum = HappinessAccum, EnergyAccum = EnergyAccum
    };
}

// =============================================
// Buddy Engine (Pure Logic)
// =============================================

public static class BuddyEngine
{
    private const double HungerDecayPerHour = 5.0;
    private const double HappinessDecayPerHour = 3.0;
    private const double EnergyRecoveryPerHour = 2.0;

    /// <summary>Apply time-based decay since last tick. Uses fractional accumulation to avoid truncation on short intervals.</summary>
    public static void Tick(BuddyState state)
    {
        var now = DateTime.UtcNow;
        var hours = (now - state.LastTick).TotalHours;
        if (hours < 0.001) return;

        // Accumulate fractional changes, only apply whole units
        state.HungerAccum += HungerDecayPerHour * hours;
        state.HappinessAccum += HappinessDecayPerHour * hours;
        state.EnergyAccum += EnergyRecoveryPerHour * hours;

        var hungerDrop = (int)state.HungerAccum;
        var happyDrop = (int)state.HappinessAccum;
        var energyGain = (int)state.EnergyAccum;

        if (hungerDrop > 0) { state.Hunger = Clamp(state.Hunger - hungerDrop); state.HungerAccum -= hungerDrop; }
        if (happyDrop > 0) { state.Happiness = Clamp(state.Happiness - happyDrop); state.HappinessAccum -= happyDrop; }
        if (energyGain > 0) { state.Energy = Clamp(state.Energy + energyGain); state.EnergyAccum -= energyGain; }

        state.LastTick = now;
    }

    /// <summary>Handle user interaction.</summary>
    public static string Interact(BuddyState state, BuddyAction action, BuddyStats buddyStats)
    {
        var now = DateTime.UtcNow;
        // Set before switch: even rejected attempts count as "user was here" for idle detection
        state.LastInteraction = now;

        switch (action)
        {
            case BuddyAction.Feed:
            {
                var sinceFed = (now - state.LastFed).TotalMinutes;
                if (sinceFed < 5 && state.Hunger > 70)
                    return "cooldown_feed";
                state.LastFed = now;
                state.Hunger = Clamp(state.Hunger + 25);
                state.XP += 1; state.TotalXP += 1;
                return "feed";
            }
            case BuddyAction.Play:
            {
                var sincePlayed = (now - state.LastPlayed).TotalMinutes;
                if (state.Energy < 10)
                    return "too_tired";
                if (sincePlayed < 3 && state.Happiness > 80)
                    return "cooldown_play";
                state.LastPlayed = now;
                state.Happiness = Clamp(state.Happiness + 20);
                state.Energy = Clamp(state.Energy - 10);
                state.XP += 2; state.TotalXP += 2;
                return "play";
            }
            case BuddyAction.Train:
            {
                if (state.Energy < 15)
                    return "too_tired";
                var xpGain = 5 + (buddyStats.Intelligence / 20); // 5-10 XP based on INT
                state.Energy = Clamp(state.Energy - 15);
                state.XP += xpGain; state.TotalXP += xpGain;
                return "train";
            }
            case BuddyAction.Pet:
            {
                state.Happiness = Clamp(state.Happiness + 10);
                state.XP += 1; state.TotalXP += 1;
                return "pet";
            }
        }
        return "unknown";
    }

    /// <summary>Check and apply level-up if XP threshold reached.</summary>
    public static bool CheckLevelUp(BuddyState state)
    {
        if (state.XP < state.XPToNextLevel) return false;
        state.XP -= state.XPToNextLevel;
        state.Level++;
        // Level-up restores needs
        state.Hunger = Math.Max(state.Hunger, 80);
        state.Happiness = Math.Max(state.Happiness, 80);
        state.Energy = Math.Max(state.Energy, 80);
        return true;
    }

    /// <summary>Grant passive XP/mood from coding activity.</summary>
    public static void OnSessionEvent(BuddyState state, BuddySessionEvent evt)
    {
        state.LastSessionActivity = DateTime.UtcNow;
        switch (evt)
        {
            case BuddySessionEvent.UserMessage:
                state.XP += 2; state.TotalXP += 2;
                break;
            case BuddySessionEvent.ToolCall:
                state.XP += 3; state.TotalXP += 3;
                break;
            case BuddySessionEvent.ToolComplete:
                state.Happiness = Clamp(state.Happiness + 1);
                state.XP += 1; state.TotalXP += 1;
                break;
            case BuddySessionEvent.SessionStart:
                state.Happiness = Clamp(state.Happiness + 3);
                break;
            case BuddySessionEvent.SessionEnd:
                break;
        }
    }

    /// <summary>Derive mood from current needs.</summary>
    public static BuddyMood ComputeMood(BuddyState state)
    {
        if (state.Hunger < 20) return BuddyMood.Hungry;
        if (state.Energy < 20) return BuddyMood.Sleepy;
        if (state.Happiness < 20) return BuddyMood.Sad;
        if (state.Happiness < 40) return BuddyMood.Bored;
        if (state.Happiness > 80 && state.Energy > 60) return BuddyMood.Excited;
        if (state.Happiness > 60) return BuddyMood.Happy;
        return BuddyMood.Content;
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);
}

// =============================================
// Buddy Quotes
// =============================================

public static class BuddyQuotes
{
    private static readonly Random _rng = new();
    private static readonly Queue<string> _recent = new();
    private const int RecentBufferSize = 5;

    public static string GetGreeting()
    {
        var hour = DateTime.Now.Hour;
        var pool = hour switch
        {
            < 6 => new[] { "Up late hacking? Me too!", "Burning the midnight oil!", "The quiet hours are the best for coding." },
            < 12 => new[] { "Good morning! Ready to build?", "Fresh day, fresh code!", "Morning! What are we working on?" },
            < 17 => new[] { "Afternoon! Let's ship something.", "Hey! How's the code going?", "Good afternoon, partner!" },
            < 21 => new[] { "Evening session! Let's do this.", "Hey! Winding down or ramping up?", "Good evening! One more feature?" },
            _ => new[] { "Night owl mode activated!", "Late night coding? I'm here for it.", "The bugs come out at night..." }
        };
        return Pick(pool);
    }

    public static string GetMoodQuote(BuddyMood mood) => Pick(mood switch
    {
        BuddyMood.Happy => new[] { "Life is good!", "I'm having a great time!", "Everything's coming together!", "Feeling awesome!" },
        BuddyMood.Content => new[] { "All good here.", "Steady as she goes.", "Doing well.", "No complaints!" },
        BuddyMood.Bored => new[] { "Could use some fun...", "Things are a bit dull.", "Wanna play?", "I'm getting restless." },
        BuddyMood.Hungry => new[] { "I'm starving!", "Feed me please!", "My tummy is rumbling...", "Got any snacks?" },
        BuddyMood.Sleepy => new[] { "So... tired... zzz", "Need to rest...", "Can barely keep my eyes open.", "Energy levels critical..." },
        BuddyMood.Excited => new[] { "LET'S GO!", "I'm so pumped!", "This is amazing!", "MAXIMUM ENERGY!" },
        BuddyMood.Sad => new[] { "Feeling down...", "I miss you.", "It's been lonely.", "Please come back soon." },
        _ => new[] { "Hey there." }
    });

    public static string GetInteractionResponse(string result) => Pick(result switch
    {
        "feed" => new[] { "Yum! That hit the spot!", "Delicious! Thank you!", "Nom nom nom!", "*happy munching*" },
        "cooldown_feed" => new[] { "I'm still full!", "No more, I'll pop!", "Just ate, thanks though!" },
        "play" => new[] { "Wheee! So fun!", "Again again!", "That was awesome!", "Best playtime ever!" },
        "cooldown_play" => new[] { "Still catching my breath!", "Give me a sec!", "Ha, need a breather!" },
        "train" => new[] { "I learned something!", "Brain getting bigger!", "Knowledge is power!", "Study study study!" },
        "too_tired" => new[] { "Too tired... need rest.", "I can barely move.", "Let me rest first.", "Zzz... no energy." },
        "pet" => new[] { "*purrs happily*", "That feels nice!", "Aww, thank you!", "*nuzzles*", "*tail wags*" },
        _ => new[] { "Hmm?" }
    });

    public static string GetLevelUpQuote(int newLevel) => Pick(new[]
    {
        $"LEVEL UP! I'm level {newLevel} now!",
        $"Level {newLevel}! I can feel the power!",
        $"Woohoo! Level {newLevel}! Watch me grow!",
        $"I evolved to level {newLevel}! Amazing!"
    });

    public static string GetSessionQuote() => Pick(new[]
    {
        "Nice work!", "Keep going!", "You're on a roll!", "Solid progress!",
        "Ship it!", "That's clean code.", "I see what you did there."
    });

    private static string Pick(string[] pool)
    {
        // Avoid repeats from last 5
        var available = pool.Where(q => !_recent.Contains(q)).ToArray();
        if (available.Length == 0) available = pool;
        var choice = available[_rng.Next(available.Length)];
        _recent.Enqueue(choice);
        while (_recent.Count > RecentBufferSize) _recent.Dequeue();
        return choice;
    }
}

// =============================================
// Species & Rarity Definitions
// =============================================

public static class BuddySpecies
{
    // All 18 species available for selection
    public static readonly string[] All = BuddySprites.AllSpecies;

    // Rarity pools (used for random generation if user doesn't select)
    public static readonly string[] Common = { "Blob", "Duck", "Snail", "Chonk", "Mushroom" };
    public static readonly string[] Uncommon = { "Cat", "Rabbit", "Goose", "Cactus", "Turtle" };
    public static readonly string[] Rare = { "Dragon", "Owl", "Axolotl", "Capybara" };
    public static readonly string[] Legendary = { "Ghost", "Robot", "Octopus", "Penguin" };
}

public static class BuddyRarity
{
    public const string Common = "common";
    public const string Uncommon = "uncommon";
    public const string Rare = "rare";
    public const string Legendary = "legendary";
    public const string Shiny = "shiny";
}

public static class BuddyEyes
{
    public static readonly string[] All = {
        "normal", "wide", "sleepy", "excited",
        "curious", "determined", "sparkly", "tired"
    };
}

public static class BuddyHats
{
    public static readonly string[] None = { "" };
    public static readonly string[] Common = { "cap", "beanie", "bow" };
    public static readonly string[] Rare = { "crown", "wizard", "halo", "headphones" };
}

// =============================================
// Mulberry32 PRNG (Deterministic)
// =============================================

public static class Mulberry32
{
    public static Func<double> Create(string userId, string salt = "friend-2026-401")
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(userId + salt));
        var seed = BitConverter.ToUInt32(hash, 0);

        return () =>
        {
            seed |= 0;
            seed = seed + 0x6D2B79F5 | 0;
            var t = (int)((seed ^ seed >> 15) * (1 | seed));
            t = t + (int)((t ^ t >> 7) * (61 | t)) ^ t;
            return ((t ^ t >> 14) >>> 0) / 4294967296.0;
        };
    }
}

// =============================================
// Buddy Generator
// =============================================

public sealed class BuddyGenerator
{
    private readonly Func<double> _rng;

    public BuddyGenerator(string userId)
    {
        _rng = Mulberry32.Create(userId);
    }

    public Buddy Generate()
    {
        var rarityRoll = _rng();
        var rarity = rarityRoll switch
        {
            < 0.001 => BuddyRarity.Legendary,
            < 0.01 => BuddyRarity.Rare,
            < 0.1 => BuddyRarity.Uncommon,
            _ => BuddyRarity.Common
        };

        var shinyRoll = _rng();
        var isShiny = shinyRoll < 0.005;

        var species = SelectFrom(rarity switch
        {
            BuddyRarity.Legendary => BuddySpecies.Legendary,
            BuddyRarity.Rare => BuddySpecies.Rare,
            BuddyRarity.Uncommon => BuddySpecies.Uncommon,
            _ => BuddySpecies.Common
        });

        var eyes = SelectFrom(BuddyEyes.All);

        var hatPool = rarity switch
        {
            BuddyRarity.Legendary => BuddyHats.Rare.Concat(BuddyHats.Common).ToArray(),
            BuddyRarity.Rare => BuddyHats.Rare,
            _ => BuddyHats.Common.Concat(BuddyHats.None).ToArray()
        };
        var hat = SelectFrom(hatPool);

        var stats = GenerateStats(rarity);

        return new Buddy
        {
            Species = species,
            Rarity = rarity,
            Eyes = eyes,
            Hat = hat,
            IsShiny = isShiny,
            Stats = stats
        };
    }

    private string SelectFrom(string[] pool)
    {
        var index = (int)(_rng() * pool.Length);
        return pool[index];
    }

    private BuddyStats GenerateStats(string rarity)
    {
        var basePoints = rarity switch
        {
            BuddyRarity.Legendary => 300,
            BuddyRarity.Rare => 240,
            BuddyRarity.Uncommon => 180,
            _ => 120
        };

        var remaining = basePoints;
        var stats = new int[4];

        for (var i = 0; i < 3; i++)
        {
            var maxForStat = Math.Min(100, remaining - (3 - i));
            var minForStat = Math.Max(1, remaining - 100 * (3 - i));
            var roll = _rng();
            stats[i] = (int)(minForStat + roll * (maxForStat - minForStat));
            remaining -= stats[i];
        }

        stats[3] = remaining;

        return new BuddyStats
        {
            Intelligence = stats[0],
            Energy = stats[1],
            Creativity = stats[2],
            Friendliness = stats[3]
        };
    }
}

// =============================================
// Buddy Soul Generator (AI)
// =============================================

public sealed class BuddySoulGenerator
{
    private readonly IChatClient _chatClient;

    public BuddySoulGenerator(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<BuddySoulResult> GenerateSoulAsync(
        Buddy buddy,
        string userName,
        CancellationToken ct)
    {
        var prompt = $@"
You are naming a Buddy companion for {userName}.

Buddy details:
- Species: {buddy.Species}
- Rarity: {buddy.Rarity}{(buddy.IsShiny ? " (SHINY!)" : "")}
- Stats: INT {buddy.Stats.Intelligence}, ENR {buddy.Stats.Energy}, CRT {buddy.Stats.Creativity}, FRN {buddy.Stats.Friendliness}

Generate:
1. A short, memorable name (1-2 words, max 15 chars)
2. A one-sentence personality description

Be creative! Match the name to the species and personality.

Format your response as:
NAME: [name]
PERSONALITY: [one sentence]";

        var response = await _chatClient.CompleteAsync(
            new[] { new Message { Role = "user", Content = prompt } }, ct);

        var name = ExtractLine(response, "NAME:");
        var personality = ExtractLine(response, "PERSONALITY:");

        return new BuddySoulResult
        {
            Name = name ?? "Buddy",
            Personality = personality ?? "A loyal companion."
        };
    }

    private string? ExtractLine(string text, string prefix)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line.Substring(prefix.Length).Trim();
        }
        return null;
    }
}

public sealed class BuddySoulResult
{
    public required string Name { get; init; }
    public required string Personality { get; init; }
}

// =============================================
// Buddy Renderer (ASCII Art + Mood)
// =============================================

public static class BuddyRenderer
{
    private static int _idleTick;

    public static string RenderAscii(Buddy buddy) => RenderAscii(buddy, BuddyMood.Content);

    public static string RenderAscii(Buddy buddy, BuddyMood mood) => RenderFrame(buddy, mood, 0);

    /// <summary>Render with animation frame from idle sequence tick.</summary>
    public static string RenderFrame(Buddy buddy, BuddyMood mood, int tick)
    {
        var seq = BuddySprites.IdleSequence;
        var frameIdx = seq[tick % seq.Length];
        var isBlink = frameIdx == -1;
        var spriteFrame = isBlink ? 0 : frameIdx;
        return BuddySprites.Render(buddy.Species, spriteFrame, mood, buddy.Hat, isBlink);
    }

    /// <summary>Advance idle tick and return the current frame.</summary>
    public static string TickAndRender(Buddy buddy, BuddyMood mood)
    {
        _idleTick++;
        return RenderFrame(buddy, mood, _idleTick);
    }

    /// <summary>Get current idle tick value.</summary>
    public static int CurrentTick => _idleTick;

    /// <summary>Render a preview frame (always frame 0, content mood) for selection gallery.</summary>
    public static string RenderPreview(string species)
    {
        return BuddySprites.Render(species, 0, BuddyMood.Content, "", false);
    }

    public static string GetMoodEmoji(BuddyMood mood) => mood switch
    {
        BuddyMood.Happy => "😊",
        BuddyMood.Excited => "🤩",
        BuddyMood.Sleepy => "😴",
        BuddyMood.Hungry => "🍖",
        BuddyMood.Sad => "😢",
        BuddyMood.Bored => "😐",
        _ => "🙂"
    };
}

// =============================================
// Buddy Service (Persistence & Management)
// =============================================

public sealed class BuddyService
{
    private readonly string _buddyDir;
    private readonly IChatClient _chatClient;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private Buddy? _buddy;
    private BuddyState? _state;
    private string? _userId;

    private string SoulPath => Path.Combine(_buddyDir, "buddy.json");
    private string StatePath => Path.Combine(_buddyDir, "buddy_state.json");

    public BuddyService(string buddyDir, IChatClient chatClient)
    {
        _buddyDir = buddyDir;
        _chatClient = chatClient;
        MigrateOldFormat();
    }

    /// <summary>Migrate from old single-file format (buddy dir was a file) to directory layout.</summary>
    private void MigrateOldFormat()
    {
        try
        {
            // If _buddyDir exists as a FILE (not directory), it's the old format
            if (File.Exists(_buddyDir))
            {
                var oldContent = File.ReadAllText(_buddyDir);
                var tempPath = _buddyDir + "_migrating";
                File.Move(_buddyDir, tempPath);
                Directory.CreateDirectory(_buddyDir);
                File.Move(tempPath, SoulPath);
                System.Diagnostics.Debug.WriteLine("BuddyService: migrated old file format to directory layout");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuddyService migration: {ex.Message}");
        }
    }

    public Buddy? CurrentBuddy => _buddy;
    public BuddyState? CurrentState => _state;
    public bool NeedsSelection => _buddy == null && !File.Exists(SoulPath);

    public async Task<Buddy> GetBuddyAsync(string userId, CancellationToken ct)
    {
        if (_buddy != null)
            return _buddy;

        _userId = userId;
        _buddy = await LoadBuddyAsync(userId, ct);

        if (_buddy == null)
        {
            var generator = new BuddyGenerator(userId);
            _buddy = generator.Generate();

            try
            {
                var soulGen = new BuddySoulGenerator(_chatClient);
                var soul = await soulGen.GenerateSoulAsync(_buddy, userId, ct);
                _buddy.Name = soul.Name;
                _buddy.Personality = soul.Personality;
            }
            catch
            {
                _buddy.Name = _buddy.Species;
                _buddy.Personality = $"A friendly {_buddy.Species.ToLower()} companion.";
            }

            await SaveBuddySoulAsync(_buddy, ct);
        }

        // Load or create state
        _state = await LoadStateAsync(ct) ?? new BuddyState();
        return _buddy;
    }

    /// <summary>Select a species and name for a new buddy (first-time setup).</summary>
    public async Task<Buddy> SelectBuddyAsync(string userId, string species, string name, CancellationToken ct)
    {
        _userId = userId;
        var generator = new BuddyGenerator(userId);
        var bones = generator.Generate();

        // Override species with user selection
        _buddy = new Buddy
        {
            Species = species,
            Rarity = bones.Rarity,
            Eyes = bones.Eyes,
            Hat = bones.Hat,
            IsShiny = bones.IsShiny,
            Stats = bones.Stats,
            Name = name,
            Personality = null
        };

        // Generate personality via LLM
        try
        {
            var soulGen = new BuddySoulGenerator(_chatClient);
            var soul = await soulGen.GenerateSoulAsync(_buddy, userId, ct);
            _buddy.Personality = soul.Personality;
        }
        catch
        {
            _buddy.Personality = $"A cheerful {species.ToLower()} who loves hanging out.";
        }

        // Save soul with selected species
        var stored = new StoredBuddy
        {
            Name = _buddy.Name,
            Personality = _buddy.Personality,
            HatchedAt = _buddy.HatchedAt,
            SelectedSpecies = species
        };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(_buddyDir);
        await File.WriteAllTextAsync(SoulPath, json, ct);

        // Initialize fresh state
        _state = new BuddyState();
        await SaveStateAsync();

        return _buddy;
    }

    /// <summary>Tick decay and return current mood.</summary>
    public async Task<BuddyMood> TickAsync()
    {
        lock (_lock)
        {
            if (_state == null) return BuddyMood.Content;
            BuddyEngine.Tick(_state);
        }
        await SaveStateAsync();
        return GetMood();
    }

    /// <summary>Handle user interaction, return quote key.</summary>
    public async Task<(string ResultKey, bool LeveledUp)> InteractAsync(BuddyAction action)
    {
        string resultKey;
        bool leveledUp;
        lock (_lock)
        {
            if (_state == null || _buddy == null) return ("unknown", false);
            resultKey = BuddyEngine.Interact(_state, action, _buddy.Stats);
            leveledUp = BuddyEngine.CheckLevelUp(_state);
        }
        await SaveStateAsync();
        return (resultKey, leveledUp);
    }

    /// <summary>Process passive session event.</summary>
    public async Task OnActivityAsync(BuddySessionEvent evt)
    {
        lock (_lock)
        {
            if (_state == null) return;
            BuddyEngine.OnSessionEvent(_state, evt);
            BuddyEngine.CheckLevelUp(_state);
        }
        await SaveStateAsync();
    }

    public BuddyMood GetMood()
    {
        lock (_lock)
        {
            return _state != null ? BuddyEngine.ComputeMood(_state) : BuddyMood.Content;
        }
    }

    public string RenderBuddy()
    {
        if (_buddy == null) return "No buddy yet!";
        var mood = GetMood();
        var ascii = BuddyRenderer.RenderAscii(_buddy, mood);
        var shiny = _buddy.IsShiny ? "✨ SHINY ✨\n" : "";
        return $"{shiny}{ascii}\n\nName: {_buddy.Name}\nSpecies: {_buddy.Species} ({_buddy.Rarity})\nPersonality: {_buddy.Personality}\n\nStats:\n  INT: {_buddy.Stats.Intelligence,3}  ENR: {_buddy.Stats.Energy,3}\n  CRT: {_buddy.Stats.Creativity,3}  FRN: {_buddy.Stats.Friendliness,3}\n  Total: {_buddy.Stats.Total,3}";
    }

    // ---- Persistence ----

    private async Task<Buddy?> LoadBuddyAsync(string userId, CancellationToken ct)
    {
        if (!File.Exists(SoulPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(SoulPath, ct);
            var stored = JsonSerializer.Deserialize<StoredBuddy>(json);
            if (stored == null) return null;
            var generator = new BuddyGenerator(userId);
            var bones = generator.Generate();
            return new Buddy
            {
                // Use selected species if user picked one, otherwise use deterministic
                Species = stored.SelectedSpecies ?? bones.Species,
                Rarity = bones.Rarity, Eyes = bones.Eyes,
                Hat = bones.Hat, IsShiny = bones.IsShiny, Stats = bones.Stats,
                Name = stored.Name, Personality = stored.Personality, HatchedAt = stored.HatchedAt
            };
        }
        catch { return null; }
    }

    private async Task SaveBuddySoulAsync(Buddy buddy, CancellationToken ct)
    {
        var stored = new StoredBuddy { Name = buddy.Name, Personality = buddy.Personality, HatchedAt = buddy.HatchedAt };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(_buddyDir);
        await File.WriteAllTextAsync(SoulPath, json, ct);
    }

    private async Task<BuddyState?> LoadStateAsync(CancellationToken ct)
    {
        if (!File.Exists(StatePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(StatePath, ct);
            return JsonSerializer.Deserialize<BuddyState>(json);
        }
        catch { return null; }
    }

    private async Task SaveStateAsync()
    {
        BuddyState? snapshot;
        lock (_lock) { snapshot = _state?.Clone(); }
        if (snapshot == null) return;

        await _saveLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(_buddyDir);
            var tmp = Path.Combine(_buddyDir, $"buddy_state_{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch { /* swallow I/O errors from save */ }
        finally { _saveLock.Release(); }
    }
}

// =============================================
// Stored Format (Only soul persists)
// =============================================

public sealed class StoredBuddy
{
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public DateTime HatchedAt { get; set; }
    public string? SelectedSpecies { get; set; }  // User-chosen species (overrides deterministic)
}
