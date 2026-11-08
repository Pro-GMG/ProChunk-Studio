
# ProChunk Studio

Minecraft world manager for Java and Bedrock (C# / .NET 10 WPF).
• Open .mcworld / .zip / world folders, automatically list installed Java and Bedrock worlds
• 2D map and 3D view, chunk delete / copy / paste / block replacement, undo / redo
• Player inventory, armor, and Ender Chest editing; chest and Shulker box contents
• level.dat and gamerule settings, NBT editor
• Chunk generation with the Minecraft engine (Bedrock Dedicated Server / official Java server.jar)

## Build

dotnet publish src/ProChunk.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
