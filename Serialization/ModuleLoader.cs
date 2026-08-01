namespace ObjektRT.Core.Serialization;

using ObjektRT.Core.AST;
using ObjektRT.Core.Parsing;
using ObjektRT.Core.Builder;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System;
using System.IO;

/// <summary>
/// Loads ObjectIR modules from text format and provides storage/caching capabilities
/// </summary>
public sealed class ModuleLoader
{
    private readonly Dictionary<string, ModuleNode> _moduleCache = new();

    /// <summary>
    /// Loads a module from text format
    /// </summary>
    public ModuleNode LoadFromText(string text)
    {
        // For now, we delegate to TextIrParser if available, or use our basic implementation
        // Since TextIrParser returns ModuleNode, it's perfect.
        var module = TextIrParser.ParseModule(text);
        CacheModule(module);
        return module;
    }

    /// <summary>
    /// Loads a module from JSON format
    /// </summary>
    public ModuleNode LoadFromJson(string json)
    {
        var module = ModuleSerializer.LoadFromJson(json);
        CacheModule(module);
        return module;
    }

    /// <summary>
    /// Loads a module from a JSON file
    /// </summary>
    public ModuleNode LoadFromJsonFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return LoadFromJson(json);
    }

    /// <summary>
    /// Loads a module from text file format
    /// </summary>
    public ModuleNode LoadFromTextFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return LoadFromText(text);
    }

    /// <summary>
    /// Saves a module to JSON file
    /// </summary>
    public void SaveToJsonFile(ModuleNode module, string filePath, bool indented = true)
    {
        var serializer = new ModuleSerializer(module);
        var json = serializer.DumpToJson(indented);
        File.WriteAllText(filePath, json);
        CacheModule(module);
    }

    /// <summary>
    /// Saves a module to text file format
    /// </summary>
    public void SaveToTextFile(ModuleNode module, string filePath)
    {
        // Using ModuleSerializer to dump text for now
        var serializer = new ModuleSerializer(module);
        var text = serializer.DumpToText();
        File.WriteAllText(filePath, text);
        CacheModule(module);
    }

    /// <summary>
    /// Loads a module from BSON (binary JSON) format
    /// </summary>
    public ModuleNode LoadFromBson(byte[] bsonData)
    {
        var module = ModuleSerializer.LoadFromBson(bsonData);
        CacheModule(module);
        return module;
    }

    /// <summary>
    /// Loads a module from a BSON file
    /// </summary>
    public ModuleNode LoadFromBsonFile(string filePath)
    {
        var bsonData = File.ReadAllBytes(filePath);
        return LoadFromBson(bsonData);
    }

    /// <summary>
    /// Gets a cached module by name
    /// </summary>
    public ModuleNode? GetCachedModule(string moduleName)
    {
        return _moduleCache.TryGetValue(moduleName, out var module) ? module : null;
    }

    /// <summary>
    /// Clears the module cache
    /// </summary>
    public void ClearCache()
    {
        _moduleCache.Clear();
    }

    private void CacheModule(ModuleNode module)
    {
        _moduleCache[module.Name] = module;
    }
}
