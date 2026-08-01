using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ObjektRT.Core.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    abstract class ModuleDescriptionAttribute : System.Attribute
    {
        public string Name { get; set; }
        public ModuleDescriptionAttribute(string name)
        {
            this.Name = name;
        }
        
       }
}
