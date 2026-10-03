using System.Text.Json;

namespace EliteSharp.Game.Ships;

/// <summary>
/// The ship types, loaded from the JSON attribute files and glTF models in
/// Assets/Ships. There is one JSON file for each of the 33 ship types (the
/// file names start with the type number, but it's the "type" property that
/// counts); ships with identical geometry can share a model. A mod (see
/// <see cref="GameAssets"/>) can replace any of the ship types with its own
/// JSON files, and any of the models with its own; the rest are the game's.
/// </summary>
public static class ShipCatalogue
{
    /// <summary>The number of ship types (the size of the XX21 table).</summary>
    public const int Count = 33;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Lock LoadLock = new();
    private static ShipBlueprint[]? _blueprints;
    private static IReadOnlyList<string> _modelPaths = [];

    /// <summary>The game's own folder containing the ship assets.</summary>
    public static string AssetFolder { get; set; } = Path.Combine(GameAssets.BaseFolder, "Ships");

    /// <summary>The folders to load the ship assets from: the mod's Ships folder, if it has one, then <see cref="AssetFolder"/>.</summary>
    private static IReadOnlyList<string> Folders =>
        GameAssets.ModFolder is { } mod && Directory.Exists(Path.Combine(mod, "Ships"))
            ? [Path.Combine(mod, "Ships"), AssetFolder]
            : [AssetFolder];

    /// <summary>The paths of the models that the ship types use.</summary>
    public static IReadOnlyList<string> ModelPaths
    {
        get
        {
            _ = Blueprints;
            return _modelPaths;
        }
    }

    /// <summary>All the ship types, in type order.</summary>
    public static IReadOnlyList<ShipBlueprint> All => Blueprints;

    /// <summary>The blueprint for a ship type (1-33).</summary>
    public static ShipBlueprint Get(int type) =>
        type is >= 1 and <= Count
            ? Blueprints[type - 1]
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Ship types run from 1 to 33");

    private static ShipBlueprint[] Blueprints
    {
        get
        {
            if (_blueprints == null)
            {
                lock (LoadLock)
                {
                    _blueprints ??= Load(Folders);
                }
            }

            return _blueprints;
        }
    }

    /// <summary>Load (or reload) the ship types from the asset folder, checking them all.</summary>
    public static void Reload()
    {
        lock (LoadLock)
        {
            _blueprints = Load(Folders);
        }
    }

    /// <summary>
    /// Load the ship types from the folders. Each ship type comes from the
    /// first folder that defines it, and each model from the first folder that
    /// has it (so a mod's model replaces the game's for every ship that uses it).
    /// </summary>
    private static ShipBlueprint[] Load(IReadOnlyList<string> folders)
    {
        var blueprints = new ShipBlueprint?[Count];
        var models = new Dictionary<string, ShipModel>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException($"The ship assets folder '{folder}' is missing");
            }

            // The ship types this folder defines (an earlier folder's take precedence)
            var defined = new ShipBlueprint?[Count];
            foreach (string path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                // Name the mod's files in full, so it's clear which file is wrong
                string file = folders.Count > 1 ? path : Path.GetFileName(path);
                if (file.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var attributes = JsonSerializer.Deserialize<ShipAttributes>(File.ReadAllText(path), JsonOptions)
                        ?? throw new InvalidDataException("The file is empty");

                    string modelPath = folders
                        .Select(f => Path.GetFullPath(Path.Combine(f, attributes.Model)))
                        .FirstOrDefault(File.Exists)
                        ?? Path.GetFullPath(Path.Combine(folder, attributes.Model));
                    if (!models.TryGetValue(modelPath, out var model))
                    {
                        model = ShipModel.Load(modelPath);
                        models[modelPath] = model;
                    }

                    // The asset name is the file name without the type number prefix
                    string id = Path.GetFileNameWithoutExtension(file);
                    int dash = id.IndexOf('-');
                    if (dash > 0 && id[..dash].All(char.IsAsciiDigit))
                    {
                        id = id[(dash + 1)..];
                    }

                    var blueprint = new ShipBlueprint(id, attributes, model);
                    if (defined[blueprint.BlueprintNumber - 1] is { } existing)
                    {
                        throw new InvalidDataException($"Ship type {blueprint.BlueprintNumber} is already defined by '{existing.Id}'");
                    }

                    defined[blueprint.BlueprintNumber - 1] = blueprint;
                    blueprints[blueprint.BlueprintNumber - 1] ??= blueprint;
                }
                catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
                {
                    errors.Add($"{file}: {e.Message}");
                }
            }
        }

        for (int i = 0; i < Count; i++)
        {
            if (blueprints[i] == null && errors.Count == 0)
            {
                errors.Add($"No ship is defined for type {i + 1}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException("The ship assets are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        var used = blueprints.Select(b => b!.Model).ToHashSet();
        _modelPaths = models.Where(m => used.Contains(m.Value)).Select(m => m.Key).ToList();
        return blueprints!;
    }
}
