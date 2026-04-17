# TypedStateBuilder

A Roslyn incremental source generator that produces **compile-time safe builders** using the *type-state pattern*.

It takes a **builder template** and generates the boilerplate needed for a strongly-typed, guided construction API where correctness is enforced by the compiler - not by runtime checks or conventions.

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
* [Optional values and defaults](#optional-values-and-defaults)
* [Validation](#validation)
* [Build methods](#build-methods)
* [Constructors](#constructors)
* [Dependency Injection](#dependency-injection)
* [Performance characteristics](#performance-characteristics)
* [Constraints and limitations](#constraints-and-limitations)
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
    private string _name;

    [StepForValue(nameof(DefaultAge))]
    private int _age;

    private int DefaultAge() => 18;

    private async Task ValidateEmail(string email)
    {
        if (!await _emailService.IsValidAsync(email))
            throw new InvalidOperationException("Invalid email");
    }

    [Build]
    public User Build()
        => new User(_name, _email, _age);
}
```

Usage:

```csharp
var user = TypedStateBuilders
    .CreateUserBuilder(emailService)
    .SetName("Alice")                 // order is flexible
    .SetEmail("alice@example.com")
    .Build();                         // age defaults to 18
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
* **no duplicated steps** (cannot call the same step twice)
* **support for dependency injection** (usable in validation, build logic, or default providers)

---

## What gets generated

For each `[TypedStateBuilder]` class, the generator produces:

* **Typed wrapper** (`TypedMyBuilder<...>`)
* **Fluent step extension methods**
* **Strongly-typed build methods**
* **Factory methods (`CreateMyBuilder`)**
* **Internal accessor layer** (via `UnsafeAccessor`)

You define a builder template, and the generator produces the repetitive type-state boilerplate.

---

## How it works

Each step is encoded as a **type-state**:

```
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

---

## Attributes API

| Attribute           | Target | Purpose             | Parameters                   | Rules                                                            |
|---------------------|--------|---------------------|------------------------------|------------------------------------------------------------------|
| `TypedStateBuilder` | Class  | Enables generation  | None                         | Must be non-nested, non-partial, no inheritance, public/internal |
| `StepForValue`      | Field  | Defines a step      | Optional: `nameof(provider)` | Field must be mutable instance field                             |
| `Build`             | Method | Defines build entry | None                         | Must be instance method                                          |
| `ValidateValue`     | Field  | Adds validation     | `nameof(validator)`          | Must match validator signature                                   |

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
* underlying fields remain mutable, but repeated calls are not expressible

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

---

## Validation

```csharp
[ValidateValue(nameof(ValidateName))]
private string _name;
```

### Rules

* must use `nameof(...)`
* must be declared on the builder
* must accept exactly one parameter (field type)
* must return `void` or `Task`

### Behavior

* runs automatically before build
* runs for **all steps** (including optional/unset)
* exceptions are **aggregated**

```
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
* async return types supported

Available only when required steps are satisfied.

---

## Constructors

Constructors are exposed via factory methods:

```csharp
TypedStateBuilders.CreateUserBuilder(...)
```

### Features

* multiple constructors supported
* parameters preserved (`ref`, `out`, defaults)
* accessibility does not matter

---

## Dependency Injection

Constructor parameters flow directly into the builder:

* store them in non-step fields
* use them in:

  * build logic
  * validation
  * default providers

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

---

## Constraints and limitations

### Builder shape

* must be a **class**
* must be **non-nested**
* must be **non-partial**
* must not use **inheritance**

### Steps

* fields only (no properties)
* must be mutable
* names must not collide after normalization

### Validators

* only `void` or `Task` supported
* executed synchronously

### Behavior constraints

* steps are **single-assignment**
* optional steps are **not marked as set automatically**
* validation runs for all steps

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

### Built-in capabilities

* optional values with defaults
* centralized validation
* exception aggregation

### Clean encapsulation

* works with **private members**
* no need to expose internals

### Reduced boilerplate

* no step interfaces
* no manual validation wiring
* no runtime guards

---

## Summary

TypedStateBuilder generates a **compile-time verified builder API** that combines:

* **flexibility** (steps in any order)
* **safety** (required steps enforced by the compiler)
* **simplicity** (no manual type-state boilerplate)

All while letting you define builders in plain, idiomatic C#.
