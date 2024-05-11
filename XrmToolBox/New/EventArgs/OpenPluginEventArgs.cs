using System;
using XrmToolBox.Extensibility.Interfaces;

namespace XrmToolBox.New.EventArgs
{
    public class OpenPluginEventArgs : System.EventArgs
    {
        public OpenPluginEventArgs(Lazy<IXrmToolBoxPlugin, IPluginMetadataExt> plugin)
        {
            Plugin = plugin;
        }

        public Lazy<IXrmToolBoxPlugin, IPluginMetadataExt> Plugin { get; }
    }
}