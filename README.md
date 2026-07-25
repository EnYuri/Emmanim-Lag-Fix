# CosmoteerModLoader

Inspired by [EML fork](https://github.com/ElectroJr/EnhancedModLoader).

Based (loosely) on [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop)

The managed C# project is built as a console app, so that dll had an entrypoint, but the resulting executable is not needed, only the dll.

The native C project is built using xmake. Rename the lib into winmm.dll.

## Usage

Put both `ModLoader.dll` and `winmm.dll` libs into the Cosmoteer Bin folder.

On linux rename the game's `Cosmoteer.dll` to `Cosmoteer_o.dll` and place ModPreLoader.dll in the Bin folder renaming it to `Cosmoteer.dll`. You can use `unmanaged.dll` instead of `winmm.dll` on linux.

## Mod development

Mod Loader will load all the dlls it finds in the folders of the enabled mods (workshop, user or built-in). First it loads dll named 0Harmony.dll, if it exists somewhere in thoose folders. If more than one file with such name exists, it will pick the first one it encounters. Then all the others dlls are loaded. The loader then looks through the loaded assemblies for the following function signatures:

```
public static void AssemblyLoadInitializer()
```

This method would be called immediately on assembly load. No game components exist yet, but all the assemblies are already loaded. This method can be used for applying harmony patches.

```
public static void GameLoadInitializer()
public static void InitializePatches()
```

These methods are called after the game has started. They can be used to add callbacks to the game director or accessing other game components. They are exactly the same and the second function is left for compatibility with EML mods.

Methods marked with `[UnamagedCallersOnly]` are also supported, but this is not required and will not add any benefits.

To start mod development it's best to install IDE, like Visual Strudio Community Edition or VS Code. The IDE should support C# development. Create a new C# Class library project. It should target the same .NET version as cosmoteer uses. Currently it is .NET 10, but this information might be outdated. Look at the cosmoteer logs to be sure, look for `.Net Runtime Version`. After that add the references to `Cosmoteer.dll` and `HalflingCore.dll`. You will need them to modify the game in any meaningful way. Most of the cosmoteer stuff is private, so you will need a publicizer, like [`Krafs.Publicizer`](https://github.com/krafs/Publicizer). Look at the documentation of that library, it requires some manual modification to the csproj file. To modify the existing cosmoteer C# code you will probably want to use [Harmony lib](https://github.com/pardeike/Harmony). You should reference it too. But do not include it with your mod, the loader provides it already.