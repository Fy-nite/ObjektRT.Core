using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ObjectIR.Core.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    abstract class ModuleCompilerAttribute : System.Attribute
    {
        public string Name { get; set; }
        public List<string> CompilerArguments;
        public ModuleCompilerAttribute(string CompilerName, List<string> CompilerArgs)
        {
            this.Name = CompilerName;
            this.CompilerArguments = CompilerArgs; 
        }
        
       }
}
