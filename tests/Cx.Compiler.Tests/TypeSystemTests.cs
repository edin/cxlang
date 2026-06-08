using Cx.Compiler.Diagnostics;
using Cx.Compiler.Semantic;

namespace Cx.Compiler.Tests;

public sealed class TypeSystemTests
{
    [Fact]
    public void ResolveDefinition_ReturnsConcreteGenericDefinitionView()
    {
        var program = ResolveTypes(
            """
            type IntVec = Vec<int>;

            struct Vec<T> {
                data: T*;
            }
            """);
        var typeSystem = new TypeSystem(program);

        var resolved = typeSystem.ResolveDefinition("IntVec");

        var symbol = Assert.IsType<TypeSymbol.Struct>(resolved.Symbol);
        Assert.Equal("Vec", symbol.Name);
        Assert.Equal("Vec<int>", resolved.DisplayName);
        Assert.Equal("int", TypeRefFormatter.ToCxString(Assert.Single(resolved.Substitutions.Values)));
    }

    [Fact]
    public void GetFieldsAndMethods_ReturnsSubstitutedMembers()
    {
        var program = ResolveTypes(
            """
            struct Vec<T> {
                data: T*;
                length: usize;
            }

            extension Vec<T> {
                fn add(value: T) -> bool {
                    return true;
                }
            }
            """);
        var typeSystem = new TypeSystem(program);

        var fields = typeSystem.GetFields("Vec<int>");
        var methods = typeSystem.GetMethods("Vec<int>");

        Assert.Equal("int*", TypeRefFormatter.ToCxString(fields.Single(field => field.Name == "data").Type));
        var add = Assert.Single(methods, method => method.Name == "add");
        Assert.Equal("int", TypeRefFormatter.ToCxString(add.ParameterTypes.Last()));
    }

    [Fact]
    public void SatisfiesRequirement_UsesRequirementMatcherThroughFacade()
    {
        var program = ResolveTypes(
            """
            requires Contiguous<T> {
                data: T*;
                length: usize;
            }

            struct Vec<T> {
                data: T*;
                length: usize;
            }
            """);
        var typeSystem = new TypeSystem(program);

        var match = typeSystem.SatisfiesRequirement(
            typeSystem.Parse("Vec<int>"),
            "Contiguous",
            [typeSystem.Parse("int")]);

        Assert.True(match.Success, string.Join(Environment.NewLine, match.Failures));
        Assert.Equal("Vec<int>", match.TypeBindings["Self"]);
        Assert.Equal("int", match.TypeBindings["T"]);
    }

    [Fact]
    public void TryResolveForeachTypes_ResolvesFixedArrayElementType()
    {
        var typeSystem = new TypeSystem(ResolveTypes(""));

        var success = typeSystem.TryResolveForeachTypes("int[4]", keyValue: false, out var valueType, out var keyType);

        Assert.True(success);
        Assert.Equal("int", valueType);
        Assert.Null(keyType);
    }

    [Fact]
    public void TryResolveForeachTypes_ResolvesIterableElementType()
    {
        var program = ResolveTypes(
            """
            requires Iterable<T, I>
            where I: Iterator<T> {
                fn iterator(self: Self*) -> I;
            }

            requires Iterator<T> {
                fn next(self: Self*) -> bool;
                fn value(self: Self*) -> T;
            }

            struct IntIterator: Iterator<int> {
                value: int;
            }

            extension IntIterator {
                fn next() -> bool {
                    return false;
                }

                fn value() -> int {
                    return self.value;
                }
            }

            struct IntList: Iterable<int, IntIterator> {
                length: usize;
            }

            extension IntList {
                fn iterator() -> IntIterator {
                    let iterator: IntIterator;
                    return iterator;
                }
            }
            """);
        var typeSystem = new TypeSystem(program);

        var success = typeSystem.TryResolveForeachTypes("IntList", keyValue: false, out var valueType, out var keyType);

        Assert.True(success);
        Assert.Equal("int", valueType);
        Assert.Null(keyType);
    }

    [Fact]
    public void TryResolveForeachTypes_ResolvesKeyValueIterableTypes()
    {
        var program = ResolveTypes(
            """
            requires KeyValueIterable<K, V, I>
            where I: KeyValueIterator<K, V> {
                fn iterator(self: Self*) -> I;
            }

            requires KeyValueIterator<K, V> {
                fn next(self: Self*) -> bool;
                fn key(self: Self*) -> K;
                fn value(self: Self*) -> V;
            }

            struct EntryIterator: KeyValueIterator<int, double> {
                value: double;
            }

            extension EntryIterator {
                fn next() -> bool {
                    return false;
                }

                fn key() -> int {
                    return 1;
                }

                fn value() -> double {
                    return self.value;
                }
            }

            struct Entries: KeyValueIterable<int, double, EntryIterator> {
                length: usize;
            }

            extension Entries {
                fn iterator() -> EntryIterator {
                    let iterator: EntryIterator;
                    return iterator;
                }
            }
            """);
        var typeSystem = new TypeSystem(program);

        var success = typeSystem.TryResolveForeachTypes("Entries", keyValue: true, out var valueType, out var keyType);

        Assert.True(success);
        Assert.Equal("int", keyType);
        Assert.Equal("double", valueType);
    }

    [Fact]
    public void FindMethod_ReturnsSubstitutedInstanceMethod()
    {
        var program = ResolveTypes(
            """
            struct Box<T> {
                value: T;
            }

            extension Box<T> {
                fn get() -> T {
                    return self.value;
                }
            }
            """);
        var typeSystem = new TypeSystem(program);

        var method = typeSystem.FindMethod("Box<int>", "get", isStatic: false, argumentCount: 0);

        Assert.NotNull(method);
        Assert.Equal("int", TypeRefFormatter.ToCxString(method.ReturnType));
    }

    [Fact]
    public void FindMethod_ReturnsSubstitutedStaticMethod()
    {
        var program = ResolveTypes(
            """
            struct Box<T> {
                value: T;
            }

            extension Box<T> {
                static fn create(value: T) -> Box<T> {
                    return Box<T> { value: value };
                }
            }
            """);
        var typeSystem = new TypeSystem(program);

        var method = typeSystem.FindMethod("Box<int>", "create", isStatic: true, argumentCount: 1);

        Assert.NotNull(method);
        Assert.Equal("Box<int>", TypeRefFormatter.ToCxString(method.ReturnType));
        Assert.Equal("int", TypeRefFormatter.ToCxString(Assert.Single(method.ParameterTypes)));
    }

    [Fact]
    public void FindMethod_ReturnsAdapterExposedInstanceMethod()
    {
        var program = ResolveTypes(
            """
            struct Vec<T> {
                value: T;
            }

            extension Vec<T> {
                fn add(value: T) -> bool {
                    self.value = value;
                    return true;
                }
            }

            type Stack<T> using Vec<T> {
                expose add as push;
            }
            """);
        var typeSystem = new TypeSystem(program);

        var method = typeSystem.FindMethod("Stack<int>", "push", isStatic: false, argumentCount: 1);

        Assert.NotNull(method);
        Assert.Equal("bool", TypeRefFormatter.ToCxString(method.ReturnType));
        Assert.Equal("Stack<int>*", TypeRefFormatter.ToCxString(method.ParameterTypes[0]));
        Assert.Equal("int", TypeRefFormatter.ToCxString(method.ParameterTypes[1]));
        Assert.Equal(ResolvedMethodKind.Exposed, method.Kind);
        var target = Assert.IsType<ResolvedMethodTarget.Exposed>(method.Target);
        Assert.Equal("Stack", target.Adapter.Name);
        Assert.Equal("push", target.Expose.ExposedName);
        Assert.Equal("add", target.InnerMethod.Name);
        Assert.IsType<ResolvedMethodTarget.Direct>(target.InnerMethod.Target);
    }

    [Fact]
    public void FindMethod_ReturnsChainedAdapterExposedTarget()
    {
        var program = ResolveTypes(
            """
            struct Vec<T> {
                value: T;
            }

            extension Vec<T> {
                fn add(value: T) -> bool {
                    self.value = value;
                    return true;
                }
            }

            type ByteBuffer using Vec<u8> {
                expose add as write_u8;
            }

            type StringBuilder using ByteBuffer {
                expose write_u8;
            }
            """);
        var typeSystem = new TypeSystem(program);

        var method = typeSystem.FindMethod("StringBuilder", "write_u8", isStatic: false, argumentCount: 1);

        Assert.NotNull(method);
        Assert.Equal("bool", TypeRefFormatter.ToCxString(method.ReturnType));
        var stringBuilderTarget = Assert.IsType<ResolvedMethodTarget.Exposed>(method.Target);
        Assert.Equal("StringBuilder", stringBuilderTarget.Adapter.Name);
        var byteBufferMethod = stringBuilderTarget.InnerMethod;
        Assert.Equal("write_u8", byteBufferMethod.Name);
        var byteBufferTarget = Assert.IsType<ResolvedMethodTarget.Exposed>(byteBufferMethod.Target);
        Assert.Equal("ByteBuffer", byteBufferTarget.Adapter.Name);
        Assert.Equal("add", byteBufferTarget.InnerMethod.Name);
        Assert.Equal("u8", TypeRefFormatter.ToCxString(byteBufferTarget.InnerMethod.ParameterTypes[1]));
        Assert.IsType<ResolvedMethodTarget.Direct>(byteBufferTarget.InnerMethod.Target);
        Assert.Equal("add", method.DirectMethod.Name);
        Assert.Equal("Vec<u8>", TypeRefFormatter.ToCxString(method.DirectMethod.OwnerType));
    }

    [Fact]
    public void FindMethod_ReturnsAdapterExposedStaticMethodWithSelfReturn()
    {
        var program = ResolveTypes(
            """
            struct Vec<T> {
                value: T;
            }

            extension Vec<T> {
                static fn with_value(value: T) -> Vec<T> {
                    return Vec<T> { value: value };
                }
            }

            type Stack<T> using Vec<T> {
                expose static with_value -> Self;
            }
            """);
        var typeSystem = new TypeSystem(program);

        var method = typeSystem.FindMethod("Stack<int>", "with_value", isStatic: true, argumentCount: 1);

        Assert.NotNull(method);
        Assert.Equal("Stack<int>", TypeRefFormatter.ToCxString(method.ReturnType));
        Assert.Equal("int", TypeRefFormatter.ToCxString(Assert.Single(method.ParameterTypes)));
    }

    [Fact]
    public void GetMethods_FiltersConstrainedExtensions()
    {
        var program = ResolveTypes(
            """
            requires Disposable<T> {
                fn free(self: Self*) -> void;
            }

            struct File: Disposable<File> {
                handle: void*;
            }

            extension File {
                fn free() -> void {
                }
            }

            struct Plain {
                value: int;
            }

            struct Option<T> {
                has_value: bool;
                value: T;
            }

            extension Option<T>
            where T: Disposable<T> {
                fn free() -> void {
                }
            }
            """);
        var typeSystem = new TypeSystem(program);

        Assert.NotNull(typeSystem.FindMethod("Option<File>", "free", isStatic: false, argumentCount: 0));
        Assert.Null(typeSystem.FindMethod("Option<Plain>", "free", isStatic: false, argumentCount: 0));
    }

    private static Cx.Compiler.Syntax.Nodes.ProgramNode ResolveTypes(string source)
    {
        var program = CompilerTestHelpers.Parse(source);
        var diagnostics = new DiagnosticBag();
        new TypeResolutionPass(diagnostics).Resolve(program);
        CompilerTestHelpers.AssertNoErrors(diagnostics);
        return program;
    }
}
