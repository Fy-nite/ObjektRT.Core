using ObjektRT.Core.Model;

namespace ObjektRT.Core.Serialization;

/// <summary>Reads ORBT binary module format into an ORBTModule.</summary>
public class ORBTReader
{
    private readonly BinaryStream _stream;
    private byte _formatVersion;

    public ORBTReader(BinaryStream stream)
    {
        _stream = stream;
    }

    public ORBTModule ReadModule()
    {
        var mod = new ORBTModule();
        ReadHeader(mod);
        ReadStringPool(mod);
        ReadTypeTable(mod);
        ReadImportTable(mod);
        ReadExportTable(mod);
        ReadMetadataBlock(mod);
        ReadMethodBodies(mod);
        return mod;
    }

    private void ReadHeader(ORBTModule mod)
    {
        // Magic: 4 literal bytes "ORBT" (4F 52 42 54), checked as raw bytes.
        // Reading them as a little-endian u32 would yield 0x5442524F ("TBRO").
        var magic = _stream.ReadBytes(4);
        if (magic[0] != (byte)'O' || magic[1] != (byte)'R'
            || magic[2] != (byte)'B' || magic[3] != (byte)'T')
            throw new InvalidDataException(
                $"Invalid ORBT magic: expected \"ORBT\", got \"{System.Text.Encoding.ASCII.GetString(magic)}\"");

        mod.FormatVersion = _stream.ReadU8();
        if (mod.FormatVersion != 0x01 && mod.FormatVersion != 0x02)
            throw new InvalidDataException($"Unsupported ORBT version: {mod.FormatVersion}");
        _formatVersion = mod.FormatVersion;

        mod.ModuleName = _stream.ReadString();

        ushort maj = _stream.ReadU16();
        ushort min = _stream.ReadU16();
        ushort pat = _stream.ReadU16();
        mod.Version = new ModuleVersion(maj, min, pat);
    }

    private void ReadStringPool(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
            mod.StringPool.Strings.Add(_stream.ReadString());
    }

    private void ReadTypeTable(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
        {
            var type = new TypeRecord
            {
                Kind = (TypeKind)_stream.ReadU8(),
                NameIndex = _stream.ReadU16(),
                NamespaceIndex = _stream.ReadU16(),
                Access = (MemberAccess)_stream.ReadU8(),
                Flags = (TypeFlags)_stream.ReadU8(),
                BaseTypeIndex = _stream.ReadI32(),
            };

            // Interfaces
            type.InterfaceCount = _stream.ReadU16();
            for (ushort j = 0; j < type.InterfaceCount; j++)
                type.InterfaceIndices.Add(_stream.ReadU16());

            // Fields
            type.FieldCount = _stream.ReadU16();
            for (ushort j = 0; j < type.FieldCount; j++)
                type.Fields.Add(ReadFieldRecord());

            // Methods
            type.MethodCount = _stream.ReadU16();
            for (ushort j = 0; j < type.MethodCount; j++)
                type.Methods.Add(ReadMethodRecord(mod.StringPool));

            // Type-level attributes (v1 extension)
            type.Attributes = ReadAttributeList(mod.StringPool);

            mod.Types.Add(type);
        }
    }

    private FieldRecord ReadFieldRecord()
    {
        var name = _stream.ReadU16();
        var type = _stream.ReadU16();
        // v0x02 modules carry a per-field static flag byte AFTER name+type.
        bool isStatic = _formatVersion >= 0x02 && _stream.ReadU8() != 0;
        return new FieldRecord(name, type, isStatic);
    }

    private ParameterRecord ReadParamRecord()
    {
        return new ParameterRecord(_stream.ReadU16(), _stream.ReadU16());
    }

    private LocalRecord ReadLocalRecord()
    {
        return new LocalRecord(_stream.ReadU16(), _stream.ReadU16());
    }

    private LabelRecord ReadLabelRecord()
    {
        return new LabelRecord(_stream.ReadU16(), _stream.ReadU32());
    }

    private MethodRecord ReadMethodRecord(StringPool pool)
    {
        var method = new MethodRecord
        {
            NameIndex = _stream.ReadU16(),
            SignatureIndex = _stream.ReadU16(),
            Access = (MemberAccess)_stream.ReadU8(),
            Flags = (MethodFlags)_stream.ReadU8(),
        };

        // Parameters
        method.ParamCount = _stream.ReadU16();
        for (ushort j = 0; j < method.ParamCount; j++)
            method.Params.Add(ReadParamRecord());

        // Locals
        method.LocalCount = _stream.ReadU16();
        for (ushort j = 0; j < method.LocalCount; j++)
            method.Locals.Add(ReadLocalRecord());

        // Labels
        method.LabelCount = _stream.ReadU16();
        for (ushort j = 0; j < method.LabelCount; j++)
            method.Labels.Add(ReadLabelRecord());

        // Method-level attributes (v1 extension)
        method.Attributes = ReadAttributeList(pool);

        // Instructions
        method.InstrCount = _stream.ReadU32();
        int dataStart = _stream.Position;

        method.Instructions.Capacity = (int)method.InstrCount;
        for (uint j = 0; j < method.InstrCount; j++)
        {
            uint pc = (uint)(_stream.Position - dataStart);
            method.Instructions.Add(ReadInstruction(_stream, pool, pc));
        }

        int dataEnd = _stream.Position;
        _stream.Seek(dataStart);
        method.RawInstructionData = _stream.ReadBytes(dataEnd - dataStart);

        return method;
    }

    /// <summary>
    /// Decodes raw method bytecode (as emitted by the ObjectIL parser or
    /// <see cref="ObjektRT.Core.Conversion.AstToModelConverter"/>) into typed
    /// instructions. Useful when a module was built in memory and only carries
    /// <see cref="MethodRecord.RawInstructionData"/>.
    /// </summary>
    public static List<Instruction> DecodeRawBytecode(byte[] raw, StringPool pool)
    {
        var stream = new BinaryStream(raw);
        var list = new List<Instruction>();
        while (!stream.Eof)
        {
            uint pc = (uint)stream.Position;
            var opcode = ReadOpcode(stream);
            var operand = ReadOperand(opcode, stream, pool);
            list.Add(new Instruction(opcode, operand, pc));
        }
        return list;
    }

    private static Instruction ReadInstruction(BinaryStream stream, StringPool pool, uint pc)
    {
        var opcode = ReadOpcode(stream);
        var operand = ReadOperand(opcode, stream, pool);
        return new Instruction(opcode, operand, pc);
    }

    private static Opcode ReadOpcode(BinaryStream stream)
    {
        int table = 0;
        while (true)
        {
            byte b = stream.ReadU8();
            if (b == 0xFF)
            {
                table++;
                if (table > 255)
                    throw new InvalidDataException("Opcode table overflow (max 256 tables)");
                continue;
            }
            return (Opcode)(table * 256 + b);
        }
    }

    private static Operand ReadOperand(Opcode opcode, BinaryStream stream, StringPool pool)
    {
        return opcode switch
        {
            // No operand
            Opcode.Nop or Opcode.Add or Opcode.Sub or Opcode.Mul
                or Opcode.Div or Opcode.Rem or Opcode.Neg
                or Opcode.Ceq or Opcode.Cne or Opcode.Cgt or Opcode.Cge
                or Opcode.Clt or Opcode.Cle or Opcode.And or Opcode.Xor or Opcode.Or
                or Opcode.Not or Opcode.Dup or Opcode.Pop or Opcode.Ldnull
                or Opcode.Ret or Opcode.Break or Opcode.Continue or Opcode.Throw
                or Opcode.Ldelem or Opcode.Stelem
                => new OperandNone(),

            // Immediate values
            Opcode.LdcI4 or Opcode.Ldc => new OperandI4(stream.ReadI32()),
            Opcode.LdcI8 => new OperandI8(stream.ReadI64()),
            Opcode.LdcR4 => new OperandR4(stream.ReadR4()),
            Opcode.LdcR8 => new OperandR8(stream.ReadR8()),

            // String constant
            Opcode.Ldstr => new OperandString(stream.ReadU16()),

            // Index-based (args, locals)
            Opcode.Ldarg or Opcode.Starg or Opcode.Ldloc or Opcode.Stloc
                => new OperandIndex(stream.ReadU16()),

            // Field reference (string pool index)
            Opcode.Ldfld or Opcode.Stfld or Opcode.Ldsfld or Opcode.Stsfld
                => new OperandFieldRef(stream.ReadU16()),

            // Method reference (name string index + param count, runtime-resolved)
            Opcode.Call or Opcode.Callvirt or Opcode.NativeCall
                => new OperandNativeCall(stream.ReadU16(), stream.ReadU16()),

            // Object creation
            Opcode.Newobj or Opcode.Newarr
                => new OperandString(stream.ReadU16()),

            // Type reference
            Opcode.Conv or Opcode.Castclass or Opcode.Isinst
                => new OperandTypeRef(stream.ReadU16()),

            // Branch
            Opcode.Br or Opcode.Brtrue or Opcode.Brfalse
                => new OperandBranch(stream.ReadI32()),

            // Structured control flow
            Opcode.If or Opcode.While => ReadConditionOperand(stream),
            Opcode.Try => ReadExceptionHandler(stream),

            _ => new OperandNone(),
        };
    }

    private static ConditionOperand ReadConditionOperand(BinaryStream stream)
    {
        var kind = (ConditionKind)stream.ReadU8();
        return kind switch
        {
            ConditionKind.Stack => new ConditionOperand(kind),
            ConditionKind.Binary => new ConditionOperand(kind, stream.ReadU8()),
            ConditionKind.Expression or ConditionKind.Block
                => new ConditionOperand(kind, 0, stream.ReadBytes((int)stream.ReadU32())),
            _ => throw new InvalidDataException($"Unknown condition kind: {kind}"),
        };
    }

    private static ExceptionHandlerOperand ReadExceptionHandler(BinaryStream stream)
    {
        uint tryLen = stream.ReadU32();
        var tryBlock = stream.ReadBytes((int)tryLen);

        ushort catchCount = stream.ReadU16();
        var catches = new CatchRecord[catchCount];
        for (int i = 0; i < catchCount; i++)
        {
            ushort typeIdx = stream.ReadU16();
            uint bodyLen = stream.ReadU32();
            catches[i] = new CatchRecord(typeIdx, stream.ReadBytes((int)bodyLen));
        }

        bool hasFinally = stream.ReadU8() != 0;
        byte[]? finallyBlock = null;
        if (hasFinally)
        {
            uint finallyLen = stream.ReadU32();
            finallyBlock = stream.ReadBytes((int)finallyLen);
        }

        return new ExceptionHandlerOperand(tryBlock, catches, hasFinally, finallyBlock);
    }

    private void ReadImportTable(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
        {
            mod.Imports.Add(new ImportEntry(
                _stream.ReadU16(),
                _stream.ReadU16(),
                (ImportKind)_stream.ReadU8(),
                _stream.ReadU8()
            ));
        }
    }

    private void ReadExportTable(ORBTModule mod)
    {
        ushort count = _stream.ReadU16();
        for (ushort i = 0; i < count; i++)
        {
            mod.Exports.Add(new ExportEntry(
                _stream.ReadU16(),
                (ImportKind)_stream.ReadU8(),
                _stream.ReadU32(),
                _stream.ReadU16()
            ));
        }
    }

    private void ReadMetadataBlock(ORBTModule mod)
    {
        ushort blockLength = _stream.ReadU16();
        if (blockLength == 0)
        {
            mod.Metadata = new MetadataBlock();
            return;
        }

        int endPos = _stream.Position + blockLength;

        while (_stream.Position < endPos)
        {
            ushort keyIndex = _stream.ReadU16();
            byte valueKind = _stream.ReadU8();

            string key = keyIndex < mod.StringPool.Count
                ? mod.StringPool.Get(keyIndex)
                : "<unknown>";

            if (valueKind == 0x01)
            {
                string val = _stream.ReadString();
                mod.Metadata.Entries.Add(new MetadataEntry(key, val));

                if (key == "spec")
                    mod.Metadata.SpecVersion = val;
            }
            else if (valueKind == 0x02)
            {
                ushort entryCount = _stream.ReadU16();
                var list = new List<string>((int)entryCount);
                for (int i = 0; i < entryCount; i++)
                    list.Add(_stream.ReadString());

                mod.Metadata.Entries.Add(new MetadataEntry(key, list));

                if (key == "require")
                    mod.Metadata.Require = list;
                else if (key == "optional")
                    mod.Metadata.Optional = list;
            }
            else
            {
                throw new InvalidDataException($"Unknown metadata value kind: {valueKind}");
            }
        }
    }

    private List<AttributeRecord> ReadAttributeList(StringPool pool)
    {
        ushort count = _stream.ReadU16();
        var attrs = new List<AttributeRecord>(count);
        for (ushort i = 0; i < count; i++)
        {
            ushort nameIdx = _stream.ReadU16();
            ushort argCount = _stream.ReadU16();
            var args = new List<ushort>(argCount);
            for (ushort j = 0; j < argCount; j++)
                args.Add(_stream.ReadU16());
            attrs.Add(new AttributeRecord(nameIdx, args));
        }
        return attrs;
    }

    private void ReadMethodBodies(ORBTModule mod)
    {
        // Method bodies are read inline as part of type/method records.
        // Nothing more to do here.
    }
}

/// <summary>High-level convenience for reading ORBT files.</summary>
public static class OrbtFileReader
{
    public static ORBTModule ReadFile(string path)
    {
        var stream = new BinaryStream(path);
        var reader = new ORBTReader(stream);
        return reader.ReadModule();
    }

    /// <summary>Read an ORBT module from an in-memory byte array.</summary>
    public static ORBTModule ReadBytes(byte[] data)
    {
        var stream = new BinaryStream(data);
        var reader = new ORBTReader(stream);
        return reader.ReadModule();
    }
}
