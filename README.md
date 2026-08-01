# ObjektRT.Core

A .NET 8/.NET 10 library for building, manipulating, serialising, and converting an **object-oriented intermediate representation (IR)**. Think LLVM IR, but designed from the ground up for OO languages — classes, interfaces, structs, generics, virtual dispatch, and all.

> **History.** This library merges two codebases that were the same IR split in two:
>
> - **ObjectIR.Core** — the rich AST, TextIR parser, JSON/BSON/FOB serialisation, fluent builder, and module composition.
> - **ObjectRT.Reader / ObjectRT.Abstractions** — the index-based **wire model** (`ORBTModule`, string pool, `ushort`-indexed records) and the ObjectIL / ORBT binary parsers used by the ObjectRT VM.
>
> ORBT, FOB/IR, and ObjectIL/TextIR are the same format family under different names — **ORBT is the binary format**, the old FOB/IR v3 payload wrapper has been removed. ObjektRT.Core keeps both representations in one library and provides a bidirectional conversion layer between them, so tooling (compilers, analysers, the ObjectRT VM, the finite compiler collection) can move between them freely.
>
> The obsolete legacy `IR` model has been removed (it was marked end-of-life in the old README and is preserved in git history).

## What you can do

- **Build** an AST with a fluent `IRBuilder` or parse it from TextIR / ObjectIL source
- **Parse** ObjectIL text into the index-based wire model (`ObjectILParser`)
- **Read / write** the ORBT binary format (`ORBTReader`, `ORBTWriter`, `OrbtFileReader`)
- **Convert** between the AST and the wire model in both directions (`Conversion/`)
- **Serialize** to JSON or BSON (`Serialization/`), or to the ORBT binary format (`ORBTReader`/`ORBTWriter`)
- **Compose** multiple modules (`Composition/`)
- **Execute** (in the ObjectRT VM) or analyse (in compilers)

## Project layout

```
ObjektRT.Core/
├── Model/          wire model — ORBTModule, StringPool, records, typed operands, wire opcodes
├── AST/            rich AST — ModuleNode, statements, instructions, AST opcodes
├── Parsing/        ObjectIL tokenizer+parser → wire model; TextIR parser → AST
├── Serialization/  ORBT binary reader/writer, BinaryStream, JSON/BSON serializers, ModuleLoader
├── Conversion/     AstToModelConverter + ModelToAstConverter (bidirectional)
├── Builder/        fluent IRBuilder
├── Composition/    module composition and dependency resolution
├── Attributes/     module metadata attributes
└── Core/           Value<T> container, ObjectNode placeholder
```

## Quick start

```bash
dotnet add package ObjektRT.Core
```

Parse ObjectIL straight into the wire model, then up into an AST:

```csharp
using ObjektRT.Core.Parsing;
using ObjektRT.Core.Conversion;

var model = new ObjectILParser(source).ParseModule();   // wire model
var ast   = new ModelToAstConverter().Convert(model);   // rich AST
var back  = new AstToModelConverter().Convert(ast);     // wire model again
```

Build an AST directly and lower it to a runnable wire module:

```csharp
using ObjektRT.Core.Conversion;
using ObjektRT.Core.AST;

var ast = new ModuleNode("MyApp") { Version = "1.0.0" };
ast.AddClass("Animal");
var model = new AstToModelConverter().Convert(ast);
```

## Conversion fidelity (v1)

- **AST → Model:** structured `if`/`while` are lowered to flat `brfalse`/`br`/`dup` bytecode with labels (identical to the ObjectIL parser); locals are hoisted to the method local table; call targets become name + parameter count (the wire call encoding). AST opcodes with no wire encoding (`Shl`, `Beq`, `Box`, `For`, `Switch`, …) throw `NotSupportedException` — check `AstToModelConverter.TryGetWireOpcode` first.
- **Model → AST:** canonical `if`/`while` shapes are reconstructed back into structured statements; anything else stays flat `SimpleInstruction`s (semantics preserved, structure flat). Field access modifiers do not exist in the wire format (fields come back `public`); wire `Enum` types map to `ClassNode` (the AST has no enum node). `try/catch` has no AST representation yet.

Opcode mappings are exposed as `AstToModelConverter.TryGetWireOpcode` / `ModelToAstConverter.TryGetAstOpcode` for front-ends.

## Building and testing

```bash
dotnet build ObjektRT.Core.slnx
dotnet run --project tests/ObjektRT.Core.Tests   # round-trip smoke tests
```

## Package

- **PackageId:** `ObjektRT.Core`
- **Version:** 1.0.0 (breaking rename from `ObjectIR.Core` 0.1.3 — old package stays published for legacy consumers)

## License

MIT — see `LICENSE`.
