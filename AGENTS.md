# AGENTS.md

## Project Type

This repository uses:

- .NET File-Based Apps
- Single-file C# CLI applications
- Lightweight automation scripts
- Minimal project structure

Primary goal:

> Fast, readable, portable CLI tooling.

---

# Core Principles

Priority order:

1. Readability
2. Simplicity
3. CLI user experience
4. Cross-platform compatibility
5. Performance

This repository is NOT intended for:

- Enterprise layered architecture
- ASP.NET applications
- Microservice platforms
- Heavy dependency injection systems

---

# Mandatory Rules

## MUST

- Use .NET File-Based Apps
- Prefer single-file implementations
- Keep startup fast
- Prefer async APIs
- Keep dependencies minimal
- Support Windows/Linux/macOS
- Optimize for CLI workflows

## MUST NOT

- Generate `.sln`
- Generate complex `.csproj`
- Introduce ASP.NET Host
- Introduce heavy DI frameworks
- Introduce unnecessary abstractions
- Over-engineer simple scripts

---

# Runtime Requirements

Recommended SDK:

- .NET 10+

Run scripts using:

```bash
dotnet run app.cs -- args
```

Or:

```bash
dotnet app.cs -- args
```

---

# Linux/macOS Executable Scripts

Use shebang:

```csharp
#!/usr/bin/env dotnet run
```

Grant execute permission:

```bash
chmod +x app.cs
```

Run directly:

```bash
./app.cs
```

---

# File-Based Apps Directives

## NuGet Packages

```csharp
#:package Spectre.Console@*
```

## Include Files

```csharp
#:include ./shared.cs
```

## Project Reference

```csharp
#:project ../Shared/Shared.csproj
```

---

# Coding Style

## Prefer

- Top-level statements
- File-scoped namespace
- `var`
- Small focused functions
- Explicit naming
- Early returns
- Async IO

Example:

```csharp
var json = await client.GetStringAsync(url);
```

---

## Avoid

- Deep abstraction layers
- Massive inheritance trees
- Large static utility classes
- Mutable global state
- Excessive LINQ allocations
- Builder-pattern abuse

---

# CLI Design Rules

All CLI tools SHOULD support:

| Argument | Description |
|---|---|
| --help | Show help |
| --version | Show version |
| --verbose | Verbose logging |

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | General Error |
| 2 | Invalid Arguments |

---

# Console Output

Preferred libraries:

- Spectre.Console
- System.Console

Recommended colors:

| Type | Color |
|---|---|
| Success | Green |
| Warning | Yellow |
| Error | Red |
| Info | Blue |

Example:

```csharp
AnsiConsole.MarkupLine("[green]Done[/]");
```

---

# Logging

Preferred:

- ZLogger
- ILogger
- Spectre.Console

Requirements:

- Errors MUST go to stderr
- Long-running tasks SHOULD show progress
- Avoid console spam

---

# Progress UI

Long-running operations SHOULD display progress indicators.

Example:

```csharp
await AnsiConsole.Progress()
    .StartAsync(async ctx =>
    {
        var task = ctx.AddTask("Processing");

        while (!task.IsFinished)
        {
            await Task.Delay(100);
            task.Increment(1);
        }
    });
```

---

# Error Handling

Always fail explicitly.

Example:

```csharp
try
{
    await RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.Exit(1);
}
```

---

# Recommended Packages

## CLI UI

```csharp
#:package Spectre.Console@*
```

## Logging

```csharp
#:package ZLogger@*
```

## HTTP

```csharp
#:package Flurl.Http@*
```

## Command Execution

```csharp
#:package CliWrap@3.8.1
```

---

# Performance Guidelines

Prefer:

- async/await
- ArrayPool
- Span<T>
- Memory<T>

Avoid:

- Frequent string concatenation
- Blocking IO
- Large temporary allocations

---

# Cross Platform Rules

Scripts MUST work on:

- Windows
- Linux
- macOS

Avoid:

- Hardcoded Windows paths
- cmd.exe-specific logic
- PowerShell-only implementations

Prefer:

```csharp
Path.Combine(...)
```

---

# AI Agent Rules

AI agents modifying this repository MUST:

- Preserve File-Based App structure
- Preserve CLI-first design
- Keep implementations simple
- Avoid unnecessary frameworks
- Avoid unnecessary abstractions
- Avoid splitting tiny scripts into many files

When possible:

- Prefer direct implementations
- Prefer readability over architecture purity

---

# Example Template

```csharp
#!/usr/bin/env dotnet run

#:package Spectre.Console@*

using Spectre.Console;

try
{
    AnsiConsole.MarkupLine("[green]CLI Started[/]");
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex);
    Environment.Exit(1);
}
```

---

# Documentation Requirements

Every CLI tool SHOULD document:

- Purpose
- Arguments
- Examples
- Exit codes

---

# Repository Philosophy

This repository values:

- Fast iteration
- Small scripts
- Low maintenance cost
- Excellent CLI experience
- Cross-platform execution

Keep things practical.

