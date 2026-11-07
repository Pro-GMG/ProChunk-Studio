namespace ProChunk.Core;

/// <summary>Blok durumu metni: "minecraft:oak_stairs[facing=east,half=bottom]".</summary>
public static class BlockState
{
    public const string Air = "minecraft:air";

    public static string BaseName(string state)
    {
        int i = state.IndexOf('[');
        return i < 0 ? state : state[..i];
    }

    public static string Normalize(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return Air;
        return s.Contains(':') ? s : "minecraft:" + s;
    }

    public static (string Name, List<(string Key, string Value)> Props) Parse(string state)
    {
        var props = new List<(string, string)>();
        int i = state.IndexOf('[');
        if (i < 0) return (state, props);
        var inner = state[(i + 1)..].TrimEnd(']');
        foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            props.Add((kv[0].Trim(), kv.Length > 1 ? kv[1].Trim() : ""));
        }
        return (state[..i], props);
    }

    public static string Format(string name, IEnumerable<(string Key, string Value)> props)
    {
        var list = props.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}").ToList();
        return list.Count == 0 ? name : $"{name}[{string.Join(",", list)}]";
    }
}
