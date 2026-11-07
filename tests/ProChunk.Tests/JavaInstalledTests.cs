using ProChunk.Core;
using ProChunk.Java;

namespace ProChunk.Tests;

/// <summary>Bilgisayardaki gerçek Java dünyalarını (yeni dimensions/ düzeni dahil) açar.</summary>
public class JavaInstalledTests
{
    [Fact]
    public void InstalledJavaWorldsOpen()
    {
        var saves = JavaWorld.FindSaves().ToList();
        if (saves.Count == 0) return; // Java kurulu değil
        foreach (var (name, path) in saves)
        {
            IWorld w = JavaWorld.Open(path);
            Assert.True(w.GetRegions(0).Count > 0, $"{name}: chunk bulunamadı");
            w.GetPlayers();
        }
    }
}
