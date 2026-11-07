using fNbt;
using ProChunk.Core.Nbt;

namespace ProChunk.Core.Items;

/// <summary>Eşya NBT'sinin sürüme göre okunup yazılması.</summary>
public interface IItemCodec
{
    bool IsEmpty(NbtCompound? item);
    string GetId(NbtCompound item);
    void SetId(NbtCompound item, string id);
    int GetCount(NbtCompound item);
    void SetCount(NbtCompound item, int count);
    int GetDamage(NbtCompound item);
    void SetDamage(NbtCompound item, int damage);
    NbtCompound Create(string id, int count);
    /// <summary>Boş yuva için yazılacak yer tutucu (Bedrock); yoksa null (Java'da girdi silinir).</summary>
    NbtCompound? EmptyPlaceholder();
    /// <summary>Eşyanın kendi içeriği (Shulker Box vb.) varsa düzenlenebilir konteyner.</summary>
    SlotContainer? Nested(NbtCompound item);
}

public static class ItemIds
{
    public static bool IsShulker(string id) => id.Contains("shulker_box", StringComparison.Ordinal);

    /// <summary>Konteyner blok varlığının yuva sayısı (bilinmiyorsa 27).</summary>
    public static int ContainerSize(string blockEntityId)
    {
        var id = blockEntityId.ToLowerInvariant().Replace("minecraft:", "").Replace("_", "");
        return id switch
        {
            "dispenser" or "dropper" => 9,
            "hopper" => 5,
            "furnace" or "blastfurnace" or "smoker" => 3,
            "brewingstand" => 5,
            "chiseledbookshelf" => 6,
            "crafter" => 9,
            "campfire" or "soulcampfire" => 4,
            _ => 27,
        };
    }

    public static bool IsContainer(string blockEntityId)
    {
        var id = blockEntityId.ToLowerInvariant().Replace("minecraft:", "").Replace("_", "");
        return id is "chest" or "trappedchest" or "barrel" or "shulkerbox" or "dispenser" or "dropper" or "hopper"
            or "furnace" or "blastfurnace" or "smoker" or "brewingstand" or "crafter" or "chiseledbookshelf"
            || id.EndsWith("shulkerbox");
    }
}

public sealed class BedrockItemCodec : IItemCodec
{
    public static readonly BedrockItemCodec Instance = new();

    public bool IsEmpty(NbtCompound? item) =>
        item is null || string.IsNullOrEmpty(item.Get<NbtString>("Name")?.Value) || GetCount(item) <= 0;

    public string GetId(NbtCompound item) => item.Get<NbtString>("Name")?.Value ?? "";
    public void SetId(NbtCompound item, string id) => NbtIO.Set(item, new NbtString("Name", id));
    public int GetCount(NbtCompound item) => NbtIO.Int(item.Get("Count")) is int c ? (c & 0xFF) : 0;
    public void SetCount(NbtCompound item, int count) => NbtIO.Set(item, new NbtByte("Count", (byte)Math.Clamp(count, 0, 255)));
    public int GetDamage(NbtCompound item) => NbtIO.Int(item.Get("Damage")) ?? 0;
    public void SetDamage(NbtCompound item, int damage) => NbtIO.Set(item, new NbtShort("Damage", (short)damage));

    public NbtCompound Create(string id, int count) => new()
    {
        new NbtString("Name", id), new NbtByte("Count", (byte)Math.Clamp(count, 1, 255)),
        new NbtShort("Damage", 0), new NbtByte("WasPickedUp", 0),
    };

    public NbtCompound? EmptyPlaceholder() => new()
    {
        new NbtByte("Count", 0), new NbtShort("Damage", 0), new NbtString("Name", ""), new NbtByte("WasPickedUp", 0),
    };

    public SlotContainer? Nested(NbtCompound item)
    {
        if (!ItemIds.IsShulker(GetId(item))) return null;
        var tag = NbtIO.EnsureCompound(item, "tag");
        return SlotContainer.Slotted(tag, "Items", "Shulker Box içeriği", 27, 0, this);
    }
}

public sealed class JavaItemCodec : IItemCodec
{
    /// <summary>1.20.5+ (DataVersion 3837): "count" int ve "components".</summary>
    public bool Modern { get; }
    public JavaItemCodec(bool modern) { Modern = modern; }

    public bool IsEmpty(NbtCompound? item) => item is null || string.IsNullOrEmpty(GetId(item)) || GetCount(item) <= 0;
    public string GetId(NbtCompound item) => item.Get<NbtString>("id")?.Value ?? "";
    public void SetId(NbtCompound item, string id) => NbtIO.Set(item, new NbtString("id", id));

    public int GetCount(NbtCompound item) =>
        NbtIO.Int(item.Get("count")) ?? NbtIO.Int(item.Get("Count")) ?? 1;

    public void SetCount(NbtCompound item, int count)
    {
        count = Math.Clamp(count, 1, 99);
        if (Modern || item.Contains("count")) { item.Remove("Count"); NbtIO.Set(item, new NbtInt("count", count)); }
        else NbtIO.Set(item, new NbtByte("Count", (byte)count));
    }

    public int GetDamage(NbtCompound item) =>
        NbtIO.Int(item.Get<NbtCompound>("components")?.Get("minecraft:damage"))
        ?? NbtIO.Int(item.Get<NbtCompound>("tag")?.Get("Damage")) ?? 0;

    public void SetDamage(NbtCompound item, int damage)
    {
        if (Modern || item.Contains("components"))
        {
            var comp = NbtIO.EnsureCompound(item, "components");
            if (damage == 0) comp.Remove("minecraft:damage"); else NbtIO.Set(comp, new NbtInt("minecraft:damage", damage));
            if (comp.Count == 0) item.Remove("components");
        }
        else
        {
            var tag = NbtIO.EnsureCompound(item, "tag");
            NbtIO.Set(tag, new NbtInt("Damage", damage));
        }
    }

    public NbtCompound Create(string id, int count) => Modern
        ? new NbtCompound { new NbtString("id", id), new NbtInt("count", Math.Clamp(count, 1, 99)) }
        : new NbtCompound { new NbtString("id", id), new NbtByte("Count", (byte)Math.Clamp(count, 1, 99)) };

    public NbtCompound? EmptyPlaceholder() => null;

    public SlotContainer? Nested(NbtCompound item)
    {
        if (!ItemIds.IsShulker(GetId(item))) return null;
        if (!Modern && !item.Contains("components"))
        {
            var bet = NbtIO.EnsureCompound(NbtIO.EnsureCompound(item, "tag"), "BlockEntityTag");
            return SlotContainer.Slotted(bet, "Items", "Shulker Box içeriği", 27, 0, this);
        }

        // 1.20.5+: components."minecraft:container" = [{slot:int, item:{...}}]
        var comps = NbtIO.EnsureCompound(item, "components");
        NbtList List() => NbtIO.EnsureList(comps, "minecraft:container");
        return new SlotContainer("Shulker Box içeriği", 27, 9,
            i => List().OfType<NbtCompound>().FirstOrDefault(e => NbtIO.Int(e.Get("slot")) == i)?.Get<NbtCompound>("item"),
            (i, it) =>
            {
                var list = List();
                foreach (var e in list.OfType<NbtCompound>().Where(e => NbtIO.Int(e.Get("slot")) == i).ToList()) list.Remove(e);
                if (it is null || IsEmpty(it)) return;
                var copy = (NbtCompound)it.Clone();
                copy.Name = "item";
                copy.Remove("Slot");
                list.Add(new NbtCompound { new NbtInt("slot", i), copy });
            }, this);
    }
}

/// <summary>Düzenlenebilir yuva dizisi (envanter, sandık, shulker...).</summary>
public sealed class SlotContainer
{
    private readonly Func<int, NbtCompound?> _get;
    private readonly Action<int, NbtCompound?> _set;

    public string Title { get; }
    public int Size { get; }
    public int Columns { get; }
    public IItemCodec Codec { get; }
    public Func<int, string>? SlotLabel { get; init; }

    public SlotContainer(string title, int size, int columns, Func<int, NbtCompound?> get, Action<int, NbtCompound?> set, IItemCodec codec)
    {
        Title = title; Size = size; Columns = columns; _get = get; _set = set; Codec = codec;
    }

    public NbtCompound? Get(int i) { var it = _get(i); return Codec.IsEmpty(it) ? null : it; }
    public void Set(int i, NbtCompound? item) => _set(i, item is null || Codec.IsEmpty(item) ? null : item);

    /// <summary>"Slot" alanlı liste (envanter, sandık). Ekran yuvası i → Slot = i + offset.</summary>
    public static SlotContainer Slotted(NbtCompound parent, string listName, string title, int size, int slotOffset,
        IItemCodec codec, int columns = 9, Func<int, string>? labels = null)
    {
        return new SlotContainer(title, size, columns,
            i => parent.Get(listName) is NbtList l ? l.OfType<NbtCompound>().FirstOrDefault(e => NbtIO.Int(e.Get("Slot")) == i + slotOffset && !codec.IsEmpty(e)) : null,
            (i, item) =>
            {
                var list = NbtIO.EnsureList(parent, listName);
                int slot = i + slotOffset;
                foreach (var e in list.OfType<NbtCompound>().Where(e => NbtIO.Int(e.Get("Slot")) == slot).ToList()) list.Remove(e);
                var entry = item is null ? codec.EmptyPlaceholder() : (NbtCompound)item.Clone();
                if (entry is null) return;
                entry.Name = null;
                NbtIO.Set(entry, new NbtByte("Slot", unchecked((byte)(sbyte)slot)));
                list.Add(entry);
            }, codec) { SlotLabel = labels };
    }

    /// <summary>Sıralı, "Slot" alanı olmayan liste (Bedrock Armor / Offhand).</summary>
    public static SlotContainer Indexed(NbtCompound parent, string listName, string title, int size, IItemCodec codec,
        int columns = 1, Func<int, string>? labels = null)
    {
        NbtList List()
        {
            var l = NbtIO.EnsureList(parent, listName);
            while (l.Count < size) l.Add(codec.EmptyPlaceholder() ?? new NbtCompound());
            return l;
        }
        return new SlotContainer(title, size, columns,
            i => parent.Get(listName) is NbtList l && i < l.Count ? l[i] as NbtCompound : null,
            (i, item) =>
            {
                var l = List();
                var entry = item is null ? codec.EmptyPlaceholder() ?? new NbtCompound() : (NbtCompound)item.Clone();
                entry.Name = null;
                entry.Remove("Slot");
                l.RemoveAt(i);
                l.Insert(i, entry);
            }, codec) { SlotLabel = labels };
    }

    /// <summary>Anahtarlı compound (Java 1.21.5+ "equipment": head/chest/legs/feet/offhand).</summary>
    public static SlotContainer Keyed(NbtCompound parent, string compoundName, string title, string[] keys, IItemCodec codec,
        Func<int, string>? labels = null)
    {
        return new SlotContainer(title, keys.Length, 1,
            i => parent.Get<NbtCompound>(compoundName)?.Get<NbtCompound>(keys[i]),
            (i, item) =>
            {
                var c = NbtIO.EnsureCompound(parent, compoundName);
                c.Remove(keys[i]);
                if (item is null) return;
                var copy = (NbtCompound)item.Clone();
                copy.Name = keys[i];
                copy.Remove("Slot");
                c.Add(copy);
            }, codec) { SlotLabel = labels };
    }
}
