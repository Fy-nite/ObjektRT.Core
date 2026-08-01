namespace ObjektRT.Core.AST;

public enum OpCode
{
    Nop,

    // Load instructions
    Ldarg,
    Ldloc,
    Ldfld,
    Ldsfld,
    Ldelem,
    Ldlen,
    Ldnull,
    LdcI4,
    LdcI8,
    LdcR4,
    LdcR8,
    Ldc,
    Ldstr,
    
    // Store instructions
    Starg,
    Stloc,
    Stfld,
    Stsfld,
    Stelem,
    
    // Arithmetic
    Add,
    Sub,
    Mul,
    Div,
    Rem,
    Neg,
    And,
    Or,
    Xor,
    Not,
    Shl,
    Shr,
    
    // Comparison
    Ceq,
    Cne,
    Cgt,
    CgtUn,
    CgeUn,
    Cge,
    Clt,
    Cle,
    
    // Control flow
    Br,
    Brtrue,
    Brfalse,
    Beq,
    Bne,
    Bgt,
    Blt,
    Ret,
    
    // Calls
    Call,
    Callvirt,
    Calli,
    /// <summary>
    ///  this is some bs from the runtime, it's not required since we reuse call.
    /// </summary>
    NativeCall,
    Newobj,
    
    // Object operations
    Newarr,
    Castclass,
    Isinst,
    Box,
    Unbox,
    
    // Stack manipulation
    Dup,
    Pop,
    
    // Conversions
    Conv,
    ConvI4,
    ConvI8,
    ConvR4,
    ConvR8,
    ConvU4,
    ConvU8,
    
    // Structured control flow (high-level)
    If,
    While,
    For,
    Switch,
    Try,
    Break,
    Continue,
    Throw
}

public static class OpCodeConverter
{
    private static readonly Dictionary<string, OpCode> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ldarg"] = OpCode.Ldarg,
        ["ldloc"] = OpCode.Ldloc,
        ["ldfld"] = OpCode.Ldfld,
        ["ldsfld"] = OpCode.Ldsfld,
        ["ldelem"] = OpCode.Ldelem,
        ["ldlen"] = OpCode.Ldlen,
        ["ldnull"] = OpCode.Ldnull,
        ["ldc.i4"] = OpCode.LdcI4,
        ["ldc.i8"] = OpCode.LdcI8,
        ["ldc.r4"] = OpCode.LdcR4,
        ["ldc.r8"] = OpCode.LdcR8,
        ["ldstr"] = OpCode.Ldstr,
        ["starg"] = OpCode.Starg,
        ["stloc"] = OpCode.Stloc,
        ["stfld"] = OpCode.Stfld,
        ["stsfld"] = OpCode.Stsfld,
        ["stelem"] = OpCode.Stelem,
        ["add"] = OpCode.Add,
        ["sub"] = OpCode.Sub,
        ["mul"] = OpCode.Mul,
        ["div"] = OpCode.Div,
        ["rem"] = OpCode.Rem,
        ["neg"] = OpCode.Neg,
        ["and"] = OpCode.And,
        ["or"] = OpCode.Or,
        ["xor"] = OpCode.Xor,
        ["not"] = OpCode.Not,
        ["shl"] = OpCode.Shl,
        ["shr"] = OpCode.Shr,
        ["ceq"] = OpCode.Ceq,
        ["cne"] = OpCode.Cne,
        ["cgt"] = OpCode.Cgt,
        ["cgt.un"] = OpCode.CgtUn,
        ["cge.un"] = OpCode.CgeUn,
        ["clt"] = OpCode.Clt,
        ["br"] = OpCode.Br,
        ["brtrue"] = OpCode.Brtrue,
        ["brfalse"] = OpCode.Brfalse,
        ["beq"] = OpCode.Beq,
        ["bne"] = OpCode.Bne,
        ["bgt"] = OpCode.Bgt,
        ["blt"] = OpCode.Blt,
        ["ret"] = OpCode.Ret,
        ["call"] = OpCode.Call,
        ["callvirt"] = OpCode.Callvirt,
        ["callnative"] = OpCode.NativeCall,
        ["calli"] = OpCode.Calli,
        ["newobj"] = OpCode.Newobj,
        ["newarr"] = OpCode.Newarr,
        ["castclass"] = OpCode.Castclass,
        ["isinst"] = OpCode.Isinst,
        ["box"] = OpCode.Box,
        ["unbox"] = OpCode.Unbox,
        ["dup"] = OpCode.Dup,
        ["pop"] = OpCode.Pop,
        ["conv.i4"] = OpCode.ConvI4,
        ["conv.i8"] = OpCode.ConvI8,
        ["conv.r4"] = OpCode.ConvR4,
        ["conv.r8"] = OpCode.ConvR8,
        ["conv.u4"] = OpCode.ConvU4,
        ["conv.u8"] = OpCode.ConvU8,
        ["if"] = OpCode.If,
        ["while"] = OpCode.While,
        ["for"] = OpCode.For,
        ["switch"] = OpCode.Switch,
        ["try"] = OpCode.Try,
        ["break"] = OpCode.Break,
        ["continue"] = OpCode.Continue,
        ["throw"] = OpCode.Throw,
    };

    public static bool TryParse(string? s, out OpCode result) => _map.TryGetValue(s ?? "", out result);

    public static OpCode Parse(string s) => TryParse(s, out var r) ? r : throw new ArgumentException($"Unknown opcode '{s}'");

    private static readonly Dictionary<OpCode, string> _reverseMap = _map.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string ToString(OpCode opcode) => _reverseMap.TryGetValue(opcode, out var s) ? s : opcode.ToString().ToLowerInvariant();
}

public enum ArithmeticOp
{
    Add,
    Sub,
    Mul,
    Div,
    Rem,
    Neg,
    And,
    Or,
    Xor,
    Not,
    Shl,
    Shr
}

public enum ComparisonOp
{
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual
}
