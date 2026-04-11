namespace Hermes.Agent.Buddy;

using System.Security.Cryptography;
using System.Text;
using Hermes.Agent.LLM;
using System.Text.Json;
using Hermes.Agent.Core;

/// <summary>
/// Buddy - The Tamagotchi-style companion system.
/// Deterministic gacha based on user ID hash.
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
// Species & Rarity Definitions
// =============================================

public static class BuddySpecies
{
    public static readonly string[] Common = { "Blob", "Cube", "Dot", "Line" };
    public static readonly string[] Uncommon = { "Cat", "Dog", "Bird", "Fish" };
    public static readonly string[] Rare = { "Dragon", "Phoenix", "Unicorn", "Griffin" };
    public static readonly string[] Legendary = { "Cosmic", "Quantum", "Void", "Star" };
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
    /// <summary>
    /// Creates a deterministic PRNG seeded from user ID + salt.
    /// Same user always gets same buddy.
    /// </summary>
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
        // Roll rarity first (determines everything else)
        var rarityRoll = _rng();
        var rarity = rarityRoll switch
        {
            < 0.001 => BuddyRarity.Legendary,  // 0.1%
            < 0.01 => BuddyRarity.Rare,         // 0.9%
            < 0.1 => BuddyRarity.Uncommon,      // 9%
            _ => BuddyRarity.Common             // 90%
        };
        
        // Roll shiny (independent of rarity)
        var shinyRoll = _rng();
        var isShiny = shinyRoll < 0.005; // 0.5% chance
        
        // Select species based on rarity
        var species = SelectFrom(rarity switch
        {
            BuddyRarity.Legendary => BuddySpecies.Legendary,
            BuddyRarity.Rare => BuddySpecies.Rare,
            BuddyRarity.Uncommon => BuddySpecies.Uncommon,
            _ => BuddySpecies.Common
        });
        
        // Select eyes
        var eyes = SelectFrom(BuddyEyes.All);
        
        // Select hat (rarer buddies get better hats)
        var hatPool = rarity switch
        {
            BuddyRarity.Legendary => BuddyHats.Rare.Concat(BuddyHats.Common).ToArray(),
            BuddyRarity.Rare => BuddyHats.Rare,
            _ => BuddyHats.Common.Concat(BuddyHats.None).ToArray()
        };
        var hat = SelectFrom(hatPool);
        
        // Generate stats (total varies by rarity)
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
        // Base stat points vary by rarity
        var basePoints = rarity switch
        {
            BuddyRarity.Legendary => 300,  // Avg 75 per stat
            BuddyRarity.Rare => 240,        // Avg 60 per stat
            BuddyRarity.Uncommon => 180,    // Avg 45 per stat
            _ => 120                         // Avg 30 per stat (Common)
        };
        
        // Distribute points randomly
        var remaining = basePoints;
        var stats = new int[4];
        
        for (var i = 0; i < 3; i++)
        {
            var maxForStat = Math.Min(100, remaining - (3 - i)); // Leave room for others
            var minForStat = Math.Max(1, remaining - (100 * (3 - i)));
            var roll = _rng();
            stats[i] = (int)(minForStat + (roll * (maxForStat - minForStat)));
            remaining -= stats[i];
        }
        
        stats[3] = remaining; // Last stat gets remainder
        
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
        
        // Parse response
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
            {
                return line.Substring(prefix.Length).Trim();
            }
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
// Buddy Renderer (ASCII Art)
// =============================================

public static class BuddyRenderer
{
    public static string RenderAscii(Buddy buddy)
    {
        var species = buddy.Species.ToLower();
        
        return species switch
        {
            "blob" => RenderBlob(buddy),
            "cube" => RenderCube(buddy),
            "dot" => RenderDot(buddy),
            "cat" => RenderCat(buddy),
            "dragon" => RenderDragon(buddy),
            _ => RenderDefault(buddy)
        };
    }
    
    private static string RenderBlob(Buddy buddy)
    {
        var eyes = GetEyeChars(buddy.Eyes);
        var hat = GetHatChars(buddy.Hat);
        
        return $@"
  {hat}
 ╭─────╮
 │{eyes[0]}   {eyes[1]}│
 │  ∆  │
 ╰─────╯
".Trim();
    }
    
    private static string RenderCube(Buddy buddy)
    {
        var eyes = GetEyeChars(buddy.Eyes);
        var hat = GetHatChars(buddy.Hat);
        
        return $@"
  {hat}
 ┌─────┐
 │{eyes[0]}   {eyes[1]}│
 │─────│
 │ △△△ │
 └─────┘
".Trim();
    }
    
    private static string RenderDot(Buddy buddy)
    {
        return @"
  ●
".Trim();
    }
    
    private static string RenderCat(Buddy buddy)
    {
        var eyes = GetEyeChars(buddy.Eyes);
        
        return $@"
  /\\_/\\  
 ( {eyes[0]}   {eyes[1]} )
 (   △   )
  \\_____/
".Trim();
    }
    
    private static string RenderDragon(Buddy buddy)
    {
        var eyes = GetEyeChars(buddy.Eyes);
        
        return $@"
      /\\    
     /  \\   
    | {eyes[0]}   {eyes[1]} |
    |  ∆  |
     \\  /
      \\/
".Trim();
    }
    
    private static string RenderDefault(Buddy buddy)
    {
        var eyes = GetEyeChars(buddy.Eyes);
        
        return $@"
 ╭─────╮
 │{eyes[0]}   {eyes[1]}│
 │  ∆  │
 ╰─────╯
".Trim();
    }
    
    private static char[] GetEyeChars(string eyeType)
    {
        return eyeType switch
        {
            "normal" => ['•', '•'],
            "wide" => ['O', 'O'],
            "sleepy" => ['-', '-'],
            "excited" => ['★', '★'],
            "curious" => ['o', 'O'],
            "determined" => ['>', '<'],
            "sparkly" => ['✦', '✦'],
            "tired" => ['_', '_'],
            _ => ['•', '•']
        };
    }
    
    private static string GetHatChars(string hatType)
    {
        return hatType switch
        {
            "cap" => "🧢",
            "beanie" => "🧶",
            "bow" => "🎀",
            "crown" => "👑",
            "wizard" => "🧙",
            "halo" => "⭕",
            "headphones" => "🎧",
            _ => ""
        };
    }
}

// =============================================
// Buddy Service (Persistence & Management)
// =============================================

public sealed class BuddyService
{
    private readonly string _configPath;
    private readonly IChatClient _chatClient;
    private Buddy? _buddy;
    
    public BuddyService(string configPath, IChatClient chatClient)
    {
        _configPath = configPath;
        _chatClient = chatClient;
    }
    
    public async Task<Buddy> GetBuddyAsync(string userId, CancellationToken ct)
    {
        if (_buddy != null)
            return _buddy;
        
        // Try to load from config
        _buddy = await LoadBuddyAsync(userId, ct);
        
        if (_buddy == null)
        {
            // Generate new buddy
            var generator = new BuddyGenerator(userId);
            _buddy = generator.Generate();
            
            // Generate soul (name + personality)
            var soulGen = new BuddySoulGenerator(_chatClient);
            var soul = await soulGen.GenerateSoulAsync(_buddy, userId, ct);
            
            _buddy.Name = soul.Name;
            _buddy.Personality = soul.Personality;
            
            // Save to config
            await SaveBuddyAsync(_buddy, ct);
        }
        
        return _buddy;
    }
    
    private async Task<Buddy?> LoadBuddyAsync(string userId, CancellationToken ct)
    {
        if (!File.Exists(_configPath))
            return null;
        
        var json = await File.ReadAllTextAsync(_configPath, ct);
        var stored = JsonSerializer.Deserialize<StoredBuddy>(json);
        
        if (stored == null)
            return null;
        
        // Regenerate bones (deterministic)
        var generator = new BuddyGenerator(userId);
        var bones = generator.Generate();
        
        return new Buddy
        {
            Species = bones.Species,
            Rarity = bones.Rarity,
            Eyes = bones.Eyes,
            Hat = bones.Hat,
            IsShiny = bones.IsShiny,
            Stats = bones.Stats,
            Name = stored.Name,
            Personality = stored.Personality,
            HatchedAt = stored.HatchedAt
        };
    }
    
    private async Task SaveBuddyAsync(Buddy buddy, CancellationToken ct)
    {
        var stored = new StoredBuddy
        {
            Name = buddy.Name,
            Personality = buddy.Personality,
            HatchedAt = buddy.HatchedAt
        };
        
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        await File.WriteAllTextAsync(_configPath, json, ct);
    }
    
    public string RenderBuddy()
    {
        if (_buddy == null)
            return "No buddy yet!";
        
        var ascii = BuddyRenderer.RenderAscii(_buddy);
        var shiny = _buddy.IsShiny ? "✨ SHINY ✨\n" : "";
        
        return $@"
{shiny}{ascii}

Name: {_buddy.Name}
Species: {_buddy.Species} ({_buddy.Rarity})
Personality: {_buddy.Personality}

Stats:
  INT: {_buddy.Stats.Intelligence,3}  ENR: {_buddy.Stats.Energy,3}
  CRT: {_buddy.Stats.Creativity,3}  FRN: {_buddy.Stats.Friendliness,3}
  Total: {_buddy.Stats.Total,3}
".Trim();
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
}

// =============================================
// Extension Methods
// =============================================

public static class BuddyExtensions
{
    /// <summary>
    /// Get buddy display for CLI
    /// </summary>
    public static string GetBuddyDisplay(this Core.Agent agent)
    {
        // Implementation depends on Agent class structure
        return "Buddy not implemented yet";
    }
}
