namespace ObjektRT.Core.Model;

/// <summary>A single decoded instruction with its opcode and typed operand.</summary>
public record Instruction(
    Opcode Opcode,
    Operand Operand,
    uint PcOffset
);
