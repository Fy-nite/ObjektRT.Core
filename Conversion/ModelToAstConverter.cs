using System.Globalization;
using System.Linq;
using System.Reflection;
using ObjektRT.Core.AST;
using ObjektRT.Core.Model;
using ObjektRT.Core.Serialization;
using Instruction = ObjektRT.Core.Model.Instruction;

namespace ObjektRT.Core.Conversion;

/// <summary>
/// Converts an <see cref="ORBTModule"/> (index-based wire model) into a rich
/// <see cref="ModuleNode"/> AST. This is the direction tooling uses to
/// analyse or re-emit modules that were parsed from ObjectIL text or read
/// from an ORBT/FOB binary.
/// </summary>
/// <remarks>
/// <para>Fidelity notes (v1):</para>
/// <list type="bullet">
/// <item>
/// <description>Flat <c>dup; brfalse … br …</c> bytecode produced by the
/// canonical <c>if</c>/<c>while</c> lowering is reconstructed back into
/// structured <see cref="IfStatement"/>/<see cref="WhileStatement"/> nodes.
/// Anything that does not match the canonical shapes stays as flat
/// <see cref="SimpleInstruction"/> statements (semantics preserved, structure
/// flat).</description>
/// </item>
/// <item>
/// <description>Method locals are emitted as <c>local</c> declarations at the
/// top of the method body (the ObjectIL text convention).</description>
/// </item>
/// <item>
/// <description>The wire model has no field access modifiers; fields come out
/// as <c>public</c>. Wire <c>Enum</c> types map to <see cref="ClassNode"/>
/// because the AST has no enum node.</description>
/// </item>
/// <item>
/// <description><c>try/catch</c> and non-stack <c>if</c>/<c>while</c>
/// condition operands have no AST representation and throw
/// <see cref="NotSupportedException"/>.</description>
/// </item>
/// </list>
/// </remarks>
public sealed class ModelToAstConverter
{
    private ORBTModule _mod = null!;

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>Converts <paramref name="mod"/> into a new AST module.</summary>
    public ModuleNode Convert(ORBTModule mod)
    {
        _mod = mod;
        var ast = new ModuleNode(mod.ModuleName)
        {
            Version = $"{mod.Version.Major}.{mod.Version.Minor}.{mod.Version.Patch}",
        };

        foreach (var type in mod.Types)
        {
            switch (type.Kind)
            {
                case TypeKind.Interface:
                    ast.Interfaces.Add(ConvertInterface(type));
                    break;
                case TypeKind.Struct:
                    ast.Structs.Add(ConvertStruct(type));
                    break;
                default:
                    ast.Classes.Add(ConvertClass(type)); // Class and Enum
                    break;
            }
        }
        return ast;
    }

    // ── Types ─────────────────────────────────────────────────────────

    private InterfaceNode ConvertInterface(TypeRecord type)
    {
        var iface = new InterfaceNode(S(type.NameIndex));
        foreach (var m in type.Methods)
        {
            var ms = new MethodSignature(S(m.NameIndex))
            {
                ReturnType = new TypeRef(S(m.SignatureIndex)),
                IsStatic = (m.Flags & MethodFlags.Static) != 0,
            };
            foreach (var p in m.Params)
                ms.Parameters.Add(new ParameterNode(S(p.NameIndex), new TypeRef(S(p.TypeIndex))));
            iface.Methods.Add(ms);
        }
        return iface;
    }

    private StructNode ConvertStruct(TypeRecord type)
    {
        var str = new StructNode(S(type.NameIndex));
        str.Attributes.AddRange(RestoreAttributes(type.Attributes));
        foreach (var f in type.Fields)
            str.Fields.Add(new FieldNode(S(f.NameIndex), new TypeRef(S(f.TypeIndex))) { Access = AccessModifier.Public, IsStatic = f.IsStatic });
        if (type.Methods.Count > 0)
            str.Methods = type.Methods.Select(ConvertMethod).ToList();
        return str;
    }

    private ClassNode ConvertClass(TypeRecord type)
    {
        var cls = new ClassNode(S(type.NameIndex))
        {
            IsAbstract = (type.Flags & TypeFlags.Abstract) != 0,
            IsSealed = (type.Flags & TypeFlags.Sealed) != 0,
        };
        cls.Attributes.AddRange(RestoreAttributes(type.Attributes));

        if (type.BaseTypeIndex >= 0 && type.BaseTypeIndex < _mod.Types.Count)
            cls.BaseTypes.Add(S(_mod.Types[type.BaseTypeIndex].NameIndex));
        foreach (var ifIdx in type.InterfaceIndices)
            cls.Interfaces.Add(S(ifIdx));

        foreach (var f in type.Fields)
            cls.Fields.Add(new FieldNode(S(f.NameIndex), new TypeRef(S(f.TypeIndex))) { Access = AccessModifier.Public, IsStatic = f.IsStatic });

        foreach (var m in type.Methods)
        {
            if (S(m.NameIndex) == ".ctor")
                cls.Constructors.Add(ConvertConstructor(m));
            else
                cls.Methods.Add(ConvertMethod(m));
        }
        return cls;
    }

    /// <summary>Restores AST attribute nodes from wire-model attribute records.</summary>
    private IEnumerable<AttributeNode> RestoreAttributes(IEnumerable<AttributeRecord> records)
    {
        foreach (var attr in records)
        {
            yield return new AttributeNode(S(attr.NameIndex), attr.ArgIndices.Select(i => S(i)));
        }
    }

    // ── Methods ───────────────────────────────────────────────────────

    private ConstructorNode ConvertConstructor(MethodRecord m)
    {
        var ctor = new ConstructorNode();
        ctor.Attributes.AddRange(RestoreAttributes(m.Attributes));
        foreach (var p in m.Params)
            ctor.Parameters.Add(new ParameterNode(S(p.NameIndex), new TypeRef(S(p.TypeIndex))));
        ctor.Body = new BlockStatement(ReconstructBody(m));
        return ctor;
    }

    private MethodNode ConvertMethod(MethodRecord m)
    {
        var method = new MethodNode(S(m.NameIndex))
        {
            ReturnType = new TypeRef(S(m.SignatureIndex)),
            IsStatic = (m.Flags & MethodFlags.Static) != 0,
            IsVirtual = (m.Flags & MethodFlags.Virtual) != 0,
            IsOverride = (m.Flags & MethodFlags.Override) != 0,
            IsAbstract = (m.Flags & MethodFlags.Abstract) != 0,
            Access = MapAccess(m.Access),
        };
        method.Attributes.AddRange(RestoreAttributes(m.Attributes));
        foreach (var p in m.Params)
            method.Parameters.Add(new ParameterNode(S(p.NameIndex), new TypeRef(S(p.TypeIndex))));
        method.Body = new BlockStatement(ReconstructBody(m));
        return method;
    }

    /// <summary>
    /// Reconstructs a method body: hoisted <c>local</c> declarations followed
    /// by the structured statements. Uses decoded instructions when present,
    /// otherwise decodes <see cref="MethodRecord.RawInstructionData"/>.
    /// </summary>
    private List<Statement> ReconstructBody(MethodRecord m)
    {
        var instructions = m.Instructions.Count > 0
            ? m.Instructions
            : ORBTReader.DecodeRawBytecode(m.RawInstructionData, _mod.StringPool);

        var statements = new List<Statement>();
        foreach (var local in m.Locals)
            statements.Add(new LocalDeclarationStatement(S(local.NameIndex), new TypeRef(S(local.TypeIndex))));

        statements.AddRange(ReconstructRange(instructions, 0, instructions.Count));
        return statements;
    }

    // ── Flat → structured reconstruction ──────────────────────────────

    private List<Statement> ReconstructRange(List<Instruction> instructions, int start, int end)
    {
        var pcMap = BuildPcMap(instructions);
        var result = new List<Statement>();
        int i = start;
        while (i < end)
        {
            if (TryMatchWhile(instructions, pcMap, i, out var whileStmt, out int consumed))
            {
                result.Add(whileStmt);
                i += consumed;
                continue;
            }
            if (TryMatchIf(instructions, pcMap, i, out var ifStmt, out consumed))
            {
                result.Add(ifStmt);
                i += consumed;
                continue;
            }
            result.Add(new InstructionStatement(ToAstInstruction(instructions[i])));
            i++;
        }
        return result;
    }

    /// <summary>
    /// Matches the canonical while lowering produced by the parser and the
    /// <see cref="AstToModelConverter"/>: <c>dup; brfalse end; body; br loop; end:</c>.
    /// </summary>
    private bool TryMatchWhile(
        List<Instruction> instructions, Dictionary<uint, int> pcMap,
        int i, out WhileStatement stmt, out int consumed)
    {
        stmt = null!;
        consumed = 0;
        if (i + 1 >= instructions.Count) return false;
        if (instructions[i].Opcode != Opcode.Dup) return false;
        if (instructions[i + 1].Opcode != Opcode.Brfalse) return false;

        int endIdx = ResolveTarget(instructions, pcMap, instructions[i + 1]);
        if (endIdx < 0) return false;

        // Body runs until a br back to the loop start.
        int j = i + 2;
        while (j < instructions.Count && j < endIdx)
        {
            if (instructions[j].Opcode == Opcode.Br
                && BranchTarget(instructions[j]) == instructions[i].PcOffset)
                break;
            j++;
        }
        if (j >= endIdx) return false; // no back-branch — not a canonical while
        if (endIdx != j + 1) return false; // label must land right after the br

        var body = new BlockStatement(ReconstructRange(instructions, i + 2, j));
        stmt = new WhileStatement("stack", body);
        consumed = endIdx + 1 - i;
        return true;
    }

    /// <summary>
    /// Matches the canonical if/else lowering produced by the parser and the
    /// <see cref="AstToModelConverter"/>:
    /// <c>brfalse else; then; br end; else: elseBlock; end:</c>.
    /// </summary>
    private bool TryMatchIf(
        List<Instruction> instructions, Dictionary<uint, int> pcMap,
        int i, out IfStatement stmt, out int consumed)
    {
        stmt = null!;
        consumed = 0;
        if (instructions[i].Opcode != Opcode.Brfalse) return false;

        int elseIdx = ResolveTarget(instructions, pcMap, instructions[i]);
        if (elseIdx <= i + 1 || elseIdx > instructions.Count) return false;

        // Look for the terminating br of the then-block: the first br whose
        // target lands after the else label (the outer end label). Internal
        // control flow (nested ifs/whiles, breaks) targets labels inside the
        // then region, so it is skipped.
        uint? endTarget = null;
        int brIndex = -1;
        for (int j = i + 1; j < elseIdx; j++)
        {
            if (instructions[j].Opcode == Opcode.Br)
            {
                uint t = BranchTarget(instructions[j]);
                int ti = ResolveTarget(instructions, pcMap, instructions[j]);
                if (ti > elseIdx)
                {
                    endTarget = t;
                    brIndex = j;
                    break;
                }
            }
        }

        BlockStatement thenBlock, elseBlock;
        if (endTarget is uint end)
        {
            int endIdx = ResolveTarget(instructions, pcMap, end);
            if (endIdx < 0 || endIdx <= brIndex || endIdx > instructions.Count)
                return false;
            thenBlock = new BlockStatement(ReconstructRange(instructions, i + 1, brIndex));
            elseBlock = new BlockStatement(ReconstructRange(instructions, brIndex + 1, endIdx));
            consumed = endIdx + 1 - i;
        }
        else
        {
            // No else: the else label is the end of the then-block.
            thenBlock = new BlockStatement(ReconstructRange(instructions, i + 1, elseIdx));
            elseBlock = null!;
            consumed = elseIdx + 1 - i;
        }

        stmt = new IfStatement("stack", thenBlock, elseBlock);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Resolves a branch's PC target to an instruction index.</summary>
    private static int ResolveTarget(List<Instruction> instructions, Dictionary<uint, int> pcMap, Instruction branch)
        => ResolveTarget(instructions, pcMap, BranchTarget(branch));

    private static int ResolveTarget(List<Instruction> instructions, Dictionary<uint, int> pcMap, uint target)
    {
        if (pcMap.TryGetValue(target, out int idx))
            return idx;
        // Label past the end of the method (trailing if/while).
        return target > instructions[^1].PcOffset ? instructions.Count : -1;
    }

    private static uint BranchTarget(Instruction instruction)
    {
        var branch = (OperandBranch)instruction.Operand;
        // Opcode (1 byte) + I32 operand (4 bytes); offset is relative to the end.
        return instruction.PcOffset + 5 + (uint)branch.PcOffset;
    }

    private static Dictionary<uint, int> BuildPcMap(List<Instruction> instructions)
    {
        var map = new Dictionary<uint, int>(instructions.Count);
        for (int i = 0; i < instructions.Count; i++)
            map[instructions[i].PcOffset] = i;
        return map;
    }

    private AST.Instruction ToAstInstruction(Instruction instruction)
    {
        // Reconstruct rich call instructions so the parameter count survives a
        // round-trip (the wire stores name + count, not argument types).
        if ((instruction.Opcode is Opcode.Call or Opcode.Callvirt or Opcode.NativeCall)
            && instruction.Operand is OperandNativeCall nc)
        {
            var name = _mod.Resolve(nc.StringIndex);
            var (declaring, method) = SplitQualified(name);
            // Prefer the declaring type's stored method signature when it lives
            // in this module — this recovers the real parameter types, including
            // the generic type parameter (T) for generic-class methods, instead
            // of falling back to the placeholder "?". Native/CLR-import calls
            // (Array.Copy, IO.Println, …) have no in-module declaration, so they
            // keep the placeholder.
            var argTypes = ResolveCallParameterTypes(declaring, method, (int)nc.ParamCount, out var returnType);
            var target = new MethodReference(new TypeRef(declaring), method, returnType, argTypes);
            return new CallInstruction(target, argTypes, instruction.Opcode == Opcode.Callvirt);
        }

        if (instruction.Opcode == Opcode.Newobj && instruction.Operand is OperandString ns)
        {
            return new NewObjInstruction(new TypeRef(_mod.Resolve(ns.StringIndex)), null, new List<TypeRef>());
        }

        return new SimpleInstruction(ToAst(instruction.Opcode), OperandToText(instruction.Operand));
    }

    /// <summary>True for the two constructor spellings used across the toolchain (.ctor / .constructor).</summary>
    private static bool IsConstructorName(string name)
        => name is ".ctor" or ".constructor";

    /// <summary>
    /// Recovers a constructor's parameter types when the binary stored none.
    /// Generic classes keep their type parameters as field types (e.g.
    /// <c>option.Value.v : T</c>, <c>Result.Ok.value : V</c>), so each such
    /// field yields a corresponding constructor parameter after the implicit
    /// <c>this</c>. This turns <c>option.Value..ctor(?, ?)</c> into
    /// <c>option.Value..ctor(object, T)</c> on the round-trip.
    /// </summary>
    private List<TypeRef> InferCtorParameterTypes(TypeRecord type, int count)
    {
        var argTypes = new List<TypeRef> { new TypeRef("object") }; // implicit this

        var declaredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _mod.Types) declaredTypes.Add(ShortNameOf(S(t.NameIndex)));

        foreach (var f in type.Fields)
        {
            var ft = S(f.TypeIndex);
            if (IsGenericParameterName(ft, declaredTypes))
                argTypes.Add(new TypeRef(ft));
        }

        while (argTypes.Count < count) argTypes.Add(new TypeRef("?"));
        if (argTypes.Count > count) argTypes = argTypes.GetRange(0, count);
        return argTypes;
    }

    /// <summary>The unqualified (short) name portion of a (possibly dotted) type name.</summary>
    private static string ShortNameOf(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot > 0 ? fullName[(dot + 1)..] : fullName;
    }

    /// <summary>
    /// A bare, unqualified type name that is neither a built-in nor a type
    /// declared in this module is treated as a generic type parameter (T, V, E, …).
    /// </summary>
    private static bool IsGenericParameterName(string typeName, HashSet<string> declaredTypes)
    {
        if (string.IsNullOrEmpty(typeName) || typeName.IndexOf('.') >= 0)
            return false;
        if (CtorBuiltinTypeNames.Contains(typeName))
            return false;
        return !declaredTypes.Contains(typeName);
    }

    /// <summary>Built-in primitive type names that must never be treated as generic parameters.</summary>
    private static readonly HashSet<string> CtorBuiltinTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "string", "bool", "double", "float", "object", "int64", "long", "null", "void",
        "byte", "sbyte", "short", "ushort", "uint", "int32", "float32", "float64",
        "uint8", "int8", "int16", "uint16", "uint32", "intptr",
    };

    private static (string Declaring, string Name) SplitQualified(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return ("", name);
        var declaring = name[..dot];
        var method = name[(dot + 1)..];
        // The `Type..ctor` / `Type..constructor` convention leaves a trailing
        // dot on the declaring part (the method name itself starts with a dot).
        // Strip it so the declaring type resolves correctly.
        if (declaring.EndsWith('.'))
        {
            method = "." + method;
            declaring = declaring[..^1];
        }
        return (declaring, method);
    }

    /// <summary>
    /// Recovers the parameter (and return) types for a native call by looking
    /// the method up in this module's type definitions. The wire format stores
    /// only the method name + argument count, so without this every native-call
    /// argument would round-trip as the placeholder <c>"?"</c>. When the callee
    /// is declared in this module (e.g. a generic-class method such as
    /// <c>thing.List.Get</c> or <c>ObjektRT.std.option.Value..ctor</c>) the
    /// stored signature — including the generic type parameter <c>T</c> — is
    /// used. Calls to external/native bindings that have no in-module
    /// declaration fall back to the placeholder.
    /// </summary>
    private List<TypeRef> ResolveCallParameterTypes(string declaring, string method, int count, out TypeRef returnType)
    {
        foreach (var type in _mod.Types)
        {
            var typeName = S(type.NameIndex);
            if (typeName != declaring && !typeName.EndsWith("." + declaring, StringComparison.Ordinal))
                continue;

            // Collect every method that could be the callee (exact name, or a
            // constructor pair .ctor/.constructor), then pick the richest one —
            // the stored newobj target (.constructor) often carries the real
            // parameter types while the explicit .ctor wrapper has them erased.
            MethodRecord? best = null;
            foreach (var m in type.Methods)
            {
                var namesMatch = S(m.NameIndex) == method;
                var bothCtor = IsConstructorName(S(m.NameIndex)) && IsConstructorName(method);
                if (!namesMatch && !bothCtor)
                    continue;

                if (best == null || m.Params.Count > best.Params.Count)
                    best = m;
            }

            if (best != null)
            {
                List<TypeRef> argTypes;
                if (best.Params.Count > 0)
                {
                    argTypes = best.Params
                        .Select(p => new TypeRef(S(p.TypeIndex)))
                        .ToList();
                }
                else if (IsConstructorName(method))
                {
                    // The binary erases constructor parameter metadata, but a
                    // generic class keeps its type parameters as field types
                    // (e.g. option.Value.v : T). Recover the constructor's
                    // parameter types from those fields so the dump shows the
                    // real T/V/E instead of "?".
                    argTypes = InferCtorParameterTypes(type, count);
                }
                else
                {
                    argTypes = new List<TypeRef>();
                }

                // Pad/truncate to the wire count so the round-tripped call keeps
                // its operand arity even if the stored signature is incomplete.
                while (argTypes.Count < count) argTypes.Add(new TypeRef("?"));
                if (argTypes.Count > count) argTypes = argTypes.GetRange(0, count);

                // Constructors always return void; the stored signature index is
                // sometimes the method name itself, so force void for ctors.
                if (IsConstructorName(method))
                {
                    returnType = TypeRef.Void;
                }
                else
                {
                    var sig = best.SignatureIndex < _mod.StringPool.Count ? S(best.SignatureIndex) : "";
                    returnType = string.IsNullOrEmpty(sig) ? TypeRef.Void : new TypeRef(sig);
                }
                return argTypes;
            }
        }

        // The callee isn't declared in this module (a native/CLR-import call
        // such as Array.Copy, Delegate.Invoke, Convert.ToString, IO.Println).
        // Fall back to C# reflection on the host type, which knows the real
        // parameter types even though the wire format doesn't.
        if (TryResolveViaReflection(declaring, method, count, out var reflected, out returnType))
            return reflected;

        returnType = TypeRef.Void;
        return Enumerable.Repeat(new TypeRef("?"), count).ToList();
    }

    /// <summary>
    /// Resolves a native/CLR-import call's parameter types by reflecting over
    /// the host CLR type. This recovers the real signatures (e.g.
    /// <c>Array.Copy(Array, int, Array, int, int)</c>) that the ORBT wire
    /// format doesn't carry. Returns false when the type can't be located, so
    /// the caller keeps the placeholder.
    /// </summary>
    private static bool TryResolveViaReflection(
        string declaring, string method, int count, out List<TypeRef> argTypes, out TypeRef returnType)
    {
        argTypes = new List<TypeRef>();
        returnType = TypeRef.Void;
        try
        {
            var type = ResolveHostType(declaring);
            if (type == null) return false;

            // Delegate.Invoke only exists on concrete delegate types, not on
            // System.Delegate itself, so reflect can't find it — but its wire
            // shape is always a single object argument.
            if (type == typeof(Delegate) && method.TrimStart('.') == "Invoke")
            {
                argTypes = new List<TypeRef> { new TypeRef("object") };
                returnType = TypeRef.Void;
                return true;
            }

            ParameterInfo[]? parameters = null;
            Type? ret = null;
            if (IsConstructorName(method))
            {
                var ctor = type.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .FirstOrDefault(c => c.GetParameters().Length == count)
                           ?? type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                if (ctor != null) { parameters = ctor.GetParameters(); ret = null; }
            }
            else
            {
                var name = method.TrimStart('.');
                var match = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.Name == name)
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault(m => m.GetParameters().Length == count)
                           ?? type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Static | BindingFlags.Instance)
                                  .Where(m => m.Name == name).FirstOrDefault();
                if (match != null) { parameters = match.GetParameters(); ret = match.ReturnType; }
            }

            if (parameters == null) return false;
            argTypes = parameters.Select(p => new TypeRef(ClrTypeToWireName(p.ParameterType))).ToList();
            returnType = ret == null ? TypeRef.Void : new TypeRef(ClrTypeToWireName(ret));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Maps a short/qualified callee type name to its CLR Type.</summary>
    private static Type? ResolveHostType(string declaring)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Array"] = "System.Array",
            ["Convert"] = "System.Convert",
            ["Delegate"] = "System.Delegate",
            ["Math"] = "System.Math",
            ["Console"] = "System.Console",
            ["String"] = "System.String",
            ["Object"] = "System.Object",
            ["Environment"] = "System.Environment",
            ["IO"] = "ObjektRT.Stdlib.System.IO",
            ["File"] = "System.IO.File",
            ["Directory"] = "System.IO.Directory",
        };
        if (aliases.TryGetValue(declaring, out var full))
            declaring = full;

        return Type.GetType(declaring)
            ?? Type.GetType(declaring + ", System.Runtime")
            ?? Type.GetType(declaring + ", System.Private.CoreLib")
            ?? Type.GetType(declaring + ", mscorlib")
            ?? Type.GetType(declaring + ", ObjektRT.Stdlib");
    }

    /// <summary>Maps a CLR <see cref="Type"/> to its ObjektIR wire type name.</summary>
    private static string ClrTypeToWireName(Type t)
    {
        if (t == typeof(int) || t == typeof(uint)) return "int32";
        if (t == typeof(long) || t == typeof(ulong)) return "int64";
        if (t == typeof(short) || t == typeof(ushort)) return "int16";
        if (t == typeof(byte) || t == typeof(sbyte)) return "uint8";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t == typeof(double)) return "float64";
        if (t == typeof(float)) return "float32";
        if (t == typeof(void)) return "void";
        if (t == typeof(object)) return "object";
        if (t.IsByRef || t.IsArray) return "object";   // type-erased at the wire
        if (t == typeof(Array) || t == typeof(Delegate)) return "object";
        if (t.IsGenericParameter) return t.Name;        // T, V, E, …
        return (t.FullName ?? t.Name).Replace('+', '.');
    }

    /// <summary>
    /// Maps an ORBT wire opcode to its AST opcode. Every wire opcode has an
    /// AST representation.
    /// </summary>
    public static bool TryGetAstOpcode(Opcode wire, out OpCode ast)
    {
        switch (wire)
        {
            case Opcode.Nop: ast = OpCode.Nop; return true;
            case Opcode.Ldc: ast = OpCode.Ldc; return true;
            case Opcode.Ldstr: ast = OpCode.Ldstr; return true;
            case Opcode.Ldarg: ast = OpCode.Ldarg; return true;
            case Opcode.Starg: ast = OpCode.Starg; return true;
            case Opcode.Ldloc: ast = OpCode.Ldloc; return true;
            case Opcode.Stloc: ast = OpCode.Stloc; return true;
            case Opcode.Add: ast = OpCode.Add; return true;
            case Opcode.Sub: ast = OpCode.Sub; return true;
            case Opcode.Mul: ast = OpCode.Mul; return true;
            case Opcode.Div: ast = OpCode.Div; return true;
            case Opcode.Rem: ast = OpCode.Rem; return true;
            case Opcode.Neg: ast = OpCode.Neg; return true;
            case Opcode.Ceq: ast = OpCode.Ceq; return true;
            case Opcode.Cne: ast = OpCode.Cne; return true;
            case Opcode.Ldfld: ast = OpCode.Ldfld; return true;
            case Opcode.Ldsfld: ast = OpCode.Ldsfld; return true;
            case Opcode.Stsfld: ast = OpCode.Stsfld; return true;
            case Opcode.Newobj: ast = OpCode.Newobj; return true;
            case Opcode.Newarr: ast = OpCode.Newarr; return true;
            case Opcode.Ldelem: ast = OpCode.Ldelem; return true;
            case Opcode.Ldlen: ast = OpCode.Ldlen; return true;
            case Opcode.Stelem: ast = OpCode.Stelem; return true;
            case Opcode.Call: ast = OpCode.Call; return true;
            case Opcode.Callvirt: ast = OpCode.Callvirt; return true;
            case Opcode.NativeCall: ast = OpCode.NativeCall; return true;
            case Opcode.Ret: ast = OpCode.Ret; return true;
            case Opcode.If: ast = OpCode.If; return true;
            case Opcode.While: ast = OpCode.While; return true;
            case Opcode.Break: ast = OpCode.Break; return true;
            case Opcode.Continue: ast = OpCode.Continue; return true;
            case Opcode.Try: ast = OpCode.Try; return true;
            case Opcode.Throw: ast = OpCode.Throw; return true;
            case Opcode.Conv: ast = OpCode.Conv; return true;
            case Opcode.Castclass: ast = OpCode.Castclass; return true;
            case Opcode.Isinst: ast = OpCode.Isinst; return true;
            case Opcode.Dup: ast = OpCode.Dup; return true;
            case Opcode.Pop: ast = OpCode.Pop; return true;
            case Opcode.Ldnull: ast = OpCode.Ldnull; return true;
            case Opcode.Not: ast = OpCode.Not; return true;
            case Opcode.Cgt: ast = OpCode.Cgt; return true;
            case Opcode.Cge: ast = OpCode.Cge; return true;
            case Opcode.Clt: ast = OpCode.Clt; return true;
            case Opcode.Cle: ast = OpCode.Cle; return true;
            case Opcode.Stfld: ast = OpCode.Stfld; return true;
            case Opcode.LdcI4: ast = OpCode.LdcI4; return true;
            case Opcode.LdcI8: ast = OpCode.LdcI8; return true;
            case Opcode.LdcR4: ast = OpCode.LdcR4; return true;
            case Opcode.LdcR8: ast = OpCode.LdcR8; return true;
            case Opcode.And: ast = OpCode.And; return true;
            case Opcode.Xor: ast = OpCode.Xor; return true;
            case Opcode.Or: ast = OpCode.Or; return true;
            case Opcode.Shl: ast = OpCode.Shl; return true;
            case Opcode.Br: ast = OpCode.Br; return true;
            case Opcode.Brtrue: ast = OpCode.Brtrue; return true;
            case Opcode.Brfalse: ast = OpCode.Brfalse; return true;
            default: ast = default; return false;
        }
    }

    private static OpCode ToAst(Opcode op)
    {
        if (TryGetAstOpcode(op, out var ast))
            return ast;
        throw new NotSupportedException($"wire opcode {op} has no AST representation");
    }

    private string? OperandToText(Operand operand) => operand switch
    {
        OperandNone => null,
        OperandI4 i4 => i4.Value.ToString(CultureInfo.InvariantCulture),
        OperandI8 i8 => i8.Value.ToString(CultureInfo.InvariantCulture),
        OperandR4 r4 => r4.Value.ToString("R", CultureInfo.InvariantCulture),
        OperandR8 r8 => r8.Value.ToString("R", CultureInfo.InvariantCulture),
        OperandString s => _mod.Resolve(s.StringIndex),
        OperandIndex ix => ix.Index.ToString(CultureInfo.InvariantCulture),
        OperandFieldRef f => _mod.Resolve(f.StringIndex),
        OperandMethodRef m => _mod.Resolve(m.StringIndex),
        OperandTypeRef t => _mod.Resolve(t.StringIndex),
        OperandNativeCall nc => _mod.Resolve(nc.StringIndex),
        OperandBranch b => b.PcOffset.ToString(CultureInfo.InvariantCulture),
        ConditionOperand => "stack",
        ExceptionHandlerOperand =>
            throw new NotSupportedException("try/catch operands cannot be represented in the AST"),
        _ => null,
    };

    private string S(ushort index) => _mod.Resolve(index);

    private static AccessModifier MapAccess(MemberAccess access) => access switch
    {
        MemberAccess.Private => AccessModifier.Private,
        MemberAccess.Protected => AccessModifier.Protected,
        MemberAccess.Internal => AccessModifier.Internal,
        _ => AccessModifier.Public,
    };
}
