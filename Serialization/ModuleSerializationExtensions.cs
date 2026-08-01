namespace ObjektRT.Core.Serialization;

using ObjektRT.Core.AST;

/// <summary>
/// Extension methods for serializing IR modules (using AST nodes)
/// </summary>
public static class ModuleSerializationExtensions
{
    /// <summary>
    /// Creates a serializer for this module
    /// </summary>
    public static ModuleSerializer Serialize(this ModuleNode module) 
        => new ModuleSerializer(module);

    /// <summary>
    /// Dumps the module as a structured object representation
    /// </summary>
    public static ModuleData Dump(this ModuleNode module)
        => module.Serialize().Dump();

    /// <summary>
    /// Dumps the module as JSON string
    /// </summary>
    public static string DumpJson(this ModuleNode module, bool indented = true)
        => module.Serialize().DumpToJson(indented);

    /// <summary>
    /// Dumps the module as human-readable text (summary of methods)
    /// </summary>
    public static string DumpText(this ModuleNode module)
        => AdvancedModuleFormats.DumpToText(module.Dump());

    /// <summary>
    /// Dumps the module as human-readable IR code (same as .ir.txt format)
    /// </summary>
    public static string DumpIRCode(this ModuleNode module)
        => module.Serialize().DumpToIRCode();

    /// <summary>
    /// Dumps the module as BSON (binary JSON) for smaller file sizes
    /// </summary>
    public static byte[] DumpBson(this ModuleNode module)
        => module.Serialize().DumpToBson();

    /// <summary>
    /// Dumps the module as CSV format for spreadsheet analysis
    /// </summary>
    public static string DumpCsv(this ModuleNode module)
        => AdvancedModuleFormats.DumpToCSV(module.Dump());

    /// <summary>
    /// Dumps the module as Markdown for documentation
    /// </summary>
    public static string DumpMarkdown(this ModuleNode module)
        => AdvancedModuleFormats.DumpToMarkdown(module.Dump());

    /// <summary>
    /// Dumps the module as YAML format
    /// </summary>
    public static string DumpYaml(this ModuleNode module)
        => AdvancedModuleFormats.DumpToYAML(module.Dump());

    /// <summary>
    /// Generates a summary report of the module
    /// </summary>
    public static string GenerateSummaryReport(this ModuleNode module)
        => AdvancedModuleFormats.GenerateSummaryReport(module.Dump());

    /// <summary>
    /// Loads a module from JSON string
    /// </summary>
    public static ModuleNode LoadFromJson(this ModuleNode module, string json)
        => ModuleSerializer.LoadFromJson(json);

    /// <summary>
    /// Loads a module from BSON (binary JSON) data
    /// </summary>
    public static ModuleNode LoadFromBson(this ModuleNode module, byte[] bsonData)
        => ModuleSerializer.LoadFromBson(bsonData);
}
