using ProChunk.Core;

namespace ProChunk.App;

/// <summary>Geri al / yinele yığını. Her işlem, etkilediği chunk'ların önceki halini saklar.</summary>
public sealed class EditHistory
{
    private sealed record Entry(string Description, int Dimension, List<(int X, int Z)> Chunks, object Snapshot);

    private readonly Stack<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private const int Limit = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.TryPeek(out var e) ? e.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var e) ? e.Description : null;

    public void Clear() { _undo.Clear(); _redo.Clear(); }

    /// <summary>İşlemi yapmadan hemen önce çağır.</summary>
    public void Record(IWorld world, int dimension, IEnumerable<(int X, int Z)> chunks, string description)
    {
        var list = chunks.Distinct().ToList();
        _undo.Push(new Entry(description, dimension, list, world.Snapshot(dimension, list)));
        _redo.Clear();
        if (_undo.Count > Limit)
        {
            var keep = _undo.Take(Limit).Reverse().ToList();
            _undo.Clear();
            foreach (var e in keep) _undo.Push(e);
        }
    }

    public (int Dimension, List<(int X, int Z)> Chunks, string Description)? Undo(IWorld world) => Move(world, _undo, _redo);
    public (int Dimension, List<(int X, int Z)> Chunks, string Description)? Redo(IWorld world) => Move(world, _redo, _undo);

    private static (int, List<(int, int)>, string)? Move(IWorld world, Stack<Entry> from, Stack<Entry> to)
    {
        if (!from.TryPop(out var e)) return null;
        to.Push(e with { Snapshot = world.Snapshot(e.Dimension, e.Chunks) });
        world.Restore(e.Snapshot);
        return (e.Dimension, e.Chunks, e.Description);
    }
}
