// Reads type and member signatures out of the IL2CPP interop assemblies that
// MelonLoader generates under MelonLoader/Il2CppAssemblies.
//
// Those assemblies carry no method bodies - IL2CPP compiled the logic to native
// code, and Il2CppInterop only regenerates managed stubs that forward to it. So
// this tool deliberately answers structural questions only: which component
// declares which field, whether a class has an Update or a LateUpdate, what the
// parameters of a method are. Anything about behaviour still has to be measured
// at runtime with WetReality.Discovery.
//
// System.Reflection.Metadata ships inside the .NET 6 shared framework, which is
// what makes this buildable with NuGet switched off.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WetReality.AssemblyProbe;

internal static class Program
{
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.Error.WriteLine(@"Usage: WetReality.AssemblyProbe <assembly-or-directory> [options]

  --type <substring>     only types whose name or namespace matches
  --members              list fields, properties and methods
  --member <substring>   only members matching, implies --members
  --base <substring>     only types whose base type matches

Matching is case-insensitive. Without --type every type is listed,
which is a lot - filter.");
            return 2;
        }

        var target = arguments[0];
        string? typeFilter = null, memberFilter = null, baseFilter = null;
        var listMembers = false;

        for (var index = 1; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--type" when index + 1 < arguments.Length:
                    typeFilter = arguments[++index];
                    break;
                case "--member" when index + 1 < arguments.Length:
                    memberFilter = arguments[++index];
                    listMembers = true;
                    break;
                case "--base" when index + 1 < arguments.Length:
                    baseFilter = arguments[++index];
                    break;
                case "--members":
                    listMembers = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {arguments[index]}");
                    return 2;
            }
        }

        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.dll").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { target };

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No assemblies found at {target}");
            return 1;
        }

        var matches = 0;

        foreach (var file in files)
        {
            try
            {
                matches += Probe(file, typeFilter, baseFilter, memberFilter, listMembers);
            }
            catch (Exception exception)
            {
                // A non-managed or truncated DLL in the folder must not abort the
                // whole sweep - report it and keep going.
                Console.Error.WriteLine($"Skipped {Path.GetFileName(file)}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{matches} matching type(s) across {files.Length} assembly file(s).");
        return 0;
    }

    private static int Probe(string file, string? typeFilter, string? baseFilter, string? memberFilter, bool listMembers)
    {
        using var stream = File.OpenRead(file);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            return 0;

        var reader = peReader.GetMetadataReader();
        var provider = new SignatureText(reader);
        var matches = 0;

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var space = reader.GetString(type.Namespace);
            var full = string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
            var baseName = SignatureText.EntityName(reader, type.BaseType);

            if (typeFilter is not null && !Contains(full, typeFilter))
                continue;
            if (baseFilter is not null && !Contains(baseName, baseFilter))
                continue;

            matches++;
            Console.WriteLine();
            Console.WriteLine($"{full}   : {baseName}   [{Path.GetFileName(file)}]");

            if (!listMembers)
                continue;

            foreach (var line in Members(reader, type, provider))
            {
                if (memberFilter is null || Contains(line, memberFilter))
                    Console.WriteLine($"    {line}");
            }
        }

        return matches;
    }

    private static IEnumerable<string> Members(MetadataReader reader, TypeDefinition type, SignatureText provider)
    {
        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            var signature = field.DecodeSignature(provider, null);
            yield return $"field   {Modifiers(field.Attributes)}{signature} {reader.GetString(field.Name)}";
        }

        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            var signature = property.DecodeSignature(provider, null);
            yield return $"prop    {signature.ReturnType} {reader.GetString(property.Name)}";
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var signature = method.DecodeSignature(provider, null);
            var parameters = string.Join(", ", signature.ParameterTypes);
            yield return $"method  {Modifiers(method.Attributes)}{signature.ReturnType} {reader.GetString(method.Name)}({parameters})";
        }
    }

    private static string Modifiers(FieldAttributes attributes)
    {
        var text = new StringBuilder();
        if ((attributes & FieldAttributes.Static) != 0) text.Append("static ");
        if ((attributes & FieldAttributes.Public) != 0) text.Append("public ");
        return text.ToString();
    }

    private static string Modifiers(MethodAttributes attributes)
    {
        var text = new StringBuilder();
        if ((attributes & MethodAttributes.Static) != 0) text.Append("static ");
        if ((attributes & MethodAttributes.Virtual) != 0) text.Append("virtual ");
        if ((attributes & MethodAttributes.Public) != 0) text.Append("public ");
        return text.ToString();
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

// Turns signature blobs into readable text. Only the shapes that actually occur
// in these assemblies are rendered precisely; the rest degrades to a name
// rather than throwing, because a probe that dies on an exotic signature is
// useless for sweeping a whole game.
internal sealed class SignatureText : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader reader;

    public SignatureText(MetadataReader reader) => this.reader = reader;

    public static string EntityName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil)
            return "-";

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                var space = reader.GetString(type.Namespace);
                var name = reader.GetString(type.Name);
                return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
            }
            case HandleKind.TypeReference:
            {
                var type = reader.GetTypeReference((TypeReferenceHandle)handle);
                var space = reader.GetString(type.Namespace);
                var name = reader.GetString(type.Name);
                return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
            }
            default:
                return handle.Kind.ToString();
        }
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Short(EntityName(reader, handle));

    public string GetTypeFromReference(MetadataReader _, TypeReferenceHandle handle, byte rawTypeKind) =>
        Short(EntityName(reader, handle));

    public string GetTypeFromSpecification(MetadataReader _, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => $"{elementType}[]";
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(shape.Rank - 1, 0))}]";
    public string GetByReferenceType(string elementType) => $"ref {elementType}";
    public string GetPointerType(string elementType) => $"{elementType}*";
    public string GetPinnedType(string elementType) => $"pinned {elementType}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetGenericTypeParameter(object? genericContext, int index) => $"T{index}";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"M{index}";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        var trimmed = genericType.Contains('`') ? genericType[..genericType.IndexOf('`')] : genericType;
        return $"{trimmed}<{string.Join(", ", typeArguments)}>";
    }

    // Namespaces make these listings unreadable: nearly every parameter is an
    // Il2Cpp or UnityEngine type, and the namespace is never the interesting
    // part when the question is which field a component declares.
    private static string Short(string name)
    {
        var cut = name.LastIndexOf('.');
        return cut < 0 ? name : name[(cut + 1)..];
    }
}
