using ObjektRT.Core.AST;
using OpCode = ObjektRT.Core.AST.OpCode;
using System;
using System.Collections.Generic;
using System.Text;

namespace ObjektRT.Core.Parsing;

public static class TextIrParser
{
    public static ModuleNode ParseModule(string text)
    {
        var tokens = Tokenize(text);
        var reader = new TokenReader(tokens);

        if (!reader.TryReadLine(out var moduleLine))
        {
            throw new TextIrParseException("Input is empty.");
        }

        var (moduleName, moduleVersion) = ParseModuleHeader(moduleLine);

        var interfaces = new List<InterfaceNode>();
        var classes = new List<ClassNode>();
        var structs = new List<StructNode>();

        while (!reader.IsAtEnd)
        {
            var attributes = ReadAttributes(reader);

            if (reader.PeekStartsWith("interface "))
            {
                interfaces.Add(ParseInterface(reader, attributes));
                continue;
            }

            if (reader.PeekStartsWith("class "))
            {
                classes.Add(ParseClass(reader, attributes));
                continue;
            }

            if (reader.PeekStartsWith("struct "))
            {
                structs.Add(ParseStruct(reader, attributes));
                continue;
            }

            throw new TextIrParseException($"Unexpected token '{reader.Peek()}' at top-level.");
        }

        return new ModuleNode(moduleName, moduleVersion, interfaces, classes) { Structs = structs  };
    }

    public static CallInstruction ParseCall(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TextIrParseException("Input is empty.");
        }

        var span = text.AsSpan().Trim();
        if (!span.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
        {
            throw new TextIrParseException("Expected input to start with 'call'.");
        }

        span = span.Slice(5).Trim();
        return ParseCallCore(span, isVirtual: false);
    }

    private static (string Name, string? Version) ParseModuleHeader(string line)
    {
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("module", StringComparison.OrdinalIgnoreCase))
        {
            throw new TextIrParseException("Expected module header like 'module Name version X'.");
        }

        var name = parts[1];
        string? version = null;
        var versionIndex = Array.IndexOf(parts, "version");
        if (versionIndex >= 0 && versionIndex + 1 < parts.Length)
        {
                #if NET47
                version = string.Join(" ", parts.Skip(versionIndex + 1));
                #else
                version = string.Join(" ", parts[(versionIndex + 1)..]);
                #endif
        }

        return (name, version);
    }

    private static StructNode ParseStruct(TokenReader reader, List<AttributeNode> attributes)
    {
        var line = reader.ReadLine();
            #if NET47
            var declaration = line.Substring("struct ".Length).Trim();
            #else
            var declaration = line["struct ".Length..].Trim();
            #endif
        if (declaration.Length == 0)
        {
            throw new TextIrParseException("Struct name is missing.");
        }

        var name = declaration.Trim();

        reader.Expect("{");

        var fields = new List<FieldNode>();

        while (!reader.PeekIs("}"))
        {
            var memberLine = reader.ReadLine();
            if (IsField(memberLine))
            {
                fields.Add(ParseField(memberLine));
                continue;
            }
            // support for constructors in structs
            if (IsConstructor(memberLine))
            {
                throw new TextIrParseException("Structs cannot contain constructors.");
            }

            throw new TextIrParseException($"Unexpected struct member '{memberLine}'.");
        }

        reader.Expect("}");
        var node = new StructNode(name);
        node.Fields.AddRange(fields);
        node.Attributes.AddRange(attributes);
        return node;
    }

    private static InterfaceNode ParseInterface(TokenReader reader, List<AttributeNode> attributes)
    {
        var line = reader.ReadLine();
            #if NET47
            var name = line.Substring("interface ".Length).Trim();
            #else
            var name = line["interface ".Length..].Trim();
            #endif
        if (name.Length == 0)
        {
            throw new TextIrParseException("Interface name is missing.");
        }

        reader.Expect("{");
        var methods = new List<MethodSignature>();

        while (!reader.PeekIs("}"))
        {
            var methodLine = reader.ReadLine();
            methods.Add(ParseMethodSignature(methodLine));
        }

        reader.Expect("}");
        var node = new InterfaceNode(name, methods);
        node.Attributes.AddRange(attributes);
        return node;
    }

    private static ClassNode ParseClass(TokenReader reader, List<AttributeNode> attributes)
    {
        var line = reader.ReadLine();
            #if NET47
            var declaration = line.Substring("class ".Length).Trim();
            #else
            var declaration = line["class ".Length..].Trim();
            #endif
        if (declaration.Length == 0)
        {
            throw new TextIrParseException("Class name is missing.");
        }

        var baseTypes = new List<string>();
    #if NET47
        var __tmp_parts = declaration.Split(new[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var parts = __tmp_parts.Select(p => p.Trim()).ToArray();
    #else
        var parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
    #endif
        var name = parts[0].Trim();
        if (parts.Length > 1)
        {
#if NET47
            baseTypes.AddRange(parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
#else
            baseTypes.AddRange(parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
#endif
        }

        reader.Expect("{");

        var fields = new List<FieldNode>();
        var constructors = new List<ConstructorNode>();
        var methods = new List<MethodNode>();

        while (!reader.PeekIs("}"))
        {
            var memberAttributes = ReadAttributes(reader);
            var memberLine = reader.ReadLine();
            if (IsField(memberLine))
            {
                // The wire model has no field attribute slot; ignore (kept for
                // forward-compat with hand-written IR that annotates fields).
                fields.Add(ParseField(memberLine));
                continue;
            }

            if (IsConstructor(memberLine))
            {
                var ctor = ParseConstructor(reader, memberLine);
                ctor.Attributes.AddRange(memberAttributes);
                constructors.Add(ctor);
                continue;
            }

            if (IsMethod(memberLine))
            {
                var method = ParseMethod(reader, memberLine);
                method.Attributes.AddRange(memberAttributes);
                methods.Add(method);
                continue;
            }

            throw new TextIrParseException($"Unexpected class member '{memberLine}'.");
        }

        reader.Expect("}");
        var node = new ClassNode(name, baseTypes, fields, constructors, methods);
        node.Attributes.AddRange(attributes);
        return node;
    }

    /// <summary>
    /// Reads any consecutive <c>@Name(args)</c> annotation lines ahead of the
    /// current position. Consumes nothing when the next line is not an annotation.
    /// </summary>
    private static List<AttributeNode> ReadAttributes(TokenReader reader)
    {
        var attributes = new List<AttributeNode>();
        while (!reader.IsAtEnd && reader.PeekStartsWith("@"))
        {
            attributes.Add(ParseAttributeLine(reader.ReadLine()));
        }
        return attributes;
    }

    /// <summary>Parses a single <c>@Name</c> or <c>@Name(arg1, arg2)</c> line.</summary>
    private static AttributeNode ParseAttributeLine(string line)
    {
        var text = line.Trim();
        if (text.Length < 2 || text[0] != '@')
        {
            throw new TextIrParseException($"Invalid annotation line '{line}'.");
        }

        var openParenIndex = text.IndexOf('(');
        if (openParenIndex < 0)
        {
            var name = text.Substring(1).Trim();
            if (name.Length == 0)
            {
                throw new TextIrParseException("Annotation name is missing.");
            }
            return new AttributeNode(name);
        }

        var name2 = text.Substring(1, openParenIndex - 1).Trim();
        if (name2.Length == 0)
        {
            throw new TextIrParseException("Annotation name is missing.");
        }

        var closeParenIndex = text.LastIndexOf(')');
        if (closeParenIndex < openParenIndex)
        {
            throw new TextIrParseException($"Invalid annotation syntax '{text}'.");
        }

        var argsText = text.Substring(openParenIndex + 1, closeParenIndex - (openParenIndex + 1));
        var args = new List<string>();
        if (argsText.Trim().Length > 0)
        {
            foreach (var item in SplitTopLevel(argsText))
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0)
                {
                    throw new TextIrParseException("Empty annotation argument.");
                }
                args.Add(trimmed);
            }
        }

        return new AttributeNode(name2, args);
    }

    private static bool IsField(string line) => line.Contains(" field ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("field ", StringComparison.OrdinalIgnoreCase);

    private static bool IsConstructor(string line) => line.StartsWith("constructor", StringComparison.OrdinalIgnoreCase);

    private static bool IsMethod(string line) => line.StartsWith("method ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("static method ", StringComparison.OrdinalIgnoreCase);

    private static FieldNode ParseField(string line)
    {
        var access = AccessModifier.Private;
        var remaining = line;

        if (remaining.StartsWith("private ", StringComparison.OrdinalIgnoreCase))
        {
            access = AccessModifier.Private;
            remaining = remaining.Substring("private ".Length);
        }
        else if (remaining.StartsWith("public ", StringComparison.OrdinalIgnoreCase))
        {
            access = AccessModifier.Public;
            remaining = remaining.Substring("public ".Length);
        }
        else if (remaining.StartsWith("protected ", StringComparison.OrdinalIgnoreCase))
        {
            access = AccessModifier.Protected;
            remaining = remaining.Substring("protected ".Length);
        }
        else if (remaining.StartsWith("internal ", StringComparison.OrdinalIgnoreCase))
        {
            access = AccessModifier.Internal;
            remaining = remaining.Substring("internal ".Length);
        }

        remaining = remaining.Trim();
        if (remaining.StartsWith("field ", StringComparison.OrdinalIgnoreCase))
        {
            remaining = remaining.Substring("field ".Length);
        }

    #if NET47
        var __tmp_parts2 = remaining.Split(new[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var parts = __tmp_parts2.Select(p => p.Trim()).ToArray();
    #else
        var parts = remaining.Split(':', 2, StringSplitOptions.TrimEntries);
    #endif
        if (parts.Length != 2)
        {
            throw new TextIrParseException("Invalid field declaration.");
        }

        var name = parts[0].Trim();
        var type = parts[1].Trim();
        if (name.Length == 0 || type.Length == 0)
        {
            throw new TextIrParseException("Invalid field declaration.");
        }

        return new FieldNode(name, new TypeRef(type), access);
    }

    private static ConstructorNode ParseConstructor(TokenReader reader, string line)
    {
        var parameters = ParseParametersFromSignature("constructor", line);
        var body = ParseBlock(reader);
        return new ConstructorNode(parameters, body);
    }

    private static MethodSignature ParseMethodSignature(string line)
    {
        var (isStatic, signature) = ConsumeStaticPrefix(line);
        if (!signature.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
        {
            throw new TextIrParseException("Expected method signature.");
        }

        var (name, parameters, returnType, implements) = ParseMethodParts(
    #if NET47
            signature.Substring("method ".Length)
    #else
            signature["method ".Length..]
    #endif
        );
        return new MethodSignature(name, parameters, returnType, isStatic, implements);
    }

    private static MethodNode ParseMethod(TokenReader reader, string line)
    {
        var (isStatic, signature) = ConsumeStaticPrefix(line);
        if (!signature.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
        {
            throw new TextIrParseException("Expected method declaration.");
        }

        var (name, parameters, returnType, implements) = ParseMethodParts(
    #if NET47
            signature.Substring("method ".Length)
    #else
            signature["method ".Length..]
    #endif
        );
        var body = ParseBlock(reader);
        return new MethodNode(name, parameters, returnType, isStatic, implements, body);
    }

    private static (bool IsStatic, string Signature) ConsumeStaticPrefix(string line)
    {
        if (line.StartsWith("static method ", StringComparison.OrdinalIgnoreCase))
        {
            #if NET47
            return (true, line.Substring("static ".Length));
            #else
            return (true, line["static ".Length..]);
            #endif
        }

        return (false, line);
    }

    private static (string Name, IReadOnlyList<ParameterNode> Parameters, TypeRef ReturnType, string? Implements) ParseMethodParts(string line)
    {
        var implementsIndex = line.IndexOf(" implements ", StringComparison.OrdinalIgnoreCase);
        string? implements = null;
        if (implementsIndex >= 0)
        {
    #if NET47
            implements = line.Substring(implementsIndex + " implements ".Length).Trim();
            line = line.Substring(0, implementsIndex).Trim();
    #else
            implements = line[(implementsIndex + " implements ".Length)..].Trim();
            line = line[..implementsIndex].Trim();
    #endif
        }

        var arrowIndex = line.IndexOf("->", StringComparison.Ordinal);
        if (arrowIndex < 0)
        {
            throw new TextIrParseException("Expected '->' in method signature.");
        }

        #if NET47
        var left = line.Substring(0, arrowIndex).Trim();
        var returnType = line.Substring(arrowIndex + 2).Trim();
        #else
        var left = line[..arrowIndex].Trim();
        var returnType = line[(arrowIndex + 2)..].Trim();
        #endif
        if (returnType.Length == 0)
        {
            throw new TextIrParseException("Missing return type.");
        }

        var openParenIndex = left.IndexOf('(');
        var closeParenIndex = left.LastIndexOf(')');
        if (openParenIndex < 0 || closeParenIndex < openParenIndex)
        {
            throw new TextIrParseException("Expected parameters in parentheses.");
        }

            #if NET47
            var name = left.Substring(0, openParenIndex).Trim();
            var parametersSpan = left.Substring(openParenIndex + 1, closeParenIndex - (openParenIndex + 1));
            #else
            var name = left[..openParenIndex].Trim();
            var parametersSpan = left[(openParenIndex + 1)..closeParenIndex];
            #endif
        var parameters = ParseParameters(parametersSpan);

        return (name, parameters, new TypeRef(returnType), implements);
    }

    private static IReadOnlyList<ParameterNode> ParseParametersFromSignature(string keyword, string line)
    {
        var start = keyword.Length;
        #if NET47
        var signature = line.Substring(start).Trim();
        #else
        var signature = line[start..].Trim();
        #endif
        var openParenIndex = signature.IndexOf('(');
        var closeParenIndex = signature.LastIndexOf(')');
        if (openParenIndex < 0 || closeParenIndex < openParenIndex)
        {
            throw new TextIrParseException("Expected parameters in parentheses.");
        }

        #if NET47
        var parametersSpan = signature.Substring(openParenIndex + 1, closeParenIndex - (openParenIndex + 1));
        #else
        var parametersSpan = signature[(openParenIndex + 1)..closeParenIndex];
        #endif
        return ParseParameters(parametersSpan);
    }

    private static BlockStatement ParseBlock(TokenReader reader)
    {
        reader.Expect("{");
        var statements = new List<Statement>();

        while (!reader.PeekIs("}"))
        {
            var line = reader.ReadLine();
            if (line.StartsWith("local ", StringComparison.OrdinalIgnoreCase))
            {
                statements.Add(ParseLocal(line));
                continue;
            }

            if (line.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
            {
                statements.Add(ParseIf(reader, line));
                continue;
            }

            if (line.StartsWith("while ", StringComparison.OrdinalIgnoreCase))
            {
                statements.Add(ParseWhile(reader, line));
                continue;
            }

            if (line.StartsWith("switch ", StringComparison.OrdinalIgnoreCase))
            {
                statements.Add(ParseSwitch(reader, line));
                continue;
            }

            statements.Add(new InstructionStatement(ParseInstruction(line)));
        }

        reader.Expect("}");
        return new BlockStatement(statements);
    }

    private static SwitchStatement ParseSwitch(TokenReader reader, string line)
    {
        var expr = ExtractCondition(line, "switch");
        reader.Expect("{");
        var cases = new List<SwitchCase>();

        while (!reader.PeekIs("}"))
        {
            var caseLine = reader.ReadLine();
            if (!caseLine.StartsWith("case ", StringComparison.OrdinalIgnoreCase))
            {
                throw new TextIrParseException($"Expected case, but found '{caseLine}'.");
            }

            var parts = caseLine["case ".Length..].Trim();
            int? val = null;
            string? stringVal = null;
            if (parts.Equals("else:", StringComparison.OrdinalIgnoreCase))
            {
                val = null;
            }
            else if (parts.EndsWith(":", StringComparison.Ordinal))
            {
                var valueText = parts[..^1];
                if (valueText.Length >= 2 && valueText.StartsWith("\"", StringComparison.Ordinal) && valueText.EndsWith("\"", StringComparison.Ordinal))
                {
                    // String case value (quotes preserved, consistent with ldstr operands)
                    stringVal = valueText;
                }
                else if (int.TryParse(valueText, out var intVal))
                {
                    val = intVal;
                }
                else
                {
                    throw new TextIrParseException($"Expected integer or string case value, found '{valueText}'.");
                }
            }
            else
            {
                throw new TextIrParseException($"Invalid case format: '{caseLine}'.");
            }

            // The .oir format seems to have case bodies without braces
            var bodyStatements = new List<Statement>();
            while (!reader.PeekIs("}") && !reader.PeekLine().TrimStart().StartsWith("case ", StringComparison.OrdinalIgnoreCase))
            {
                var stmtLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(stmtLine)) continue;
                
                // Simplified parsing of block contents
                if (stmtLine.TrimStart().StartsWith("local ", StringComparison.OrdinalIgnoreCase))
                    bodyStatements.Add(ParseLocal(stmtLine.Trim()));
                else if (stmtLine.TrimStart().StartsWith("if ", StringComparison.OrdinalIgnoreCase))
                    bodyStatements.Add(ParseIf(reader, stmtLine.Trim()));
                else if (stmtLine.TrimStart().StartsWith("while ", StringComparison.OrdinalIgnoreCase))
                    bodyStatements.Add(ParseWhile(reader, stmtLine.Trim()));
                else
                    bodyStatements.Add(new InstructionStatement(ParseInstruction(stmtLine.Trim())));
            }
            
            cases.Add(new SwitchCase(val, new BlockStatement(bodyStatements)) { StringValue = stringVal });
        }

        reader.Expect("}");
        return new SwitchStatement(expr, cases);
    }

    private static LocalDeclarationStatement ParseLocal(string line)
    {
    #if NET47
        var __tmp_parts3 = line.Substring("local ".Length).Split(new[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var parts = __tmp_parts3.Select(p => p.Trim()).ToArray();
    #else
        var parts = line["local ".Length..].Split(':', 2, StringSplitOptions.TrimEntries);
    #endif
        if (parts.Length != 2)
        {
            throw new TextIrParseException("Invalid local declaration.");
        }

        return new LocalDeclarationStatement(parts[0], new TypeRef(parts[1]));
    }

    private static IfStatement ParseIf(TokenReader reader, string line)
    {
        var condition = ExtractCondition(line, "if");
        var thenBlock = ParseBlock(reader);
        BlockStatement? elseBlock = null;

        if (!reader.IsAtEnd && reader.PeekIs("else"))
        {
            reader.ReadLine();
            elseBlock = ParseBlock(reader);
        }

        return new IfStatement(condition, thenBlock, elseBlock);
    }

    private static WhileStatement ParseWhile(TokenReader reader, string line)
    {
        var condition = ExtractCondition(line, "while");
        var body = ParseBlock(reader);
        return new WhileStatement(condition, body);
    }

    private static string ExtractCondition(string line, string keyword)
    {
        var start = keyword.Length;
        #if NET47
        var trimmed = line.Substring(start).Trim();
        #else
        var trimmed = line[start..].Trim();
        #endif
        var openParenIndex = trimmed.IndexOf('(');
        var closeParenIndex = trimmed.LastIndexOf(')');
        if (openParenIndex < 0 || closeParenIndex < openParenIndex)
        {
            throw new TextIrParseException($"Expected condition in parentheses for '{keyword}'.");
        }

    #if NET47
        return trimmed.Substring(openParenIndex + 1, closeParenIndex - (openParenIndex + 1)).Trim();
    #else
        return trimmed[(openParenIndex + 1)..closeParenIndex].Trim();
    #endif
    }

    private static Instruction ParseInstruction(string line)
    {
        if (line.StartsWith("callvirt ", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCallCore(line.Substring("callvirt ".Length).AsSpan().Trim(), isVirtual: true);
        }

        if (line.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCallCore(line.Substring("call ".Length).AsSpan().Trim(), isVirtual: false);
        }

        if (line.StartsWith("newobj ", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNewObj(line.Substring("newobj ".Length).Trim());
        }

        var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var opcode = parts[0];
        var operand = parts.Length > 1 ? parts[1].Trim() : null;
        return new SimpleInstruction(OpCodeConverter.Parse(opcode), operand);
    }

    private static CallInstruction ParseCallCore(ReadOnlySpan<char> span, bool isVirtual)
    {
        var arrowIndex = span.IndexOf("->", StringComparison.Ordinal);
        if (arrowIndex < 0)
        {
            throw new TextIrParseException("Expected '->' before return type.");
        }

        var left = span.Slice(0, arrowIndex).Trim();
        var right = span.Slice(arrowIndex + 2).Trim();
        if (right.IsEmpty)
        {
            throw new TextIrParseException("Return type is missing.");
        }

        var (methodRef, args) = ParseTargetAndArgs(left);
        var returnTypeNode = new TypeRef(right.ToString());

        // Update the return type and parameter types on the method reference
        var updatedMethodRef = methodRef with { 
            ReturnType = returnTypeNode,
            ParameterTypes = args.ToList()
        };

        return new CallInstruction(updatedMethodRef, args, isVirtual);
    }

    private static NewObjInstruction ParseNewObj(string text)
    {
        var span = text.AsSpan().Trim();
        var ctorIndex = span.IndexOf(".constructor", StringComparison.OrdinalIgnoreCase);
        if (ctorIndex < 0)
        {
            return new NewObjInstruction(new TypeRef(span.ToString()), null, new List<TypeRef>());
        }

        var typeName = span.Slice(0, ctorIndex).Trim();
        var ctorSpan = span.Slice(ctorIndex + 1).Trim();
        if (!ctorSpan.StartsWith("constructor", StringComparison.OrdinalIgnoreCase))
        {
            throw new TextIrParseException("Invalid constructor reference.");
        }

        var openParenIndex = ctorSpan.IndexOf('(');
        var closeParenIndex = ctorSpan.LastIndexOf(')');
        if (openParenIndex < 0 || closeParenIndex < openParenIndex)
        {
            throw new TextIrParseException("Expected constructor arguments.");
        }

        var argsSpan = ctorSpan.Slice(openParenIndex + 1, closeParenIndex - (openParenIndex + 1));
        var args = ParseTypeList(argsSpan);
        var argList = args.ToList();
        var method = new MethodReference(new TypeRef(typeName.ToString()), "constructor", TypeRef.Void, argList);
        return new NewObjInstruction(new TypeRef(typeName.ToString()), method, argList);
    }

    private static (MethodReference Method, IReadOnlyList<TypeRef> Args) ParseTargetAndArgs(ReadOnlySpan<char> text)
    {
        var openParenIndex = text.IndexOf('(');
        var closeParenIndex = text.LastIndexOf(')');
        if (openParenIndex < 0 || closeParenIndex < openParenIndex)
        {
            throw new TextIrParseException("Expected argument list in parentheses.");
        }

        var targetSpan = text.Slice(0, openParenIndex).Trim();
        var argsSpan = text.Slice(openParenIndex + 1, closeParenIndex - (openParenIndex + 1));

        if (targetSpan.IsEmpty)
        {
            throw new TextIrParseException("Missing call target.");
        }

        var lastDot = targetSpan.LastIndexOf('.');
        if (lastDot < 0)
        {
            throw new TextIrParseException("Target must be qualified as Type.Method.");
        }

        var typeName = targetSpan.Slice(0, lastDot).Trim();
        var methodName = targetSpan.Slice(lastDot + 1).Trim();

        if (typeName.IsEmpty || methodName.IsEmpty)
        {
            throw new TextIrParseException("Invalid target format.");
        }

        var method = new MethodReference(new TypeRef(typeName.ToString()), methodName.ToString(), TypeRef.Void, new List<TypeRef>());
        var args = ParseTypeList(argsSpan);
        return (method, args);
    }

    private static IReadOnlyList<TypeRef> ParseTypeList(ReadOnlySpan<char> text)
    {
        var list = new List<TypeRef>();
        var raw = text.ToString().Trim();
        if (raw.Length == 0)
        {
            return list;
        }

        foreach (var item in SplitTopLevel(raw))
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                throw new TextIrParseException("Empty argument type.");
            }

            list.Add(new TypeRef(trimmed));
        }

        return list;
    }

    private static IReadOnlyList<ParameterNode> ParseParameters(ReadOnlySpan<char> text)
    {
        var list = new List<ParameterNode>();
        var raw = text.ToString().Trim();
        if (raw.Length == 0)
        {
            return list;
        }

        foreach (var item in SplitTopLevel(raw))
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                throw new TextIrParseException("Empty parameter.");
            }

#if NET47
            var __tmp_parts4 = trimmed.Split(new[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var parts = __tmp_parts4.Select(p => p.Trim()).ToArray();
#else
            var parts = trimmed.Split(':', 2, StringSplitOptions.TrimEntries);
#endif
            if (parts.Length != 2)
            {
                throw new TextIrParseException("Invalid parameter format.");
            }

            list.Add(new ParameterNode(parts[0], new TypeRef(parts[1])));
        }

        return list;
    }

    private static IEnumerable<string> SplitTopLevel(string input)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var angleDepth = 0;
        var bracketDepth = 0;

        foreach (var ch in input)
        {
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case ',' when angleDepth == 0 && bracketDepth == 0:
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
            }

            sb.Append(ch);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var sb = new StringBuilder();
            foreach (var ch in line)
            {
                if (ch == '{' || ch == '}')
                {
                    var current = sb.ToString().Trim();
                    if (current.Length > 0)
                    {
                        tokens.Add(current);
                    }

                    tokens.Add(ch.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);
            }

            var final = sb.ToString().Trim();
            if (final.Length > 0)
            {
                tokens.Add(final);
            }
        }

        return tokens;
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
    }

    private sealed class TokenReader
    {
        private readonly List<string> _tokens;
        private int _index;

        public TokenReader(List<string> tokens)
        {
            _tokens = tokens;
        }

        public bool IsAtEnd => _index >= _tokens.Count;

        public string Peek() => _tokens[_index];

        public bool PeekIs(string token) => !IsAtEnd && _tokens[_index].Equals(token, StringComparison.OrdinalIgnoreCase);

        public bool PeekStartsWith(string prefix) => !IsAtEnd && _tokens[_index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        public bool TryReadLine(out string line)
        {
            if (IsAtEnd)
            {
                line = string.Empty;
                return false;
            }

            line = _tokens[_index++];
            return true;
        }

        public string ReadLine()
        {
            if (!TryReadLine(out var line))
            {
                throw new TextIrParseException("Unexpected end of input.");
            }

            return line;
        }

        public void Expect(string token)
        {
            var value = ReadLine();
            if (!value.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new TextIrParseException($"Expected '{token}' but found '{value}'.");
            }
        }

        public string PeekLine()
        {
            if (IsAtEnd)
            {
                throw new TextIrParseException("Unexpected end of input.");
            }
            return _tokens[_index];
        }
    }
}
