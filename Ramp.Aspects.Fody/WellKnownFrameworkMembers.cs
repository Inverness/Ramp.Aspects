using System.Linq;
using Mono.Cecil;

namespace Ramp.Aspects.Fody
{
    internal class WellKnownFrameworkMembers
    {
        internal readonly TypeDefinition Exception;
        internal readonly TypeDefinition AsyncStateMachineAttribute;
        internal readonly TypeDefinition IteratorStateMachineAttribute;

        internal WellKnownFrameworkMembers(ModuleDefinition currentModule)
        {
            AssemblyNameReference runtimeAssemblyName = currentModule.AssemblyReferences.FirstOrDefault(n => n.Name == "System.Runtime");
            if (runtimeAssemblyName == null)
                runtimeAssemblyName = currentModule.AssemblyReferences.First(n => n.Name == "mscorlib");

            AssemblyDefinition runtimeAssembly = currentModule.AssemblyResolver.Resolve(runtimeAssemblyName);
            ModuleDefinition module = runtimeAssembly.MainModule;

            Exception = module.GetType("System.Exception");
            AsyncStateMachineAttribute = module.GetType("System.Runtime.CompilerServices.AsyncStateMachineAttribute");
            IteratorStateMachineAttribute = module.GetType("System.Runtime.CompilerServices.IteratorStateMachineAttribute");
        }
    }
}