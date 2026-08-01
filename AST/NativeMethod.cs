using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace ObjektRT.Core.AST
{
    public class NativeMethod
    {
       
        public Func<Value<Object>[], Value<Object>> Method { get; }

        public NativeMethod( Func<Value<Object>[], Value<Object>> method)
        {
         
            Method = method;
        }
    }
}
