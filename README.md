# CX

CX is an experimental C-like language and CLI prototype that transpiles to
readable C.

It is not production-ready. The syntax, compiler internals, standard library,
and tooling are still changing. The project is useful today for experiments,
small native programs, C interop ideas, and exploring what a friendlier
C-facing language could feel like.

Website: [cxlang.dev](https://cxlang.dev)

Source: [github.com/edin/cxlang](https://github.com/edin/cxlang)

## What Works

- Transpile CX source to readable C.
- Build and run native programs through a local C compiler.
- Functions, structs, methods, extensions, and static methods.
- Generics, type wrappers, and constrained extensions.
- Tagged unions with `match`.
- Interfaces lowered to C-friendly function-pointer tables.
- Structural requirements and generic `where` clauses.
- `cx test` with embedded stdlib tests and project test discovery.
- A small standard library with containers, strings, files, allocators, JSON, and bitmap helpers.

See the full feature catalog: [cxlang.dev/features](https://cxlang.dev/features)

See current project status: [cxlang.dev/status](https://cxlang.dev/status)

## Tiny Example

```cx
import c.stdio;

fn main() -> int {
    let values = Vec<int>.create();
    values.add(10);
    values.add(20);
    values.add(30);

    foreach value in values {
        printf("Value %d\n", value);
    }

    values.free();
    return 0;
}
```

Generated C is meant to stay ordinary and readable. CX features lower into C
shapes like functions, structs, enums, tagged records, function pointers, and
explicit allocator calls.

## Install

Install the CLI as `cx` for the current Windows user:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-cx.ps1
```

Open a new terminal after install:

```powershell
cx --help
```

You can also run the CLI directly from the repo:

```powershell
dotnet run --project src/Cx.Cli -- --help
```

## Quick Start

Create and run a new project:

```powershell
cx new hello-cx
cd hello-cx
cx run
cx test
```

Run examples from this repository:

```powershell
dotnet run --project src/Cx.Cli -- run examples/allocator-collections.cx
dotnet run --project src/Cx.Cli -- run examples/tagged-union.cx
dotnet run --project src/Cx.Cli -- run examples/interfaces.cx
```

Run the configured raytracer sample from `cx.toml`:

```powershell
dotnet run --project src/Cx.Cli -- run
```

Run embedded standard-library tests:

```powershell
dotnet run --project src/Cx.Cli -- test --std
```

## Project Config

The CLI reads `cx.toml` when no input path is provided:

```toml
name = "raytracer"
kind = "exe"
sources = ["examples/raytracer.cx"]
output = "build/bin/raytracer.exe"
c_output = "build/c/raytracer.c"
cc = "gcc"
cc_args = ["-O2"]
```

Then:

```powershell
cx build
cx run
cx test
```

`cx test` uses configured sources and also discovers test files under a top-level
`tests` directory. Build/run commands do not include `tests` unless listed
explicitly.

## Standard Library

Current useful pieces include:

- `Vec<T>`
- `StringView`
- `StringBuilder`
- `HashMap<K, V>`
- `HashSet<T>`
- `Option<T>`
- `Result<T, E>`
- `File`
- `Path`
- `Bitmap`
- `Allocator`
- `JsonWriter`

The standard library is early and intentionally small. APIs may change.

## Repository Layout

```text
src/Cx.Compiler/          compiler library
src/Cx.Cli/               command-line tool
src/Cx.Compiler/Std/      embedded CX standard library files
examples/                 CX example programs
tests/                    .NET compiler tests
site/                     Astro website for cxlang.dev
scripts/install-cx.ps1    local CLI install script
```

## Development

Build and test:

```powershell
dotnet build Cx.sln
dotnet test Cx.sln
dotnet run --project src/Cx.Cli -- test --std
```

Build the website:

```powershell
cd site
npm install
npm run build
```

## Notes

CX is an experiment. It is moving quickly, and the current codebase is still
closer to a serious prototype than a stable language distribution.

Contributions, experiments, examples, bug reports, and design feedback are very
welcome.
