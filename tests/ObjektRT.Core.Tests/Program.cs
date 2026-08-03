using ObjektRT.Core.AST;
using ObjektRT.Core.Conversion;
using ObjektRT.Core.Model;
using ObjektRT.Core.Parsing;
using ObjektRT.Core.Serialization;

// ── Tiny assertion harness (no external test framework) ────────────────────
var failures = new List<string>();
int passed = 0;

void Check(bool condition, string name, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  FAIL  {name}{(detail != null ? $"\n        {detail}" : "")}");
    }
}

static byte[] RawOf(ORBTModule m, string typeName, string methodName)
{
    var type = m.Types.First(t => m.Resolve(t.NameIndex) == typeName);
    var method = type.Methods.First(md => m.Resolve(md.NameIndex) == methodName);
    return method.RawInstructionData;
}

// ── Samples ────────────────────────────────────────────────────────────────

const string HelloOil = """
module Hello version 1.0.0

class Program {
    static method Main() -> void {
        ldc.i4 42
        ldc.i4 58
        add
        ret
    }
}
""";

// The while-loop sample mirrors Lattice's main.oir grammar (locals declared
// mid-body, dotted call targets with parameter lists).
const string LoopOir = """
module test version 1.0.0

class Program
{
    static method Main() -> void {
        ldstr "--- While loop counting to 5:"
        call IO.Println(object) -> void
        local i: int32
        ldc.i4 0
        stloc i
        ldloc i
        ldc.i4 50000
        clt
        while (stack) {
            ldloc i
            call IO.Println(object) -> void
            ldloc i
            ldc.i4 1
            add
            stloc i
            ldloc i
            ldc.i4 50000
            clt
        }
    }
}
""";

// ── 1. ObjectIL text → wire model → AST → wire model ─────────────────────

Console.WriteLine("== 1. ObjectIL parse + AST conversion (hello.oil) ==");

var helloModel1 = new ObjectILParser(HelloOil).ParseModule();
var helloAst = new ModelToAstConverter().Convert(helloModel1);
var helloModel2 = new AstToModelConverter().Convert(helloAst);

Check(helloModel2.ModuleName == "Hello", "module name preserved");
Check(helloModel2.Types.Count == 1, "type count preserved");
Check(helloModel2.Types[0].Methods.Count == 1, "method count preserved");
Check(
    helloModel2.Types[0].Methods[0].NameIndex != 0
    && helloModel2.Resolve(helloModel2.Types[0].Methods[0].NameIndex) == "Main",
    "method name preserved");

var helloBytes1 = RawOf(helloModel1, "Program", "Main");
var helloBytes2 = RawOf(helloModel2, "Program", "Main");
Check(helloBytes1.SequenceEqual(helloBytes2),
    "AST round-trip is byte-identical",
    $"parser={BitConverter.ToString(helloBytes1)} converter={BitConverter.ToString(helloBytes2)}");

// ── 2. TextIR (AST) → wire model → AST: while reconstruction ──────────────

Console.WriteLine("== 2. TextIR while-loop reconstruction ==");

var loopAst1 = TextIrParser.ParseModule(LoopOir);
var loopModel = new AstToModelConverter().Convert(loopAst1);
var loopAst2 = new ModelToAstConverter().Convert(loopModel);

var main2 = loopAst2.Classes.First(c => c.Name == "Program").Methods.First(m => m.Name == "Main");
var whileCount = CountWhile(main2.Body);
Check(whileCount == 1, "while statement reconstructed", $"found {whileCount}");
var whileStmt = main2.Body.Statements.OfType<WhileStatement>().First();
Check(whileStmt.Condition == "stack", "while condition is 'stack'");
Check(main2.Locals.Count == 0, "locals hoisted to method body (AST convention)");

var bodyHasLocal = main2.Body.Statements.OfType<LocalDeclarationStatement>().Any(l => l.Name == "i");
Check(bodyHasLocal, "local 'i' emitted at body top");

var loopBytes1 = RawOf(loopModel, "Program", "Main");
var loopModel2 = new AstToModelConverter().Convert(loopAst2);
var loopBytes2 = RawOf(loopModel2, "Program", "Main");
Check(loopBytes1.SequenceEqual(loopBytes2),
    "while round-trip is byte-identical",
    $"first={BitConverter.ToString(loopBytes1)} second={BitConverter.ToString(loopBytes2)}");

// ── 3. ORBT binary round-trip ──────────────────────────────────────────────

Console.WriteLine("== 3. ORBT binary round-trip ==");

var orbtBytes = new ORBTWriter().WriteModule(loopModel);
var loopModelFromBinary = OrbtFileReader.ReadBytes(orbtBytes);
var loopAst3 = new ModelToAstConverter().Convert(loopModelFromBinary);
Check(loopAst3.Name == "test", "module name survives binary round-trip");

var main3 = loopAst3.Classes.First(c => c.Name == "Program").Methods.First(m => m.Name == "Main");
Check(CountWhile(main3.Body) == 1, "while survives binary round-trip");

var loopModel4 = new AstToModelConverter().Convert(loopAst3);
Check(
    RawOf(loopModel4, "Program", "Main").SequenceEqual(loopBytes1),
    "binary → AST → wire is byte-identical");

// ── 3b. Static field metadata (text + binary) ─────────────────────────────

Console.WriteLine("== 3b. Static field metadata ==");

const string StaticFieldOil = """
module FieldsTest version 1.0.0

class Counter {
    static field total: int32
    field label: string
}
""";

var fieldsModel1 = new ObjectILParser(StaticFieldOil).ParseModule();
var counterType = fieldsModel1.Types.First(t => fieldsModel1.Resolve(t.NameIndex) == "Counter");
var totalField = counterType.Fields.First(f => fieldsModel1.Resolve(f.NameIndex) == "total");
var labelField = counterType.Fields.First(f => fieldsModel1.Resolve(f.NameIndex) == "label");
Check(totalField.IsStatic && !labelField.IsStatic, "static flag parsed from text IR");

var fieldsBytes = new ORBTWriter().WriteModule(fieldsModel1);
var fieldsModel2 = OrbtFileReader.ReadBytes(fieldsBytes);
var total2 = fieldsModel2.Types[0].Fields.First(f => fieldsModel2.Resolve(f.NameIndex) == "total");
var label2 = fieldsModel2.Types[0].Fields.First(f => fieldsModel2.Resolve(f.NameIndex) == "label");
Check(total2.IsStatic && !label2.IsStatic, "static flag survives ORBT binary round-trip");

// AST restore: ModelToAstConverter maps the flag back onto FieldNode.IsStatic.
var fieldsAst = new ModelToAstConverter().Convert(fieldsModel2);
var counterAst = fieldsAst.Classes.First(c => c.Name == "Counter");
Check(counterAst.Fields.First(f => f.Name == "total").IsStatic
      && !counterAst.Fields.First(f => f.Name == "label").IsStatic,
    "static flag restored to AST");

// ── 4. Opcode mapping coverage ─────────────────────────────────────────────

Console.WriteLine("== 4. Opcode coverage ==");

var unmappedWire = Enum.GetValues<Opcode>()
    .Where(w => !ModelToAstConverter.TryGetAstOpcode(w, out _))
    .Select(w => w.ToString())
    .ToList();
Check(unmappedWire.Count == 0,
    "every wire opcode has an AST representation",
    unmappedWire.Count > 0 ? $"missing: {string.Join(", ", unmappedWire)}" : null);

var unmappedAst = Enum.GetValues<OpCode>()
    .Where(a => !AstToModelConverter.TryGetWireOpcode(a, out _))
    .Select(a => a.ToString())
    .ToList();
var expectedUnmapped = new HashSet<string> { "Ldlen", "Shl", "Shr", "CgtUn", "CgeUn", "Beq", "Bne", "Bgt", "Blt", "Calli", "Box", "Unbox", "ConvI4", "ConvI8", "ConvR4", "ConvR8", "ConvU4", "ConvU8", "For", "Switch" };
var extraUnmapped = unmappedAst.Where(a => !expectedUnmapped.Contains(a)).ToList();
Check(extraUnmapped.Count == 0,
    "AST opcodes without wire encoding match the documented set",
    extraUnmapped.Count > 0 ? $"unexpected: {string.Join(", ", extraUnmapped)}" : null);

// Every mappable AST opcode must round-trip through the wire and back.
var brokenRoundTrip = Enum.GetValues<OpCode>()
    .Where(a => AstToModelConverter.TryGetWireOpcode(a, out var w)
                && ModelToAstConverter.TryGetAstOpcode(w, out var back)
                && back != a)
    .Select(a => a.ToString())
    .ToList();
Check(brokenRoundTrip.Count == 0,
    "mappable AST opcodes round-trip to themselves",
    brokenRoundTrip.Count > 0 ? $"broken: {string.Join(", ", brokenRoundTrip)}" : null);

// ── Summary ────────────────────────────────────────────────────────────────

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"ALL {passed} CHECKS PASSED");
    return 0;
}

Console.WriteLine($"{failures.Count} CHECK(S) FAILED:");
foreach (var f in failures)
    Console.WriteLine($"  - {f}");
return 1;

// ── Helpers ────────────────────────────────────────────────────────────────

static int CountWhile(BlockStatement block)
{
    int n = 0;
    foreach (var stmt in block.Statements)
    {
        switch (stmt)
        {
            case WhileStatement w:
                n += 1 + CountWhile(w.Body);
                break;
            case IfStatement i:
                n += CountWhile(i.Then);
                if (i.Else != null) n += CountWhile(i.Else);
                break;
            case SwitchStatement s:
                foreach (var c in s.Cases) n += CountWhile(c.Body);
                break;
        }
    }
    return n;
}
