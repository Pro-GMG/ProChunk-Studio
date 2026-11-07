using System.Windows;
using fNbt;
using ProChunk.App.Inspector;
using ProChunk.Core;
using ProChunk.Core.Items;
using ProChunk.Core.Nbt;

namespace ProChunk.App.Items;

/// <summary>Dünyadaki bir konteyner bloğunun (sandık, varil, shulker...) içeriğini açıp düzenler.</summary>
public static class ContainerAccess
{
    public static bool IsContainerBlock(string state)
    {
        var n = BlockState.BaseName(state);
        if (n.EndsWith("ender_chest")) return false; // Ender Chest içeriği oyuncuya aittir
        return n.EndsWith("chest") || n.EndsWith("barrel") || n.EndsWith("shulker_box") || n.EndsWith("hopper")
            || n.EndsWith("dispenser") || n.EndsWith("dropper") || n.EndsWith("furnace") || n.EndsWith("smoker")
            || n.EndsWith("brewing_stand") || n.EndsWith("crafter");
    }

    public static (int X, int Y, int Z)? Position(NbtCompound be) =>
        NbtIO.Int(be.Get("x")) is int x && NbtIO.Int(be.Get("y")) is int y && NbtIO.Int(be.Get("z")) is int z ? (x, y, z) : null;

    public static string Id(NbtCompound be) => be.Get<NbtString>("id")?.Value ?? "?";

    /// <summary>Dünya koordinatındaki konteyneri açar. Değişiklik onaylanırsa geri alınabilir şekilde yazar.</summary>
    public static void Open(Window owner, IEditorHost host, int x, int y, int z)
    {
        if (host.World is not { } world) return;
        int dim = host.Dimension, cx = x >> 4, cz = z >> 4;
        var volume = world.ReadChunk(dim, cx, cz);
        var state = volume?.GetBlock(x & 15, y, z & 15) ?? BlockState.Air;

        var list = world.ReadBlockEntities(dim, cx, cz);
        int index = list.FindIndex(e => Position(e) == (x, y, z));

        if (index < 0)
        {
            if (!IsContainerBlock(state))
            {
                MessageBox.Show(owner, $"{x}, {y}, {z} konumundaki blok bir konteyner değil:\n{state}", "Konteyner", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(owner, $"Bu {BlockState.BaseName(state)} için henüz içerik verisi yok. Boş bir içerik oluşturulsun mu?",
                    "Konteyner", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            list.Add(world.NewContainer(state, x, y, z));
            index = list.Count - 1;
        }

        var entity = (NbtCompound)list[index].Clone();
        var id = Id(entity);
        if (!entity.Contains("Items") && !ItemIds.IsContainer(id))
        {
            var raw = new Nbt.NbtEditorWindow(entity, $"{id} ({x}, {y}, {z})") { Owner = owner };
            if (raw.ShowDialog() != true) return;
        }
        else
        {
            var container = SlotContainer.Slotted(entity, "Items", $"{BlockState.BaseName(state).Replace("minecraft:", "")}  ({x}, {y}, {z})",
                ItemIds.ContainerSize(id), 0, world.Items);
            var win = new ContainerWindow($"{id} — {x}, {y}, {z}", container) { Owner = owner };
            if (win.ShowDialog() != true) return;
        }
        list[index] = entity;
        host.ApplyBlockEntities(cx, cz, list, $"{id} içeriği ({x}, {y}, {z})");
    }
}
