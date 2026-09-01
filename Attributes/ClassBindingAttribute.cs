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

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class FieldBindingAttribute : Attribute
{
    public string? Name { get; }
    public FieldBindingAttribute(string? name = null) => Name = name;
}
