using Cove.Server.Plugins;
using Cove.Server.Utils;
using System.Reflection;

namespace Cove.Server
{
    public partial class CoveServer
    {
        private bool arePluginsEnabled = false;

        public readonly List<PluginInstance> loadedPlugins = new List<PluginInstance>();
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

            List<Assembly> pluginAssemblys = new();

            // get all files in the plugins folder
            foreach (string fileName in Directory.GetFiles(pluginsFolder))
            {
                try
                {
                    AssemblyName thisFile = AssemblyName.GetAssemblyName(fileName); ;
                    pluginAssemblys.Add(Assembly.LoadFrom(fileName));
                }
                catch (BadImageFormatException)
                {
                    Log($"File: {fileName} is not a plugin!");
                }
            }

            Log($"Found {pluginAssemblys.Count} plugins!");

            foreach (Assembly assembly in pluginAssemblys)
            {
                // Plugins are third party code, and one of them being broken (wrong dependency version, missing
                // config keys, a throwing constructor or onInit) must not stop the server from starting.
                try
                {
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

                            object instance = Activator.CreateInstance(type, this);
                            CovePlugin plugin = instance as CovePlugin;
                            if (plugin != null)
                            {
                                PluginInstance thisInstance = new(plugin, config["name"], config["id"], config["author"]);

                                loadedPlugins.Add(thisInstance);
                                Log($"Plugin Init: {config["name"]}");
                                plugin.onInit(); // start the plugin!
                            }
                            else
                                Log($"Unable to load {type.FullName}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log($"Failed to load plugin assembly {assembly.GetName().Name}, it was skipped:");
                    Log(e.ToString());
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
