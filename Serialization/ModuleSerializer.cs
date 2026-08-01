namespace ObjektRT.Core.Serialization;

using ObjektRT.Core.AST;
using OpCode = ObjektRT.Core.AST.OpCode;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System;

#pragma warning disable CS1591

/// <summary>
/// Provides serialization and dumping capabilities for IR modules (using AST nodes)
/// </summary>
public sealed class ModuleSerializer
{
    private readonly ModuleNode _module;

    public ModuleSerializer(ModuleNode module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>
    /// Dumps the module as a structured object representation
    /// </summary>
    public ModuleData Dump() => DumpModule();

    /// <summary>
    /// Dumps the module as JSON string
    /// </summary>
    public string DumpToJson(bool indented = true)
    {
        var data = DumpModule();
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(data, options);
    }

    /// <summary>
    /// Dumps the module as human-readable text
    /// </summary>
    public string DumpToText()
    {
        var sb = new StringBuilder();
   
        sb.AppendLine($"Module: {_module.Name}");
        sb.AppendLine($"Version: {_module.Version}");
        
        if (_module.Classes.Count > 0)
        {
            sb.AppendLine($"\nClasses ({_module.Classes.Count}):");
            foreach (var cls in _module.Classes)
            {
                DumpClass(sb, cls, indent: 1);
            }
        }

        if (_module.Interfaces.Count > 0)
        {
            sb.AppendLine($"\nInterfaces ({_module.Interfaces.Count}):");
            foreach (var iface in _module.Interfaces)
            {
                DumpInterface(sb, iface, indent: 1);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Dumps the module as human-readable IR code (same as .ir.txt format)
    /// </summary>
    public string DumpToIRCode()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"module {_module.Name} version {_module.Version ?? "1.0.0"}");
        sb.AppendLine();

        foreach (var iface in _module.Interfaces)
        {
            DumpInterfaceAsIRCode(sb, iface);
            sb.AppendLine();
        }

        foreach (var cls in _module.Classes)
        {
            DumpClassAsIRCode(sb, cls);
            sb.AppendLine();
        }

        foreach (var str in _module.Structs)
        {
            DumpStructAsIRCode(sb, str);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void DumpInterfaceAsIRCode(StringBuilder sb, InterfaceNode interfaceDef)
    {
        sb.AppendLine($"interface {interfaceDef.Name} {{");
        foreach (var method in interfaceDef.Methods)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {p.ParameterType.Name}"));
            sb.AppendLine($"    method {method.Name}({parameters}) -> {method.ReturnType.Name}");
        }
        sb.AppendLine("}");
    }

    private void DumpClassAsIRCode(StringBuilder sb, ClassNode classDef)
    {
        var inheritance = new List<string>();
        if (classDef.BaseType != null) inheritance.Add(classDef.BaseType);
        inheritance.AddRange(classDef.Interfaces);

        var inheritanceStr = inheritance.Count > 0 ? $" : {string.Join(", ", inheritance)}" : "";
        sb.AppendLine($"class {classDef.Name}{inheritanceStr} {{");

        foreach (var field in classDef.Fields)
        {
            sb.AppendLine($"    {field.Access.ToString().ToLower()} field {field.Name}: {field.FieldType.Name}");
        }

        if (classDef.Fields.Count > 0 && (classDef.Methods.Count > 0 || classDef.Constructors.Count > 0))
            sb.AppendLine();

        foreach (var ctor in classDef.Constructors)
        {
            DumpConstructorAsIRCode(sb, ctor);
            if (ctor != classDef.Constructors.Last() || classDef.Methods.Count > 0) sb.AppendLine();
        }

        foreach (var method in classDef.Methods)
        {
            DumpMethodAsIRCode(sb, method);
            if (method != classDef.Methods.Last()) sb.AppendLine();
        }

        sb.AppendLine("}");
    }

    private void DumpStructAsIRCode(StringBuilder sb, StructNode structDef)
    {
        sb.AppendLine($"struct {structDef.Name} {{");
        foreach (var field in structDef.Fields)
        {
            sb.AppendLine($"    field {field.Name}: {field.FieldType.Name}");
        }
        sb.AppendLine("}");
    }

    private void DumpConstructorAsIRCode(StringBuilder sb, ConstructorNode ctor)
    {
        var parameters = string.Join(", ", ctor.Parameters.Select(p => $"{p.Name}: {p.ParameterType.Name}"));
        sb.AppendLine($"    constructor({parameters}) {{");
        DumpBlockAsIRCode(sb, ctor.Body, 2);
        sb.AppendLine("    }");
    }

    private void DumpMethodAsIRCode(StringBuilder sb, MethodNode method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {p.ParameterType.Name}"));
        var implements = method.Implements != null ? $" implements {method.Implements}" : "";
        var staticPrefix = method.IsStatic ? "static " : "";
        
        sb.AppendLine($"    {staticPrefix}method {method.Name}({parameters}) -> {method.ReturnType.Name}{implements} {{");
        
        foreach (var local in method.Locals)
        {
            sb.AppendLine($"        local {local.Name}: {local.LocalType.Name}");
        }

        if (method.Locals.Count > 0 && method.Body.Statements.Count > 0)
            sb.AppendLine();

        DumpBlockAsIRCode(sb, method.Body, 2);
        sb.AppendLine("    }");
    }

    private void DumpBlockAsIRCode(StringBuilder sb, BlockStatement block, int indentLevel)
    {
        var ind = new string(' ', indentLevel * 4);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            var stmt = block.Statements[i];
            
            if (stmt.Location != null)
            {
                sb.AppendLine($"{ind}// #line {stmt.Location.Line}:{stmt.Location.Column}{(stmt.Location.SourceLine != null ? $" \"{stmt.Location.SourceLine.Trim()}\"" : "")}");
            }

            if (stmt is InstructionStatement instStmt)
            {
                // Look ahead for while pattern
                if (i + 2 < block.Statements.Count && block.Statements[i + 2] is WhileStatement whileLookahead)
                {
                    if (block.Statements[i] is InstructionStatement s1 && block.Statements[i + 1] is InstructionStatement s2)
                    {
                        if (IsSimpleLoad(s1.Instruction) && IsSimpleLoad(s2.Instruction))
                        {
                            var left = RenderOperandFromLoad(s1.Instruction);
                            var right = RenderOperandFromLoad(s2.Instruction);
                            sb.AppendLine($"{ind}while ({left} {whileLookahead.Condition} {right}) {{");
                            DumpBlockAsIRCode(sb, whileLookahead.Body, indentLevel + 1);
                            sb.AppendLine($"{ind}}}");
                            i += 2;
                            continue;
                        }
                    }
                }
                
                sb.AppendLine($"{ind}{DumpInstruction(instStmt.Instruction)}");
            }
            else if (stmt is IfStatement ifStmt)
            {
                sb.AppendLine($"{ind}if ({ifStmt.Condition}) {{");
                DumpBlockAsIRCode(sb, ifStmt.Then, indentLevel + 1);
                sb.AppendLine($"{ind}}}");
                if (ifStmt.Else != null)
                {
                    sb.AppendLine($"{ind}else {{");
                    DumpBlockAsIRCode(sb, ifStmt.Else, indentLevel + 1);
                    sb.AppendLine($"{ind}}}");
                }
            }
            else if (stmt is WhileStatement whileStmt)
            {
                sb.AppendLine($"{ind}while ({whileStmt.Condition}) {{");
                DumpBlockAsIRCode(sb, whileStmt.Body, indentLevel + 1);
                sb.AppendLine($"{ind}}}");
            }
            else if (stmt is SwitchStatement switchStmt)
            {
                sb.AppendLine($"{ind}switch ({switchStmt.Expression}) {{");
                foreach (var swCase in switchStmt.Cases)
                {
                    var caseVal = swCase.Value?.ToString() ?? "else";
                    sb.AppendLine($"{ind}    case {caseVal}:");
                    DumpBlockAsIRCode(sb, swCase.Body, indentLevel + 2);
                }
                sb.AppendLine($"{ind}}}");
            }
        }
    }

    private string DumpInstruction(Instruction inst)
    {
        return inst switch
        {
            SimpleInstruction si => si.Operand != null ? $"{OpCodeConverter.ToString(si.OpCode)} {si.Operand}" : OpCodeConverter.ToString(si.OpCode),
            CallInstruction ci => $"{(ci.IsVirtual ? "callvirt" : "call")} {ci.Target.DeclaringType.Name}.{ci.Target.Name}({string.Join(", ", ci.Target.ParameterTypes.Select(p => p.Name))}) -> {ci.Target.ReturnType.Name}",
            NewObjInstruction noi => $"newobj {noi.Type.Name}.constructor({string.Join(", ", noi.Constructor?.ParameterTypes.Select(p => p.Name) ?? Array.Empty<string>())})",
            _ => inst.ToString() ?? inst.GetType().Name
        };
    }

    private static bool IsSimpleLoad(Instruction inst)
    {
        if (inst is SimpleInstruction si)
        {
            return si.OpCode is OpCode.Ldloc or OpCode.Ldarg or OpCode.LdcI4 or OpCode.LdcI8 or OpCode.Ldstr;
        }
        return false;
    }

    private static string RenderOperandFromLoad(Instruction inst)
    {
        if (inst is SimpleInstruction si) return si.Operand ?? "";
        return "?";
    }

    private ModuleData DumpModule()
    {
        var types = new List<TypeData>();
        types.AddRange(_module.Classes.Select(DumpClassData));
        types.AddRange(_module.Interfaces.Select(DumpInterfaceData));
        types.AddRange(_module.Structs.Select(DumpStructData));

        return new ModuleData
        {
            Name = _module.Name,
            Version = _module.Version?.ToString() ?? "1.0.0",
            Metadata = new Dictionary<string, string>(), // AST currently has no metadata field
            Types = types.ToArray()
        };
    }

    private TypeData DumpClassData(ClassNode cls)
    {
        return new TypeData
        {
            Kind = "Class",
            Name = cls.Name,
            Namespace = cls.Namespace,
            Access = cls.Modifiers.Count > 0 ? cls.Modifiers[0].ToString() : "Public",
            IsAbstract = cls.IsAbstract,
            IsSealed = cls.IsSealed,
            BaseType = cls.BaseType,
            Interfaces = cls.Interfaces.ToArray(),
            GenericParameters = cls.GenericParameters.Select(g => g.Name).ToArray(),
            Fields = cls.Fields.Select(DumpFieldData).ToArray(),
            Methods = cls.Methods.Select(DumpMethodData).ToArray(),
            Constructors = cls.Constructors.Select(DumpConstructorData).ToArray()
        };
    }

    private TypeData DumpInterfaceData(InterfaceNode iface)
    {
        return new TypeData
        {
            Kind = "Interface",
            Name = iface.Name,
            Namespace = iface.Namespace,
            Access = iface.Access.ToString(),
            Methods = iface.Methods.Select(m => new MethodData 
            { 
                Name = m.Name, 
                ReturnType = m.ReturnType.Name,
                IsStatic = m.IsStatic,
                Parameters = m.Parameters.Select(p => new ParameterData { Name = p.Name, Type = p.ParameterType.Name }).ToArray()
            }).ToArray()
        };
    }

    private TypeData DumpStructData(StructNode str)
    {
        return new TypeData
        {
            Kind = "Struct",
            Name = str.Name,
            Namespace = str.Namespace,
            Access = str.Access.ToString(),
            Fields = str.Fields.Select(DumpFieldData).ToArray()
        };
    }

    private FieldData DumpFieldData(FieldNode field)
    {
        return new FieldData
        {
            Name = field.Name,
            Type = field.FieldType.Name,
            Access = field.Access.ToString(),
            IsStatic = field.IsStatic,
            IsReadOnly = field.IsReadOnly,
            InitialValue = field.InitialValue?.ToString()
        };
    }

    private MethodData DumpMethodData(MethodNode method)
    {
        var instructionsNode = JsonNode.Parse(InstructionSerializer.SerializeInstructions(method.Body.Statements).GetRawText()) ?? new JsonArray();

        return new MethodData
        {
            Name = method.Name,
            ReturnType = method.ReturnType.Name,
            Access = method.Access.ToString(),
            IsStatic = method.IsStatic,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsAbstract = method.IsAbstract,
            Parameters = method.Parameters.Select(p => new ParameterData
            {
                Name = p.Name,
                Type = p.ParameterType.Name
            }).ToArray(),
            LocalVariables = method.Locals.Select(l => new LocalVariableData
            {
                Name = l.Name,
                Type = l.LocalType.Name
            }).ToArray(),
            InstructionCount = method.Body.Statements.Count,
            Instructions = instructionsNode
        };
    }

    private MethodData DumpConstructorData(ConstructorNode ctor)
    {
        var instructionsNode = JsonNode.Parse(InstructionSerializer.SerializeInstructions(ctor.Body.Statements).GetRawText()) ?? new JsonArray();

        return new MethodData
        {
            Name = ".ctor",
            ReturnType = "void",
            IsConstructor = true,
            Parameters = ctor.Parameters.Select(p => new ParameterData
            {
                Name = p.Name,
                Type = p.ParameterType.Name
            }).ToArray(),
            InstructionCount = ctor.Body.Statements.Count,
            Instructions = instructionsNode
        };
    }

    private void DumpClass(StringBuilder sb, ClassNode cls, int indent = 0)
    {
        var ind = new string(' ', indent * 2);
        var modifiers = new List<string>();
        if (cls.IsAbstract) modifiers.Add("abstract");
        if (cls.IsSealed) modifiers.Add("sealed");
        if (cls.IsStatic) modifiers.Add("static");
        
        var modifierStr = modifiers.Count > 0 ? string.Join(" ", modifiers) + " " : "";
        sb.AppendLine($"{ind}{modifierStr}class {cls.Name}");
        
        if (cls.BaseType != null)
            sb.AppendLine($"{ind}  : {cls.BaseType}");
    
        if (cls.Interfaces.Count > 0)
            sb.AppendLine($"{ind}  implements: {string.Join(", ", cls.Interfaces)}");
            
        foreach (var method in cls.Methods)
        {
            sb.AppendLine($"{ind}    {method.Access} {method.ReturnType.Name} {method.Name}(...) [{method.Body.Statements.Count} inst]");
        }
    }

    private void DumpInterface(StringBuilder sb, InterfaceNode iface, int indent = 0)
    {
        var ind = new string(' ', indent * 2);
        sb.AppendLine($"{ind}interface {iface.Name}");
        foreach (var method in iface.Methods)
        {
            sb.AppendLine($"{ind}    {method.ReturnType.Name} {method.Name}(...)");
        }
    }

    public static ModuleNode LoadFromJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var data = JsonSerializer.Deserialize<ModuleData>(json, options) ?? throw new InvalidOperationException("Failed to deserialize module data");
        return LoadModule(data);
    }

    public static ModuleNode LoadModule(ModuleData data)
    {
        var module = new ModuleNode(data.Name)
        {
            Version = data.Version
        };

        foreach (var typeData in data.Types)
        {
            switch (typeData.Kind)
            {
                case "Class":
                    module.Classes.Add(LoadClass(typeData));
                    break;
                case "Interface":
                    module.Interfaces.Add(LoadInterface(typeData));
                    break;
                case "Struct":
                    module.Structs.Add(LoadStruct(typeData));
                    break;
            }
        }

        return module;
    }

    private static ClassNode LoadClass(TypeData data)
    {
        var cls = new ClassNode(data.Name)
        {
            Namespace = data.Namespace,
            IsAbstract = data.IsAbstract,
            IsSealed = data.IsSealed,
            BaseType = data.BaseType
        };

        if (Enum.TryParse<AccessModifier>(data.Access, out var acc))
            cls.Modifiers.Add(acc);

        cls.Interfaces.AddRange(data.Interfaces);
        cls.GenericParameters.AddRange(data.GenericParameters.Select(p => new GenericParameterNode(p)));

        foreach (var fd in data.Fields)
        {
            var field = new FieldNode(fd.Name, new TypeRef(fd.Type))
            {
                IsStatic = fd.IsStatic,
                IsReadOnly = fd.IsReadOnly,
                InitialValue = fd.InitialValue
            };
            if (Enum.TryParse<AccessModifier>(fd.Access, out var facc)) field.Access = facc;
            cls.Fields.Add(field);
        }

        foreach (var md in data.Methods)
        {
            var method = new MethodNode(md.Name)
            {
                ReturnType = new TypeRef(md.ReturnType),
                IsStatic = md.IsStatic,
                IsVirtual = md.IsVirtual,
                IsOverride = md.IsOverride,
                IsAbstract = md.IsAbstract
            };
            if (Enum.TryParse<AccessModifier>(md.Access, out var macc)) method.Access = macc;
            method.Parameters.AddRange(md.Parameters.Select(p => new ParameterNode(p.Name, new TypeRef(p.Type))));
            method.Locals.AddRange(md.LocalVariables.Select(l => new LocalDeclarationStatement(l.Name, new TypeRef(l.Type))));
            
            if (md.Instructions is JsonArray array)
            {
                using var doc = JsonDocument.Parse(array.ToJsonString());
                method.Body.Statements.AddRange(InstructionSerializer.DeserializeInstructions(doc.RootElement));
            }
            cls.Methods.Add(method);
        }

        if (data.Constructors != null)
        {
            foreach (var cd in data.Constructors)
            {
                var ctor = new ConstructorNode();
                ctor.Parameters.AddRange(cd.Parameters.Select(p => new ParameterNode(p.Name, new TypeRef(p.Type))));
                if (cd.Instructions is JsonArray array)
                {
                    using var doc = JsonDocument.Parse(array.ToJsonString());
                    ctor.Body.Statements.AddRange(InstructionSerializer.DeserializeInstructions(doc.RootElement));
                }
                cls.Constructors.Add(ctor);
            }
        }

        return cls;
    }

    private static InterfaceNode LoadInterface(TypeData data)
    {
        var iface = new InterfaceNode(data.Name) { Namespace = data.Namespace };
        if (Enum.TryParse<AccessModifier>(data.Access, out var acc)) iface.Access = acc;
        foreach (var md in data.Methods)
        {
            var sig = new MethodSignature(md.Name) { ReturnType = new TypeRef(md.ReturnType), IsStatic = md.IsStatic };
            sig.Parameters.AddRange(md.Parameters.Select(p => new ParameterNode(p.Name, new TypeRef(p.Type))));
            iface.Methods.Add(sig);
        }
        return iface;
    }

    private static StructNode LoadStruct(TypeData data)
    {
        var str = new StructNode(data.Name) { Namespace = data.Namespace };
        if (Enum.TryParse<AccessModifier>(data.Access, out var acc)) str.Access = acc;
        foreach (var fd in data.Fields)
        {
            var field = new FieldNode(fd.Name, new TypeRef(fd.Type));
            if (Enum.TryParse<AccessModifier>(fd.Access, out var facc)) field.Access = facc;
            str.Fields.Add(field);
        }
        return str;
    }

    public byte[] DumpToBson()
    {
        var data = DumpModule();
        var bsonDoc = BsonSerializer.ModuleDataToBson(data);
        return bsonDoc.ToBson();
    }

    public static ModuleNode LoadFromBson(byte[] bsonData)
    {
        using var stream = new MemoryStream(bsonData);
        using var reader = new BsonBinaryReader(stream);
        var context = BsonDeserializationContext.CreateRoot(reader);
        var bsonDoc = BsonDocumentSerializer.Instance.Deserialize(context);
        var moduleData = BsonSerializer.BsonToModuleData(bsonDoc);
        return LoadModule(moduleData);
    }
}

public sealed class ModuleData
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public TypeData[] Types { get; set; } = Array.Empty<TypeData>();
    public FunctionData[] Functions { get; set; } = Array.Empty<FunctionData>();
}

public sealed class TypeData
{
    public string Kind { get; set; } = string.Empty; 
    public string Name { get; set; } = string.Empty;
    public string? Namespace { get; set; }
    public string Access { get; set; } = string.Empty;
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    public string? BaseType { get; set; }
    public string[] Interfaces { get; set; } = Array.Empty<string>();
    public string[] BaseInterfaces { get; set; } = Array.Empty<string>();
    public string? UnderlyingType { get; set; }
    public string[] GenericParameters { get; set; } = Array.Empty<string>();
    public FieldData[] Fields { get; set; } = Array.Empty<FieldData>();
    public PropertyData[] Properties { get; set; } = Array.Empty<PropertyData>();
    public MethodData[] Methods { get; set; } = Array.Empty<MethodData>();
    public MethodData[] Constructors { get; set; } = Array.Empty<MethodData>();
}

public sealed class FieldData
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsReadOnly { get; set; }
    public string? InitialValue { get; set; }
}

public sealed class PropertyData
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public bool HasGetter { get; set; }
    public bool HasSetter { get; set; }
    public string? GetterAccess { get; set; }
    public string? SetterAccess { get; set; }
}

public sealed class MethodData
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsConstructor { get; set; }
    public ParameterData[] Parameters { get; set; } = Array.Empty<ParameterData>();
    public LocalVariableData[] LocalVariables { get; set; } = Array.Empty<LocalVariableData>();
    public int InstructionCount { get; set; }
    public JsonNode? Instructions { get; set; }
}

public sealed class FunctionData
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public ParameterData[] Parameters { get; set; } = Array.Empty<ParameterData>();
    public LocalVariableData[] LocalVariables { get; set; } = Array.Empty<LocalVariableData>();
    public int InstructionCount { get; set; }
    public JsonNode? Instructions { get; set; }
}

public sealed class ParameterData
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public sealed class LocalVariableData
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
