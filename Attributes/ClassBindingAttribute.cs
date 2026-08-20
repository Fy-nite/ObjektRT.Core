namespace ObjektRT.Core.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class ClassBindingAttribute : Attribute
{
    public string Name { get; }
    public ClassBindingAttribute(string name) => Name = name;
}

[AttributeUsage(AttributeTargets.Method)]
public class MethodBindingAttribute : Attribute
{
    public string? Name { get; }
    public MethodBindingAttribute(string? name = null) => Name = name;
}
