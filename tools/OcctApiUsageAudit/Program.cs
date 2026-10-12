using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 2) throw new ArgumentException("Output JSON followed by frozen Cadoryx assembly directories required.");
string output = Path.GetFullPath(args[0]);
if (File.Exists(output)) throw new IOException("Immutable audit output already exists.");
var binaries = args.Skip(1).SelectMany(path => Directory.EnumerateFiles(Path.GetFullPath(path), "Cadoryx*.dll"))
    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
if (binaries.Length == 0) throw new InvalidOperationException("No compiled Cadoryx assemblies found.");
var assemblies = new List<object>();
var types = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var members = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var nativeImports = new List<object>();
void Add(SortedDictionary<string, SortedSet<string>> map, string symbol, string assembly)
{
    if (!map.TryGetValue(symbol, out var owners)) map[symbol] = owners = new(StringComparer.Ordinal);
    owners.Add(assembly);
}
foreach (string binary in binaries)
{
    using var stream = File.OpenRead(binary);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata) throw new BadImageFormatException(binary);
    var reader = pe.GetMetadataReader();
    string name = reader.GetString(reader.GetAssemblyDefinition().Name);
    var provider = new Names(reader);
    int typeCount = 0, memberCount = 0;
    foreach (var handle in reader.TypeReferences)
    {
        string type = provider.GetTypeFromReference(reader, handle, 0);
        if (!Names.IsOcct(type)) continue;
        Add(types, type, name); typeCount++;
    }
    foreach (var handle in reader.MemberReferences)
    {
        var member = reader.GetMemberReference(handle);
        string parent = provider.Type(member.Parent);
        if (!Names.IsOcct(parent)) continue;
        string memberName = reader.GetString(member.Name);
        string signature;
        if (member.GetKind() == MemberReferenceKind.Method)
        {
            var method = member.DecodeMethodSignature(provider, (object?)null);
            signature = $"{parent}::{memberName}`{method.GenericParameterCount}({string.Join(",", method.ParameterTypes)}):{method.ReturnType};header={method.Header.RawValue};required={method.RequiredParameterCount}";
        }
        else signature = $"{parent}::{memberName}:{member.DecodeFieldSignature(provider, (object?)null)}";
        Add(members, signature, name); memberCount++;
    }
    foreach (var handle in reader.MethodDefinitions)
    {
        var method = reader.GetMethodDefinition(handle);
        if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
        var import = method.GetImport();
        string module = reader.GetString(reader.GetModuleReference(import.Module).Name);
        if (!module.Contains("Occt", StringComparison.OrdinalIgnoreCase)) continue;
        nativeImports.Add(new { assembly = name, module, entryPoint = reader.GetString(import.Name), method = reader.GetString(method.Name) });
    }
    assemblies.Add(new { name, path = binary, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))), typeCount, memberCount,
        scope = name == "Cadoryx.Tests" ? "TEST_REFERENCES" : "APPLICATION_AND_INCLUDED_DIAGNOSTIC_REFERENCES" });
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    recordedAt = DateTimeOffset.UtcNow,
    state = "STATIC_COMPILED_OCCT_TYPE_MEMBER_AND_DIRECT_NATIVE_IMPORT_REFERENCES_NOT_RUNTIME_ACCEPTANCE",
    assemblyCount = assemblies.Count,
    typeCount = types.Count,
    memberCount = members.Count,
    assemblies,
    types = types.Select(row => new { symbol = row.Key, consumers = row.Value }),
    members = members.Select(row => new { symbol = row.Key, consumers = row.Value }),
    directNativeImports = nativeImports,
    sdkAssembliesLoadedOrNativeCodeExecuted = false,
    provesCompleteOcctCapabilityCoverage = false
}, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"PASS metadata only: {assemblies.Count} assemblies, {types.Count} OCCT types, {members.Count} OCCT members, {nativeImports.Count} direct native imports. {output}");

sealed class Names(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    public static bool IsOcct(string name) => name.StartsWith("[OcctSharp]", StringComparison.Ordinal) || name.StartsWith("[OcctSharp.", StringComparison.Ordinal);
    public string Type(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
        HandleKind.MethodDefinition => Type(reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()),
        _ => $"<{handle.Kind}>"
    };
    public string GetTypeFromReference(MetadataReader _, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name), ns = reader.GetString(type.Namespace);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference) return Type(type.ResolutionScope) + "+" + name;
        string assembly = type.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name)
            : reader.GetString(reader.GetAssemblyDefinition().Name);
        return $"[{assembly}]{(ns.Length == 0 ? name : ns + "." + name)}";
    }
    public string GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle); string name = reader.GetString(type.Name), ns = reader.GetString(type.Namespace);
        if (!type.GetDeclaringType().IsNil) return Type(type.GetDeclaringType()) + "+" + name;
        return $"[{reader.GetString(reader.GetAssemblyDefinition().Name)}]{(ns.Length == 0 ? name : ns + "." + name)}";
    }
    public string GetTypeFromSpecification(MetadataReader _, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    public string GetArrayType(string element, ArrayShape shape) => element + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string element) => element + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => $"methodptr[{signature.Header.RawValue}]({string.Join(",", signature.ParameterTypes)}):{signature.ReturnType}";
    public string GetGenericInstantiation(string type, ImmutableArray<string> arguments) => type + "<" + string.Join(",", arguments) + ">";
    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string type, bool required) => type + (required ? " modreq(" : " modopt(") + modifier + ")";
    public string GetPinnedType(string element) => element + " pinned";
    public string GetPointerType(string element) => element + "*";
    public string GetPrimitiveType(PrimitiveTypeCode code) => "System." + code;
    public string GetSZArrayType(string element) => element + "[]";
}
