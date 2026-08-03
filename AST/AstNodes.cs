using System.Collections.Generic;
using System.Linq;

namespace ObjektRT.Core.AST;

public sealed record SourceLocation(int Line, int Column, string? SourceLine = null);

public abstract record AstNode
{
    public SourceLocation? Location { get; set; }
}

public sealed record ModuleNode(string Name) : AstNode
{
    public string? Version { get; set; }
    public List<InterfaceNode> Interfaces { get; } = new();
    public List<ClassNode>     Classes { get; } = new();
    public List<StructNode>    Structs { get; set; } = new();
    /// <summary>
    /// ModuleSpace Attributes
    /// </summary>
    public List<Attribute>?    ModuleAttributes { get; } = new();
    /// <summary>
    /// Constructs a new ModuleNode 
    /// </summary>
    /// <param name="name">the name for the module</param>
    /// <param name="version">The version of the module</param>
    /// <param name="interfaces">Any interfaces you already have</param>
    /// <param name="classes">Any classes you already have</param>
    public ModuleNode(string name, string? version, List<InterfaceNode> interfaces, List<ClassNode> classes) : this(name)
    {
        Version = version;
        Interfaces.AddRange(interfaces);
        Classes.AddRange(classes);
    }
    /// <summary>
    /// Creates and attaches a class to the Current Moduleode.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public ClassNode AddClass(string name)
    {
        var node = new ClassNode(name);
        Classes.Add(node);
        return node;
    }
}

public sealed record InterfaceNode(string Name) : AstNode
{
    public string? Namespace { get; set; }
    public AccessModifier Access { get; set; } = AccessModifier.Public;
    public List<MethodSignature> Methods { get; } = new();
    public List<AttributeNode> Attributes { get; } = new();

    public InterfaceNode(string name, List<MethodSignature> methods) : this(name)
    {
        Methods.AddRange(methods);
    }
}

public sealed record StructNode(string Name) : AstNode
{
    public string? Namespace { get; set; }
    public AccessModifier Access { get; set; } = AccessModifier.Public;
    public List<MethodNode>? Methods { get; set; }
    public List<FieldNode> Fields { get; } = new();
    public List<AttributeNode> Attributes { get; } = new();
}

public sealed record ClassNode(string Name) : AstNode
{
    public string? Namespace { get; set; }
    public string? BaseType { get; set; }
    public List<string> BaseTypes { get; } = new();
    public List<string> Interfaces { get; } = new();
    public List<AccessModifier> Modifiers { get; } = new();
    public List<FieldNode> Fields { get; } = new();
    public List<ConstructorNode> Constructors { get; } = new();
    public List<MethodNode> Methods { get; } = new();
    public List<AttributeNode> Attributes { get; } = new();
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    public bool IsStatic { get; set; }
    public List<GenericParameterNode> GenericParameters { get; } = new();

    public ClassNode(string name, List<string> baseTypes, List<FieldNode> fields, List<ConstructorNode> constructors, List<MethodNode> methods) : this(name)
    {
        BaseTypes.AddRange(baseTypes);
        Fields.AddRange(fields);
        Constructors.AddRange(constructors);
        Methods.AddRange(methods);
    }
}

public sealed record GenericParameterNode(string Name) : AstNode;

/// <summary>
/// An annotation/attribute applied to a type or member. Matches the wire
/// model's <c>AttributeRecord</c>: a name plus ordered positional arguments
/// stored as text (string arguments retain their quotes).
/// </summary>
public sealed record AttributeNode(string Name) : AstNode
{
    public List<string> Arguments { get; } = new();

    public AttributeNode(string name, IEnumerable<string> arguments) : this(name)
    {
        Arguments.AddRange(arguments);
    }
}

public sealed record FieldNode(string Name, TypeRef FieldType) : AstNode
{
    public AccessModifier Access { get; set; } = AccessModifier.Public;
    public bool IsStatic { get; set; }
    public bool IsReadOnly { get; set; }
    public object? InitialValue { get; set; }

    public FieldNode(string name, TypeRef fieldType, AccessModifier access) : this(name, fieldType)
    {
        Access = access;
    }
}

public sealed record MethodSignature(string Name) : AstNode
{
    public List<ParameterNode> Parameters { get; } = new();
    public TypeRef ReturnType { get; set; } = TypeRef.Void;
    public bool IsStatic { get; set; }
    public string? Implements { get; set; }

    public MethodSignature(string name, IEnumerable<ParameterNode> parameters, TypeRef returnType, bool isStatic, string? implements) : this(name)
    {
        Parameters.AddRange(parameters);
        ReturnType = returnType;
        IsStatic = isStatic;
        Implements = implements;
    }
}

public sealed record ConstructorNode() : AstNode
{
    public List<ParameterNode> Parameters { get; } = new();
    public List<AttributeNode> Attributes { get; } = new();
    public BlockStatement Body { get; set; } = new(new());

    public ConstructorNode(IEnumerable<ParameterNode> parameters, BlockStatement body) : this()
    {
        Parameters.AddRange(parameters);
        Body = body;
    }
}

public sealed record MethodNode(string Name) : AstNode
{
    public List<ParameterNode> Parameters { get; } = new();
    public TypeRef ReturnType { get; set; } = TypeRef.Void;
    public bool IsStatic { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAbstract { get; set; }
    public string? Implements { get; set; }
    public BlockStatement Body { get; set; } = new(new());
    public AccessModifier Access { get; set; } = AccessModifier.Public;
    public List<LocalDeclarationStatement> Locals { get; } = new();
    public List<AttributeNode> Attributes { get; } = new();
    public NativeMethod? NativeImpl { get; set; }

    public MethodNode(string name, IEnumerable<ParameterNode> parameters, TypeRef returnType, bool isStatic, string? implements, BlockStatement body) : this(name)    
    {
        Parameters.AddRange(parameters);
        ReturnType = returnType;
        IsStatic = isStatic;
        Implements = implements;
        Body = body;
    }

    public MethodNode(string name, IEnumerable<ParameterNode> parameters, TypeRef returnType, bool isStatic, NativeMethod nativeImpl) : this(name)
    {
        Parameters.AddRange(parameters);
        ReturnType = returnType;
        IsStatic = isStatic;
        NativeImpl = nativeImpl;
        // Console.WriteLine($"[METHODNODE] Created native method: {name}, NativeImpl is {(NativeImpl != null ? "SET" : "NULL")}");
    }
}

public sealed record ParameterNode(string Name, TypeRef ParameterType) : AstNode;

public sealed record TypeRef(string Name) : AstNode
{
    public static readonly TypeRef Void = new("void");
    public static readonly TypeRef Int32 = new("int32");
    public static readonly TypeRef Int64 = new("int64");
    public static readonly TypeRef Float32 = new("float32");
    public static readonly TypeRef Float64 = new("float64");
    public static readonly TypeRef String = new("string");
    public static readonly TypeRef Bool = new("bool");
    public static readonly TypeRef Object = new("object");

    public static implicit operator TypeRef(string name) => new TypeRef(name);
}

public enum AccessModifier
{
    Private,
    Public,
    Protected,
    Internal
}

public abstract record Statement : AstNode;

public sealed record BlockStatement(List<Statement> Statements) : Statement;

public sealed record LocalDeclarationStatement(
    string Name,
    TypeRef LocalType
) : Statement;

public sealed record InstructionStatement(Instruction Instruction) : Statement;

public sealed record IfStatement(
    string Condition,
    BlockStatement Then,
    BlockStatement? Else
) : Statement;

public sealed record WhileStatement(
    string Condition,
    BlockStatement Body
) : Statement;

public sealed record SwitchCase(
    int? Value,
    BlockStatement Body
) : AstNode
{
    /// <summary>
    /// Optional string case value (e.g. <c>case "start":</c>). Mutually exclusive with <see cref="Value"/>.
    /// </summary>
    public string? StringValue { get; init; }
}

public sealed record SwitchStatement(
    string Expression,
    List<SwitchCase> Cases
) : Statement;

public abstract record Instruction : AstNode;

public sealed record SimpleInstruction(
    global::ObjektRT.Core.AST.OpCode OpCode,
    string? Operand = null
) : Instruction;

public sealed record MethodReference(
    TypeRef DeclaringType,
    string Name,
    TypeRef ReturnType,
    List<TypeRef> ParameterTypes
) : AstNode
{
    public List<TypeRef> GenericArguments { get; } = new();
    public NativeMethod? NativeImpl { get; init; } = null;
}

public sealed record FieldReference(
    TypeRef DeclaringType,
    string Name,
    TypeRef FieldType
) : AstNode;

public sealed record CallInstruction(
    MethodReference Target,
    IReadOnlyList<TypeRef> Arguments,
    bool IsVirtual
) : Instruction;

public sealed record NewObjInstruction(
    TypeRef Type,
    MethodReference? Constructor,
    IReadOnlyList<TypeRef> Arguments
) : Instruction;
