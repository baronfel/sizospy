using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Sizospy.Core.Tests;

internal static class MstatFixtureFactory
{
    public static void Write(string path)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString("fixture.mstat"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString("fixture.mstat"),
            new Version(2, 2),
            default,
            default,
            (AssemblyFlags)0,
            AssemblyHashAlgorithm.None);
        var assemblyReference = metadata.AddAssemblyReference(
            metadata.GetOrAddString("Sample"),
            new Version(1, 0),
            default,
            default,
            (AssemblyFlags)0,
            default);
        var typeReference = metadata.AddTypeReference(
            assemblyReference,
            metadata.GetOrAddString("Sample"),
            metadata.GetOrAddString("SampleType"));

        var signature = new BlobBuilder();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: false)
            .Parameters(0, returnType => returnType.Void(), parameters => { });
        var signatureHandle = metadata.GetOrAddBlob(signature);
        var foldedMethod = metadata.AddMemberReference(
            typeReference,
            metadata.GetOrAddString("Folded"),
            signatureHandle);
        var canonicalMethod = metadata.AddMemberReference(
            typeReference,
            metadata.GetOrAddString("Canonical"),
            signatureHandle);

        var methodBodies = new BlobBuilder();
        var methodBodyEncoder = new MethodBodyStreamEncoder(methodBodies);

        var blobCode = new BlobBuilder();
        var blobInstructions = new InstructionEncoder(blobCode);
        blobInstructions.LoadString(metadata.GetOrAddUserString("fixture blob"));
        blobInstructions.LoadConstantI4(42);
        blobInstructions.OpCode(ILOpCode.Ret);
        var blobBodyOffset = methodBodyEncoder.AddMethodBody(blobInstructions);

        var resourceCode = new BlobBuilder();
        var resourceInstructions = new InstructionEncoder(resourceCode);
        resourceInstructions.LoadConstantI4(MetadataTokens.GetToken(assemblyReference));
        resourceInstructions.LoadString(metadata.GetOrAddUserString("fixture.resources"));
        resourceInstructions.LoadConstantI4(42);
        resourceInstructions.OpCode(ILOpCode.Ret);
        var resourceBodyOffset = methodBodyEncoder.AddMethodBody(resourceInstructions);

        var deduplicationCode = new BlobBuilder();
        var deduplicationInstructions = new InstructionEncoder(deduplicationCode);
        deduplicationInstructions.OpCode(ILOpCode.Ldtoken);
        deduplicationInstructions.Token(foldedMethod);
        deduplicationInstructions.LoadConstantI4(1);
        deduplicationInstructions.OpCode(ILOpCode.Ldtoken);
        deduplicationInstructions.Token(canonicalMethod);
        deduplicationInstructions.LoadConstantI4(0);
        deduplicationInstructions.OpCode(ILOpCode.Ret);
        var deduplicationBodyOffset = methodBodyEncoder.AddMethodBody(deduplicationInstructions);

        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Blobs"),
            signatureHandle,
            blobBodyOffset,
            MetadataTokens.ParameterHandle(1));
        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("ManifestResources"),
            signatureHandle,
            resourceBodyOffset,
            MetadataTokens.ParameterHandle(1));
        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("DeduplicatedMethods"),
            signatureHandle,
            deduplicationBodyOffset,
            MetadataTokens.ParameterHandle(1));

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(
                imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            methodBodies,
            mappedFieldData: new BlobBuilder(),
            managedResources: new BlobBuilder(),
            strongNameSignatureSize: 0,
            entryPoint: default,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: content => BlobContentId.FromHash(
                System.Security.Cryptography.SHA256.HashData(content.SelectMany(static b => b.GetBytes()).ToArray())));
        var image = new BlobBuilder();
        pe.Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
    }
}
