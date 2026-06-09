using Cx.Compiler.Diagnostics;
using Cx.Compiler.Lowering;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Tests;

public sealed class GenericLoweringServicesTests
{
    [Fact]
    public void GenericUseCollector_FindsExplicitAndInferredGenericCalls()
    {
        var program = CompilerTestHelpers.Parse(
            """
            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main() -> int {
                let explicitValue: int = identity<int>(10);
                return identity(explicitValue);
            }
            """);
        CompilerTestHelpers.Resolve(program);

        var collector = new GenericUseCollector(program);
        var uses = collector
            .Collect(program)
            .Select(use => $"{use.Function.Name}<{string.Join(",", use.TypeArguments)}>")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("identity<int>", uses);
        Assert.Empty(collector.RawGenericUseAuditEntries);
    }

    [Fact]
    public void RawGenericUseCollector_ReportsOnlyUsesNotAlreadyKnown()
    {
        var program = CompilerTestHelpers.Parse(
            """
            fn identity<T>(value: T) -> T {
                return value;
            }
            """);
        var generic = Assert.Single(program.Functions);
        var knownUses = new HashSet<GenericFunctionUseKey>
        {
            GenericFunctionUseKey.Create(generic, ["int"]),
        };
        var collector = new RawGenericUseCollector([generic]);

        Assert.Empty(collector.Collect("identity<int>(10)", new Dictionary<string, string>(), "raw test", knownUses));
        Assert.Empty(collector.AuditEntries);

        var uses = collector.Collect("identity<float>(1.0)", new Dictionary<string, string>(), "raw test", knownUses).ToList();

        var use = Assert.Single(uses);
        Assert.Equal(generic, use.Function);
        Assert.Equal(["float"], use.TypeArguments);
        var rawUse = Assert.Single(collector.AuditEntries);
        Assert.Equal("raw test", rawUse.Context);
        Assert.Equal("identity", rawUse.FunctionName);
        Assert.Equal(["float"], rawUse.TypeArguments);
        Assert.Equal("explicit type argument call", rawUse.Reason);
    }

    [Fact]
    public void GenericUseCollector_UsesCallResolverForNormalGenericCalls()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;

                static fn make(value: T) -> Box<T> {
                    return Box<T> { value: value };
                }

                fn replace(value: T) -> bool {
                    self.value = value;
                    return true;
                }
            }

            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main() -> int {
                let box: Box<int> = Box<int>.make(10);
                box.replace(20);
                return identity(box.value);
            }
            """);
        CompilerTestHelpers.Resolve(program);

        var uses = new GenericUseCollector(program)
            .Collect(program)
            .Select(use => $"{(use.Function.OwnerTypeNode is null ? "" : use.Function.OwnerTypeNode.ToTypeName() + ".")}{use.Function.Name}<{string.Join(",", use.TypeArguments)}>")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Box.make<int>", uses);
        Assert.Contains("Box.replace<int>", uses);
        Assert.Contains("identity<int>", uses);
    }

    [Fact]
    public void GenericUseCollector_UsesResolvedAdapterExposedCalls()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type u8 = unsigned char;
            type usize = unsigned long long;

            struct MiniVec<T> {
                static fn with_capacity(capacity: usize) -> MiniVec<T> {
                    return MiniVec<T> {};
                }

                fn add(value: T) -> bool {
                    return true;
                }
            }

            type MiniByteBuffer using MiniVec<u8> {
                expose static with_capacity -> Self;
                expose add as write_u8;
            }

            type MiniStringBuilder using MiniByteBuffer {
                expose static with_capacity -> Self;
                expose write_u8;
            }

            fn main() -> int {
                let builder: MiniStringBuilder = MiniStringBuilder.with_capacity(8);
                builder.write_u8(65);
                return 0;
            }
            """);
        var diagnostics = new DiagnosticBag();
        program = TypeAdapterLoweringPass.Apply(program, diagnostics);
        CompilerTestHelpers.AssertNoErrors(diagnostics);
        CompilerTestHelpers.Resolve(program);

        var uses = new GenericUseCollector(program)
            .Collect(program)
            .Select(use => $"{use.Function.OwnerTypeNode?.ToTypeName()}.{use.Function.Name}<{string.Join(",", use.TypeArguments)}>")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MiniVec.with_capacity<u8>", uses);
        Assert.Contains("MiniVec.add<u8>", uses);
    }

    [Fact]
    public void GenericUseCollector_UsesCallResolverForAdapterSelfApiCalls()
    {
        var program = CompilerTestHelpers.Parse(
            """
            type u8 = unsigned char;

            struct MiniVec<T> {
                fn add(value: T) -> bool {
                    return true;
                }
            }

            type MiniByteBuffer using MiniVec<u8> {
                expose add as write_u8;
            }

            type MiniStringBuilder using MiniByteBuffer {
                expose write_u8;

                fn append_byte(value: u8) -> bool {
                    return self.write_u8(value);
                }
            }

            fn main() -> int {
                let builder: MiniStringBuilder = MiniStringBuilder {};
                return builder.append_byte((u8)65) ? 0 : 1;
            }
            """);
        var diagnostics = new DiagnosticBag();
        program = TypeAdapterLoweringPass.Apply(program, diagnostics);
        CompilerTestHelpers.AssertNoErrors(diagnostics);
        CompilerTestHelpers.Resolve(program);

        var uses = new GenericUseCollector(program)
            .Collect(program)
            .Select(use => $"{use.Function.OwnerTypeNode?.ToTypeName()}.{use.Function.Name}<{string.Join(",", use.TypeArguments)}>")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MiniVec.add<u8>", uses);
    }

    [Fact]
    public void GenericTypeRewriter_RewritesNestedConcreteStructTypes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn use_box(value: Box<Box<int>>*) -> Box<int> {
                return value.value;
            }
            """);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);
        var concreteStructNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Box_int",
            "Box_Box_int",
        };

        var rewritten = GenericTypeRewriter.Rewrite(program, concreteStructNames);
        var function = Assert.Single(rewritten.Functions);

        Assert.Equal("Box_int", function.ReturnTypeNode.ToTypeName());
        Assert.Equal("Box_Box_int*", Assert.Single(function.Parameters).TypeNode.ToTypeName());
        var resolvedParameter = Assert.IsType<Cx.Compiler.Semantic.TypeRef.Pointer>(Assert.Single(function.Parameters).TypeNode?.Semantic.Type);
        Assert.Equal("Box_Box_int", Assert.IsType<Cx.Compiler.Semantic.TypeRef.Named>(resolvedParameter.Element).Name);
    }

    [Fact]
    public void GenericFunctionSpecializer_RewritesTypeNodesBesideCompatibilityStrings()
    {
        var program = CompilerTestHelpers.Parse(
            """
            fn identity<T>(value: T) -> T {
                let local: T = value;
                return local;
            }
            """);
        var generic = Assert.Single(program.Functions);

        var specialized = GenericFunctionSpecializer.Specialize(generic, ["int"]);
        var parameter = Assert.Single(specialized.Parameters);
        var local = Assert.IsType<LetStatement>(specialized.Body[0]);

        Assert.Equal("int", specialized.ReturnTypeNode.ToTypeName());
        Assert.Equal("int", parameter.TypeNode.ToTypeName());
        Assert.Equal("int", local.TypeNode?.TypeName);
    }

    [Fact]
    public void GenericFunctionSpecializer_RewritesSemanticTypeRefsOnTypeNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn identity<T>(value: Box<T>*) -> Box<T>* {
                let local: Box<T>* = value;
                return local;
            }
            """);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);
        var generic = program.Functions.Single();

        var specialized = GenericFunctionSpecializer.Specialize(generic, ["int"]);
        var parameter = Assert.Single(specialized.Parameters);
        var local = Assert.IsType<LetStatement>(specialized.Body[0]);

        Assert.Equal("Box<int>*", parameter.TypeNode.ToTypeName());
        Assert.Equal("Box<int>*", TypeRefFormatter.ToCxString(parameter.TypeNode!.Semantic.Type!));
        Assert.Equal("Box<int>*", local.TypeNode?.TypeName);
        Assert.Equal("Box<int>*", TypeRefFormatter.ToCxString(local.TypeNode!.Semantic.Type!));
    }

    [Fact]
    public void GenericTypeRewriter_RewritesExpressionTypeNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main(value: void*) -> int {
                let casted: Box<int>* = (Box<int>*)value;
                let bytes: usize = sizeof(Box<int>);
                let box: Box<int> = Box<int> { value: 10 };
                let same = identity<Box<int>>(box);
                let map = fn(value: Box<int>) -> Box<int> => value;
                return 0;
            }
            """);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var rewritten = GenericTypeRewriter.Rewrite(program, new HashSet<string>(StringComparer.Ordinal) { "Box_int" });
        var body = rewritten.Functions.Single(function => function.Name == "main").Body;
        var cast = Assert.IsType<CastExpressionNode>(Assert.IsType<LetStatement>(body[0]).Initializer);
        var sizeOf = Assert.IsType<SizeOfExpressionNode>(Assert.IsType<LetStatement>(body[1]).Initializer);
        var initializer = Assert.IsType<InitializerExpressionNode>(Assert.IsType<LetStatement>(body[2]).Initializer);
        var genericCall = Assert.IsType<GenericCallExpressionNode>(Assert.IsType<LetStatement>(body[3]).Initializer);
        var functionExpression = Assert.IsType<FunctionExpressionNode>(Assert.IsType<LetStatement>(body[4]).Initializer);

        Assert.Equal("Box_int*", cast.TargetTypeNode?.TypeName);
        Assert.Equal("Box_int", sizeOf.TypeOperandNode?.TypeName);
        Assert.Equal("Box_int", initializer.TypeNameNode?.TypeName);
        Assert.Equal(["Box_int"], genericCall.TypeArgumentNodes.Select(node => node.TypeName).ToList());
        Assert.Equal("Box_int", functionExpression.ReturnTypeNode?.TypeName);
        Assert.Equal("Box_int", Assert.Single(functionExpression.Parameters).TypeNode.ToTypeName());
    }

    [Fact]
    public void GenericTypeRewriter_DoesNotShareSemanticInfoWithOriginalNodes()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            fn use_box(value: Box<int>) -> Box<int> {
                return value;
            }
            """);
        var original = Assert.Single(program.Functions);
        original.Semantic.ModuleName = "app.main";

        var rewritten = GenericTypeRewriter.Rewrite(program, new HashSet<string>(StringComparer.Ordinal) { "Box_int" });
        var rewrittenFunction = Assert.Single(rewritten.Functions);

        Assert.Equal("app.main", rewrittenFunction.Semantic.ModuleName);
        Assert.NotSame(original.Semantic, rewrittenFunction.Semantic);

        rewrittenFunction.Semantic.ModuleName = "rewritten";
        Assert.Equal("app.main", original.Semantic.ModuleName);
    }

    [Fact]
    public void GenericCallRetargeter_RepointsResolvedCallsToSpecializedFunction()
    {
        var program = CompilerTestHelpers.Parse(
            """
            fn identity<T>(value: T) -> T {
                return value;
            }

            fn main() -> int {
                return identity<int>(10);
            }
            """);
        CompilerTestHelpers.Resolve(program);
        var generic = program.Functions.Single(function => function.Name == "identity");
        var specialized = GenericFunctionSpecializer.Specialize(generic, ["int"]);
        var specializations = new Dictionary<string, FunctionNode>(StringComparer.Ordinal)
        {
            ["identity<int>"] = specialized,
        };

        GenericCallRetargeter.Retarget(program, specializations);

        var main = program.Functions.Single(function => function.Name == "main");
        var ret = Assert.IsType<ReturnStatement>(Assert.Single(main.Body));
        var call = Assert.IsType<GenericCallExpressionNode>(ret.Expression);
        Assert.Same(specialized, call.Semantic.ResolvedCall?.Function);
        Assert.Same(specialized.Semantic.Symbol, call.Semantic.Symbol);
    }

    [Fact]
    public void GenericStructSpecializer_CreatesConcreteStructFromTypeUsage()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
            }

            struct Holder {
                box: Box<int>;
            }
            """);

        var structs = GenericStructSpecializer.Specialize(program, []);

        var box = Assert.Single(structs);
        var field = Assert.Single(box.Fields);
        Assert.Equal("Box_int", box.Name);
        Assert.Equal("int", field.TypeNode?.TypeName);
    }

    [Fact]
    public void GenericStructSpecializer_RewritesTypeNodesAndSemanticTypeRefs()
    {
        var program = CompilerTestHelpers.Parse(
            """
            struct Box<T> {
                value: T;
                next: Box<T>*;
            }

            struct Holder {
                box: Box<int>;
            }
            """);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);

        var structs = GenericStructSpecializer.Specialize(program, []);
        var box = Assert.Single(structs);
        var value = box.Fields.Single(field => field.Name == "value");
        var next = box.Fields.Single(field => field.Name == "next");

        Assert.Equal("int", value.TypeNode?.TypeName);
        Assert.Equal("int", TypeRefFormatter.ToCxString(value.TypeNode!.Semantic.Type!));
        Assert.Equal("Box<int>*", next.TypeNode?.TypeName);
        Assert.Equal("Box<int>*", TypeRefFormatter.ToCxString(next.TypeNode!.Semantic.Type!));
    }
}
