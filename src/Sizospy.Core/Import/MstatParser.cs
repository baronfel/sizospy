using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Sizospy.Import;

internal static class MstatParser
{
    public static async Task ParseAsync(
        string path,
        ImportBuilder builder,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
            if (!peReader.HasMetadata)
            {
                throw new SizospyException($"MSTAT '{path}' is not a managed PE file.", "invalid-mstat");
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                throw new SizospyException($"MSTAT '{path}' does not contain an assembly definition.", "invalid-mstat");
            }

            var version = reader.GetAssemblyDefinition().Version;
            if (version.Major != 2 || version.Minor is < 0 or > 2)
            {
                throw new SizospyException(
                    $"Unsupported MSTAT version {version.Major}.{version.Minor}. Sizospy supports versions 2.0 through 2.2.",
                    "unsupported-mstat-version");
            }

            builder.Metadata["mstat_version"] = $"{version.Major}.{version.Minor}";
            builder.Metadata["mstat_path"] = Path.GetFullPath(path);
            builder.Metadata["physical_size_source"] = "mstat";

            BlobReader? namesReader = null;
            try
            {
                namesReader = peReader.GetSectionData(".names").GetReader();
            }
            catch (InvalidOperationException)
            {
                builder.Diagnostics.Add(new ImportDiagnostic(
                    DiagnosticSeverity.Warning,
                    "mstat-names-missing",
                    "The MSTAT has no .names section; compiler identities will fall back to metadata names.",
                    path));
            }

            var module = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1));
            foreach (var methodHandle in module.GetMethods())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var method = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(method.Name);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var instructions = ReadInstructions(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader(), reader);
                switch (methodName)
                {
                    case "Types":
                        ParseTypes(instructions, reader, namesReader, builder, path);
                        break;
                    case "Methods":
                        ParseMethods(instructions, reader, namesReader, builder, path);
                        break;
                    case "Blobs":
                        ParseBlobs(instructions, builder, path);
                        break;
                    case "RvaFields" when version.Minor >= 1:
                        ParseRvaFields(instructions, reader, namesReader, builder, path);
                        break;
                    case "FrozenObjects" when version.Minor >= 1:
                        ParseFrozenObjects(instructions, reader, namesReader, builder, path);
                        break;
                    case "ManifestResources" when version.Minor >= 1:
                        ParseManifestResources(instructions, reader, builder, path);
                        break;
                    case "DeduplicatedMethods" when version.Minor >= 2:
                        ParseDeduplicatedMethods(instructions, reader, namesReader, builder, path);
                        break;
                }
            }
        }
        catch (BadImageFormatException ex)
        {
            throw new SizospyException($"MSTAT '{path}' is not a valid managed assembly: {ex.Message}", "invalid-mstat", ex);
        }
        catch (IOException ex)
        {
            throw new SizospyException($"Unable to read MSTAT '{path}': {ex.Message}", "mstat-io", ex);
        }
    }

    private static void ParseTypes(
        IReadOnlyList<IlValue> values,
        MetadataReader reader,
        BlobReader? names,
        ImportBuilder builder,
        string path)
    {
        EnsureMultiple(values, 3, "Types", path);
        for (var i = 0; i < values.Count; i += 3)
        {
            var handle = values[i].AsHandle("Types", path);
            var size = values[i + 1].AsInt("Types", path);
            var ownership = DescribeType(reader, handle);
            var compilerName = ReadName(names, values[i + 2].AsInt("Types", path)) ?? ownership.DisplayName;
            var memberId = AddOwnership(builder, ownership, NodeKind.Type, ownership.DisplayName, null);
            builder.GetOrAddNode(compilerName, ownership.DisplayName, NodeKind.Type, size, memberId, "mstat");
        }
    }

    private static void ParseMethods(
        IReadOnlyList<IlValue> values,
        MetadataReader reader,
        BlobReader? names,
        ImportBuilder builder,
        string path)
    {
        EnsureMultiple(values, 5, "Methods", path);
        for (var i = 0; i < values.Count; i += 5)
        {
            var handle = values[i].AsHandle("Methods", path);
            var codeSize = values[i + 1].AsInt("Methods", path);
            var gcInfoSize = values[i + 2].AsInt("Methods", path);
            var ehInfoSize = values[i + 3].AsInt("Methods", path);
            var ownership = DescribeMember(reader, handle);
            var compilerName = ReadName(names, values[i + 4].AsInt("Methods", path)) ?? ownership.DisplayName;
            var memberId = AddOwnership(builder, ownership, NodeKind.Method, ownership.DisplayName, ownership.MemberName);
            builder.GetOrAddNode(
                compilerName,
                ownership.DisplayName,
                NodeKind.Method,
                checked((long)codeSize + gcInfoSize + ehInfoSize),
                memberId,
                "mstat");
        }
    }

    private static void ParseBlobs(IReadOnlyList<IlValue> values, ImportBuilder builder, string path)
    {
        EnsureMultiple(values, 2, "Blobs", path);
        for (var i = 0; i < values.Count; i += 2)
        {
            var name = values[i].AsString("Blobs", path);
            var size = values[i + 1].AsInt("Blobs", path);
            builder.GetOrAddNode($"mstat:blob:{name}", name, NodeKind.RuntimeArtifact, size, null, "mstat");
        }
    }

    private static void ParseRvaFields(
        IReadOnlyList<IlValue> values,
        MetadataReader reader,
        BlobReader? names,
        ImportBuilder builder,
        string path)
    {
        EnsureMultiple(values, 3, "RvaFields", path);
        for (var i = 0; i < values.Count; i += 3)
        {
            var handle = values[i].AsHandle("RvaFields", path);
            _ = values[i + 1].AsInt("RvaFields", path);
            var ownership = DescribeMember(reader, handle);
            var compilerName = ReadName(names, values[i + 2].AsInt("RvaFields", path)) ?? ownership.DisplayName;
            var memberId = AddOwnership(builder, ownership, NodeKind.Field, ownership.DisplayName, ownership.MemberName);
            // MSTAT 2.x also reports these bytes through Blobs for backward compatibility.
            builder.GetOrAddNode(compilerName, ownership.DisplayName, NodeKind.Field, 0, memberId, "mstat");
        }
    }

    private static void ParseFrozenObjects(
        IReadOnlyList<IlValue> values,
        MetadataReader reader,
        BlobReader? names,
        ImportBuilder builder,
        string path)
    {
        EnsureMultiple(values, 4, "FrozenObjects", path);
        for (var i = 0; i < values.Count; i += 4)
        {
            var instanceType = DescribeType(reader, values[i].AsHandle("FrozenObjects", path));
            _ = values[i + 1].AsInt("FrozenObjects", path);
            var compilerName = ReadName(names, values[i + 2].AsInt("FrozenObjects", path)) ??
                               $"Frozen object: {instanceType.DisplayName}";
            var owner = values[i + 3].Kind == IlValueKind.Handle
                ? DescribeType(reader, values[i + 3].AsHandle("FrozenObjects", path))
                : instanceType;
            var memberId = AddOwnership(builder, owner, NodeKind.Data, compilerName, null);
            builder.GetOrAddNode(compilerName, compilerName, NodeKind.Data, 0, memberId, "mstat");
        }
    }

    private static void ParseManifestResources(
        IReadOnlyList<IlValue> values,
        MetadataReader reader,
        ImportBuilder builder,
        string path)
    {
        EnsureMultiple(values, 3, "ManifestResources", path);
        for (var i = 0; i < values.Count; i += 3)
        {
            var assemblyToken = values[i].AsInt("ManifestResources", path);
            var handle = MetadataTokens.EntityHandle(assemblyToken);
            var assembly = handle.Kind == HandleKind.AssemblyReference
                ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)handle).Name)
                : $"token-0x{assemblyToken:x8}";
            var resource = values[i + 1].AsString("ManifestResources", path);
            _ = values[i + 2].AsInt("ManifestResources", path);
            var displayName = $"{assembly}: {resource}";
            var memberId = builder.GetOrAddLogicalMember(
                $"resource|{assembly}|{resource}",
                displayName,
                NodeKind.Data,
                assembly,
                null,
                null,
                resource);
            builder.GetOrAddNode($"mstat:resource:{assembly}:{resource}", displayName, NodeKind.Data, 0, memberId, "mstat");
        }
    }

    private static void ParseDeduplicatedMethods(
        IReadOnlyList<IlValue> values,
        MetadataReader reader,
        BlobReader? names,
        ImportBuilder builder,
        string path)
    {
        var index = 0;
        while (index < values.Count)
        {
            if (index + 2 > values.Count)
            {
                throw InvalidStream("DeduplicatedMethods", path);
            }

            var original = DescribeMember(reader, values[index++].AsHandle("DeduplicatedMethods", path));
            var count = values[index++].AsInt("DeduplicatedMethods", path);
            if (count < 0 || index + (count * 2) > values.Count)
            {
                throw InvalidStream("DeduplicatedMethods", path);
            }

            var originalId = builder.GetOrAddNode(original.DisplayName, original.DisplayName, NodeKind.Method, 0, null, "mstat");
            for (var j = 0; j < count; j++)
            {
                var target = DescribeMember(reader, values[index++].AsHandle("DeduplicatedMethods", path));
                var targetName = ReadName(names, values[index++].AsInt("DeduplicatedMethods", path)) ?? target.DisplayName;
                var targetId = builder.GetOrAddNode(targetName, target.DisplayName, NodeKind.Method, 0, null, "mstat");
                builder.AddEdge(originalId, targetId, "deduplicated method body", "deduplication", null, "mstat");
            }
        }
    }

    private static List<IlValue> ReadInstructions(BlobReader il, MetadataReader reader)
    {
        var values = new List<IlValue>();
        while (il.RemainingBytes > 0)
        {
            var opcode = il.ReadByte();
            switch (opcode)
            {
                case 0x00:
                case 0x2A:
                    break;
                case 0x15:
                    values.Add(IlValue.FromInt(-1));
                    break;
                case >= 0x16 and <= 0x1E:
                    values.Add(IlValue.FromInt(opcode - 0x16));
                    break;
                case 0x1F:
                    values.Add(IlValue.FromInt(il.ReadSByte()));
                    break;
                case 0x20:
                    values.Add(IlValue.FromInt(il.ReadInt32()));
                    break;
                case 0x72:
                {
                    var handle = MetadataTokens.UserStringHandle(il.ReadInt32() & 0x00FFFFFF);
                    values.Add(IlValue.FromString(reader.GetUserString(handle)));
                    break;
                }
                case 0xD0:
                    values.Add(IlValue.FromHandle(MetadataTokens.EntityHandle(il.ReadInt32())));
                    break;
                default:
                    throw new BadImageFormatException($"Unsupported opcode 0x{opcode:x2} in an MSTAT data stream.");
            }
        }

        return values;
    }

    private static string? ReadName(BlobReader? names, int offset)
    {
        if (names is null || offset < 0 || offset >= names.Value.Length)
        {
            return null;
        }

        var reader = names.Value;
        reader.Offset = offset;
        return reader.ReadSerializedString();
    }

    private static Ownership DescribeMember(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        if (handle.Kind != HandleKind.MemberReference)
        {
            return new Ownership(null, null, null, $"token-0x{MetadataTokens.GetToken(handle):x8}", $"token-0x{MetadataTokens.GetToken(handle):x8}");
        }

        var member = reader.GetMemberReference((MemberReferenceHandle)handle);
        var memberName = reader.GetString(member.Name);
        var type = DescribeType(reader, member.Parent);
        return type with
        {
            MemberName = memberName,
            DisplayName = $"{type.DisplayName}.{memberName}",
        };
    }

    private static Ownership DescribeType(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeSpecification)
        {
            var spec = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
            var display = spec.DecodeSignature(SignatureNameProvider.Instance, reader);
            return ParseDisplayOwnership(display);
        }

        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)handle);
            var name = reader.GetString(type.Name);
            var namespaceName = reader.GetString(type.Namespace);
            var assembly = ResolveAssembly(reader, type.ResolutionScope);
            var display = string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}.{name}";
            return new Ownership(assembly, namespaceName, display, null, display);
        }

        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            var name = reader.GetString(type.Name);
            var namespaceName = reader.GetString(type.Namespace);
            var assembly = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : null;
            var display = string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}.{name}";
            return new Ownership(assembly, namespaceName, display, null, display);
        }

        var token = $"token-0x{MetadataTokens.GetToken(handle):x8}";
        return new Ownership(null, null, token, null, token);
    }

    private static string? ResolveAssembly(MetadataReader reader, EntityHandle scope)
    {
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
            HandleKind.TypeReference => ResolveAssembly(reader, reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope),
            _ => null,
        };
    }

    private static Ownership ParseDisplayOwnership(string display)
    {
        var genericStart = display.IndexOf('<');
        var owner = genericStart >= 0 ? display[..genericStart] : display;
        var lastDot = owner.LastIndexOf('.');
        return lastDot >= 0
            ? new Ownership(null, owner[..lastDot], display, null, display)
            : new Ownership(null, null, display, null, display);
    }

    private static long AddOwnership(
        ImportBuilder builder,
        Ownership ownership,
        NodeKind kind,
        string displayName,
        string? memberName)
    {
        var key = string.Join('|', ownership.Assembly, ownership.Namespace, ownership.TypeName, memberName, kind);
        return builder.GetOrAddLogicalMember(
            key,
            displayName,
            kind,
            ownership.Assembly,
            ownership.Namespace,
            ownership.TypeName,
            memberName);
    }

    private static void EnsureMultiple(IReadOnlyCollection<IlValue> values, int width, string stream, string path)
    {
        if (values.Count % width != 0)
        {
            throw InvalidStream(stream, path);
        }
    }

    private static SizospyException InvalidStream(string stream, string path) =>
        new($"MSTAT '{path}' has a malformed {stream} record stream.", "invalid-mstat");

    private enum IlValueKind { Integer, String, Handle }

    private readonly record struct IlValue(IlValueKind Kind, int Integer, string? String, EntityHandle Handle)
    {
        public static IlValue FromInt(int value) => new(IlValueKind.Integer, value, null, default);
        public static IlValue FromString(string value) => new(IlValueKind.String, 0, value, default);
        public static IlValue FromHandle(EntityHandle value) => new(IlValueKind.Handle, 0, null, value);

        public int AsInt(string stream, string path) =>
            Kind == IlValueKind.Integer ? Integer : throw InvalidStream(stream, path);

        public string AsString(string stream, string path) =>
            Kind == IlValueKind.String ? String! : throw InvalidStream(stream, path);

        public EntityHandle AsHandle(string stream, string path) =>
            Kind == IlValueKind.Handle ? Handle : throw InvalidStream(stream, path);
    }

    private sealed class SignatureNameProvider : ISignatureTypeProvider<string, MetadataReader>
    {
        public static SignatureNameProvider Instance { get; } = new();

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => $"{elementType}&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "methodptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericMethodParameter(MetadataReader genericContext, int index) => $"!!{index}";
        public string GetGenericTypeParameter(MetadataReader genericContext, int index) => $"!{index}";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => $"{elementType}*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => $"{elementType}[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            DescribeType(reader, handle).DisplayName;
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            DescribeType(reader, handle).DisplayName;
        public string GetTypeFromSpecification(MetadataReader reader, MetadataReader genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed record Ownership(
        string? Assembly,
        string? Namespace,
        string? TypeName,
        string? MemberName,
        string DisplayName);
}
