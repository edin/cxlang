# CX

CX is a small experiment in tooling around a C-like language that emits C code.

The compiler embeds a first standard-library definition file at
`Cx.Compiler/Std/Core/*.cx`, currently providing core aliases like `i32`,
`u64`, `usize`, `bool`, plus generic `Indexed<T>`, `KeyValue<K, V>`,
`Option<T>`, `StringView`, `Slice<T>`, `Vec<T>`, `Stack<T>`, `HashSet<T>`,
`HashMap<K, V>`, and `Router<TRequest, TResponse>` definitions. The core files
declare `module std.core`; the exact-match router declares `module std.router`.
`true`, `false`, and `null` are language literals. Booleans lower to `1` and
`0`; `null` lowers to `NULL` and automatically emits `<stddef.h>` when used.

The first milestone keeps the language intentionally close to C while adding a few
syntax conveniences that are easy to parse and easy to lower:

```c
fn add(a: int, b: int) -> int {
    let total: int = a + b;
    return total;
}
```

emits:

```c
int add(int a, int b)
{
    int total = a + b;
    return total;
}
```

## Run

Install the CLI as `cx` for the current Windows user:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-cx.ps1
```

Open a new terminal after install, then use:

```powershell
cx --help
cx run
```

```powershell
dotnet run --project src/Cx.Cli -- transpile examples/hello.cplus
```

Equivalent explicit command:

```powershell
dotnet run --project src/Cx.Cli -- transpile examples/hello.cplus -o build/c/hello.c
```

The CLI also accepts a directory and compiles all `*.cx` and `*.cplus` files under it:

```powershell
dotnet run --project src/Cx.Cli -- transpile examples/module-merge -o build/c/module-merge.c
```

To transpile, compile with `gcc`, and run in one step:

```powershell
dotnet run --project src/Cx.Cli -- run examples/vec-functions.cplus
```

The CLI can also read `cx.toml` from the current directory when the input path
is omitted:

```toml
name = "raytracer"
kind = "exe"
sources = ["examples/raytracer.cplus"]
output = "build/bin/raytracer-config.exe"
c_output = "build/c/raytracer-config.c"
cc = "gcc"
cc_args = ["-lm"]
```

With that config:

```powershell
dotnet run --project src/Cx.Cli -- transpile
dotnet run --project src/Cx.Cli -- build
dotnet run --project src/Cx.Cli -- run
```

`cx test` uses the configured sources and also discovers test files under a
top-level `tests` directory. Build/run commands do not include `tests` unless
you list it explicitly:

```powershell
dotnet run --project src/Cx.Cli -- test
dotnet run --project src/Cx.Cli -- test --std
```

To pass native compiler/linker flags, repeat `--cc-arg`. For the libmicrohttpd
smoke test with vcpkg on Windows:

```powershell
$env:PATH = "D:\vcpkg\installed\x64-windows\bin;$env:PATH"
dotnet run --project src\Cx.Cli -- run examples\microhttpd-hello.cplus --cc-arg "-ID:\vcpkg\installed\x64-windows\include" --cc-arg "-LD:\vcpkg\installed\x64-windows\lib" --cc-arg "-llibmicrohttpd-dll"
```

The nicer experimental `HttpServer` wrapper sample is
`examples\microhttpd-wrapper.cplus`. To keep it running while you try it in a
browser:

```powershell
dotnet run --project src\Cx.Cli -- transpile examples\microhttpd-wrapper.cplus -o build\c\microhttpd-wrapper.c
gcc build\c\microhttpd-wrapper.c -o build\bin\microhttpd-wrapper.exe "-ID:\vcpkg\installed\x64-windows\include" "-LD:\vcpkg\installed\x64-windows\lib" -llibmicrohttpd-dll
$env:PATH = "D:\vcpkg\installed\x64-windows\bin;$env:PATH"
.\build\bin\microhttpd-wrapper.exe
```

Win32/OpenGL rotating cube sample:

```powershell
dotnet run --project src\Cx.Cli -- transpile examples\win32-opengl.cplus -o build\c\win32-opengl.c
gcc build\c\win32-opengl.c -o build\bin\win32-opengl.exe -lopengl32 -lgdi32 -luser32
.\build\bin\win32-opengl.exe
```

If a C compiler is available:

```powershell
gcc build/c/hello.c -o build/bin/hello.exe
.\build\bin\hello.exe
```

## Current Syntax

- Modules: `module app.main;`
- Files with the same module declaration are merged into one declaration set.
- Module imports: `import std.math;` or `import std.math as math;`
- Symbol imports: `from std.math import sqrt;` or `from std.math import sqrt as square_root;`
- C includes: `include <math.h>;` or `include "local.h";`
- C externs: `extern fn sqrt(x: double) -> double;`
- Type aliases: `type String = char*;` and `type IntList = Vec<int>;`
- Function type aliases: `type BinaryOp = fn (int, int) -> int;`
- Enums: `enum ObjectType { SPHERE, PLANE }` emit C `typedef enum`.
- Functions: `fn name(param: type) -> type { ... }`
- Interfaces: `interface Allocator { fn alloc_bytes(size: usize) -> void*; }` emits a C struct with `void* state` and `v_` function pointers.
- Interface calls: `allocator.alloc_bytes(128)` lowers to `allocator.v_alloc_bytes(allocator.state, 128)`.
- Interfaces can also be used as structural constraints: `struct Arena: Allocator { ... }` requires instance methods whose first parameter is `Self*`, and `where A: Allocator` enables zero-cost generic direct dispatch.
- Generic functions and methods with explicit calls: `Vec.with_capacity<float>(2)` and `values.add<float>(1.5)`.
- Forward declarations are emitted automatically for CX functions.
- Static methods: `static fn Vec.empty() -> Vec<int> { ... }`, called as `Vec.empty()` and emitted as regular C functions.
- Struct-body methods: inside `struct Vec { static fn create(...) { ... } fn push(self: Vec*, ...) { ... } }`, the owner type is inferred; spelling `fn Vec.push(...)` inside the struct is also accepted.
- Tagged-union body methods use the same lifting rule, so `union Thing { Sphere: Sphere; static fn sphere(...) -> Thing { ... } }` is valid.
- Variables: `let name: type = expression;`
- Constants: `const name: type = expression;`
- Top-level globals: `let runs: int = 0;` and `const scale: int = 2;` emit C global declarations.
- Fixed arrays: `let values: int[3] = { 1, 2, 3 };`
- Foreach over fixed arrays: `foreach item in values { ... }` yields `Indexed<T>` and uses the declared array length.
- Null pointers: `let next: Node* = null;` (`null` is rejected for obvious non-pointer or arithmetic uses.)
- Generic structs: `struct Slice<T> { data: T*; length: usize; }`
- Struct requirement clauses: `struct Vec<T>: IndexedIterable<T> { ... }` validate required fields/methods at compile time.
- Standard generic containers: `Slice<T>`, `Vec<T>`, `Stack<T>`, `HashSet<T>`, and `HashMap<K, V>` monomorphize to C typedefs like `Slice_u8`, `Vec_int`, and `HashMap_int_float`.
- Generic `Vec<T>` std helpers: `Vec.create<T>`, `Vec.with_capacity<T>`, `push<T>`/`add<T>`, `insert<T>`, `get<T>`, `first<T>`, `last<T>`, `pop<T>`, `swap_remove<T>`, `reserve<T>`, `remove_at<T>`, `as_slice<T>`, `is_empty<T>`, `clear<T>`, and `free<T>`.
- Generic `Stack<T>` std helpers: `Stack.create<T>`, `Stack.with_capacity<T>`, `push<T>`, `pop<T>`, `peek<T>`, `is_empty<T>`, and `free<T>`.
- Generic hash collections: `HashSet<T>` and `HashMap<K, V>` use open addressing and accept caller-supplied `u64` hashes in `add`, `contains`, `remove`, `put`, and `get` until the language grows a proper hashing requirement.
- Iteration wrappers: `Indexed<T>` for index/value loops and `KeyValue<K, V>` for key/value loops.
- Optional values: `Option.some<T>(value)`, `Option.none<T>()`, `is_some<T>`, `is_none<T>`, `as_ptr<T>`, and `unwrap_or<T>`.
- Borrowed strings: `StringView.from_cstr`, `slice`, `equals`, `equals_cstr`, `starts_with`, `find_char`, `parse_int`, and `print`.
- Router: `Router<TRequest, TResponse>` from `std.router` stores fixed routes, supports exact paths and `{param}` segments, passes `RouteContext<TRequest>*` to handlers, and returns `Option<TResponse>`.
- Structural requirements: `requires Iterator<T> { fn next(self: Self*) -> bool; value: T; }`
- Generic parameter requirements: `struct HashSet<T> where T: Hash<T> + Equal<T> { ... }`
- Three-way comparison: `left <=> right` lowers through `Compare<T>` as `compare(left, right)`.
- Structs: `struct Vec2 { x: float; y: float; }`
- Tagged unions: `union Value { Number: int; Position: Point; }`
- Tagged union tags can be referenced as `Value.Number`, and unambiguous payload assignments auto-wrap: `value = 100;`
- Tagged union constructors: `Value.Position(100, 200)` forwards into the payload initializer.
- Tagged union payloads can be read as `value.Number` or `value.Position`.
- Tagged union matching: `match value { Number: n => return n; _ => return 0; }`
- Struct constructors: `Point(100, 200)` emits a field-order compound literal.
- Named struct initializers: `Point { x: 100, y: 200 }` emits a C designated compound literal.
- Named interface initializers: `Allocator { state: arena, v_alloc_bytes: arena_alloc_bytes }` emits a C designated compound literal.
- Methods: `fn Vec2.length(self: Vec2*) -> double { ... }`, emitted as regular C functions.
- `Self` inside owned functions aliases the owner type, so `fn Counter.reset(self: Self*)` is valid.
- Attribute declarations: `attribute json_name on field { name: char*; }`
- Attribute applications: `@derive(Debug)` and `@json_name("id")` are parsed and target-checked for future macro/derive expansion.
- `@derive(Debug)` generates a `debug(self: Type*)` method for non-generic structs; `@debug_skip` fields are omitted.
- Non-capturing lambdas lower to generated functions for function-pointer use: `fn(left: int, right: int) => left <=> right` and `fn(req: HttpRequest*) -> HttpResponse { return HttpResponse.text("ok"); }`.
- C interop type spelling accepts `struct Name*`, `union Name*`, `enum Name`, and `const char*`.
- CLI `run` accepts native compiler options with `--cc-arg`, useful for linking external C libraries.
- Returns: `return expression;`
- Control flow: `if`, `else if`, `else`, `while`, and `for` blocks.
- Switch statements: `switch (value) { case SPHERE: { ... } default: { ... } }`
- Foreach over fixed arrays and `data: T*` + `length: usize` structures yields `Indexed<T>` wrappers with `item.index` and `item.value`.
- Protocol foundation: named `requires` blocks describe structural requirements and are parsed into the compiler model.
- Requirement matching: the compiler can structurally match fields and methods, infer generic requirement parameters, and report requirement failures.
- Plain C-style expressions, calls, field access, pointer operators, and array indexing are passed through.
- Pointer `.field` lowering works for pointer parameters and local pointer variables, so `thing.type` lowers to `thing->type` when `thing` is `Thing*`.

Protocol syntax:

```c
requires IndexedIterable<T> {
    data: T*;
    length: usize;
}

requires Iterable<T, I> {
    fn iterator(self: Self*) -> I;
}

requires Iterator<T> {
    fn next(self: Self*) -> bool;
    value: T;
}

requires Equal<T> {
    fn equals(left: T, right: T) -> bool;
}

requires Hash<T> {
    fn hash(value: T) -> u64;
}

requires Compare<T> {
    fn compare(left: T, right: T) -> int;
}
```

`requires` declarations do not emit C. They are the semantic foundation for
structural checks; `foreach` now uses the matcher for `IndexedIterable<T>` and
still emits a direct indexed loop for `Slice<T>` and `Vec<T>` shapes.
Generic `where` clauses validate concrete generic uses, so `HashSet<T>` can
state that its key type must satisfy `Hash<T> + Equal<T>`. The first std
implementations provide those core requirements for `int`; broader primitive
coverage and requirement-function name mangling are still future work.

## Direction

The solution is split into two assemblies:

- `Cx.Compiler`: reusable compiler library with `Lexer`, `Parser`, `Syntax`, and `Diagnostics` folders.
- `Cx.Cli`: command-line file IO and argument handling.

The lexer follows a matcher-table design like `Rest.Parser`: add keywords in
`KeywordDefinitions`, fixed text tokens in `TokenDefinitions`, or a custom
`ITokenMatcher` implementation for richer token rules.

Good next steps are diagnostics with richer source spans, modules/includes,
structs, and a small formatter or language server surface.
