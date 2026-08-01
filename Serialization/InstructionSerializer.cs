namespace ObjectIR.Core.Serialization;

using ObjectIR.Core.AST;
using ObjectIR.Core.Ast;
using OpCode = ObjectIR.Core.Ast.OpCode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serializes and deserializes AST instructions to/from JSON
/// </summary>
public sealed class InstructionSerializer
{
    /// <summary>
    /// Serializes a list of statements (instructions) to JSON array
    /// </summary>
    public static JsonElement SerializeInstructions(List<Statement> statements)
    {
        var data = new List<InstructionData>();
        foreach (var stmt in statements)
        {
            try
            {
                data.Add(CreateStatementData(stmt));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize statement {stmt?.GetType().Name}", ex);
            }
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var array = JsonSerializer.Serialize(data, options);
        return JsonDocument.Parse(array).RootElement;
    }

    private static InstructionData CreateStatementData(Statement statement)
    {
        return statement switch
        {
            InstructionStatement ism => CreateInstructionData(ism.Instruction),
            LocalDeclarationStatement lds => new InstructionData
            {
                OpCode = "local",
                Operand = new { name = lds.Name, type = lds.LocalType.Name }
            },
            IfStatement ifs => new InstructionData
            {
                OpCode = "if",
                Operand = new
                {
                    condition = ifs.Condition,
                    thenBlock = ifs.Then.Statements.Select(CreateStatementData).ToList(),
                    elseBlock = ifs.Else?.Statements.Select(CreateStatementData).ToList()
                }
            },
            WhileStatement ws => new InstructionData
            {
                OpCode = "while",
                Operand = new
                {
                    condition = ws.Condition,
                    body = ws.Body.Statements.Select(CreateStatementData).ToList()
                }
            },
            SwitchStatement sw => new InstructionData
            {
                OpCode = "switch",
                Operand = new
                {
                    expression = sw.Expression,
                    cases = sw.Cases.Select(c => new
                    {
                        value = c.Value,
                        body = c.Body.Statements.Select(CreateStatementData).ToList()
                    }).ToList()
                }
            },
            _ => throw new NotSupportedException($"Statement type {statement.GetType().Name} not supported")
        };
    }

    private static InstructionData CreateInstructionData(Instruction instruction)
    {
        return instruction switch
        {
            SimpleInstruction si => new InstructionData
            {
                OpCode = OpCodeConverter.ToString(si.OpCode),
                Operand = si.Operand
            },
            CallInstruction ci => new InstructionData
            {
                OpCode = ci.IsVirtual ? "callvirt" : "call",
                Operand = new
                {
                    target = new 
                    { 
                        declaringType = ci.Target.DeclaringType.Name, 
                        name = ci.Target.Name,
                        returnType = ci.Target.ReturnType.Name,
                        parameterTypes = ci.Target.ParameterTypes.Select(p => p.Name).ToArray()
                    },
                    arguments = ci.Arguments.Select(a => a.Name).ToArray()
                }
            },
            NewObjInstruction noi => new InstructionData
            {
                OpCode = "newobj",
                Operand = new 
                { 
                    type = noi.Type.Name,
                    constructor = noi.Constructor != null ? new 
                    {
                        declaringType = noi.Constructor.DeclaringType.Name,
                        name = noi.Constructor.Name,
                        returnType = noi.Constructor.ReturnType.Name,
                        parameterTypes = noi.Constructor.ParameterTypes.Select(p => p.Name).ToArray()
                    } : null
                }
            },
            _ => throw new NotSupportedException($"Instruction type {instruction.GetType().Name} not supported")
        };
    }

    /// <summary>
    /// Deserializes a JSON array to statement list
    /// </summary>
    public static List<Statement> DeserializeInstructions(JsonElement jsonArray)
    {
        var statements = new List<Statement>();

        if (jsonArray.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Expected JSON array for instructions", nameof(jsonArray));
        }

        foreach (var element in jsonArray.EnumerateArray())
        {
            statements.Add(DeserializeStatement(element));
        }

        return statements;
    }

    public static Statement DeserializeStatement(JsonElement element)
    {
        var opCode = element.GetProperty("opCode").GetString() ?? throw new InvalidOperationException("opCode missing");
        var operand = element.TryGetProperty("operand", out var op) ? op : default(JsonElement);

        return opCode switch
        {
            "local" => new LocalDeclarationStatement(operand.GetProperty("name").GetString()!, new TypeRef(operand.GetProperty("type").GetString()!)),
            "if" => DeserializeIf(operand),
            "while" => DeserializeWhile(operand),
            "switch" => DeserializeSwitch(operand),
            _ => new InstructionStatement(DeserializeInstruction(opCode, operand))
        };
    }

    private static SwitchStatement DeserializeSwitch(JsonElement operand)
    {
        var expr = operand.GetProperty("expression").GetString()!;
        var cases = new List<SwitchCase>();
        foreach (var c in operand.GetProperty("cases").EnumerateArray())
        {
            int? val = null;
            if (c.TryGetProperty("value", out var vp) && vp.ValueKind == JsonValueKind.Number)
                val = vp.GetInt32();
            
            var body = new BlockStatement(DeserializeInstructions(c.GetProperty("body")));
            cases.Add(new SwitchCase(val, body));
        }
        return new SwitchStatement(expr, cases);
    }

    private static Instruction DeserializeInstruction(string opCode, JsonElement operand)
    {
        return opCode switch
        {
            "call" or "callvirt" => new CallInstruction(
                DeserializeMethodReference(operand.GetProperty("target")),
                new List<TypeRef>(), // Simplified
                opCode == "callvirt"
            ),
            "newobj" => new NewObjInstruction(
                new TypeRef(operand.GetProperty("type").GetString()!), 
                operand.TryGetProperty("constructor", out var cp) && cp.ValueKind == JsonValueKind.Object ? DeserializeMethodReference(cp) : null, 
                new List<TypeRef>()
            ),
            _ => new SimpleInstruction(OpCodeConverter.Parse(opCode), operand.ValueKind == JsonValueKind.String ? operand.GetString() : null)
        };
    }

    private static MethodReference DeserializeMethodReference(JsonElement element)
    {
        var decl = new TypeRef(element.GetProperty("declaringType").GetString()!);
        var name = element.GetProperty("name").GetString()!;
        var ret = new TypeRef(element.GetProperty("returnType").GetString()!);
        var parameters = new List<TypeRef>();
        if (element.TryGetProperty("parameterTypes", out var pp) && pp.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pp.EnumerateArray())
                parameters.Add(new TypeRef(p.GetString()!));
        }
        return new MethodReference(decl, name, ret, parameters);
    }

    private static IfStatement DeserializeIf(JsonElement operand)
    {
        var cond = operand.GetProperty("condition").GetString()!;
        var then = new BlockStatement(DeserializeInstructions(operand.GetProperty("thenBlock")));
        BlockStatement? els = null;
        if (operand.TryGetProperty("elseBlock", out var ep) && ep.ValueKind == JsonValueKind.Array)
        {
            els = new BlockStatement(DeserializeInstructions(ep));
        }
        return new IfStatement(cond, then, els);
    }

    private static WhileStatement DeserializeWhile(JsonElement operand)
    {
        var cond = operand.GetProperty("condition").GetString()!;
        var body = new BlockStatement(DeserializeInstructions(operand.GetProperty("body")));
        return new WhileStatement(cond, body);
    }

    private sealed class InstructionData
    {
        [JsonPropertyName("opCode")]
        public string OpCode { get; set; } = string.Empty;

        [JsonPropertyName("operand")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Operand { get; set; }
    }
}
