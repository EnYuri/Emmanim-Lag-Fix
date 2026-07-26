#define STEAM

using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

[assembly: AssemblyVersion("1.4.3.0")]
[assembly: AssemblyFileVersion("1.4.3.0")]
namespace ModLoader
{

    public partial class ModLoader
    {
        // Guid for harmony 2.4.2.0
        // Must be updated if another version of the library is shipped
        private static readonly Guid HarmonyGuid = new("dc2e7251-4b84-4883-90eb-eb05a041522c");
        private static readonly Guid ModLoaderGuid = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId;
        private static readonly string ModLoaderName = Assembly.GetExecutingAssembly().GetName().Name ?? string.Empty;

        private static Dictionary<MethodInfo, Guid> DelayedInitMethods = [];

        private static Dictionary<Guid, (string file, bool duplicated, bool consented, bool fromTrusted, string? error)> LibraryFiles = [];
        private static Dictionary<Halfling.IO.AbsolutePath, HashSet<Guid>> EncounteredLibraries = [];
        private static Dictionary<Halfling.IO.AbsolutePath, ValueTuple<string, HashSet<ValueTuple<string, Guid>>>> TrustedLibraries = [];
        private static HashSet<Guid> KnownModLibraries = [];
        private static HashSet<Guid> LibrariesInContext = [];
        private static HashSet<Halfling.IO.AbsolutePath> TrustedMods = [];
        private static HashSet<Halfling.IO.AbsolutePath> ModsWithProblematicLibs = [];
        private static bool showErrorMessageOnce = false;

        /// <summary>
        /// This is a dummy method to call functions that has UnmanagedCallersOnly attributes
        /// </summary>
        /// <param name="funcPtr">The pointer to the function to be called</param>
        private static readonly Action<IntPtr> CallFromUnmanaged;

        [LibraryImport("winmm.dll", EntryPoint = "CallFromUnmanaged")]
        private static unsafe partial void CallFromUnmanagedWindows(IntPtr funcPtr);

        [LibraryImport("unmanaged.dll", EntryPoint = "CallFromUnmanaged")]
        private static unsafe partial void CallFromUnmanagedLinux(IntPtr funcPtr);


        static ModLoader() {
            try
            {
                Marshal.Prelink(((Action<IntPtr>)CallFromUnmanagedWindows).Method);
                CallFromUnmanaged = CallFromUnmanagedWindows;
            }
            catch (Exception)
            {
                // ignore
            }
            try
            {
                Marshal.Prelink(((Action<IntPtr>)CallFromUnmanagedLinux).Method);
                CallFromUnmanaged = CallFromUnmanagedLinux;
            }
            catch (Exception)
            {
                // ignore
            }
            if (CallFromUnmanaged == null)
            {
                throw new DllNotFoundException("Could not find a dll with the correct native method");
            }
        }


        /// <summary>
        /// The main entrypoint. Here we do the minimum game init to get the location of the settings file.
        /// </summary>
        [STAThread]
        static public void Main(string[] argv)
        {
            if (Cosmoteer.GameApp.IsNoModsMode)
            {
                return;
            }

            // we need steam to get steamid for the settings file location
            Cosmoteer.Steamworks.Steam.Init();
            // we need to remove the callback that was added by init, or the game will not launch
            // (it will try to add it again)
            foreach (var callback in Cosmoteer.Steamworks.Steam.s_callbacks.Select(pair => pair.Value as IDisposable))
                callback?.Dispose();
            Cosmoteer.Steamworks.Steam.s_callbacks.Clear();

            // also needed for settings file location
            Halfling.App.Platform = Halfling.Platforms.Platform.Create();

            // create a logfile that will be used during the mod loader work
            Directory.CreateDirectory(Cosmoteer.Paths.LogsFolder);
            var loggerWriter = Halfling.Logging.Logger.SetupLogOutputFile(Cosmoteer.Paths.LogsFolder / $"log{DateTime.Now:yyyy-MM-dd HH_mm_ss}_modloader.txt");

            // if no settings file exists, no mods are enabled, skip the load
            if (!File.Exists(Cosmoteer.Paths.SettingsFile))
            {
                Halfling.Logging.Logger.Log($"Setting file not found: {Cosmoteer.Paths.SettingsFile}\nMod loading will not continue.");
            } else
            {
                LoadLibs();
            }

            // stop writing to the local log
            Halfling.Logging.Logger.UnregisterLogOutputWriter(loggerWriter);

            // start the actual game
            Cosmoteer.GameApp.Main(argv);
        }

        static void AddTrustedLibrary(Cosmoteer.Mods.ModInfo mod, string libName, Guid libGuid)
        {
            if (!TrustedLibraries.TryGetValue(mod.Folder, out var value))
            {
                value = (mod.Version ?? string.Empty, []);
                TrustedLibraries[mod.Folder] = value;
            }
            value.Item2.RemoveWhere(lib => lib.Item1 == libName);
            value.Item2.Add((libName, libGuid));
        }

        static bool CheckTrustedWithUpdate(Cosmoteer.Mods.ModInfo mod, string libName, Guid libGuid)
        {
            if (TrustedLibraries.TryGetValue(mod.Folder, out var modItem))
            {
                // first let's check if the lib is there, this is the most common situation
                if (modItem.Item2.Any(lib => lib.Item1 == libName && lib.Item2 == libGuid))
                {
                    return true;
                }

                // if the name is the same, but the GUID has changed
                if (modItem.Item2.Any(lib => lib.Item1 == libName))
                {
                    modItem.Item2.RemoveWhere(lib => lib.Item1 == libName);
                    // if the mod version got updated we consider it normal
                    if (mod.Version != modItem.Item1)
                    {
                        modItem.Item2.Add((libName, libGuid));
                        Halfling.Logging.Logger.Log($"Library {libName} for the mod {mod.Name} was updated to the new version");
                        return true;
                    }
                    // otherwise not
                    else
                    {
                        Halfling.Logging.Logger.Log($"Library {libName} for the mod {mod.Name} has changed, while the mod version remained the same.");
                        Halfling.Logging.Logger.Log("This is considered a suspicios behavior, the library is not trusted anymore.");
                        return false;
                    }
                }
            }

            // support for the old config
            if (KnownModLibraries.Contains(libGuid))
            {
                AddTrustedLibrary(mod, libName, libGuid);
                Halfling.Logging.Logger.Log($"Library {libName} for the mod {mod.Name} was imported from the old config");
                return true;
            }

            return false;
        }

        static void UpdateModVersion(Cosmoteer.Mods.ModInfo mod)
        {
            if (TrustedLibraries.TryGetValue(mod.Folder, out var modItem) && modItem.Item1 != mod.Version)
            {
                var oldVersion = modItem.Item1;
                TrustedLibraries[mod.Folder] = (mod.Version ?? string.Empty, modItem.Item2);
                Halfling.Logging.Logger.Log($"Mod {mod.Name} was updated from version {oldVersion} to {mod.Version}");

            }
        }

        /// <summary>
        /// Scans the given folder in search for dll files
        /// </summary>
        /// <param name="mod">The mod to scan</param>
        /// <returns>path for harmony lib, if found</returns>
        static private string? LoadLibsForMod(Cosmoteer.Mods.ModInfo mod)
        {
            EncounteredLibraries[mod.Folder] = [];
            bool isModLoader = false;
            string? harmonyFile = null;

            foreach (var file in Directory.EnumerateFiles(mod.Folder, "*.dll", SearchOption.AllDirectories))
            {
                Halfling.Logging.Logger.Log($"found dll file {file}");

                try
                {
                    var peReader = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(file));
                    if (!peReader.HasMetadata)
                    {
                        Halfling.Logging.Logger.Log($"File {file} doesn't have an assembly metadata, ignored");
                        continue;
                    }
                    var mdReader = peReader.GetMetadataReader();
                    if (!mdReader.IsAssembly)
                    {
                        Halfling.Logging.Logger.Log($"File {file} is not an assembly, ignored");
                        continue;
                    }
                    var guid = mdReader.GetGuid(mdReader.GetModuleDefinition().Mvid);
                    if (guid == ModLoaderGuid)
                    {
                        Halfling.Logging.Logger.Log($"Library {file} is a mod-loader library, ignored");
                        isModLoader = true;
                        continue;
                    }
                    var libName = AssemblyName.GetAssemblyName(file).Name ?? string.Empty;
                    if (libName == string.Empty)
                    {
                        Halfling.Logging.Logger.Log($"File {file} is an assembly with empty name, ignored");
                        continue;
                    }
                    if (libName == "0Harmony" || guid == HarmonyGuid)
                    {
                        if (guid != HarmonyGuid)
                        {
                            Halfling.Logging.Logger.Log($"Found harmony library {file} with incorrect GUID");
                            Halfling.Logging.Logger.Log($"{guid}");
                            Halfling.Logging.Logger.Log($"The file loading is disabled for security reasons");
                        } else
                        {
                            harmonyFile = file;
                        }
                        continue;
                    }

                    // add the library to the list here
                    EncounteredLibraries[mod.Folder].Add(guid);

                    // first check if the library is already in context
                    // this could happen if the user disables and then enables the mod
                    // trust the libraries automatically, since they were already trusted once
                    if (LibrariesInContext.Contains(guid))
                    {
                        TrustedLibraries[mod.Folder].Item2.RemoveWhere(lib => lib.Item1 == Path.GetFileName(file));
                        TrustedLibraries[mod.Folder].Item2.Add((Path.GetFileName(file), guid));
                    }
                    // next we see if we already encountered this library in another mod
                    // in which case the file is marked as duplicated and ignored
                    else if (LibraryFiles.TryGetValue(guid, out var lib))
                    {
                        if (file != lib.file)
                        {
                            Halfling.Logging.Logger.Log($"Library {file} duplicates another mod library {lib.file}");
                            Halfling.Logging.Logger.Log("This may signal a suspicious behaviour, both libraries are disabled");
                            lib.duplicated = true;
                            lib.error = $"Two or more mods have the same library {Path.GetFileName(file)}";
                            LibraryFiles[guid] = lib;

                            // mark both mods as problematic to prevent other libs from these mods to load
                            Halfling.Collections.ExtensionsHashSet.AddRange(ModsWithProblematicLibs, EncounteredLibraries.Where(kvp => kvp.Value.Contains(guid)).Select(kvp => kvp.Key));
                        }
                    }
                    // next we check if the mod is trusted, which means all the libs gets trusted automatically
                    else if (TrustedMods.Contains(mod.Folder))
                    {
                        LibraryFiles.Add(guid, (file: file, duplicated: false, consented: true, fromTrusted: true, error: default));
                    }
                    // next we check if the library is trusted, which includes updates for updated mod version
                    else if (CheckTrustedWithUpdate(mod, Path.GetFileName(file), guid))
                    {
                        LibraryFiles.Add(guid, (file: file, duplicated: false, consented: true, fromTrusted: false, error: default));
                    }
                    // finally we deal with unknown libraries
                    else
                    {
                        ModsWithProblematicLibs.Add(mod.Folder);
                        if (libName == ModLoaderName)
                        {
                            LibraryFiles.Add(guid, (file: file, duplicated: false, consented: false, fromTrusted: false, error: $"Unknown library {Path.GetFileName(file)}, it appears to be a newer version of the ModLoader, please repeat the installation procedure"));
                            Halfling.Logging.Logger.Log($"Library {file} is not in the list of known assemblies, ignored");
                        }
                        else
                        {
                            LibraryFiles.Add(guid, (file: file, duplicated: false, consented: false, fromTrusted: false, error: $"Unknown library {Path.GetFileName(file)}, open the mods list and trust the libraries for the relevant mod"));
                            Halfling.Logging.Logger.Log($"Library {file} is not in the list of known assemblies, ignored");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Halfling.Logging.Logger.Log($"failed to load lib from {file}, exception\n{ex}");
                }
            }
            if (isModLoader)
            {
                // remove all the libs from the list
                foreach (var guid in EncounteredLibraries[mod.Folder])
                {
                    LibraryFiles.Remove(guid);
                }
                EncounteredLibraries[mod.Folder].Clear();
            }

            if (EncounteredLibraries[mod.Folder].Count == 0)
            {
                EncounteredLibraries.Remove(mod.Folder);
            }

            return harmonyFile;
        }

        /// <summary>
        /// Finds and loads mod libraries into the context
        /// </summary>
        static public void LoadLibs()
        {
            try
            {
                var settingsFile = new Halfling.ObjectText.OTFile(Cosmoteer.Paths.SettingsFile);

                Halfling.Logging.Logger.Log($"Reading mod settings from {settingsFile}");

                settingsFile.MakeAtPath("GameSettings");
                var serializer = new Halfling.Serialization.ObjectText.ObjectTextSerializer(true);
                var reader = serializer.GetGenericReaderForPath(settingsFile, "GameSettings");
                var enabledMods = reader.ReadFromPath<HashSet<Halfling.IO.AbsolutePath>>(nameof(Cosmoteer.Settings.EnabledMods));
                if (reader.HasPath(nameof(TrustedLibraries)))
                {
                    TrustedLibraries = reader.ReadFromPath<Dictionary<Halfling.IO.AbsolutePath, ValueTuple<string, HashSet<ValueTuple<string, Guid>>>>>(nameof(TrustedLibraries));
                                             //.Select(kvp => new KeyValuePair<Halfling.IO.AbsolutePath, ValueTuple<string, HashSet<ValueTuple<string, Guid>>>>(kvp.Key, (kvp.Value.Item1, [.. kvp.Value.Item2.Select(item => (item.Item1, new Guid(item.Item2)))])))
                                             //.ToDictionary();
                }
                // support for the old config
                else if (reader.HasPath(nameof(KnownModLibraries)))
                {
                    KnownModLibraries = reader.ReadFromPath<HashSet<Guid>>(nameof(KnownModLibraries));
                }

                if (reader.HasPath(nameof(TrustedMods)))
                {
                    TrustedMods = reader.ReadFromPath<HashSet<Halfling.IO.AbsolutePath>>(nameof(TrustedMods));
                }

                string? harmonyLib = null;

                HashSet<Halfling.IO.AbsolutePath> validMods = [];

                foreach (Halfling.IO.AbsolutePath modFolder in enabledMods)
                {
                    if (!Directory.Exists(modFolder))
                    {
                        Halfling.Logging.Logger.Log($"Found non-existent mod folder {modFolder}");
                        continue;
                    }

                    Cosmoteer.Mods.ModInfo modInfo;

                    try
                    {
                        var otFile = new Halfling.ObjectText.OTFile(Cosmoteer.Mods.ModInfo.GetModInfoPath(modFolder)?.ToString() ?? string.Empty);
                        modInfo = new Cosmoteer.Mods.ModInfo(modFolder, Cosmoteer.Mods.ModInstallSource.User, null, serializer.CreateGenericSerialReader(otFile), false);
                    }
                    catch (Exception ex)
                    {
                        Halfling.Logging.Logger.Log($"Error reading mod folder {modFolder}:\n{ex}");
                        continue;
                    }

                    if (string.IsNullOrEmpty(modInfo.Version))
                    {
                        // version is optional, but we use it for update detection
                        modInfo.Version = "unknown";
                    }
                    validMods.Add(modFolder);
                    var file = LoadLibsForMod(modInfo);
                    if (file != null)
                    {
                        if (harmonyLib == null)
                        {
                            harmonyLib = file;
                            Halfling.Logging.Logger.Log($"Found Harmony lib {file}");
                        }
                        else
                        {
                            Halfling.Logging.Logger.Log($"Found duplicated Harmony lib {file}, ignored");
                        }
                    }
                    if (TrustedLibraries.TryGetValue(modInfo.Folder, out var value))
                    {
                        // remove all the trusted libraries that are no longer present
                        value.Item2.RemoveWhere(lib => !LibraryFiles.Where(kvp => kvp.Value.fromTrusted == false).Any(kvp => kvp.Key == lib.Item2));
                        if (value.Item1 != modInfo.Version)
                        {
                            var oldVersion = value.Item1;
                            TrustedLibraries[modInfo.Folder] = (modInfo.Version ?? "unknown", value.Item2);
                            Halfling.Logging.Logger.Log($"Mod {modInfo.Name} was updated from version {oldVersion} to {modInfo.Version}");
                        }
                    }
                }

                // remove known libraries for mods that are no longer present
                var toRemove = TrustedLibraries.Keys.Where(key => !validMods.Contains(key)).ToArray();
                foreach (var item in toRemove)
                {
                    TrustedLibraries.Remove(item);
                }

                // remove trusted mods that were disabled or removed
                TrustedMods.IntersectWith(validMods);

                // if no harmony found, that means the mod loader is disabled or broken
                // skip the load, some libs might break anyway
                if (harmonyLib == null)
                {
                    Halfling.Logging.Logger.Log($"Harmony lib not found.\nMod loading will not continue.");
                    return;
                }

                try
                {
                    AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", false);
                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(harmonyLib);
                    Halfling.Logging.Logger.Log($"loaded harmony lib from {harmonyLib}");
                    PatchMethods(assembly);
                }
                catch (Exception ex)
                {
                    Halfling.Logging.Logger.Log($"failed to load harmony lib from {harmonyLib}, exception\n{ex}");
                }

                static bool isMethodPreInit(MethodInfo method) => method.Name == "AssemblyLoadInitializer" && method.GetParameters().Length == 0 && method.ReturnType == typeof(void);
                static bool isMethodPostInit(MethodInfo method) => method.Name == "GameLoadInitializer" && method.GetParameters().Length == 0 && method.ReturnType == typeof(void);
                static bool isMethodEMLInit(MethodInfo method) => method.Name == "InitializePatches" && method.GetParameters().Length == 0; // EML doesn't require void return

                foreach (var (guid, file) in LibraryFiles.Where(lib => !lib.Value.duplicated && lib.Value.consented).Select(lib => (lib.Key, lib.Value.file)).ToList())
                {
                    var mod = EncounteredLibraries.First(kvp => kvp.Value.Contains(guid)).Key;
                    if (ModsWithProblematicLibs.Contains(mod))
                    {
                        Halfling.Logging.Logger.Log($"Library {file} was not loaded, because some other files from the mod have problems.");
                        continue;
                    }

                    try
                    {
                        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
                        Halfling.Logging.Logger.Log($"loaded mod lib from {file}");

                        foreach (var type in assembly.GetTypes())
                        {
                            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            {
                                if (isMethodPreInit(method))
                                {
                                    // EML required a special attribute, which prevents calling from managed code
                                    if (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>() == null)
                                    {
                                        method.Invoke(null, null);
                                        Halfling.Logging.Logger.Log($"called init method {method.Name} for mod lib {file}");
                                    }
                                    else
                                    {
                                        CallFromUnmanaged(method.MethodHandle.GetFunctionPointer());
                                        Halfling.Logging.Logger.Log($"called unmanaged init method {method.Name} for mod lib {file}");
                                    }
                                }

                                if (isMethodPostInit(method) || isMethodEMLInit(method))
                                {
                                    DelayedInitMethods.Add(method, guid);
                                }

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Halfling.Logging.Logger.Log($"failed to load mod lib from {file}, exception\n{ex}");
                        if (LibraryFiles.TryGetValue(guid, out var modlib))
                        {
                            modlib.error = ex.Message;
                            LibraryFiles[guid] = modlib;
                        }

                        // mark mod as problematic to skip other libraries from it
                        ModsWithProblematicLibs.Add(mod);
                    }
                }

                LibrariesInContext = [.. AssemblyLoadContext.Default.Assemblies.Select(a => a.ManifestModule.ModuleVersionId)];

                Halfling.Logging.Logger.Log("Mod loading complete. Starting the game now.");
                return;
            }
            catch (Exception ex)
            {
                Halfling.Logging.Logger.Log($"Exception during mod loading:\n{ex}");
                Halfling.Logging.Logger.Log($"Game will attempt to load without mods");
                return;
            }
        }

        /// <summary>
        /// This function asks harmony to patch various cosmoteer methods
        /// 
        /// It utilizes advanced reflection magic to keep this assembly independent from 0Harmony.dll
        /// 
        /// Harmony is still needed, but with this we can load it into context manually.
        /// Also it won't crash if for some reason harmony is not found.
        /// </summary>
        /// <param name="harmony">Loaded harmony assembly</param>
        static void PatchMethods(Assembly harmony)
        {
            var classHarmony = harmony.GetType("HarmonyLib.Harmony");
            var classHarmonyMethod = harmony.GetType("HarmonyLib.HarmonyMethod");

            var classHarmonyConstructor = classHarmony?.GetConstructor([typeof(string)]);
            var harmonyMethodConstructor = classHarmonyMethod?.GetConstructor([typeof(MethodInfo)]);

            var titleScreenConstructor = typeof(Cosmoteer.Gui.TitleScreen).GetConstructor([]);
            var titleScreenTranspilerMethodInfo = typeof(ModLoader).GetMethod(nameof(TitleScreenTranspiler), BindingFlags.Static | BindingFlags.NonPublic);
            var titleScreenTranspilerHarmonyMethod = harmonyMethodConstructor?.Invoke([titleScreenTranspilerMethodInfo]);

            var populateModList = typeof(Cosmoteer.Mods.ModInfo).GetMethod(nameof(Cosmoteer.Mods.ModInfo.LoadMods));
            var modListPostfixMethodInfo = typeof(ModLoader).GetMethod(nameof(ModListPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var modListPostfixHarmonyMethod = harmonyMethodConstructor?.Invoke([modListPostfixMethodInfo]);

            var tryLoadMod = typeof(Cosmoteer.Mods.ModInfo).GetMethod(nameof(Cosmoteer.Mods.ModInfo.TryLoadMod));
            var tryLoadModPrefixMethodInfo = typeof(ModLoader).GetMethod(nameof(TryLoadModPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var tryLoadModPrefixHarmonyMethod = harmonyMethodConstructor?.Invoke([tryLoadModPrefixMethodInfo]);

            var onModSelected = typeof(Cosmoteer.Gui.ModsDialog).GetMethod(nameof(Cosmoteer.Gui.ModsDialog.OnModSelected), BindingFlags.Instance | BindingFlags.NonPublic);
            var onModSelectedPrefixMethodInfo = typeof(ModLoader).GetMethod(nameof(OnModSelectedPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var onModSelectedPrefixHarmonyMethod = harmonyMethodConstructor?.Invoke([onModSelectedPrefixMethodInfo]);
            var onModSelectedPostfixMethodInfo = typeof(ModLoader).GetMethod(nameof(OnModSelectedPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var onModSelectedPostfixHarmonyMethod = harmonyMethodConstructor?.Invoke([onModSelectedPostfixMethodInfo]);

            var refreshToggleButtons = typeof(Cosmoteer.Gui.ModsDialog).GetMethod(nameof(Cosmoteer.Gui.ModsDialog.RefreshToggleButtons), BindingFlags.Instance | BindingFlags.NonPublic);
            var refreshToggleButtonsPostfixMethodInfo = typeof(ModLoader).GetMethod(nameof(RefreshToggleButtonsPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var refreshToggleButtonsPostfixHarmonyMethod = harmonyMethodConstructor?.Invoke([refreshToggleButtonsPostfixMethodInfo]);

            var settingsWriteTo = typeof(Cosmoteer.Settings).GetMethod(nameof(Cosmoteer.Settings.WriteTo));
            var settingsWritePostfixMethodInfo = typeof(ModLoader).GetMethod(nameof(SettingsWritePostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var settingsWritePostfixHarmonyMethod = harmonyMethodConstructor?.Invoke([settingsWritePostfixMethodInfo]);

            var applicationMain = typeof(Halfling.Application.Bases.GenericApp).GetMethod(nameof(Halfling.Application.Bases.GenericApp.ApplicationMain));
            var applicationMainPrefixMethodInfo = typeof(ModLoader).GetMethod(nameof(ApplicationMainPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var applicationMainPrefixHarmonyMethod = harmonyMethodConstructor?.Invoke([applicationMainPrefixMethodInfo]);

            var getCommandLineArgs = typeof(Environment).GetMethod(nameof(Environment.GetCommandLineArgs));
            var getCommandLineArgsPrefixMethodInfo = typeof(ModLoader).GetMethod(nameof(GetCommandLineArgsPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var getCommandLineArgsPrefixHarmonyMethod = harmonyMethodConstructor?.Invoke([getCommandLineArgsPrefixMethodInfo]);


            var harmonyObj = classHarmonyConstructor?.Invoke(["Cosmoteer.ModLoader"]);
            var harmonyPatchMethod = classHarmony?.GetMethod("Patch");

            harmonyPatchMethod?.Invoke(harmonyObj, [titleScreenConstructor, null, null, titleScreenTranspilerHarmonyMethod, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [populateModList, null, modListPostfixHarmonyMethod, null, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [tryLoadMod, tryLoadModPrefixHarmonyMethod, null, null, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [onModSelected, onModSelectedPrefixHarmonyMethod, onModSelectedPostfixHarmonyMethod, null, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [refreshToggleButtons, null, refreshToggleButtonsPostfixHarmonyMethod, null, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [settingsWriteTo, null, settingsWritePostfixHarmonyMethod, null, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [applicationMain, applicationMainPrefixHarmonyMethod, null, null, null]);
            harmonyPatchMethod?.Invoke(harmonyObj, [getCommandLineArgs, getCommandLineArgsPrefixHarmonyMethod, null, null, null]);
        }

        /// <summary>
        /// Patches Cosmoteer.Gui.TitleScreen() constructor by replacing the game version string.
        /// 
        /// Accesses all the Harmony stuff via reflection, to not depend on the assembly directly.
        /// </summary>
        private static IEnumerable<object> TitleScreenTranspiler(IEnumerable<object> instructions)
        {
            // instruction to replace:
            // 889	0C20	ldstr	"{game version}"
            FieldInfo? opCodeField = null;
            FieldInfo? operandField = null;
            // cosmoteer declares game version as const, so we need to extract it through reflection, otherwise compiler will evaluate it at compile time
            var gameVersion = typeof(Cosmoteer.Versions).GetField(nameof(Cosmoteer.Versions.GameVersionBuild))?.GetValue(null) as string;
            foreach (var instruction in instructions)
            {
                if (opCodeField == null)
                    opCodeField = instruction.GetType().GetField("opcode");

                var opCode = opCodeField?.GetValue(instruction);

                if (opCode != null && (System.Reflection.Emit.OpCode)opCode == System.Reflection.Emit.OpCodes.Ldstr)
                {
                    if (operandField == null)
                        operandField = instruction.GetType().GetField("operand");
                    var operand = operandField?.GetValue(instruction);
                    if (gameVersion == (operand as string))
                    {
                        operandField?.SetValue(instruction, $"{gameVersion} with YAML ver. {Assembly.GetExecutingAssembly().GetName().Version}");
                    }
                }
            }

            return instructions;
        }

        /// <summary>
        /// Patches Cosmoteer.Mods.ModInfo.LoadMods
        /// 
        /// Adds errors caught from mod libs to the errorList
        /// </summary>
        private static void ModListPostfix(ref List<Cosmoteer.Mods.ModInfo> __result, IList<(string ModID, string Error)>? errorList)
        {
            if (showErrorMessageOnce || errorList == null)
            {
                return;
            }

            foreach (var mod in __result)
            {
                if (EncounteredLibraries.TryGetValue(mod.Folder, out var modFolder))
                {
                    var errors = modFolder.Select(guid => LibraryFiles[guid].error).Where(error => error != null);

                    if (errors.Any())
                    {
                        errorList.Add((mod.ID, string.Join('\n', errors)));
                    }
                }
            }

            showErrorMessageOnce = true;
        }

        /// <summary>
        /// Patches Cosmoteer.Mods.ModInfo.TryLoadMod
        /// 
        /// Changes loadActions to false if the mod has unloaded libs.
        /// This prevents cosmoteer from crashing if the mod references custom C#
        /// structures in its actions. Mod won't work anyway, but at least the game will launch.
        /// </summary>
        private static void TryLoadModPrefix(string modFolder, ref bool loadActions)
        {
            if (loadActions && ModsWithProblematicLibs.Contains(new Halfling.IO.AbsolutePath(modFolder)))
            {
                Halfling.Logging.Logger.Log($"Mod {modFolder} has problematic libs, so its actions are not loaded");
                loadActions = false;
            }
        }

        /// <summary>
        /// Constructs a label from parameters 
        /// </summary>
        private static Halfling.Gui.Label FormatLibList(string caption, string color, string[] libs)
        {
            var res = $"<{color}>{caption}";
            foreach (var lib in libs)
            {
                res += "\n" + lib;
            }
            res += $"</{color}>";
            var label = new Halfling.Gui.Label();
            label.Text = res;
            label.TextRenderer.HAlignment = Halfling.Graphics.Text.HAlignment.Left;
            label.TextRenderer.OversizeMode = Halfling.Graphics.Text.OversizeMode.Wrap;
            label.TextRenderer.XmlFormatting = true;
            label.AutoSize.AutoHeightMode = Halfling.Gui.AutoSizeMode.Enable;
            return label;
        }

        /// <summary>
        /// Callback for the "Trust" button, adds libs to the list of trusted
        /// </summary>
        private static void OnTrustButtonClicked(object? sender, EventArgs e)
        {
            if (sender is Halfling.Gui.Components.Input.WidgetClickController controller && controller.Widget?.UserData is Cosmoteer.Gui.ModsDialog dialog)
            {
                var modInfo = dialog._mods.SelectedWidget?.ModInfo;
                if (modInfo == null)
                {
                    return;
                }
                foreach (var guid in EncounteredLibraries[modInfo.Folder])
                {
                    AddTrustedLibrary(modInfo, Path.GetFileName(LibraryFiles[guid].file), guid);
                }
                dialog.OnModSelected(sender, new Halfling.Gui.WidgetEventArgs(controller.Widget));
            }    
        }

        /// <summary>
        /// Callback for the "Trust Mod" button, adds mod to the list of trusted
        /// </summary>
        private static void OnTrustModButtonClicked(object? sender, EventArgs e)
        {
            if (sender is Halfling.Gui.Components.Selection.WidgetTriggeredSelectionController controller && controller.Widget?.UserData is Cosmoteer.Gui.ModsDialog dialog)
            {
                var folder = dialog._mods.SelectedWidget?.ModInfo.Folder;
                if (folder == null)
                {
                    return;
                }
                if (controller.IsSelected)
                    TrustedMods.Add(folder);
                else
                    TrustedMods.Remove(folder);
                dialog.OnModSelected(sender, new Halfling.Gui.WidgetEventArgs(controller.Widget));
            }
        }


        /// <summary>
        /// Patches Cosmoteer.Gui.ModsDialog.OnModSelected
        /// 
        /// Tries to find mod libraries if they are not in the list
        /// </summary>
        private static void OnModSelectedPrefix(Cosmoteer.Gui.ModsDialog __instance)
        {
            if (__instance._mods.SelectedWidget == null)
            {
                return;
            }
            if (__instance._mods.SelectedWidget.IsModEnabled && !EncounteredLibraries.ContainsKey(__instance._mods.SelectedWidget.ModInfo.Folder))
            {
                LoadLibsForMod(__instance._mods.SelectedWidget.ModInfo);
            }
        }

        /// <summary>
        /// Patches Cosmoteer.Gui.ModsDialog.OnModSelected
        /// 
        /// Adds labels showing the status of mod libs
        /// </summary>
        private static void OnModSelectedPostfix(Cosmoteer.Gui.ModsDialog __instance)
        {
            var count = __instance._descBox.Children.Count;
            if (count < 3)
            {
                return;
            }
            var modInfo = __instance._mods.SelectedWidget?.ModInfo;
            if (modInfo == null)
            {
                return;
            }
            if (EncounteredLibraries.TryGetValue(modInfo.Folder, out var mod) && mod.Count != 0)
            {
                // libs to be displayed as loaded (in green): libraries that are in context
                var libsLoaded = mod.Where(guid => LibrariesInContext.Contains(guid)).Select(guid => Path.GetFileName(LibraryFiles[guid].file)).ToArray();
                // libs to be displayed as known (also green): libraries that are not in context, in TrustedLibraries, not duplicated, and not failed to load (consented and with error)
                var libsKnown = TrustedLibraries.TryGetValue(modInfo.Folder, out var trustedLibs) ? mod.Where(guid => !LibrariesInContext.Contains(guid) && trustedLibs.Item2.Any(lib => lib.Item2 == guid)).Select(guid => LibraryFiles[guid]).Where(lib => lib.duplicated == false && (lib.consented == false || lib.error == null)).Select(lib => Path.GetFileName(lib.file)).ToArray() : [];
                // libs to be displayed as failed to load (in red): not duplicated, consented and with error
                var libsError = mod.Select(guid => LibraryFiles[guid]).Where(lib => lib.duplicated == false && lib.consented == true && lib.error != null).Select(lib => Cosmoteer.Localization.Strings.FormatText("ModLoader/libError", [Path.GetFileName(lib.file), lib.error])).ToArray();
                // libs to be displayed as duplicates (in red): duplicated
                var libsDup = mod.Select(guid => (guid, LibraryFiles[guid])).Where(lib => lib.Item2.duplicated == true).Select(lib => Cosmoteer.Localization.Strings.FormatText("ModLoader/libDup", [Path.GetFileName(lib.Item2.file), EncounteredLibraries.Where(kvp => kvp.Value.Contains(lib.guid) && kvp.Key != modInfo.Folder).Select(kvp => __instance._mods.Children.Select(child => child.ModInfo).FirstOrDefault(modinfo => modinfo.Folder == kvp.Key)?.Name).Aggregate("", (current, next) => current + (current.Length > 0 && next?.Length > 0 ? ", " : "") + next)])).ToArray(); // because I can!
                // libs to be displayed as untrusted (in red): not duplicated, not consented and not known
                var libsUnknown = mod.Select(guid => LibraryFiles[guid]).Where(lib => lib.duplicated == false && lib.consented == false).Select(lib => Path.GetFileName(lib.file)).Except(libsKnown).ToArray();

                if (TrustedMods.Contains(modInfo.Folder))
                {
                    libsKnown = libsKnown.Concat(libsUnknown).ToArray();
                    libsUnknown = [];
                }

                if (modInfo.Description != null)
                {
                    count--;
                }
                if (modInfo.Logo != null)
                {
                    count--;
                }
                if (libsLoaded.Length != 0)
                {
                    __instance._descBox.Children.Insert(count, FormatLibList(Cosmoteer.Localization.Strings.GetText("ModLoader/libsLoaded"), "good", libsLoaded));
                }
                if (libsKnown.Length != 0)
                {
                    __instance._descBox.Children.Insert(count, FormatLibList(Cosmoteer.Localization.Strings.GetText("ModLoader/libsKnown"), "good", libsKnown));
                }

                if (libsDup.Length != 0)
                {
                    __instance._descBox.Children.Insert(count, FormatLibList(Cosmoteer.Localization.Strings.GetText("ModLoader/libsDup"), "bad", libsDup));
                }
                if (libsError.Length != 0)
                {
                    __instance._descBox.Children.Insert(count, FormatLibList(Cosmoteer.Localization.Strings.GetText("ModLoader/libsError"), "bad", libsError));
                }
                if (libsUnknown.Length != 0)
                {
                    var btn = new Halfling.Gui.Button(Cosmoteer.Gui.WidgetRules.Instance.GoodButton)
                    {
                        PercentileRect = new Halfling.Geometry.Rect(0f, 0f, 50f, 10f),
                        Right = -5f,
                        TextProvider = Cosmoteer.Localization.Strings.KeyString("ModLoader/trust"),
                        SelfActive = true,
                        SelfInputActive = libsUnknown.Length > 0 && !TrustedMods.Contains(modInfo.Folder),
                        UserData = __instance
                    };
                    btn.Clicked += OnTrustButtonClicked;
                    __instance._descBox.Children.Insert(count, btn);
                    __instance._descBox.Children.Insert(count, FormatLibList(Cosmoteer.Localization.Strings.GetText("ModLoader/libsUnknown"), "bad", libsUnknown));
                }
                if (modInfo.Folder.IsSubPathOf(Cosmoteer.Paths.UserModsFolder))
                {
                    var modBtn = new Halfling.Gui.ToggleButton(Cosmoteer.Gui.WidgetRules.Instance.ToggleCheckButton)
                    {
                        PercentileRect = new Halfling.Geometry.Rect(0f, 0f, 50f, 10f),
                        Right = -5f,
                        TextProvider = Cosmoteer.Localization.Strings.KeyString("ModLoader/trustMod"),
                        SelfActive = true,
                        IsSelected = TrustedMods.Contains(modInfo.Folder),
                        SelfInputActive = true,
                        UserData = __instance
                    };
                    modBtn.SelectionChanged += OnTrustModButtonClicked;
                    __instance._descBox.Children.Insert(1, modBtn);
                }

            }
        }

        /// <summary>
        /// Patches Cosmoteer.Gui.ModsDialog.RefreshToggleButtons
        /// 
        /// Updates the mod window when the mod is enabled or disabled
        /// </summary>
        private static void RefreshToggleButtonsPostfix(Cosmoteer.Gui.ModsDialog __instance)
        {
            if (__instance._mods.SelectedWidget == null)
            {
                return;
            }
            if (__instance._mods.SelectedWidget.IsModEnabled && !EncounteredLibraries.ContainsKey(__instance._mods.SelectedWidget.ModInfo.Folder))
            {
                LoadLibsForMod(__instance._mods.SelectedWidget.ModInfo);
                OnModSelectedPostfix(__instance);
            }
            if (!__instance._mods.SelectedWidget.IsModEnabled && EncounteredLibraries.TryGetValue(__instance._mods.SelectedWidget.ModInfo.Folder, out var libs))
            {
                foreach(var guid in libs)
                {
                    if (!LibrariesInContext.Contains(guid))
                    {
                        LibraryFiles.Remove(guid);
                    }
                }
                TrustedLibraries.Remove(__instance._mods.SelectedWidget.ModInfo.Folder);
                EncounteredLibraries.Remove(__instance._mods.SelectedWidget.ModInfo.Folder);
                __instance.OnModSelected(null, new Halfling.Gui.WidgetEventArgs(__instance._mods.SelectedWidget));
            }
        }

        /// <summary>
        /// Patches Cosmoteer.Settings.WriteTo
        /// 
        /// Adds the list of trusted libs to the saved settings
        /// </summary>
        private static void SettingsWritePostfix(Halfling.Serialization.Generic.GenericSerialWriter writer)
        {
            // remove all the trusted libraries that are no longer present
            foreach (var libs in TrustedLibraries.Values)
            {
                libs.Item2.RemoveWhere(lib => !LibraryFiles.Where(kvp => kvp.Value.fromTrusted == false).Any(kvp => kvp.Key == lib.Item2));
            }
            var toRemove = TrustedLibraries.Where(kvp => kvp.Value.Item2.Count == 0).Select(kvp => kvp.Key).ToArray();
            foreach (var item in toRemove)
            {
                TrustedLibraries.Remove(item);
            }

            // remove the mods that are not enabled
            TrustedMods.IntersectWith(Cosmoteer.Settings.EnabledMods);

            if (TrustedLibraries.Count > 0)
            {
                writer.WriteToPath(nameof(TrustedLibraries), TrustedLibraries);
            }
            if (TrustedMods.Count > 0)
            {
                writer.WriteToPath(nameof(TrustedMods), TrustedMods);
            }

        }

        /// <summary>
        /// Patches Halfling.Application.Bases.ApplicationMain
        /// 
        /// Runs delayed inits before the game loop starts
        /// </summary>
        private static void ApplicationMainPrefix()
        {
            foreach (var (method, guid) in DelayedInitMethods)
            {
                var lib = LibraryFiles[guid].file;
                try
                {
                    if (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>() == null)
                    {
                        method.Invoke(null, null);
                        Halfling.Logging.Logger.Log($"called delayed init method {method.Name} for mod lib {lib}");
                    }
                    else
                    {
                        CallFromUnmanaged(method.MethodHandle.GetFunctionPointer());
                        Halfling.Logging.Logger.Log($"called delayed unmanaged init method {method.Name} for mod lib {lib}");
                    }
                }
                catch (Exception ex)
                {
                    Halfling.Logging.Logger.Log($"failed to load mod lib from {lib}, exception\n{ex}");
                    if (LibraryFiles.TryGetValue(guid, out var modlib))
                    {
                        modlib.error = ex.Message;
                        LibraryFiles[guid] = modlib;
                    }
                }
            }
        }

        /// <summary>
        /// Replaces System.Environment.GetCommandLineArgs
        /// 
        /// Cosmoteer uses it several times to find out its own name.
        /// </summary>
        private static bool GetCommandLineArgsPrefix(ref string[] __result)
        {
            __result = [Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "Cosmoteer.dll"))];
            return false;
        }
    }

    [Halfling.Serialization.DefaultSerializer]
    public class GuidSerializer : Halfling.Serialization.Binary.IBinarySerializer,
                                  Halfling.Serialization.Base.IBaseSerializer<Halfling.Serialization.Binary.BinarySerializer, BinaryWriter>,
                                  Halfling.Serialization.ObjectBits.IObjectBitsSerializer, Halfling.Serialization.Base.IBaseSerializer<Halfling.Serialization.ObjectBits.ObjectBitsSerializer, Halfling.ObjectBits.OBNode>,
                                  Halfling.Serialization.ObjectText.IObjectTextSerializer, Halfling.Serialization.Base.IBaseSerializer<Halfling.Serialization.ObjectText.ObjectTextSerializer, Halfling.ObjectText.IOTNode>,
                                  Halfling.Serialization.Binary.IBinaryDeserializer,
                                  Halfling.Serialization.Base.IBaseDeserializer<Halfling.Serialization.Binary.BinarySerializer, BinaryReader>,
                                  Halfling.Serialization.ObjectBits.IObjectBitsDeserializer,
                                  Halfling.Serialization.Base.IBaseDeserializer<Halfling.Serialization.ObjectBits.ObjectBitsSerializer, Halfling.ObjectBits.OBNode>,
                                  Halfling.Serialization.ObjectText.IObjectTextDeserializer,
                                  Halfling.Serialization.Base.IBaseDeserializer<Halfling.Serialization.ObjectText.ObjectTextSerializer, Halfling.ObjectText.IOTNode>
    {
        public bool CanWrite(Type type)
        {
            return type == typeof(Guid);
        }

        public void Write(Halfling.Serialization.Binary.BinarySerializer s, BinaryWriter writer, object? obj, Type type, Halfling.Serialization.ProgressTracker? progressTracker, MemberInfo? member)
        {
            writer.Write(obj?.ToString() ?? string.Empty);
        }

        public void Write(Halfling.Serialization.ObjectBits.ObjectBitsSerializer s, Halfling.ObjectBits.OBNode node, object? obj, Type type, Halfling.Serialization.ProgressTracker? progressTracker, MemberInfo? member)
        {
            using BinaryWriter writer = node.GetDataWriter();
            writer.Write(obj?.ToString() ?? string.Empty);
        }

        public void Write(Halfling.Serialization.ObjectText.ObjectTextSerializer s, Halfling.ObjectText.IOTNode node, object? obj, Type type, Halfling.Serialization.ProgressTracker? progressTracker, MemberInfo? member)
        {
            Halfling.ObjectText.OTFieldNode.Replace(node, obj?.ToString() ?? string.Empty);
        }

        public bool CanRead(Type type)
        {
            return type == typeof(Guid);
        }

        public object? Read(Halfling.Serialization.Binary.BinarySerializer s, BinaryReader reader, Type type, Halfling.Serialization.ProgressTracker? progressTracker, MemberInfo? member)
        {
            try
            {
                return new Guid(reader.ReadString());
            }
            catch
            {
                return null;
            }
        }

        public object? Read(Halfling.Serialization.ObjectBits.ObjectBitsSerializer s, Halfling.ObjectBits.OBNode node, Type type, Halfling.Serialization.ProgressTracker? progressTracker, MemberInfo? member)
        {
            try
            {
                return new Guid(node.GetDataReader().ReadString());
            }
            catch
            {
                return null;
            }
        }

        public object? Read(Halfling.Serialization.ObjectText.ObjectTextSerializer s, Halfling.ObjectText.IOTNode node, Type type, Halfling.Serialization.ProgressTracker? progressTracker, MemberInfo? member)
        {
            if (node is Halfling.ObjectText.OTFieldNode f)
            {
                try
                {
                    return new Guid(f.Value);
                }
                catch
                {
                    return null;
                }
            }
            throw new Halfling.Serialization.DeserializeException("Cannot read string from non-Field node at path \"" + node.PathWithFile + "\".");
        }
    }
}
