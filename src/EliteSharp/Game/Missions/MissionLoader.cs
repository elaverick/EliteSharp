using System.Globalization;
using EliteSharp.Game.Ships;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Game.Missions;

/// <summary>The mission files are invalid; the message lists every problem found.</summary>
public sealed class MissionLoadException(IReadOnlyList<string> errors)
    : Exception("The mission files are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    /// <summary>The problems, one per line, each giving the file, the mission and where in it the problem is.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Loads the mission definitions from the YAML files in a folder (see the
/// README in Assets/Missions for the format) and checks them thoroughly, so a
/// mistake in a mission file is reported when the game starts, rather than
/// when the mission happens.
/// </summary>
public static class MissionLoader
{
    /// <summary>The game's own folder containing the mission files.</summary>
    public static string DefaultFolder => Path.Combine(GameAssets.BaseFolder, "Missions");

    /// <summary>The mission file with the sequences that any mission can run.</summary>
    public const string CommonFile = "common.yml";

    /// <summary>The number of bits in the mission byte (TP).</summary>
    private const int MissionBits = 8;

    /// <summary>The ship names that the mission files can use (the ship asset names, such as "constrictor").</summary>
    private static IReadOnlyDictionary<string, int> ShipTypes =>
        ShipCatalogue.All.ToDictionary(b => b.Id, b => b.BlueprintNumber, StringComparer.Ordinal);

    /// <summary>
    /// Load and check the game's missions: the mod's (see <see cref="GameAssets"/>),
    /// if it has a Missions folder, or else the game's own. If the mod's missions
    /// don't include common.yml, they use the game's.
    /// </summary>
    public static MissionCatalogue LoadGame()
    {
        var folders = GameAssets.FoldersNamed("Missions");
        if (folders.Count == 0)
        {
            throw new MissionLoadException([$"The missions folder '{DefaultFolder}' is missing"]);
        }

        var files = ReadFolder(folders[0]);
        string common = Path.Combine(DefaultFolder, CommonFile);
        if (!files.Any(f => f.Name.Equals(CommonFile, StringComparison.OrdinalIgnoreCase)) && File.Exists(common))
        {
            files.Add((CommonFile, File.ReadAllText(common)));
            files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        }

        return Load(files);
    }

    /// <summary>Load and check all the mission files in a folder.</summary>
    public static MissionCatalogue Load(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new MissionLoadException([$"The missions folder '{folder}' is missing"]);
        }

        return Load(ReadFolder(folder));
    }

    /// <summary>Read the mission files in a folder, as (file name, contents), in order of their names.</summary>
    private static List<(string Name, string Text)> ReadFolder(string folder) =>
        Directory.EnumerateFiles(folder, "*.yml")
            .Order(StringComparer.Ordinal)
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToList();

    /// <summary>Load and check a set of mission files, given as (file name, contents), in the order the missions are checked.</summary>
    public static MissionCatalogue Load(IEnumerable<(string Name, string Text)> files)
    {
        var errors = new List<string>();
        var documents = new List<(string File, YamlMappingNode Root)>();
        foreach (var (name, text) in files)
        {
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(text));
                if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                {
                    errors.Add($"{name}: the file must contain a single mapping (a set of 'key: value' lines)");
                    continue;
                }

                documents.Add((name, root));
            }
            catch (YamlException e)
            {
                errors.Add($"{name} (line {e.Start.Line}): {e.Message}");
            }
            catch (InvalidOperationException e)
            {
                // YamlDotNet reports some malformed files this way
                errors.Add($"{name}: the file isn't valid YAML ({e.Message})");
            }
        }

        // First the shared sequences and the missions' names, stages and
        // values, which the steps and conditions refer to
        var context = new Context(errors, ShipTypes);
        var missionDocuments = new List<(string File, YamlMappingNode Root, MissionHeader Header)>();
        var sequenceDocuments = new List<(string File, YamlMappingNode Root)>();
        foreach (var (file, root) in documents)
        {
            if (root.Children.ContainsKey(new YamlScalarNode("id")))
            {
                if (context.ReadHeader(new Node(file, "", root)) is { } header)
                {
                    missionDocuments.Add((file, root, header));
                }
            }
            else
            {
                sequenceDocuments.Add((file, root));
            }
        }

        context.CheckHeaders(missionDocuments.Select(m => m.Header).ToList());
        foreach (var (file, root) in sequenceDocuments)
        {
            context.DeclareSequences(new Node(file, "", root));
        }

        foreach (var (file, root) in sequenceDocuments)
        {
            context.ReadSequences(new Node(file, "", root));
        }

        var missions = new List<MissionDefinition>();
        foreach (var (file, root, header) in missionDocuments)
        {
            if (context.ReadMission(new Node(file, $"mission '{header.Id}'", root), header) is { } mission)
            {
                missions.Add(mission);
            }
        }

        if (errors.Count > 0)
        {
            throw new MissionLoadException(errors);
        }

        return new MissionCatalogue(missions, context.Sequences);
    }

    /// <summary>A mission's id, stages and values, read before the rest so that other missions can refer to them.</summary>
    private sealed record MissionHeader(
        string Id,
        string File,
        IReadOnlyDictionary<string, Node> Fields,
        MissionProgress Progress,
        IReadOnlyDictionary<string, MissionValue> Values,
        IReadOnlyDictionary<string, Node> ValueNodes);

    /// <summary>A YAML node, with where it is for error messages.</summary>
    private readonly record struct Node(string File, string Owner, YamlNode Yaml, string Path = "")
    {
        public string Where
        {
            get
            {
                string owner = Owner.Length > 0 ? $", {Owner}" : "";
                string path = Path.Length > 0 ? $", {Path}" : "";
                return $"{File}{owner}{path} (line {Yaml.Start.Line})";
            }
        }

        public Node Child(string key, YamlNode yaml) => this with { Yaml = yaml, Path = Path.Length > 0 ? $"{Path}.{key}" : key };

        public Node Item(int index, YamlNode yaml) => this with { Yaml = yaml, Path = $"{Path}[{index}]" };
    }

    /// <summary>What is being checked, and what it can refer to.</summary>
    private sealed class Context(List<string> errors, IReadOnlyDictionary<string, int> shipTypes)
    {
        private readonly Dictionary<string, MissionHeader> _headers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Node> _sequenceNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SharedSequence> _sequences = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, SharedSequence> Sequences => _sequences;

        private void Error(Node node, string message) => errors.Add($"{node.Where}: {message}");

        // --------------------------------------------------------------------
        // Reading YAML
        // --------------------------------------------------------------------

        /// <summary>The node's entries, if it is a mapping with only the given keys (and has the required ones).</summary>
        private Dictionary<string, Node>? Mapping(Node node, string[] required, string[] optional)
        {
            if (node.Yaml is not YamlMappingNode mapping)
            {
                Error(node, $"expected a mapping (with {Describe(required, optional)})");
                return null;
            }

            var entries = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var (keyNode, value) in mapping.Children)
            {
                string key = (keyNode as YamlScalarNode)?.Value ?? "";
                if (!required.Contains(key) && !optional.Contains(key))
                {
                    Error(node.Child(key, keyNode), $"unknown field '{key}' (expected {Describe(required, optional)})");
                    continue;
                }

                entries[key] = node.Child(key, value);
            }

            foreach (string key in required.Where(k => !entries.ContainsKey(k)))
            {
                Error(node, $"the field '{key}' is missing");
            }

            return required.All(entries.ContainsKey) ? entries : null;
        }

        private static string Describe(string[] required, string[] optional) =>
            string.Join(", ", required.Select(k => $"'{k}'").Concat(optional.Select(k => $"'{k}' (optional)")));

        private List<Node>? Sequence(Node node)
        {
            if (node.Yaml is not YamlSequenceNode sequence)
            {
                Error(node, "expected a list");
                return null;
            }

            return [.. sequence.Children.Select((item, i) => node.Item(i, item))];
        }

        private string? Scalar(Node node)
        {
            if (node.Yaml is not YamlScalarNode scalar || scalar.Value == null)
            {
                Error(node, "expected a single value");
                return null;
            }

            return scalar.Value;
        }

        private int? Integer(Node node, int minimum, int maximum)
        {
            if (Scalar(node) is not { } text)
            {
                return null;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            {
                Error(node, $"expected a whole number from {minimum} to {maximum}, not '{text}'");
                return null;
            }

            return value;
        }

        private bool? Boolean(Node node)
        {
            string? text = Scalar(node);
            switch (text)
            {
                case "true":
                    return true;
                case "false":
                    return false;
                case null:
                    return null;
                default:
                    Error(node, $"expected true or false, not '{text}'");
                    return null;
            }
        }

        private static bool IsName(string text) =>
            text.Length > 0 && char.IsAsciiLetterLower(text[0]) && text.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

        // --------------------------------------------------------------------
        // Things that are referred to
        // --------------------------------------------------------------------

        private int? Ship(Node node)
        {
            if (Scalar(node) is not { } name)
            {
                return null;
            }

            if (!shipTypes.TryGetValue(name, out int type))
            {
                Error(node, $"there is no ship '{name}' (the ships are {string.Join(", ", shipTypes.Keys.Order())})");
                return null;
            }

            return type;
        }

        private int? Galaxy(Node node) => Integer(node, 1, GalaxyAtlas.GalaxyCount) is { } galaxy ? galaxy - 1 : null;

        private StarSystem? System(Node node, int galaxy)
        {
            if (Scalar(node) is not { } name)
            {
                return null;
            }

            var matches = GalaxyAtlas.Find(galaxy, name).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            Error(node, matches.Count == 0
                ? $"there is no system called '{name}' in galaxy {galaxy + 1}"
                : $"there is more than one system called '{name}' in galaxy {galaxy + 1}");
            return null;
        }

        /// <summary>A system given as a mapping with 'galaxy' and 'name'.</summary>
        private StarSystem? SystemInGalaxy(Node node)
        {
            if (Mapping(node, ["galaxy", "name"], []) is not { } fields || Galaxy(fields["galaxy"]) is not { } galaxy)
            {
                return null;
            }

            return System(fields["name"], galaxy);
        }

        private int? Stage(Node node, MissionHeader mission)
        {
            if (Scalar(node) is not { } name)
            {
                return null;
            }

            if (!mission.Progress.Stages.TryGetValue(name, out int stage))
            {
                Error(node, $"mission '{mission.Id}' has no stage '{name}' (its stages are {string.Join(", ", mission.Progress.Stages.Keys)})");
                return null;
            }

            return stage;
        }

        /// <summary>Some text, checking its references and characters.</summary>
        private MissionText? Text(Node node, MissionHeader? mission, IReadOnlySet<int>? galaxies)
        {
            if (Scalar(node) is not { } source)
            {
                return null;
            }

            MissionText text;
            try
            {
                text = MissionText.Parse(source);
            }
            catch (FormatException e)
            {
                Error(node, e.Message);
                return null;
            }

            CheckText(node, text, mission, galaxies, []);
            return text;
        }

        /// <summary>
        /// Check that text only uses characters the game can print, and that
        /// its references (and theirs) have values in the galaxies where the
        /// text can be shown (null for any galaxy).
        /// </summary>
        private void CheckText(Node node, MissionText text, MissionHeader? mission, IReadOnlySet<int>? galaxies, List<string> referencedFrom)
        {
            foreach (var part in text.Parts.Where(p => !p.IsReference))
            {
                if (part.Text.FirstOrDefault(c => c < ' ' || c > '~') is var bad and not '\0')
                {
                    Error(node, $"the text contains a character the game can't print (code {(int)bad}); use a separate line for each line of text");
                }
            }

            foreach (string name in text.References)
            {
                if (name == MissionText.CommanderName)
                {
                    continue;
                }

                if (mission == null)
                {
                    Error(node, $"'{{{{{name}}}}}': shared sequences can only use {{{{{MissionText.CommanderName}}}}}");
                    continue;
                }

                if (!mission.Values.TryGetValue(name, out var value))
                {
                    var known = mission.Values.Keys.Append(MissionText.CommanderName);
                    Error(node, $"'{{{{{name}}}}}' has no value (the values are {string.Join(", ", known)})");
                    continue;
                }

                if (referencedFrom.Contains(name))
                {
                    Error(node, $"'{{{{{name}}}}}' refers to itself (through {string.Join(" -> ", referencedFrom)})");
                    continue;
                }

                if (value is GalaxyValue byGalaxy)
                {
                    var missing = galaxies == null
                        ? Enumerable.Range(0, GalaxyAtlas.GalaxyCount).Where(g => !byGalaxy.ByGalaxy.ContainsKey(g)).ToList()
                        : galaxies.Where(g => !byGalaxy.ByGalaxy.ContainsKey(g)).Order().ToList();
                    if (missing.Count > 0)
                    {
                        Error(node, $"'{{{{{name}}}}}' has no value for galaxy {string.Join(", ", missing.Select(g => g + 1))}, where this text can be shown");
                    }
                }

                var inner = value switch
                {
                    FixedValue f => [f.Text],
                    GalaxyValue g => g.ByGalaxy.Where(e => galaxies == null || galaxies.Contains(e.Key)).Select(e => e.Value),
                    RandomValue r => r.Choices,
                    _ => [],
                };
                foreach (var innerText in inner)
                {
                    CheckText(mission.ValueNodes[name], innerText, mission, galaxies, [.. referencedFrom, name]);
                }
            }
        }

        // --------------------------------------------------------------------
        // Missions
        // --------------------------------------------------------------------

        private static readonly string[] MissionFields =
            ["id", "name", "progress", "requires", "values", "docking", "target", "shipRemoved", "encounters", "systemDescriptions"];

        public MissionHeader? ReadHeader(Node root)
        {
            var idNode = ((YamlMappingNode)root.Yaml).Children[new YamlScalarNode("id")];
            string? id = (idNode as YamlScalarNode)?.Value;
            string expected = Path.GetFileNameWithoutExtension(root.File);
            if (id != expected)
            {
                Error(root.Child("id", idNode), $"the mission's id is '{id}', but it must be the same as the file name ('{expected}')");
                return null;
            }

            root = root with { Owner = $"mission '{id}'" };
            if (Mapping(root, ["id", "name", "progress"], MissionFields) is not { } fields || Progress(fields["progress"]) is not { } progress)
            {
                return null;
            }

            var values = new Dictionary<string, MissionValue>(StringComparer.Ordinal);
            var valueNodes = new Dictionary<string, Node>(StringComparer.Ordinal);
            if (fields.TryGetValue("values", out var valuesNode) && valuesNode.Yaml is YamlMappingNode valuesMapping)
            {
                foreach (var (keyNode, valueNode) in valuesMapping.Children)
                {
                    string name = (keyNode as YamlScalarNode)?.Value ?? "";
                    var node = valuesNode.Child(name, valueNode);
                    if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                    {
                        Error(node, $"'{name}' isn't a valid value name (use letters, digits, '-' and '_')");
                        continue;
                    }

                    if (name == MissionText.CommanderName)
                    {
                        Error(node, $"'{name}' is built in, and can't be redefined");
                        continue;
                    }

                    if (ReadValueShape(node) is { } value)
                    {
                        values[name] = value;
                        valueNodes[name] = node;
                    }
                }
            }
            else if (fields.TryGetValue("values", out var badValues))
            {
                Error(badValues, "expected a mapping of value names to values");
            }

            return new MissionHeader(id, root.File, fields, progress, values, valueNodes);
        }

        /// <summary>Read a value, parsing its text without checking its references (which may refer to values not read yet).</summary>
        private MissionValue? ReadValueShape(Node node)
        {
            MissionText? Parse(Node textNode)
            {
                if (Scalar(textNode) is not { } source)
                {
                    return null;
                }

                try
                {
                    return MissionText.Parse(source);
                }
                catch (FormatException e)
                {
                    Error(textNode, e.Message);
                    return null;
                }
            }

            if (node.Yaml is YamlScalarNode)
            {
                return Parse(node) is { } text ? new FixedValue(text) : null;
            }

            if (node.Yaml is not YamlMappingNode mapping || mapping.Children.Count != 1)
            {
                Error(node, "expected some text, 'byGalaxy' (a mapping of galaxy numbers to text) or 'oneOf' (a list of text to choose from at random)");
                return null;
            }

            var (keyNode, valueNode) = mapping.Children.First();
            string kind = (keyNode as YamlScalarNode)?.Value ?? "";
            var inner = node.Child(kind, valueNode);
            switch (kind)
            {
                case "byGalaxy":
                {
                    if (valueNode is not YamlMappingNode byGalaxy)
                    {
                        Error(inner, "expected a mapping of galaxy numbers (1-8) to text");
                        return null;
                    }

                    var texts = new Dictionary<int, MissionText>();
                    foreach (var (galaxyNode, textNode) in byGalaxy.Children)
                    {
                        string key = (galaxyNode as YamlScalarNode)?.Value ?? "";
                        if (Galaxy(inner.Child(key, galaxyNode)) is { } galaxy && Parse(inner.Child(key, textNode)) is { } text)
                        {
                            texts[galaxy] = text;
                        }
                    }

                    return new GalaxyValue(texts);
                }

                case "oneOf":
                {
                    if (Sequence(inner) is not { } items)
                    {
                        return null;
                    }

                    if (items.Count is < 1 or > 256)
                    {
                        Error(inner, "expected from 1 to 256 choices");
                        return null;
                    }

                    var choices = items.Select(Parse).ToList();
                    return choices.All(c => c != null) ? new RandomValue(choices!) : null;
                }

                default:
                    Error(inner, $"unknown kind of value '{kind}' (expected 'byGalaxy' or 'oneOf')");
                    return null;
            }
        }

        private MissionProgress? Progress(Node node)
        {
            if (Mapping(node, ["bits", "stages"], []) is not { } fields || Sequence(fields["bits"]) is not { } bitNodes)
            {
                return null;
            }

            var bits = bitNodes.Select(b => Integer(b, 0, MissionBits - 1)).ToList();
            if (bits.Count == 0 || bits.Any(b => b == null))
            {
                if (bits.Count == 0)
                {
                    Error(fields["bits"], "expected at least one bit number");
                }

                return null;
            }

            int first = bits.Min()!.Value;
            if (bits.Distinct().Count() != bits.Count || bits.Max() - first + 1 != bits.Count)
            {
                Error(fields["bits"], "expected a list of neighbouring bit numbers (such as [0, 1])");
                return null;
            }

            if (fields["stages"].Yaml is not YamlMappingNode stagesMapping)
            {
                Error(fields["stages"], "expected a mapping of stage names to values");
                return null;
            }

            var stages = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (keyNode, valueNode) in stagesMapping.Children)
            {
                string name = (keyNode as YamlScalarNode)?.Value ?? "";
                var stageNode = fields["stages"].Child(name, valueNode);
                if (!IsName(name))
                {
                    Error(stageNode, $"'{name}' isn't a valid stage name (use lower case letters, digits and '-')");
                    continue;
                }

                if (Integer(stageNode, 0, (1 << bits.Count) - 1) is not { } value)
                {
                    continue;
                }

                if (stages.ContainsValue(value))
                {
                    Error(stageNode, $"the stage value {value} is used by more than one stage");
                    continue;
                }

                stages[name] = value;
            }

            if (!stages.ContainsValue(0))
            {
                Error(fields["stages"], "one stage must have the value 0 (a new commander's missions all start at 0)");
            }

            return new MissionProgress(first, bits.Count, stages);
        }

        /// <summary>Check that the missions' stages don't share bits of the mission byte.</summary>
        public void CheckHeaders(List<MissionHeader> headers)
        {
            foreach (var header in headers)
            {
                if (!_headers.TryAdd(header.Id, header))
                {
                    errors.Add($"{header.File}: there is more than one mission '{header.Id}'");
                }
            }

            for (int i = 0; i < headers.Count; i++)
            {
                for (int j = i + 1; j < headers.Count; j++)
                {
                    var a = headers[i].Progress;
                    var b = headers[j].Progress;
                    if (a.FirstBit < b.FirstBit + b.BitCount && b.FirstBit < a.FirstBit + a.BitCount)
                    {
                        errors.Add($"{headers[j].File}: mission '{headers[j].Id}' uses the same bits of the mission byte as mission '{headers[i].Id}'");
                    }
                }
            }
        }

        public MissionDefinition? ReadMission(Node root, MissionHeader header)
        {
            var fields = header.Fields;
            if (Scalar(fields["name"]) is not { } name)
            {
                return null;
            }

            int errorCount = errors.Count;

            // The values' text (their references can now be checked)
            foreach (var (valueName, value) in header.Values)
            {
                var texts = value switch
                {
                    FixedValue f => [f.Text],
                    GalaxyValue g => g.ByGalaxy.Values,
                    RandomValue r => r.Choices,
                    _ => [],
                };
                foreach (var text in texts)
                {
                    CheckText(header.ValueNodes[valueName], text, header, new HashSet<int>(), [valueName]);
                }
            }

            var requires = new List<StageRequirement>();
            if (fields.TryGetValue("requires", out var requiresNode))
            {
                if (requiresNode.Yaml is YamlMappingNode requiresMapping)
                {
                    foreach (var (keyNode, valueNode) in requiresMapping.Children)
                    {
                        string otherId = (keyNode as YamlScalarNode)?.Value ?? "";
                        var node = requiresNode.Child(otherId, valueNode);
                        if (otherId == header.Id)
                        {
                            Error(node, "a mission can't require itself (use the docking events' 'stage' instead)");
                        }
                        else if (!_headers.TryGetValue(otherId, out var other))
                        {
                            Error(node, $"there is no mission '{otherId}'");
                        }
                        else if (Stage(node, other) is { } stage)
                        {
                            requires.Add(new StageRequirement(otherId, stage));
                        }
                    }
                }
                else
                {
                    Error(requiresNode, "expected a mapping of mission ids to the stage each must be at");
                }
            }

            var dockingEvents = new List<DockingEvent>();
            if (fields.TryGetValue("docking", out var dockingNode) && Sequence(dockingNode) is { } dockingItems)
            {
                foreach (var item in dockingItems)
                {
                    if (DockingEvent(item, header) is { } dockingEvent)
                    {
                        dockingEvents.Add(dockingEvent);
                    }
                }
            }

            MissionTarget? target = null;
            if (fields.TryGetValue("target", out var targetNode))
            {
                target = Target(targetNode, header);
            }

            var removedEvents = new List<ShipRemovedEvent>();
            if (fields.TryGetValue("shipRemoved", out var removedNode) && Sequence(removedNode) is { } removedItems)
            {
                foreach (var item in removedItems)
                {
                    if (Mapping(item, ["ship", "steps"], []) is { } removedFields
                        && Ship(removedFields["ship"]) is { } type
                        && Steps(removedFields["steps"], header, null) is { } steps)
                    {
                        removedEvents.Add(new ShipRemovedEvent(type, steps));
                    }
                }
            }

            var encounters = new List<Encounter>();
            if (fields.TryGetValue("encounters", out var encountersNode) && Sequence(encountersNode) is { } encounterItems)
            {
                foreach (var item in encounterItems)
                {
                    if (Mapping(item, ["stage", "ship", "chanceIn256"], []) is { } encounterFields
                        && Stage(encounterFields["stage"], header) is { } stage
                        && Ship(encounterFields["ship"]) is { } type
                        && Integer(encounterFields["chanceIn256"], 1, 256) is { } chance)
                    {
                        encounters.Add(new Encounter(stage, type, chance));
                    }
                }
            }

            SystemDescriptions? descriptions = null;
            if (fields.TryGetValue("systemDescriptions", out var descriptionsNode))
            {
                descriptions = SystemDescriptions(descriptionsNode, header);
            }

            if (errors.Count > errorCount)
            {
                return null;
            }

            return new MissionDefinition(
                header.Id, name, header.File, header.Progress, requires, header.Values, dockingEvents, target, removedEvents, encounters, descriptions);
        }

        private DockingEvent? DockingEvent(Node node, MissionHeader mission)
        {
            if (Mapping(node, ["name", "when", "steps"], []) is not { } fields || Scalar(fields["name"]) is not { } name)
            {
                return null;
            }

            // Read the conditions and the steps, even if one has a problem, so
            // that every problem is reported
            bool ok = true;
            int? stage = null;
            HashSet<int>? galaxies = null;
            StarSystem? system = null;
            int minimumKillTally = 0;
            if (Mapping(fields["when"], ["stage"], ["galaxy", "system", "minimumKillTally"]) is { } when)
            {
                stage = Stage(when["stage"], mission);
                bool galaxiesOk = true;
                if (when.TryGetValue("galaxy", out var galaxyNode))
                {
                    var galaxyItems = galaxyNode.Yaml is YamlSequenceNode ? Sequence(galaxyNode) ?? [] : [galaxyNode];
                    galaxies = [.. galaxyItems.Select(Galaxy).OfType<int>()];
                    galaxiesOk = galaxies.Count == galaxyItems.Count && galaxies.Count > 0;
                    ok &= galaxiesOk;
                }

                if (when.TryGetValue("system", out var systemNode) && galaxiesOk)
                {
                    if (galaxies is not { Count: 1 })
                    {
                        Error(systemNode, "a system can only be given with a single 'galaxy'");
                        ok = false;
                    }
                    else
                    {
                        system = System(systemNode, galaxies.First());
                        ok &= system != null;
                    }
                }

                if (when.TryGetValue("minimumKillTally", out var tallyNode))
                {
                    int? tally = Integer(tallyNode, 0, 0xFFFF);
                    minimumKillTally = tally ?? 0;
                    ok &= tally != null;
                }
            }

            var steps = Steps(fields["steps"], mission, galaxies);
            if (!ok || stage == null || steps == null)
            {
                return null;
            }

            return new DockingEvent(name, new EventCondition(stage.Value, galaxies, system, minimumKillTally), steps);
        }

        private MissionTarget? Target(Node node, MissionHeader mission)
        {
            if (Mapping(node, ["ship", "system", "stage", "aggression", "ecm"], []) is not { } fields
                || Ship(fields["ship"]) is not { } type
                || SystemInGalaxy(fields["system"]) is not { } system
                || Stage(fields["stage"], mission) is not { } stage
                || Integer(fields["aggression"], 0, 63) is not { } aggression
                || Boolean(fields["ecm"]) is not { } ecm)
            {
                return null;
            }

            // The AI byte: bit 7 turns on the ship's tactics, bits 1-6 are its
            // aggression and bit 0 says whether it has an E.C.M.
            int ai = 0x80 | (aggression << 1) | (ecm ? 1 : 0);
            return new MissionTarget(type, system, stage, ai);
        }

        private SystemDescriptions? SystemDescriptions(Node node, MissionHeader mission)
        {
            if (Mapping(node, ["stages", "systems"], []) is not { } fields
                || Sequence(fields["stages"]) is not { } stageNodes
                || Sequence(fields["systems"]) is not { } systemNodes)
            {
                return null;
            }

            var stages = stageNodes.Select(s => Stage(s, mission)).ToList();
            var systems = new List<SystemDescription>();
            foreach (var item in systemNodes)
            {
                if (Mapping(item, ["galaxy", "system", "text"], []) is not { } entry || Galaxy(entry["galaxy"]) is not { } galaxy)
                {
                    continue;
                }

                if (System(entry["system"], galaxy) is { } system && Text(entry["text"], mission, new HashSet<int> { galaxy }) is { } text)
                {
                    if (systems.Any(s => s.System == system))
                    {
                        Error(entry["system"], $"{system.Name} already has a description");
                        continue;
                    }

                    systems.Add(new SystemDescription(system, text));
                }
            }

            return stages.All(s => s != null) ? new SystemDescriptions(stages.Select(s => s!.Value).ToHashSet(), systems) : null;
        }

        // --------------------------------------------------------------------
        // Sequences and steps
        // --------------------------------------------------------------------

        public void DeclareSequences(Node root)
        {
            if (Mapping(root, ["sequences"], []) is not { } fields)
            {
                errors.Add($"{root.File}: a file without an 'id' must be a shared file, with only 'sequences'");
                return;
            }

            if (fields["sequences"].Yaml is not YamlMappingNode sequences)
            {
                Error(fields["sequences"], "expected a mapping of sequence names to lists of steps");
                return;
            }

            foreach (var (keyNode, valueNode) in sequences.Children)
            {
                string name = (keyNode as YamlScalarNode)?.Value ?? "";
                var node = fields["sequences"].Child(name, valueNode);
                if (!name.All(char.IsAsciiLetterOrDigit) || name.Length == 0)
                {
                    Error(node, $"'{name}' isn't a valid sequence name (use letters and digits)");
                }
                else if (!_sequenceNodes.TryAdd(name, node))
                {
                    Error(node, $"the sequence '{name}' is already defined in {_sequenceNodes[name].File}");
                }
            }
        }

        public void ReadSequences(Node root)
        {
            foreach (var (name, node) in _sequenceNodes.Where(s => s.Value.File == root.File))
            {
                if (Steps(node, null, null) is { } steps)
                {
                    _sequences[name] = new SharedSequence(name, node.File, steps);
                }
            }
        }

        /// <summary>
        /// A list of steps. Shared sequences have no mission, so they can only
        /// use steps that don't depend on one. The galaxies are where the steps
        /// can run (null for any), for checking galaxy-dependent text.
        /// </summary>
        private List<MissionStep>? Steps(Node node, MissionHeader? mission, IReadOnlySet<int>? galaxies)
        {
            if (Sequence(node) is not { } items)
            {
                return null;
            }

            var steps = new List<MissionStep>();
            bool ok = true;
            foreach (var item in items)
            {
                if (Step(item, mission, galaxies) is { } step)
                {
                    steps.Add(step);
                }
                else
                {
                    ok = false;
                }
            }

            if (!ok)
            {
                return null;
            }

            // The ship shown by showShipUntilKey must be introduced first
            bool introduced = false;
            foreach (var (step, item) in steps.Zip(items))
            {
                introduced |= step is IntroduceShipStep;
                if (step is ShowShipUntilKeyStep && !introduced)
                {
                    Error(item, "'showShipUntilKey' needs an 'introduceShip' earlier in the same list of steps");
                    ok = false;
                }
            }

            return ok ? steps : null;
        }

        private static readonly string[] SimpleCommands = ["clearScreen", "waitForKey", "showShipUntilKey"];

        private static readonly string[] Commands =
        [
            "setStage", "changeStage", "addCash", "addKillTally", "fitEquipment", "run", "clearScreen", "text", "pause",
            "waitForKey", "introduceShip", "showShipUntilKey",
        ];

        private MissionStep? Step(Node node, MissionHeader? mission, IReadOnlySet<int>? galaxies)
        {
            string location = node.Where;
            if (node.Yaml is YamlScalarNode { Value: { } simple })
            {
                switch (simple)
                {
                    case "clearScreen":
                        return new ClearScreenStep { Location = location };
                    case "waitForKey":
                        return new WaitForKeyStep { Location = location };
                    case "showShipUntilKey":
                        return new ShowShipUntilKeyStep { Location = location };
                    default:
                        Error(node, Commands.Contains(simple)
                            ? $"'{simple}' needs a value ('{simple}: ...')"
                            : $"unknown command '{simple}' (the commands are {string.Join(", ", Commands)})");
                        return null;
                }
            }

            if (node.Yaml is not YamlMappingNode { Children.Count: 1 } mapping)
            {
                Error(node, "expected a command, such as 'clearScreen' or 'setStage: hunting'");
                return null;
            }

            var (keyNode, valueNode) = mapping.Children.First();
            string command = (keyNode as YamlScalarNode)?.Value ?? "";
            var value = node.Child(command, valueNode);

            if (mission == null && command is "setStage" or "changeStage" or "addCash" or "addKillTally" or "fitEquipment")
            {
                Error(value, $"'{command}' can only be used in a mission, not in a shared sequence");
                return null;
            }

            switch (command)
            {
                case "setStage":
                    return Stage(value, mission!) is { } stage ? new SetStageStep(stage) { Location = location } : null;

                case "changeStage":
                {
                    if (value.Yaml is not YamlMappingNode changes)
                    {
                        Error(value, "expected a mapping of stages to the stages they change to");
                        return null;
                    }

                    var map = new Dictionary<int, int>();
                    foreach (var (fromNode, toNode) in changes.Children)
                    {
                        string fromName = (fromNode as YamlScalarNode)?.Value ?? "";
                        if (Stage(value.Child(fromName, fromNode), mission!) is { } from && Stage(value.Child(fromName, toNode), mission!) is { } to)
                        {
                            map[from] = to;
                        }
                    }

                    return map.Count == changes.Children.Count ? new ChangeStageStep(map) { Location = location } : null;
                }

                case "addCash":
                {
                    if (Scalar(value) is not { } text)
                    {
                        return null;
                    }

                    // Cash is kept in tenths of a credit
                    if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal credits)
                        || credits <= 0 || credits > 100_000_000 || decimal.Round(credits, 1) != credits)
                    {
                        Error(value, $"expected an amount of credits (to one decimal place), not '{text}'");
                        return null;
                    }

                    return new AddCashStep((int)(credits * 10)) { Location = location };
                }

                case "addKillTally":
                    return Integer(value, 1, 0xFFFF) is { } amount ? new AddKillTallyStep(amount) { Location = location } : null;

                case "fitEquipment":
                {
                    string? name = Scalar(value);
                    switch (name)
                    {
                        case "navalEnergyUnit":
                            return new FitEquipmentStep(MissionEquipment.NavalEnergyUnit) { Location = location };
                        case null:
                            return null;
                        default:
                            Error(value, $"unknown equipment '{name}' (a mission can fit: navalEnergyUnit)");
                            return null;
                    }
                }

                case "run":
                {
                    if (Scalar(value) is not { } name)
                    {
                        return null;
                    }

                    if (!_sequenceNodes.ContainsKey(name))
                    {
                        Error(value, $"there is no shared sequence '{name}' (the sequences are {string.Join(", ", _sequenceNodes.Keys)})");
                        return null;
                    }

                    if (mission == null)
                    {
                        Error(value, "a shared sequence can't run another sequence");
                        return null;
                    }

                    return new RunSequenceStep(name) { Location = location };
                }

                case "text":
                {
                    if (Mapping(value, ["lines"], ["row", "column", "justify", "newlineAtEnd"]) is not { } fields
                        || Sequence(fields["lines"]) is not { } lineNodes)
                    {
                        return null;
                    }

                    bool hasRow = fields.TryGetValue("row", out var rowNode);
                    bool hasColumn = fields.TryGetValue("column", out var columnNode);
                    int? row = hasRow ? Integer(rowNode, 1, 23) : null;
                    int? column = hasColumn ? Integer(columnNode, 1, 32) : null;
                    bool? justify = fields.TryGetValue("justify", out var justifyNode) ? Boolean(justifyNode) : false;
                    bool? newlineAtEnd = fields.TryGetValue("newlineAtEnd", out var newlineNode) ? Boolean(newlineNode) : true;
                    var lines = lineNodes.Select(l => Text(l, mission, galaxies)).ToList();
                    if ((hasRow && row == null) || (hasColumn && column == null) || justify == null || newlineAtEnd == null
                        || lines.Any(l => l == null))
                    {
                        return null;
                    }

                    return new TextStep(row, column, justify.Value, lines!, newlineAtEnd.Value) { Location = location };
                }

                case "pause":
                {
                    if (Scalar(value) is not { } text)
                    {
                        return null;
                    }

                    // The pause is in seconds, and the game counts fiftieths
                    // of a second (the BBC's screen refreshes)
                    if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds)
                        || seconds * 50 != decimal.Round(seconds * 50) || seconds * 50 < 1 || seconds * 50 > 255)
                    {
                        Error(value, $"expected a number of seconds from 0.02 to 5.1, in fiftieths of a second, not '{text}'");
                        return null;
                    }

                    return new PauseStep((int)(seconds * 50)) { Location = location };
                }

                case "introduceShip":
                    return Ship(value) is { } type ? new IntroduceShipStep(type) { Location = location } : null;

                default:
                    Error(value, SimpleCommands.Contains(command)
                        ? $"'{command}' doesn't take a value (write it on its own)"
                        : $"unknown command '{command}' (the commands are {string.Join(", ", Commands)})");
                    return null;
            }
        }
    }
}
