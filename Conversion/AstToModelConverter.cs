using System.Globalization;
using ObjektRT.Core.AST;
using OpCode = ObjektRT.Core.AST.OpCode;
using ObjektRT.Core.Model;

namespace ObjektRT.Core.Conversion;

/// <summary>
/// Converts an <see cref="ModuleNode"/> (rich AST) into an
/// <see cref="ORBTModule"/> (index-based wire model). This is the direction
/// compilers use to emit runnable modules: build an AST (via
/// <see cref="Parsing.TextIrParser"/>, the <see cref="Builder.IRBuilder"/>, or
/// a front-end), then convert to the wire model and run/compile it.
/// </summary>
/// <remarks>
/// <para>Fidelity notes (v1):</para>
/// <list type="bullet">
/// <item>
/// <description>Structured <c>if</c>/<c>while</c> statements are lowered to flat
/// <c>brfalse</c>/<c>br</c>/<c>dup</c> bytecode with labels, exactly like the
/// ObjectIL parser does.</description>
/// </item>
/// <item>
/// <description>Local declarations are hoisted to the method's local table
/// (the ORBT format declares locals at method top).</description>
/// </item>
/// <item>
/// <description>AST opcodes with no ORBT wire encoding (<c>Ldlen</c>, <c>Shl</c>,
/// <c>Shr</c>, <c>Beq</c>, <c>Calli</c>, <c>Box</c>, <c>For</c>, <c>Switch</c>, …)
/// throw <see cref="NotSupportedException"/>.</description>
/// </item>
/// <item>
/// <description>Field access modifiers and method bodies stored as
/// <c>CallInstruction</c>/<c>NewObjInstruction</c> are preserved structurally;
/// call targets become name + parameter count (the wire call encoding).</description>
/// </item>
/// </list>
/// </remarks>
public sealed class AstToModelConverter
{
    private readonly ORBTModule _mod = new();
    private readonly Dictionary<string, ushort> _intern = new();

    // ── Per-method emission state ─────────────────────────────────────
    private List<byte> _code = new();
    private uint _instrCount;
    private readonly List<(int BytePos, int LabelId)> _fixups = new();
    private readonly Dictionary<int, int> _labelPos = new();
    private int _nextLabelId;

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>Converts <paramref name="ast"/> into a new wire-model module.</summary>
    public ORBTModule Convert(ModuleNode ast)
    {
        _mod.ModuleName = ast.Name;
        _mod.FormatVersion = 0x02; // v0x02: FieldRecord gains an IsStatic flag byte
        _mod.Version = ParseVersion(ast.Version);

        foreach (var iface in ast.Interfaces)
            AddInterface(iface);
        foreach (var cls in ast.Classes)
            AddClass(cls);
        foreach (var str in ast.Structs)
            AddStruct(str);

        return _mod;
    }

    // ── Types ─────────────────────────────────────────────────────────

    private void AddInterface(InterfaceNode iface)
    {
        var type = NewType(TypeKind.Interface, iface.Name, isAbstract: true, isSealed: false);
        AddAttributes(type, iface.Attributes);
        foreach (var ms in iface.Methods)
        {
            var method = new MethodRecord
            {
                NameIndex = Intern(ms.Name),
                SignatureIndex = Intern(ms.ReturnType.Name),
                Access = MemberAccess.Public,
                Flags = ms.IsStatic ? MethodFlags.Static : MethodFlags.None,
            };
            AddParameters(method, ms.Parameters);
            type.Methods.Add(method);
        }
        type.MethodCount = (ushort)type.Methods.Count;
    }

    private void AddClass(ClassNode cls)
    {
        var type = NewType(TypeKind.Class, cls.Name, cls.IsAbstract, cls.IsSealed);
        AddAttributes(type, cls.Attributes);

        // Base type → type-table index (only resolvable when declared in this
        // module; the ORBT format cannot reference external bases).
        if (cls.BaseTypes.Count > 0)
            type.BaseTypeIndex = FindTypeIndex(cls.BaseTypes[0]);

        // Remaining base types + interfaces → string-pool interface indices.
        for (int i = 1; i < cls.BaseTypes.Count; i++)
            type.InterfaceIndices.Add(Intern(cls.BaseTypes[i]));
        foreach (var iface in cls.Interfaces)
            type.InterfaceIndices.Add(Intern(iface));
        type.InterfaceCount = (ushort)type.InterfaceIndices.Count;

        foreach (var f in cls.Fields)
            type.Fields.Add(new FieldRecord(Intern(f.Name), Intern(f.FieldType.Name), f.IsStatic));
        type.FieldCount = (ushort)type.Fields.Count;

        foreach (var ctor in cls.Constructors)
            type.Methods.Add(ConvertConstructor(ctor));
        foreach (var m in cls.Methods)
            type.Methods.Add(ConvertMethod(m));
        type.MethodCount = (ushort)type.Methods.Count;
    }

    private void AddStruct(StructNode str)
    {
        var type = NewType(TypeKind.Struct, str.Name, isAbstract: false, isSealed: false);
        AddAttributes(type, str.Attributes);
        foreach (var f in str.Fields)
            type.Fields.Add(new FieldRecord(Intern(f.Name), Intern(f.FieldType.Name), f.IsStatic));
        type.FieldCount = (ushort)type.Fields.Count;

        if (str.Methods != null)
        {
            foreach (var m in str.Methods)
                type.Methods.Add(ConvertMethod(m));
        }
        type.MethodCount = (ushort)type.Methods.Count;
    }

    private TypeRecord NewType(TypeKind kind, string name, bool isAbstract, bool isSealed)
    {
        var t = new TypeRecord
        {
            Kind = kind,
            NameIndex = Intern(name),
            NamespaceIndex = 0,
            Access = MemberAccess.Public,
            Flags = (isAbstract ? TypeFlags.Abstract : TypeFlags.None)
                  | (isSealed ? TypeFlags.Sealed : TypeFlags.None),
            BaseTypeIndex = -1,
        };
        _mod.Types.Add(t);
        return t;
    }

    private int FindTypeIndex(string name)
    {
        for (int i = 0; i < _mod.Types.Count; i++)
        {
            if (_mod.Resolve(_mod.Types[i].NameIndex) == name)
                return i;
        }
        return -1;
    }

    // ── Methods ───────────────────────────────────────────────────────

    private MethodRecord ConvertConstructor(ConstructorNode ctor)
    {
        var method = new MethodRecord
        {
            NameIndex = Intern(".ctor"),
            SignatureIndex = Intern("void"),
            Access = MemberAccess.Public,
            Flags = MethodFlags.None,
        };
        AddAttributes(method, ctor.Attributes);
        AddParameters(method, ctor.Parameters);
        ConvertBody(method, ctor.Body);
        return method;
    }

    private MethodRecord ConvertMethod(MethodNode m)
    {
        var method = new MethodRecord
        {
            NameIndex = Intern(m.Name),
            SignatureIndex = Intern(m.ReturnType.Name),
            Access = MapAccess(m.Access),
            Flags = (m.IsStatic ? MethodFlags.Static : MethodFlags.None)
                  | (m.IsVirtual ? MethodFlags.Virtual : MethodFlags.None)
                  | (m.IsOverride ? MethodFlags.Override : MethodFlags.None)
                  | (m.IsAbstract ? MethodFlags.Abstract : MethodFlags.None),
        };
        AddAttributes(method, m.Attributes);
        AddParameters(method, m.Parameters);
        ConvertBody(method, m.Body);
        return method;
    }

    private void AddAttributes(TypeRecord type, IEnumerable<AttributeNode> attributes)
    {
        foreach (var attr in attributes)
        {
            type.Attributes.Add(new AttributeRecord(Intern(attr.Name), attr.Arguments.Select(Intern).ToList()));
        }
    }

    private void AddAttributes(MethodRecord method, IEnumerable<AttributeNode> attributes)
    {
        foreach (var attr in attributes)
        {
            method.Attributes.Add(new AttributeRecord(Intern(attr.Name), attr.Arguments.Select(Intern).ToList()));
        }
    }

    private void AddParameters(MethodRecord method, IEnumerable<ParameterNode> parameters)
    {
        foreach (var p in parameters)
            method.Params.Add(new ParameterRecord(Intern(p.Name), Intern(p.ParameterType.Name)));
        method.ParamCount = (ushort)method.Params.Count;
    }

    // ── Method bodies ─────────────────────────────────────────────────

    private void ConvertBody(MethodRecord method, BlockStatement body)
    {
        ResetEmitter();

        // ObjectIL declares locals at method top; hoist them from the AST.
        var seen = new HashSet<string>();
        CollectLocals(body, method, seen);
        method.LocalCount = (ushort)method.Locals.Count;

        EmitStatements(body.Statements);

        ResolveFixups();
        method.InstrCount = _instrCount;
        method.RawInstructionData = _code.ToArray();
    }

    private void CollectLocals(BlockStatement block, MethodRecord method, HashSet<string> seen)
    {
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case LocalDeclarationStatement local:
                    if (seen.Add(local.Name))
                        method.Locals.Add(new LocalRecord(Intern(local.Name), Intern(local.LocalType.Name)));
                    break;
                case IfStatement ifStmt:
                    CollectLocals(ifStmt.Then, method, seen);
                    if (ifStmt.Else != null)
                        CollectLocals(ifStmt.Else, method, seen);
                    break;
                case WhileStatement whileStmt:
                    CollectLocals(whileStmt.Body, method, seen);
                    break;
                case SwitchStatement sw:
                    foreach (var c in sw.Cases)
                        CollectLocals(c.Body, method, seen);
                    break;
            }
        }
    }

    private void EmitStatements(IReadOnlyList<Statement> statements)
    {
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case LocalDeclarationStatement:
                    break; // hoisted to the local table
                case IfStatement ifStmt:
                    EmitIf(ifStmt);
                    break;
                case WhileStatement whileStmt:
                    EmitWhile(whileStmt);
                    break;
                case SwitchStatement:
                    throw new NotSupportedException("switch statements have no ORBT wire encoding");
                case InstructionStatement inst:
                    EmitInstruction(inst.Instruction);
                    break;
            }
        }
    }

    /// <summary>
    /// Lowers <c>if (stack) {A} else {B}</c> to
    /// <c>brfalse else; A; br end; else: B; end:</c> — identical to the ObjectIL parser.
    /// </summary>
    private void EmitIf(IfStatement ifStmt)
    {
        int elseLabel = FreshLabel();
        int endLabel = FreshLabel();

        EmitBranch((byte)Opcode.Brfalse, elseLabel);
        EmitStatements(ifStmt.Then.Statements);

        if (ifStmt.Else != null)
        {
            EmitBranch((byte)Opcode.Br, endLabel);
            PlaceLabel(elseLabel);
            EmitStatements(ifStmt.Else.Statements);
        }
        else
        {
            PlaceLabel(elseLabel);
        }
        PlaceLabel(endLabel);
    }

    /// <summary>
    /// Lowers <c>while (stack) {B}</c> to
    /// <c>loop: dup; brfalse end; B; br loop; end:</c> — identical to the ObjectIL parser.
    /// </summary>
    private void EmitWhile(WhileStatement whileStmt)
    {
        int loopLabel = FreshLabel();
        int endLabel = FreshLabel();

        PlaceLabel(loopLabel);
        EmitOpcode(Opcode.Dup); // keep the stack value for the loop body
        EmitBranch((byte)Opcode.Brfalse, endLabel);

        EmitStatements(whileStmt.Body.Statements);

        EmitBranch((byte)Opcode.Br, loopLabel);
        PlaceLabel(endLabel);
    }

    private void EmitInstruction(AST.Instruction instruction)
    {
        switch (instruction)
        {
            case SimpleInstruction simple:
                EmitSimple(simple);
                break;
            case CallInstruction call:
                EmitCall(call);
                break;
            case NewObjInstruction newObj:
                EmitNewObj(newObj);
                break;
            default:
                throw new NotSupportedException($"Unsupported AST instruction {instruction.GetType().Name}");
        }
    }

    private void EmitSimple(SimpleInstruction simple)
    {
        var wire = ToWire(simple.OpCode);
        EmitOpcode(wire);

        // call / callvirt / callnative — name + parameter count (U16 + U16).
        // In simple form the parameter count is unknown; emit 0.
        if (wire is Opcode.Call or Opcode.Callvirt or Opcode.NativeCall)
        {
            EmitU16(simple.Operand != null ? Intern(simple.Operand) : (ushort)0);
            EmitU16(0);
            return;
        }

        // Branches — numeric PC-relative offset.
        if (wire is Opcode.Br or Opcode.Brtrue or Opcode.Brfalse)
        {
            int offset = simple.Operand != null
                ? int.Parse(simple.Operand, CultureInfo.InvariantCulture)
                : 0;
            EmitI32(offset);
            return;
        }

        switch (wire)
        {
            case Opcode.LdcI4:
            case Opcode.Ldc:
                EmitI32(ParseI4(simple.Operand));
                break;
            case Opcode.LdcI8:
                EmitI64(ParseI8(simple.Operand));
                break;
            case Opcode.LdcR4:
                EmitR4(ParseR4(simple.Operand));
                break;
            case Opcode.LdcR8:
                EmitR8(ParseR8(simple.Operand));
                break;

            // U16 pool index operands (numeric text stays a raw index,
            // anything else is interned — mirrors the ObjectIL parser).
            case Opcode.Ldstr:
            case Opcode.Newobj:
            case Opcode.Newarr:
            case Opcode.Ldarg:
            case Opcode.Starg:
            case Opcode.Ldloc:
            case Opcode.Stloc:
            case Opcode.Ldfld:
            case Opcode.Stfld:
            case Opcode.Ldsfld:
            case Opcode.Stsfld:
            case Opcode.Conv:
            case Opcode.Castclass:
            case Opcode.Isinst:
                EmitU16(InternOrIndex(simple.Operand));
                break;

            case Opcode.If:
            case Opcode.While:
                EmitU8((byte)ConditionKind.Stack);
                break;

            case Opcode.Try:
                throw new NotSupportedException("try/catch has no SimpleInstruction wire encoding");

            // No operand.
            default:
                break;
        }
    }

    private void EmitCall(CallInstruction call)
    {
        EmitOpcode(call.IsVirtual ? Opcode.Callvirt : Opcode.Call);
        EmitU16(Intern(MethodDisplayName(call.Target)));
        EmitU16((ushort)call.Arguments.Count);
    }

    private void EmitNewObj(NewObjInstruction newObj)
    {
        EmitOpcode(Opcode.Newobj);
        EmitU16(Intern(newObj.Type.Name));
    }

    private static string MethodDisplayName(MethodReference target)
    {
        if (target.DeclaringType != null && !string.IsNullOrEmpty(target.DeclaringType.Name))
            return target.DeclaringType.Name + "." + target.Name;
        return target.Name;
    }

    // ── Opcode mapping ────────────────────────────────────────────────

    /// <summary>
    /// Maps an AST opcode to its ORBT wire opcode, or <c>false</c> when the
    /// AST opcode has no wire encoding (e.g. <c>Shl</c>, <c>Beq</c>, <c>Box</c>,
    /// <c>For</c>, <c>Switch</c>).
    /// </summary>
    public static bool TryGetWireOpcode(OpCode ast, out Opcode wire)
    {
        switch (ast)
        {
            case OpCode.Nop: wire = Opcode.Nop; return true;
            case OpCode.Ldc: wire = Opcode.Ldc; return true;
            case OpCode.Ldstr: wire = Opcode.Ldstr; return true;
            case OpCode.Ldarg: wire = Opcode.Ldarg; return true;
            case OpCode.Starg: wire = Opcode.Starg; return true;
            case OpCode.Ldloc: wire = Opcode.Ldloc; return true;
            case OpCode.Stloc: wire = Opcode.Stloc; return true;
            case OpCode.Add: wire = Opcode.Add; return true;
            case OpCode.Sub: wire = Opcode.Sub; return true;
            case OpCode.Mul: wire = Opcode.Mul; return true;
            case OpCode.Div: wire = Opcode.Div; return true;
            case OpCode.Rem: wire = Opcode.Rem; return true;
            case OpCode.Neg: wire = Opcode.Neg; return true;
            case OpCode.Ceq: wire = Opcode.Ceq; return true;
            case OpCode.Cne: wire = Opcode.Cne; return true;
            case OpCode.Ldfld: wire = Opcode.Ldfld; return true;
            case OpCode.Ldsfld: wire = Opcode.Ldsfld; return true;
            case OpCode.Stsfld: wire = Opcode.Stsfld; return true;
            case OpCode.Newobj: wire = Opcode.Newobj; return true;
            case OpCode.Newarr: wire = Opcode.Newarr; return true;
            case OpCode.Ldelem: wire = Opcode.Ldelem; return true;
            case OpCode.Stelem: wire = Opcode.Stelem; return true;
            case OpCode.Call: wire = Opcode.Call; return true;
            case OpCode.Callvirt: wire = Opcode.Callvirt; return true;
            case OpCode.NativeCall: wire = Opcode.NativeCall; return true;
            case OpCode.Ret: wire = Opcode.Ret; return true;
            case OpCode.If: wire = Opcode.If; return true;
            case OpCode.While: wire = Opcode.While; return true;
            case OpCode.Break: wire = Opcode.Break; return true;
            case OpCode.Continue: wire = Opcode.Continue; return true;
            case OpCode.Try: wire = Opcode.Try; return true;
            case OpCode.Throw: wire = Opcode.Throw; return true;
            case OpCode.Conv: wire = Opcode.Conv; return true;
            case OpCode.Castclass: wire = Opcode.Castclass; return true;
            case OpCode.Isinst: wire = Opcode.Isinst; return true;
            case OpCode.Dup: wire = Opcode.Dup; return true;
            case OpCode.Pop: wire = Opcode.Pop; return true;
            case OpCode.Ldnull: wire = Opcode.Ldnull; return true;
            case OpCode.Not: wire = Opcode.Not; return true;
            case OpCode.Cgt: wire = Opcode.Cgt; return true;
            case OpCode.Cge: wire = Opcode.Cge; return true;
            case OpCode.Clt: wire = Opcode.Clt; return true;
            case OpCode.Cle: wire = Opcode.Cle; return true;
            case OpCode.Stfld: wire = Opcode.Stfld; return true;
            case OpCode.LdcI4: wire = Opcode.LdcI4; return true;
            case OpCode.LdcI8: wire = Opcode.LdcI8; return true;
            case OpCode.LdcR4: wire = Opcode.LdcR4; return true;
            case OpCode.LdcR8: wire = Opcode.LdcR8; return true;
            case OpCode.And: wire = Opcode.And; return true;
            case OpCode.Xor: wire = Opcode.Xor; return true;
            case OpCode.Or: wire = Opcode.Or; return true;
            case OpCode.Br: wire = Opcode.Br; return true;
            case OpCode.Brtrue: wire = Opcode.Brtrue; return true;
            case OpCode.Brfalse: wire = Opcode.Brfalse; return true;
            default: wire = default; return false;
        }
    }

    private static Opcode ToWire(OpCode op)
    {
        if (TryGetWireOpcode(op, out var wire))
            return wire;
        throw new NotSupportedException($"AST opcode {op} has no ORBT wire encoding");
    }

    // ── Emission helpers ──────────────────────────────────────────────

    private void ResetEmitter()
    {
        _code = new List<byte>();
        _instrCount = 0;
        _fixups.Clear();
        _labelPos.Clear();
        _nextLabelId = 0;
    }

    private int FreshLabel() => _nextLabelId++;

    private void PlaceLabel(int id) => _labelPos[id] = _code.Count;

    private void EmitOpcode(Opcode op)
    {
        _code.Add((byte)op);
        _instrCount++;
    }

    private void EmitBranch(byte opcode, int labelId)
    {
        _code.Add(opcode);
        _fixups.Add((_code.Count, labelId));
        EmitI32(0);
        _instrCount++;
    }

    private void ResolveFixups()
    {
        foreach (var (pos, label) in _fixups)
        {
            if (!_labelPos.TryGetValue(label, out int target))
                throw new InvalidOperationException($"Unresolved label {label}");
            EmitI32At(pos, target - (pos + 4));
        }
        _fixups.Clear();
    }

    private void EmitU8(byte v) => _code.Add(v);

    private void EmitU16(ushort v)
    {
        _code.Add((byte)(v & 0xFF));
        _code.Add((byte)(v >> 8));
    }

    private void EmitI32(int v)
    {
        _code.Add((byte)(v & 0xFF));
        _code.Add((byte)((v >> 8) & 0xFF));
        _code.Add((byte)((v >> 16) & 0xFF));
        _code.Add((byte)((v >> 24) & 0xFF));
    }

    private void EmitI32At(int pos, int v)
    {
        _code[pos + 0] = (byte)(v & 0xFF);
        _code[pos + 1] = (byte)((v >> 8) & 0xFF);
        _code[pos + 2] = (byte)((v >> 16) & 0xFF);
        _code[pos + 3] = (byte)((v >> 24) & 0xFF);
    }

    private void EmitI64(long v)
    {
        for (int i = 0; i < 8; i++)
            _code.Add((byte)((v >> (i * 8)) & 0xFF));
    }

    private void EmitR4(float v)
    {
        int bits = BitConverter.SingleToInt32Bits(v);
        EmitI32(bits);
    }

    private void EmitR8(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        EmitI64(bits);
    }

    // ── Operand parsing ───────────────────────────────────────────────

    private ushort InternOrIndex(string? text)
    {
        if (text != null && ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
            return idx;
        return Intern(text ?? "");
    }

    private static int ParseI4(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long ParseI8(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static float ParseR4(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static double ParseR8(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d;

    // ── String pool / version helpers ─────────────────────────────────

    private ushort Intern(string s)
    {
        if (_intern.TryGetValue(s, out var idx))
            return idx;
        idx = _mod.StringPool.Add(s);
        _intern[s] = idx;
        return idx;
    }

    private static ModuleVersion ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new ModuleVersion(0, 0, 0);
        var parts = version.Split('.');
        return new ModuleVersion(
            ParsePart(parts, 0),
            ParsePart(parts, 1),
            ParsePart(parts, 2));
    }

    private static ushort ParsePart(string[] parts, int index) =>
        index < parts.Length && ushort.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : (ushort)0;

    private static MemberAccess MapAccess(AccessModifier access) => access switch
    {
        AccessModifier.Private => MemberAccess.Private,
        AccessModifier.Protected => MemberAccess.Protected,
        AccessModifier.Internal => MemberAccess.Internal,
        _ => MemberAccess.Public,
    };
}
