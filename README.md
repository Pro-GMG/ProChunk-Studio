# ProChunk Studio

Java ve Bedrock için Minecraft dünya yöneticisi (C# / .NET 10 WPF).

- `.mcworld` / `.zip` / dünya klasörü açma, kurulu Java ve Bedrock dünyalarını otomatik listeleme
- 2D harita ve 3D görünüm, chunk silme / kopyalama / yapıştırma / blok değiştirme, geri al / yinele
- Oyuncu envanteri, zırh ve Ender Chest düzenleme; sandık ve Shulker kutusu içerikleri
- level.dat ve gamerule ayarları, NBT düzenleyici
- Minecraft motoruyla chunk üretimi (Bedrock Dedicated Server / resmi Java server.jar)

## Derleme

```
dotnet publish src/ProChunk.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```
