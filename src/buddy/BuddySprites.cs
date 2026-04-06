namespace Hermes.Agent.Buddy;

/// <summary>
/// 18 species, each with 3 animation frames (idle, fidget, fidget2).
/// Frame -1 = blink (eyes become -). Applied dynamically, not stored here.
/// Each frame is 5 lines tall. {E} = eye placeholder replaced at render time.
/// </summary>
public static class BuddySprites
{
    // Idle animation sequence: mostly rest, occasional fidgets and blinks
    public static readonly int[] IdleSequence = [0, 0, 0, 0, 1, 0, 0, 0, -1, 0, 0, 2, 0, 0, 0];

    public static readonly string[] AllSpecies =
    [
        "Duck", "Goose", "Blob", "Cat", "Dragon", "Octopus", "Owl", "Penguin", "Turtle",
        "Snail", "Ghost", "Axolotl", "Capybara", "Cactus", "Robot", "Rabbit", "Mushroom", "Chonk"
    ];

    /// <summary>Get the 3 animation frames for a species. Returns string[3], each a multi-line ASCII art.</summary>
    public static string[] GetFrames(string species) => species.ToLower() switch
    {
        "duck" => DuckFrames,
        "goose" => GooseFrames,
        "blob" => BlobFrames,
        "cat" => CatFrames,
        "dragon" => DragonFrames,
        "octopus" => OctopusFrames,
        "owl" => OwlFrames,
        "penguin" => PenguinFrames,
        "turtle" => TurtleFrames,
        "snail" => SnailFrames,
        "ghost" => GhostFrames,
        "axolotl" => AxolotlFrames,
        "capybara" => CapybaraFrames,
        "cactus" => CactusFrames,
        "robot" => RobotFrames,
        "rabbit" => RabbitFrames,
        "mushroom" => MushroomFrames,
        "chonk" => ChonkFrames,
        _ => BlobFrames
    };

    /// <summary>Get a compact face for narrow display contexts.</summary>
    public static string GetFace(string species) => species.ToLower() switch
    {
        "duck" => "({E}>",
        "goose" => "({E}>>",
        "cat" => "={E}ω{E}=",
        "dragon" => "<{E}~{E}>",
        "owl" => "({E}v{E})",
        "penguin" => "({E}v{E})",
        "rabbit" => "(\\{E} {E}/)",
        "ghost" => "~{E}o{E}~",
        "robot" => "[{E}_{E}]",
        "octopus" => "~{E}_{E}~",
        _ => "({E} {E})"
    };

    // ============================
    // DUCK
    // ============================
    private static readonly string[] DuckFrames =
    [
        "    __      \n  <({E} )___  \n   (  ._>   \n    `--'    \n            ",
        "    __      \n  <({E} )___  \n   (  ._>   \n    `--'~   \n            ",
        "    __      \n  <({E} )___  \n   (  .__>  \n    `--'    \n            "
    ];

    // ============================
    // GOOSE
    // ============================
    private static readonly string[] GooseFrames =
    [
        "     __     \n    ({E} )>   \n   / |  |   \n  /  |  |   \n /___'--'   ",
        "     __     \n    ({E} )>>  \n   / |  |   \n  /  |  |   \n /___'--'   ",
        "     __     \n    ({E} )>   \n   / |  |   \n  /  | /    \n /___'~     "
    ];

    // ============================
    // BLOB
    // ============================
    private static readonly string[] BlobFrames =
    [
        "  .------.  \n /  {E}  {E}  \\ \n|    w    | \n \\      /  \n  '----'   ",
        "  .------.  \n /  {E}  {E}  \\ \n|    w    | \n  \\    /   \n   '--'    ",
        "  .------.  \n / {E}   {E}  \\ \n|    w    | \n \\      /  \n  '----'   "
    ];

    // ============================
    // CAT
    // ============================
    private static readonly string[] CatFrames =
    [
        "  /\\_/\\    \n ( {E} {E} )   \n  ( w )    \n  /| |\\   \n (_' '_)   ",
        "  /\\_/\\    \n ( {E} {E} )   \n  ( w )    \n  /| |\\~  \n (_' '_)   ",
        " ~/\\_/\\    \n ( {E} {E} )   \n  ( w )    \n  /| |\\   \n (_' '_)   "
    ];

    // ============================
    // DRAGON
    // ============================
    private static readonly string[] DragonFrames =
    [
        "   /\\_/|   \n  / {E} {E} \\  \n <(  ~  )> \n   \\===/ ~ \n    '-'    ",
        "   /\\_/|   \n  / {E} {E} \\  \n <(  ~  )> \n   \\===/~  \n    '-' *  ",
        "   /\\_/| * \n  / {E} {E} \\  \n <(  ~  )> \n   \\===/   \n    '-'    "
    ];

    // ============================
    // OCTOPUS
    // ============================
    private static readonly string[] OctopusFrames =
    [
        "  .----.   \n ( {E}  {E} )  \n  (    )   \n /||/||\\   \n            ",
        "  .----.   \n ( {E}  {E} )  \n  (    )   \n \\||\\||/   \n            ",
        "  .----.   \n ({E}   {E})   \n  (    )   \n /|\\||/|   \n            "
    ];

    // ============================
    // OWL
    // ============================
    private static readonly string[] OwlFrames =
    [
        "   {{\\_}}   \n  ( {E} {E} )  \n  ( (v) )  \n   /| |\\   \n  /_| |_\\  ",
        "   {{\\_}}   \n  ( {E} {E} )  \n  ( (v) )  \n   /| |\\   \n  /_|_|_\\  ",
        "  ~{{\\_}}   \n  ( {E} {E} )  \n  ( (v) )  \n   /| |\\   \n  /_| |_\\  "
    ];

    // ============================
    // PENGUIN
    // ============================
    private static readonly string[] PenguinFrames =
    [
        "   .--.    \n  / {E}{E} \\   \n  | >> |   \n  /|  |\\   \n _/ '--'\\_  ",
        "   .--.    \n  / {E}{E} \\   \n  | >> |   \n  /|  |\\~  \n _/ '--'\\_  ",
        "   .--.    \n  / {E}{E} \\   \n  | >> |   \n ~/|  |\\   \n _/ '--'\\_  "
    ];

    // ============================
    // TURTLE
    // ============================
    private static readonly string[] TurtleFrames =
    [
        "   _____   \n  / {E} {E} \\  \n /=======\\ \n |_|___|_| \n            ",
        "   _____   \n  / {E} {E} \\  \n /=======\\ \n  |___|_|  \n            ",
        "   _____   \n  /{E}  {E}  \\ \n /=======\\ \n |_|___|_| \n            "
    ];

    // ============================
    // SNAIL
    // ============================
    private static readonly string[] SnailFrames =
    [
        "    @  @   \n    || ||   \n  .({E}  {E}).  \n /  ___  \\ \n '-------' ",
        "    @  @   \n    | \\|   \n  .({E}  {E}).  \n /  ___  \\ \n '-------' ",
        "    @  @   \n    |/ |   \n  .({E}  {E}).  \n /  ___  \\ \n  '------' "
    ];

    // ============================
    // GHOST
    // ============================
    private static readonly string[] GhostFrames =
    [
        "  .-----.  \n /  {E} {E}  \\ \n|   ooo  | \n|       | \n \\_/\\_/\\_/ ",
        "  .-----.  \n /  {E} {E}  \\ \n|   ooo  | \n|       | \n  \\_/\\_/\\  ",
        "  .-----.  \n / {E}  {E}  \\ \n|   ooo  | \n|       | \n \\_/\\_/\\_/ "
    ];

    // ============================
    // AXOLOTL
    // ============================
    private static readonly string[] AxolotlFrames =
    [
        " \\\\   //  \n  ({E} {E})   \n  ( w )    \n --|--|--  \n   ~~~~    ",
        " \\\\   //  \n  ({E} {E})   \n  ( w )    \n --|--|--  \n   ~~~~ ~  ",
        "  \\\\  //  \n  ({E} {E})   \n  ( w )    \n --|--|--  \n    ~~~~   "
    ];

    // ============================
    // CAPYBARA
    // ============================
    private static readonly string[] CapybaraFrames =
    [
        "  .-----.  \n /  {E}  {E} \\ \n | (  n) | \n |_______|~\n  || ||    ",
        "  .-----.  \n /  {E}  {E} \\ \n | (  n) | \n |_______| \n  || ||  ~ ",
        "  .-----.  \n / {E}   {E} \\ \n | (  n) | \n |_______|~\n  || ||    "
    ];

    // ============================
    // CACTUS
    // ============================
    private static readonly string[] CactusFrames =
    [
        "   .-.     \n  ({E} {E})   \n --|w|     \n   | |--   \n  \\___/    ",
        "   .-.     \n  ({E} {E})   \n --|w|     \n   | |--   \n  \\___/ *  ",
        "   .-.  *  \n  ({E} {E})   \n --|w|     \n   | |--   \n  \\___/    "
    ];

    // ============================
    // ROBOT
    // ============================
    private static readonly string[] RobotFrames =
    [
        "  [===]    \n  |{E} {E}|   \n  |___|    \n  /| |\\   \n _/ '-' \\_ ",
        "  [===]    \n  |{E} {E}|   \n  |___|    \n  /| |\\   \n _/  ~  \\_ ",
        "  [===]    \n  |{E} {E}|   \n  |___|    \n ~/| |\\   \n _/ '-' \\_ "
    ];

    // ============================
    // RABBIT
    // ============================
    private static readonly string[] RabbitFrames =
    [
        "  (\\  /)  \n  ( {E}{E} )   \n  ( >< )   \n  /|  |\\   \n (_'--'_)  ",
        "  (\\  /)  \n  ( {E}{E} )   \n  ( >< )   \n  /|  |\\~  \n (_'--'_)  ",
        " ~(\\  /)  \n  ( {E}{E} )   \n  ( >< )   \n  /|  |\\   \n (_'--'_)  "
    ];

    // ============================
    // MUSHROOM
    // ============================
    private static readonly string[] MushroomFrames =
    [
        "  .oOOo.   \n / o  o \\ \n({E}      {E})\n  | w |    \n  '---'    ",
        "  .oOOo.   \n / o  o \\ \n({E}      {E})\n  | w |    \n  '---' ~  ",
        "  .oOOo.   \n /  oo  \\ \n({E}      {E})\n  | w |    \n  '---'    "
    ];

    // ============================
    // CHONK
    // ============================
    private static readonly string[] ChonkFrames =
    [
        "  .-----.  \n / {E}   {E} \\ \n|   w    | \n|       | \n '-._.-'   ",
        "  .-----.  \n / {E}   {E} \\ \n|   w    | \n \\     /  \n  '-.-'    ",
        " .------. \n/ {E}    {E} \\\n|   w    | \n|       | \n '-._.-'   "
    ];

    // ============================
    // HAT OVERLAYS (12 chars wide, replaces top line)
    // ============================
    public static string GetHatOverlay(string hat) => hat switch
    {
        "crown" =>      "   \\^^^/    ",
        "tophat" =>     "   [___]    ",
        "propeller" =>  "    -+-     ",
        "halo" =>       "   (   )    ",
        "wizard" =>     "    /^\\     ",
        "beanie" =>     "   (___)    ",
        "headphones" => "   {~v~}    ",
        "cap" =>        "    -->     ",
        "bow" =>        "    }*{     ",
        _ => ""
    };

    /// <summary>Eye characters for each mood state.</summary>
    public static (string Left, string Right) GetMoodEyes(BuddyMood mood) => mood switch
    {
        BuddyMood.Happy => ("^", "^"),
        BuddyMood.Excited => ("★", "★"),
        BuddyMood.Sleepy => ("-", "-"),
        BuddyMood.Hungry => ("o", "o"),
        BuddyMood.Sad => (";", ";"),
        BuddyMood.Bored => ("-", "-"),
        _ => ("•", "•")
    };

    /// <summary>Blink eyes (used for frame -1).</summary>
    public static (string Left, string Right) BlinkEyes => ("-", "-");

    /// <summary>Render a sprite frame with mood eyes and optional hat.</summary>
    public static string Render(string species, int frameIndex, BuddyMood mood, string hat, bool isBlink = false)
    {
        var frames = GetFrames(species);
        var idx = Math.Clamp(frameIndex, 0, frames.Length - 1);
        var frame = frames[idx];

        // Replace eye placeholders
        var (el, er) = isBlink ? BlinkEyes : GetMoodEyes(mood);
        var lines = frame.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // Replace first {E} with left eye, second with right eye
            var firstIdx = lines[i].IndexOf("{E}");
            if (firstIdx >= 0)
            {
                lines[i] = lines[i].Substring(0, firstIdx) + el + lines[i].Substring(firstIdx + 3);
                var secondIdx = lines[i].IndexOf("{E}");
                if (secondIdx >= 0)
                    lines[i] = lines[i].Substring(0, secondIdx) + er + lines[i].Substring(secondIdx + 3);
            }
        }

        // Apply hat overlay on first line if hat exists and first line is mostly empty
        var hatOverlay = GetHatOverlay(hat);
        if (!string.IsNullOrEmpty(hatOverlay) && lines.Length > 0 && lines[0].Trim().Length < 4)
        {
            lines[0] = hatOverlay;
        }

        return string.Join("\n", lines);
    }
}
