using System.Globalization;
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
            var argTypes = Enumerable.Repeat(new TypeRef("?"), (int)nc.ParamCount).ToList();
            var target = new MethodReference(new TypeRef(declaring), method, TypeRef.Void, argTypes);
            return new CallInstruction(target, argTypes, instruction.Opcode == Opcode.Callvirt);
        }

        if (instruction.Opcode == Opcode.Newobj && instruction.Operand is OperandString ns)
        {
            return new NewObjInstruction(new TypeRef(_mod.Resolve(ns.StringIndex)), null, new List<TypeRef>());
        }

        return new SimpleInstruction(ToAst(instruction.Opcode), OperandToText(instruction.Operand));
    }

    private static (string Declaring, string Name) SplitQualified(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? ("", name) : (name[..dot], name[(dot + 1)..]);
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
