using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace jd.cli.tests;

// A type as metadata names it: the assembly that defines it and its full name (nested types joined by '+', generic ones with their arity,
// as reflection's Type.FullName spells a definition).
internal sealed record TypeName(string Assembly, string FullName);

// A member a method body reaches by a call, newobj, ldftn or field access: the top-level type whose code it is, where it is written (the source
// member, qualified by its type: "jd.cli.ApproveCli+Request..ctor"), and the member reached (its declaring type, its name and, for a method, its
// parameter types).
internal sealed record MemberUse(string Type, string Where, TypeName Target, string TargetMember, IReadOnlyList<string> Parameters);

// A type that a type of the scanned assembly names: in an interface it implements, a field, a method signature or local, a type operand, or as the
// declaring type or in the signature of a member it reaches. Generic arguments count, so Func<IRecordStore> names IRecordStore. Type and Where as
// for a MemberUse.
internal sealed record TypeMention(string Type, string Where, TypeName Named);

// Decodes every method body of an assembly with System.Reflection.Metadata (docs/architecture/application.md section 4). The members of
// compiler-generated types (closures, async state machines) are attributed to the type and the source member they were written in, so a finding
// names code as written.
internal sealed class IlScan
{
    private const string InterfacesMember = "(its interfaces)";

    private IlScan(IReadOnlyList<MemberUse> uses, IReadOnlyList<TypeMention> mentions) => (Uses, Mentions) = (uses, mentions);

    public IReadOnlyList<MemberUse> Uses { get; }

    public IReadOnlyList<TypeMention> Mentions { get; }

    public static IlScan Of(string assemblyPath)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        var reader = pe.GetMetadataReader();
        var names = new Names(reader);
        var uses = new List<MemberUse>();
        var mentions = new List<TypeMention>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var owner = names.TopLevel(handle);
            var typeName = reader.GetString(type.Name);
            var written = names.Written(handle);
            string? Where(string name) => SourceMember(typeName, name) is { } source ? $"{written}.{source}" : null;
            void Mention(string where, Sig sig) => mentions.AddRange(sig.All.Select(named => new TypeMention(owner, where, named)));
            if (Where(InterfacesMember) is { } implementing)
            {
                foreach (var implementation in type.GetInterfaceImplementations())
                {
                    Mention(implementing, names.Decode(reader.GetInterfaceImplementation(implementation).Interface));
                }
            }

            // A closure's fields hold what the member it was written in captured, and that member named each when it got it.
            foreach (var field in type.GetFields().Select(reader.GetFieldDefinition))
            {
                if (Where(reader.GetString(field.Name)) is { } where)
                {
                    Mention(where, field.DecodeSignature(names, null));
                }
            }

            foreach (var method in type.GetMethods().Select(reader.GetMethodDefinition))
            {
                if (Where(reader.GetString(method.Name)) is not { } member)
                {
                    continue; // a closure's constructor: it only stores what its member captured
                }

                var signature = method.DecodeSignature(names, null);
                foreach (var part in signature.ParameterTypes.Prepend(signature.ReturnType))
                {
                    Mention(member, part);
                }

                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                if (!body.LocalSignature.IsNil)
                {
                    foreach (var local in reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null))
                    {
                        Mention(member, local);
                    }
                }

                var il = body.GetILReader();
                while (il.RemainingBytes > 0)
                {
                    var opCode = ReadOpCode(ref il);
                    if (!CarriesToken(opCode))
                    {
                        Skip(ref il, opCode);
                        continue;
                    }

                    var token = MetadataTokens.EntityHandle(il.ReadInt32());
                    if (names.Member(token) is { } reached)
                    {
                        uses.Add(new MemberUse(owner, member, reached.Declaring.Type, reached.Name, reached.Parameters));
                        foreach (var part in reached.Signature.Prepend(reached.Declaring))
                        {
                            Mention(member, part);
                        }
                    }
                    else
                    {
                        Mention(member, names.Decode(token));
                    }
                }
            }
        }

        return new IlScan(uses, mentions);
    }

    // The source member that the member `name` of a type named `typeName` was written in. A member of an ordinary type is itself, or, for a lambda
    // or local function ("<RunAsync>b__0_0", "<RunAsync>g__Local|0_1"), the member it is in. Every member of a state machine ("<RunAsync>d__3",
    // its fields the locals hoisted across an await) was written in RunAsync. A closure ("<>c", "<>c__DisplayClass3_0") names no member: its
    // lambdas name theirs, and its fields and constructor were written in none (null).
    private static string? SourceMember(string typeName, string name) =>
        typeName.StartsWith('<') ? Generated(typeName) ?? (name.StartsWith('<') ? Generated(name) : null) : Generated(name) ?? name;

    // "<Name>…" and "<<Name>…>…" name the source member Name; "<>c" names none.
    private static string? Generated(string name)
    {
        var start = name.Length - name.TrimStart('<').Length;
        var end = name.IndexOf('>', StringComparison.Ordinal);
        return start > 0 && end > start ? name[start..end] : null;
    }

    private static ILOpCode ReadOpCode(ref BlobReader il)
    {
        var first = il.ReadByte();
        return first == 0xFE ? (ILOpCode)(0xFE00 | il.ReadByte()) : (ILOpCode)first;
    }

    // The operations whose operand is a metadata token of a member or a type.
    private static bool CarriesToken(ILOpCode opCode) => opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp or ILOpCode.Ldftn or ILOpCode.Ldvirtftn
        or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld or ILOpCode.Ldtoken
        or ILOpCode.Box or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Newarr or ILOpCode.Initobj or ILOpCode.Ldobj or ILOpCode.Stobj or ILOpCode.Cpobj
        or ILOpCode.Unbox or ILOpCode.Unbox_any or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem or ILOpCode.Mkrefany or ILOpCode.Refanyval
        or ILOpCode.Sizeof or ILOpCode.Constrained;

    private static void Skip(ref BlobReader il, ILOpCode opCode)
    {
        if (opCode == ILOpCode.Switch)
        {
            var targets = il.ReadInt32(); // read first: `Offset +=` would read the offset before the count
            il.Offset += 4 * targets;
            return;
        }

        il.Offset += OperandSize(opCode);
    }

    // The size of the operand of an operation that carries no token and is not a switch.
    internal static int OperandSize(ILOpCode opCode) => opCode.IsBranch() ? opCode.GetBranchOperandSize() : opCode switch
    {
        ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or ILOpCode.Ldc_i4_s or ILOpCode.Unaligned => 1,
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,
        ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4 or ILOpCode.Ldstr or ILOpCode.Calli => 4,
        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,
        _ => 0,
    };

    // The types a signature names: the type itself (an array, reference or pointer is its element) and every type in it, generic arguments included.
    private sealed record Sig(TypeName Type, ImmutableArray<TypeName> All);

    // A member a token names: its declaring type, its name and the types of its signature (a method's return type first, then its parameters).
    private sealed record Reached(Sig Declaring, string Name, IReadOnlyList<Sig> Signature, IReadOnlyList<string> Parameters);

    private sealed class Names(MetadataReader reader) : ISignatureTypeProvider<Sig, object?>
    {
        private static readonly Sig GenericParameter = new(new TypeName(string.Empty, "!"), []);

        private readonly string _assembly = reader.GetString(reader.GetAssemblyDefinition().Name);

        public string TopLevel(TypeDefinitionHandle handle)
        {
            var declaring = reader.GetTypeDefinition(handle).GetDeclaringType();
            return declaring.IsNil ? Of(handle).FullName : TopLevel(declaring);
        }

        // The type the code of a type was written in: the type, or for a compiler-generated one nested in another ("<…"), that one's.
        public string Written(TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            return reader.GetString(type.Name).StartsWith('<') && !type.GetDeclaringType().IsNil ? Written(type.GetDeclaringType()) : Of(handle).FullName;
        }

        // The member a token names, or null when it names a type.
        public Reached? Member(EntityHandle token)
        {
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)token);
                    return Method(Plain(Of(method.GetDeclaringType())), reader.GetString(method.Name), method.DecodeSignature(this, null));
                case HandleKind.FieldDefinition:
                    var field = reader.GetFieldDefinition((FieldDefinitionHandle)token);
                    return new Reached(Plain(Of(field.GetDeclaringType())), reader.GetString(field.Name), [field.DecodeSignature(this, null)], []);
                case HandleKind.MethodSpecification:
                    return Member(reader.GetMethodSpecification((MethodSpecificationHandle)token).Method);
                case HandleKind.MemberReference:
                    var reference = reader.GetMemberReference((MemberReferenceHandle)token);
                    var parent = Decode(reference.Parent);
                    var name = reader.GetString(reference.Name);
                    return reference.GetKind() == MemberReferenceKind.Method
                        ? Method(parent, name, reference.DecodeMethodSignature(this, null))
                        : new Reached(parent, name, [reference.DecodeFieldSignature(this, null)], []);
                default:
                    return null;
            }
        }

        public Sig Decode(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => Plain(Of((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => Plain(Of((TypeReferenceHandle)handle)),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
            _ => GenericParameter,
        };

        public Sig GetPrimitiveType(PrimitiveTypeCode typeCode) => Plain(new TypeName("System.Runtime", "System." + typeCode));

        public Sig GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => Plain(Of(handle));

        public Sig GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) => Plain(Of(handle));

        public Sig GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public Sig GetGenericInstantiation(Sig genericType, ImmutableArray<Sig> typeArguments) => genericType with { All = [.. genericType.All, .. typeArguments.SelectMany(a => a.All)] };

        public Sig GetSZArrayType(Sig elementType) => elementType;

        public Sig GetArrayType(Sig elementType, ArrayShape shape) => elementType;

        public Sig GetByReferenceType(Sig elementType) => elementType;

        public Sig GetPointerType(Sig elementType) => elementType;

        public Sig GetPinnedType(Sig elementType) => elementType;

        public Sig GetModifiedType(Sig modifier, Sig unmodifiedType, bool isRequired) => unmodifiedType;

        public Sig GetGenericMethodParameter(object? genericContext, int index) => GenericParameter;

        public Sig GetGenericTypeParameter(object? genericContext, int index) => GenericParameter;

        public Sig GetFunctionPointerType(MethodSignature<Sig> signature) => GenericParameter;

        private static Sig Plain(TypeName name) => new(name, [name]);

        private static Reached Method(Sig declaring, string name, MethodSignature<Sig> signature) =>
            new(declaring, name, [signature.ReturnType, .. signature.ParameterTypes], signature.ParameterTypes.Select(p => p.Type.FullName).ToList());

        private static string Qualified(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";

        private TypeName Of(TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            return new TypeName(_assembly, declaring.IsNil ? Qualified(reader.GetString(type.Namespace), name) : $"{Of(declaring).FullName}+{name}");
        }

        private TypeName Of(TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            var name = reader.GetString(type.Name);
            var scope = type.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
            {
                var outer = Of((TypeReferenceHandle)scope);
                return outer with { FullName = $"{outer.FullName}+{name}" };
            }

            var assembly = scope.Kind == HandleKind.AssemblyReference ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : _assembly;
            return new TypeName(assembly, Qualified(reader.GetString(type.Namespace), name));
        }
    }
}
