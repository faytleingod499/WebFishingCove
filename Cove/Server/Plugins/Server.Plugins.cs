using Cove.Server.Plugins;
using Cove.Server.Utils;
using System.Reflection;
using System.Runtime.Loader;

namespace Cove.Server
{
    public partial class CoveServer
    {
        private bool arePluginsEnabled = false;

        public readonly List<PluginInstance> loadedPlugins = new List<PluginInstance>();

        /// <summary>True while plugins are being reloaded with !reloadplugins. Plugins can use it to skip things like
        /// "server started / stopped" announcements that only make sense for a real start or stop.</summary>
        public volatile bool PluginsReloading;

        /// <summary>True once the server has begun shutting down.</summary>
        public bool IsStopping => Volatile.Read(ref stopping) != 0;

        // Every plugin file is loaded into a context of its own, from a copy in memory. That means the dll on disk is
        // not locked, so it can be replaced while the server runs, and the old version can be unloaded again.
        private sealed class PluginLoadContext : AssemblyLoadContext
        {
            private readonly string folder;

            public PluginLoadContext(string name, string folder) : base(name, isCollectible: true)
            {
                this.folder = folder;
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                // Cove itself must be the copy that is already running, never a second one
                if (assemblyName.Name == null || assemblyName.Name == "Cove")
                    return null;

                // libraries a plugin brings along in the plugins folder
                string path = Path.Combine(folder, assemblyName.Name + ".dll");
                if (!File.Exists(path))
                    return null;

                using var stream = new MemoryStream(File.ReadAllBytes(path));
                return LoadFromStream(stream);
            }
        }

        private sealed class LoadedPluginFile
        {
            public string FilePath = "";
            public PluginLoadContext Context = null!;
            public List<PluginInstance> Instances = new();
            public List<string> Commands = new();
        }

        private readonly List<LoadedPluginFile> pluginFiles = new();
        public void loadAllPlugins(bool skipWarning = false)
        {
            if (!arePluginsEnabled) return; // plugins are disabled!

            if (!skipWarning)
            {
                Log("");
                Log("------------ WARNING ------------");
                Log("YOU HAVE ENABLED PLUGINS, PLUGINS RUN CODE THAT IS NOT APPROVED OR MADE BY COVE");
                Log("ANY AND ALL DAMMAGE TO YOUR COMPUTER IS YOU AND YOUR FAULT ALONE");
                Log("DO NOT RUN ANY UNTRUSTED PLUGINS!");
                Log("IF YOU ARE RUNNING UNTRUSTED PLUGINS EXIT COVE NOW");
                Log("------------ WARNING ------------");
                Log("");

                Thread.Sleep(5000);
            }

            Log("Loading Plugins...");

            string pluginsFolder = $"{AppDomain.CurrentDomain.BaseDirectory}plugins";

            int found = 0;
            foreach (string fileName in Directory.GetFiles(pluginsFolder, "*.dll"))
            {
                if (loadPluginFile(fileName) != null)
                    found++;
            }

            Log($"Found {found} plugins!");
        }

        // Loads one dll from the plugins folder and starts every plugin in it. Returns null when it holds no plugin.
        private LoadedPluginFile? loadPluginFile(string fileName)
        {
            string pluginsFolder = Path.GetDirectoryName(fileName)!;
            var file = new LoadedPluginFile
            {
                FilePath = fileName,
                Context = new PluginLoadContext(Path.GetFileNameWithoutExtension(fileName), pluginsFolder),
            };

            // Plugins are third party code, and one of them being broken (wrong dependency version, missing
            // config keys, a throwing constructor or onInit) must not stop the server from starting.
            try
            {
                Assembly assembly;
                try
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(fileName));
                    assembly = file.Context.LoadFromStream(stream);
                }
                catch (BadImageFormatException)
                {
                    Log($"File: {fileName} is not a plugin!");
                    file.Context.Unload();
                    return null;
                }

                // Get all types in the assembly
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // some types failed to load (usually a missing dependency), still use the ones that did
                    Log($"Some types in {assembly.GetName().Name} could not be loaded: {e.LoaderExceptions.FirstOrDefault()?.Message}");
                    types = e.Types.Where(t => t != null).ToArray()!;
                }

                // Iterate over each type and check if it inherits from CovePlugin
                foreach (Type type in types)
                {
                    if (type.IsClass && type.IsSubclassOf(typeof(CovePlugin)))
                    {
                        string pluginConfig = readConfigFromPlugin($"{assembly.GetName().Name}.plugin.cfg", assembly);
                        if (pluginConfig == string.Empty) continue; // no config file found, its probably not a plugin
                        Dictionary<string, string> config = ConfigReader.ReadFile(pluginConfig);

                        if (!config.ContainsKey("name") || !config.ContainsKey("id") || !config.ContainsKey("author"))
                        {
                            Log($"The plugin.cfg of {assembly.GetName().Name} must contain name, id and author, skipping it.");
                            continue;
                        }

                        // note which commands the plugin adds, so they can be removed again when it is unloaded
                        var commandsBefore = Commands.Select(c => c.Command).ToHashSet();

                        object instance = Activator.CreateInstance(type, this);
                        CovePlugin plugin = instance as CovePlugin;
                        if (plugin != null)
                        {
                            PluginInstance thisInstance = new(plugin, config["name"], config["id"], config["author"]);

                            loadedPlugins.Add(thisInstance);
                            file.Instances.Add(thisInstance);
                            Log($"Plugin Init: {config["name"]}");
                            try
                            {
                                plugin.onInit(); // start the plugin!
                            }
                            finally
                            {
                                file.Commands.AddRange(Commands.Select(c => c.Command).Where(c => !commandsBefore.Contains(c)));
                            }
                        }
                        else
                            Log($"Unable to load {type.FullName}");
                    }
                }
            }
            catch (Exception e)
            {
                Log($"Failed to load plugin assembly {Path.GetFileName(fileName)}, it was skipped:");
                Log(e.ToString());
            }

            if (file.Instances.Count == 0)
            {
                file.Context.Unload(); // just a library, or a plugin that failed to start
                return null;
            }

            pluginFiles.Add(file);
            return file;
        }

        // Stops every plugin in a file (onEnd), removes the commands it added and unloads it.
        private void unloadPluginFile(LoadedPluginFile file)
        {
            foreach (PluginInstance instance in file.Instances)
            {
                SafePluginCall(instance, () => instance.plugin.onEnd());
                loadedPlugins.Remove(instance);
            }

            Commands.RemoveAll(c => file.Commands.Contains(c.Command));
            pluginFiles.Remove(file);
            file.Instances.Clear();

            // the memory is given back once nothing refers to the old code anymore
            file.Context.Unload();
        }

        /// <summary>
        /// Stops and loads plugins again from the plugins folder, without restarting the server. With no argument every
        /// plugin is reloaded and new dll files are picked up, with an id only the plugin with that id.
        /// </summary>
        public string reloadPlugins(string? pluginId = null)
        {
            if (!arePluginsEnabled)
                return "Plugins are not enabled on this server.";

            string pluginsFolder = $"{AppDomain.CurrentDomain.BaseDirectory}plugins";
            if (!Directory.Exists(pluginsFolder))
                return "There is no plugins folder.";

            lock (stateLock)
            {
                PluginsReloading = true;
                try
                {
                    List<LoadedPluginFile> targets = pluginId == null
                        ? pluginFiles.ToList()
                        : pluginFiles.Where(f => f.Instances.Any(i => i.pluginID.Equals(pluginId, StringComparison.OrdinalIgnoreCase))).ToList();

                    if (pluginId != null && targets.Count == 0)
                        return $"No loaded plugin has the id \"{pluginId}\".";

                    var paths = targets.Select(t => t.FilePath).ToList();
                    foreach (LoadedPluginFile file in targets)
                        unloadPluginFile(file);

                    // everything again for a full reload (this also finds new files), or just the files that were stopped
                    IEnumerable<string> toLoad = pluginId == null ? Directory.GetFiles(pluginsFolder, "*.dll") : paths.Where(File.Exists);

                    var names = new List<string>();
                    foreach (string path in toLoad)
                    {
                        LoadedPluginFile? loaded = loadPluginFile(path);
                        if (loaded != null)
                            names.AddRange(loaded.Instances.Select(i => i.pluginName));
                    }

                    Log($"Reloaded {names.Count} plugins.");
                    return names.Count == 0 ? "No plugins are loaded now." : $"Reloaded {names.Count} plugin(s): {string.Join(", ", names)}";
                }
                finally
                {
                    PluginsReloading = false;
                }
            }
        }

        string readConfigFromPlugin(string fileIdentifyer, Assembly asm)
        {
            using (Stream fileStream = asm.GetManifestResourceStream(fileIdentifyer))
            {
                if (fileStream != null)
                {
                    StreamReader reader = new StreamReader(fileStream);
                    return reader.ReadToEnd();
                }
                else
                {
                    return "";
                }
            }
        }
    }
}
