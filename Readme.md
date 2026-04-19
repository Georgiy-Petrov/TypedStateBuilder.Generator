# TypedStateBuilder

A Roslyn incremental source generator that produces **compile-time safe builders** using the *type-state pattern*.

It takes a **builder template** and generates the boilerplate needed for a strongly-typed, guided construction API where correctness is enforced by the compiler - not by runtime checks or conventions.

[![NuGet](https://img.shields.io/nuget/v/TypedStateBuilder.Generator.svg?logo=nuget)](https://www.nuget.org/packages/TypedStateBuilder.Generator)

---

## Table of Contents

* [Why this exists](#why-this-exists)
* [What this solves](#what-this-solves)
* [Comparison](#comparison)
* [Example](#example)
* [What gets generated](#what-gets-generated)
* [How it works](#how-it-works)
* [Attributes API](#attributes-api)
* [Defining steps](#defining-steps)
* [Step overloads](#step-overloads)
* [Optional values and defaults](#optional-values-and-defaults)
* [Validation](#validation)
* [Build methods](#build-methods)
* [Constructors](#constructors)
* [Dependency Injection](#dependency-injection)
* [Performance characteristics](#performance-characteristics)
* [Constraints and limitations](#constraints-and-limitations)
* [Diagnostics overview](#diagnostics-overview)
* [Why use it](#why-use-it)
* [Summary](#summary)

---

## Why this exists

Traditional builders in C# rely on:

* runtime validation
* defensive programming
* developer discipline

This often leads to:

* missing required values
* duplicated or conflicting assignments
* scattered validation logic
* bugs discovered only at runtime

---

## What this solves

TypedStateBuilder shifts structural correctness to **compile time**, while keeping flexibility:

* `Build()` is only available when all required values are set
* required steps can be executed **in any order**
* each step can only be called **once (in the typed API)**
* optional values can be defaulted automatically
* validation is centralized and always executed
* one logical step can expose multiple input shapes via overloads

Result: **invalid builder usage becomes unrepresentable code**, without sacrificing fluent API flexibility.

> Note: value correctness is still enforced at runtime via validation.

---

## Comparison

| Feature                  | Simple Builder | Interface Step Builder | TypedStateBuilder |
|--------------------------|----------------|------------------------|-------------------|
| Compile-time safety      | ❌              | ✅                      | ✅                 |
| Ensures required steps   | ❌              | ✅                      | ✅                 |
| Prevents duplicate steps | ❌              | ✅                      | ✅                 |
| Flexible ordering        | ✅              | ❌                      | ✅                 |
| Boilerplate required     | Low            | High                   | Low               |
| Runtime overhead         | Low            | Medium                 | Low               |
| Default values           | Manual         | Manual                 | Built-in          |
| Validation               | Manual         | Manual                 | Built-in          |
| Step overloads           | Manual         | Manual                 | Built-in          |
| IDE experience           | High           | Medium                 | High              |

---

## Example

Define a builder template:

```csharp
[TypedStateBuilder]
public class UserBuilder
{
    private readonly IEmailService _emailService;

    public UserBuilder(IEmailService emailService)
    {
        _emailService = emailService;
    }

    [StepForValue]
    [ValidateValue(nameof(ValidateEmail))]
    private string _email;

    [StepForValue]
    [StepOverload(nameof(FullNameToName))]
    private string _name;

    [StepForValue(nameof(DefaultAge))]
    private int _age;

    private int DefaultAge() => 18;

    private string FullNameToName(string firstName, string lastName)
        => $"{firstName} {lastName}";

    private async Task ValidateEmail(string email)
    {
        if (!await _emailService.IsValidAsync(email))
            throw new InvalidOperationException("Invalid email");
    }

    [Build]
    public User Build()
        => new User(_name, _email, _age);
}
````

Usage:

```csharp
var user = TypedStateBuilders
    .CreateUserBuilder(emailService)
    .SetName("Alice", "Walker")       // overload-generated step
    .SetEmail("alice@example.com")
    .Build();                         // age defaults to 18
```

The direct step method still exists too:

```csharp
var user = TypedStateBuilders
    .CreateUserBuilder(emailService)
    .SetName("Alice Walker")
    .SetEmail("alice@example.com")
    .Build();
```

Invalid usage is caught at compile time:

```csharp
var invalid = TypedStateBuilders
    .CreateUserBuilder(emailService)
    .SetName("Alice")
    .Build(); // ❌ compile-time error (email not set)
```

### Key takeaway

You get:

* **compile-time guarantees** (required steps must be set)
* **flexible ordering** (no enforced sequence)
* **no duplicated steps** (cannot call the same logical step twice)
* **multiple input forms for a step** via `StepOverload`
* **support for dependency injection** (usable in validation, build logic, default providers, or overload methods)

---

## What gets generated

For each `[TypedStateBuilder]` class, the generator produces:

* **Typed wrapper** (`TypedMyBuilder<...>`)
* **Fluent step extension methods**
* **Fluent step overload extension methods**
* **Strongly-typed build methods**
* **Factory methods (`CreateMyBuilder`)**
* **Internal accessor layer** (via `UnsafeAccessor`)

You define a builder template, and the generator produces the repetitive type-state boilerplate.

---

## How it works

Each step is encoded as a **type-state**:

```text
ValueUnset → ValueSet
```

The wrapper carries one state per step as generic parameters.

Example progression:

```csharp
TypedBuilder<ValueUnset, ValueUnset, ValueUnset>
    → SetName  → TypedBuilder<ValueSet, ValueUnset, ValueUnset>
    → SetEmail → TypedBuilder<ValueSet, ValueSet, ValueUnset>
```

`Build()` becomes available only when all required states are `ValueSet`.

Step overloads reuse the same state transition. For example, both of these set the same logical step:

```csharp
builder.SetName("Alice Walker");
builder.SetName("Alice", "Walker");
```

Both transition the same step from `ValueUnset` to `ValueSet`.

---

## Attributes API

| Attribute           | Target | Purpose                   | Parameters                   | Rules                                                            |
|---------------------|--------|---------------------------|------------------------------|------------------------------------------------------------------|
| `TypedStateBuilder` | Class  | Enables generation        | None                         | Must be non-nested, non-partial, no inheritance, public/internal |
| `StepForValue`      | Field  | Defines a step            | Optional: `nameof(provider)` | Field must be mutable instance field                             |
| `StepOverload`      | Field  | Adds step input overloads | `nameof(method)`             | Field must already be a step                                     |
| `Build`             | Method | Defines build entry       | None                         | Must be instance method                                          |
| `ValidateValue`     | Field  | Adds validation           | `nameof(validator)`          | Must match validator signature                                   |

---

## Defining steps

```csharp
[StepForValue]
private string _name;
```

### Rules

* must be a **field**
* must be **instance**
* must not be `static`
* must not be `readonly`

Each step generates:

```csharp
builder.SetName(value)
```

### Important behavior

* each step can be called **only once** in the typed API
* enforced by the **type system**, not runtime checks
* underlying fields remain mutable, but repeated calls are not expressible through generated step methods

---

## Step overloads

`StepOverload` lets one step expose additional generated extension methods.

```csharp
[StepForValue]
[StepOverload(nameof(CreateName))]
private string _name;

private string CreateName(string first, string last)
    => $"{first} {last}";
```

This generates an additional overload of the step method:

```csharp
builder.SetName(string value)
builder.SetName(string first, string last)
```

### Behavior

When an overload-generated step method is called:

1. the referenced builder method is invoked
2. its return value becomes the field value
3. the original step is applied internally
4. the step state changes from `ValueUnset` to `ValueSet`

So this:

```csharp
builder.SetName("Alice", "Walker")
```

behaves like:

```csharp
var value = CreateName("Alice", "Walker");
builder.SetName(value);
```

### Rules

A `StepOverload` target method:

* must use `nameof(...)`
* must be declared on the same builder class
* must be a method
* must be **non-generic**
* must return the **same type as the target field**
* may be instance or static
* may have any parameter list supported by the generator parameter model

### Multiple overloads

You can add multiple overload methods to the same step:

```csharp
[StepForValue]
[StepOverload(nameof(CreateFromFullName))]
[StepOverload(nameof(CreateFromParts))]
private string _name;
```

### Important restrictions

Generated step overloads must not collide by signature.

Examples of invalid configurations:

* two overload methods that would both generate `SetName(string value)`
* a direct step plus an overload method that would generate the same parameter signature
* multiple parameterless overload methods for the same step

This is invalid:

```csharp
[StepForValue]
[StepOverload(nameof(CreateDefaultA))]
[StepOverload(nameof(CreateDefaultB))]
private string _name;

private string CreateDefaultA() => "A";
private string CreateDefaultB() => "B";
```

because both would generate:

```csharp
builder.SetName()
```

Only **one parameterless `StepOverload`** is allowed per step.

---

## Optional values and defaults

```csharp
[StepForValue(nameof(DefaultAge))]
private int _age;
```

### Behavior

* default values are assigned during `Create...`
* the step becomes **optional**
* optional steps can be skipped before calling `Build()`

**Important details**

* the step is still `ValueUnset` in the type-state system
* you can still call the step explicitly afterward
* optionality affects **build availability**, not initial state
* default providers run eagerly during builder creation

### Rules

A default provider:

* must use `nameof(...)`
* must be declared on the builder
* must be parameterless
* must be non-generic
* must return the exact field type

---

## Validation

```csharp
[ValidateValue(nameof(ValidateName))]
private string _name;
```

### Rules

* must use `nameof(...)`
* must be declared on the builder
* must accept exactly one parameter of the field type
* must be non-generic
* must return `void` or `Task`

### Behavior

* runs automatically before build
* runs for **all steps**
* exceptions are **aggregated**

```csharp
throw new AggregateException(...)
```

### Execution details

* async validators are executed synchronously
* internally, `GetAwaiter().GetResult()` is used
* this introduces **blocking behavior**

---

## Build methods

```csharp
[Build]
public User Build() => ...
```

### Features

* multiple build methods supported
* parameters preserved
* generic methods supported
* return type preserved

Available only when required steps are satisfied.

### Rules

A build method:

* must be marked with `[Build]`
* must be an **instance method**
* may be generic
* may have parameters and optional parameter defaults

---

## Constructors

Constructors are exposed via factory methods:

```csharp
TypedStateBuilders.CreateUserBuilder(...)
```

### Features

* multiple constructors supported
* parameters preserved (`ref`, `out`, `in`, defaults)
* constructor signatures are surfaced as generated `Create...` methods

---

## Dependency Injection

Constructor parameters flow directly into the builder:

* store them in non-step fields
* use them in:

  * build logic
  * validation
  * default providers
  * step overload methods

---

## Performance characteristics

* incremental generator (fast IDE experience)
* no reflection
* minimal runtime overhead
* wrapper allocation per step transition
* direct access via `UnsafeAccessor`

### Notes

* validation may allocate when exceptions occur
* async validation introduces blocking due to `GetAwaiter().GetResult()`
* step overloads add no extra runtime abstraction beyond the generated forwarding call

---

## Constraints and limitations

### Builder shape

* must be a **class**
* must be **non-nested**
* must be **non-partial**
* must not use **inheritance**
* must be **public** or **internal**

### Steps

* fields only (no properties)
* must be mutable
* names must not collide after normalization

### Step overloads

* only valid on fields that are already steps
* overload target methods must be non-generic
* overload target methods must return the exact field type
* generated overload signatures must not collide
* only one parameterless overload is allowed per step

### Validators

* only `void` or `Task` supported
* executed synchronously

### Behavior constraints

* steps are **single-assignment** in the generated typed API
* optional steps are **not marked as set automatically**
* validation runs before build
* wrappers share the same underlying mutable builder instance

---

## Diagnostics overview

The generator reports diagnostics when builder definitions violate its rules.

Current diagnostic IDs:

| ID       | Meaning                                                 |
|----------|---------------------------------------------------------|
| `TSB001` | Invalid builder shape                                   |
| `TSB002` | Static step field not supported                         |
| `TSB003` | Readonly step field not supported                       |
| `TSB005` | Invalid build method                                    |
| `TSB006` | Invalid step default provider syntax                    |
| `TSB007` | Invalid step default provider member                    |
| `TSB008` | Duplicate generated step method                         |
| `TSB009` | Builder has no steps                                    |
| `TSB010` | Builder has no build methods                            |
| `TSB011` | Invalid validator syntax                                |
| `TSB012` | Invalid validator member                                |
| `TSB013` | Invalid step overload syntax                            |
| `TSB014` | Invalid step overload member                            |
| `TSB015` | Duplicate generated step overload method                |
| `TSB016` | Multiple parameterless step overloads are not supported |

---

## Why use it

### Strong compile-time guarantees

* cannot call `Build()` prematurely
* cannot forget required steps
* cannot call steps multiple times

### Flexible API design

* steps in **any order**
* no rigid step chaining
* supports generics and multiple build paths
* supports multiple generated input forms for the same logical step

### Built-in capabilities

* optional values with defaults
* centralized validation
* exception aggregation
* overload-based value construction for steps

### Clean encapsulation

* works with **private members**
* no need to expose internals

### Reduced boilerplate

* no step interfaces
* no manual validation wiring
* no manual forwarding overload methods in the fluent API
* no runtime guards for structural correctness

---

## Summary

TypedStateBuilder generates a **compile-time verified builder API** that combines:

* **flexibility** (steps in any order)
* **safety** (required steps enforced by the compiler)
* **simplicity** (no manual type-state boilerplate)

It supports:

* required and optional steps
* validation
* multiple build methods
* constructor-based creation
* step overload generation

All while letting you define builders in plain, idiomatic C#.