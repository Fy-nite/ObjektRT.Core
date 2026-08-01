using System;

namespace ObjektRT.Core.Parsing;

public sealed class TextIrParseException : Exception
{
    public TextIrParseException(string message) : base(message) { }
}
