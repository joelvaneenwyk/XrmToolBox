using System.Reflection;

namespace XrmToolBox.Extensibility.Interfaces
{
    public interface IEarlyBoundProxy
    {
        Assembly GetEarlyBoundProxyAssembly();
    }
}
