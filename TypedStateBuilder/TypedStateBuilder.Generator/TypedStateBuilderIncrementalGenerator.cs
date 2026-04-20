using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TypedStateBuilder.Generator.EquatableCollections;

namespace TypedStateBuilder.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class TypedStateBuilderIncrementalGenerator : IIncrementalGenerator
{
    private const string QualifiedIValueState = "global::TypedStateBuilder.IValueState";
    private const string QualifiedValueSet = "global::TypedStateBuilder.ValueSet";
    private const string QualifiedValueUnset = "global::TypedStateBuilder.ValueUnset";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource(
                "TypedStateBuilder.Attributes.g.cs",
                SourceText.From(AttributeSources, Encoding.UTF8));
        });

        var builderCandidates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "TypedStateBuilder.TypedStateBuilderAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => AnalyzeBuilderCandidate(ctx, ct))
            .Where(static x => x.Model is not null || x.Diagnostics.Count > 0);

        context.RegisterSourceOutput(builderCandidates, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (result.Model is null)
            {
                return;
            }

            var hintNameBase = SanitizeHintName(result.Model.Value.BuilderFullyQualifiedName);

            spc.AddSource(
                $"{hintNameBase}.TypedStateBuilder.g.cs",
                SourceText.From(GenerateBuilderSource(result.Model.Value), Encoding.UTF8));
        });
    }

    private static BuilderAnalysisResult AnalyzeBuilderCandidate(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Diagnostic>();

        if (context.TargetNode is not ClassDeclarationSyntax classSyntax ||
            context.TargetSymbol is not INamedTypeSymbol builder)
        {
            return new BuilderAnalysisResult(null, diagnostics.ToEquatableArray());
        }

        var symbols = ResolveKnownSymbols(context.SemanticModel.Compilation);
        var model = AnalyzeBuilder(
            builder,
            classSyntax,
            context.SemanticModel.Compilation,
            symbols,
            diagnostics,
            cancellationToken);

        return new BuilderAnalysisResult(model, diagnostics.ToEquatableArray());
    }

    private static BuilderModel? AnalyzeBuilder(
        INamedTypeSymbol builder,
        ClassDeclarationSyntax targetClassSyntax,
        Compilation compilation,
        KnownSymbols symbols,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (builder.TypeKind != TypeKind.Class)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidBuilderShape,
                builder.Locations.FirstOrDefault(),
                builder.Name,
                "builder must be a class"));
            return null;
        }

        if (builder.ContainingType is not null)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidBuilderShape,
                builder.Locations.FirstOrDefault(),
                builder.Name,
                "builder must be non-nested"));
            return null;
        }

        if (targetClassSyntax.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidBuilderShape,
                targetClassSyntax.Identifier.GetLocation(),
                builder.Name,
                "builder must be non-partial"));
            return null;
        }

        if (builder.BaseType is not null && builder.BaseType.SpecialType != SpecialType.System_Object)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidBuilderShape,
                builder.Locations.FirstOrDefault(),
                builder.Name,
                "inheritance is not supported"));
            return null;
        }

        var generatedAccessibility = builder.DeclaredAccessibility switch
        {
            Accessibility.Public => GeneratedAccessibility.Public,
            Accessibility.Internal => GeneratedAccessibility.Internal,
            _ => GeneratedAccessibility.Invalid
        };

        if (generatedAccessibility == GeneratedAccessibility.Invalid)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidBuilderShape,
                builder.Locations.FirstOrDefault(),
                builder.Name,
                "builder must be public or internal"));
            return null;
        }

        var typeParameters = CreateTypeParameterModels(builder.TypeParameters);
        var typeParameterConstraints = BuildTypeParameterConstraints(typeParameters);

        var buildMethods = GetBuildMethods(builder, symbols.BuildAttribute, diagnostics);
        if (buildMethods.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.NoBuildMethods,
                builder.Locations.FirstOrDefault(),
                builder.Name));
            return null;
        }

        var stepFields = new List<StepModel>();
        var usedStateNames =
            new HashSet<string>(builder.TypeParameters.Select(static tp => tp.Name), StringComparer.Ordinal);
        var stepMethodNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in builder.GetMembers().OfType<IFieldSymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, builder))
            {
                continue;
            }

            var stepAttribute = GetStepAttribute(member, symbols);
            var branchAttribute = GetStepBranchAttribute(member, symbols);

            if (branchAttribute is not null && stepAttribute is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.StepBranchRequiresStepForValue,
                    member.Locations.FirstOrDefault(),
                    member.Name,
                    builder.Name));
                continue;
            }

            if (stepAttribute is null)
            {
                continue;
            }

            if (member.IsStatic)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.StaticStepField,
                    member.Locations.FirstOrDefault(),
                    member.Name,
                    builder.Name));
                continue;
            }

            if (member.IsReadOnly)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.ReadonlyStepField,
                    member.Locations.FirstOrDefault(),
                    member.Name,
                    builder.Name));
                continue;
            }

            var stepName = GetStepMethodName(member.Name);
            if (!stepMethodNames.Add(stepName))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DuplicateStepMethod,
                    member.Locations.FirstOrDefault(),
                    member.Name,
                    builder.Name,
                    stepName));
                continue;
            }

            var stateName = GetUniqueStateParameterName(member.Name, usedStateNames);
            usedStateNames.Add(stateName);

            string? branchPath = null;
            if (branchAttribute is not null)
            {
                branchPath = ReadBranchPath(member, branchAttribute, diagnostics);
            }

            var defaultModel = ReadDefaultModel(
                compilation,
                member,
                stepAttribute,
                diagnostics,
                cancellationToken);

            var validators = ReadValidators(
                compilation,
                member,
                GetValidationAttributes(member, symbols),
                symbols,
                diagnostics,
                cancellationToken);

            var overloads = ReadStepOverloads(
                compilation,
                member,
                GetStepOverloadAttributes(member, symbols),
                diagnostics,
                cancellationToken);

            ValidateStepMethodSignatureCollisions(
                member,
                builder,
                stepName,
                member.Type,
                overloads,
                diagnostics);

            stepFields.Add(new StepModel(
                FieldName: member.Name,
                FieldTypeName: member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StateName: stateName,
                MethodName: stepName,
                BranchPath: branchPath,
                Default: defaultModel,
                IsRequired: defaultModel is null,
                Validators: validators,
                Overloads: overloads));
        }

        if (stepFields.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.NoStepFields,
                builder.Locations.FirstOrDefault(),
                builder.Name));
            return null;
        }

        var hasBranchedSteps = stepFields.Any(static s => s.BranchPath is not null);

        if (hasBranchedSteps)
        {
            foreach (var buildMethod in buildMethods)
            {
                if (string.IsNullOrWhiteSpace(buildMethod.TargetBranchPath))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.BranchedBuilderRequiresBuildTarget,
                        builder.Locations.FirstOrDefault(),
                        buildMethod.Name,
                        builder.Name));
                }
            }
        }

        var constructors = GetConstructors(builder).ToEquatableArray();

        foreach (var buildMethod in buildMethods)
        {
            if (buildMethod.IsStatic)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidBuildMethod,
                    builder.Locations.FirstOrDefault(),
                    buildMethod.Name,
                    builder.Name,
                    "[Build] methods must be instance methods"));
            }
        }

        if (diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var builderNamespace = builder.ContainingNamespace.IsGlobalNamespace
            ? null
            : builder.ContainingNamespace.ToDisplayString();

        var wrapperName = "Typed" + builder.Name;
        var accessorClassName = builder.Name + "_Accessors";
        var extensionClassName = "Typed" + builder.Name + "Extensions";
        var wrapperQualifiedName = builderNamespace is null
            ? $"global::{wrapperName}"
            : $"global::{builderNamespace}.{wrapperName}";
        var accessorQualifiedName = builderNamespace is null
            ? $"global::{accessorClassName}"
            : $"global::{builderNamespace}.{accessorClassName}";

        return new BuilderModel(
            BuilderName: builder.Name,
            BuilderFullyQualifiedName: builder.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: builderNamespace,
            GeneratedAccessibility: generatedAccessibility,
            WrapperName: wrapperName,
            FullyQualifiedWrapperName: wrapperQualifiedName,
            ExtensionClassName: extensionClassName,
            CreateClassName: "TypedStateBuilders",
            AccessorClassName: accessorClassName,
            FullyQualifiedAccessorClassName: accessorQualifiedName,
            CreateMethodName: "Create" + builder.Name,
            Constructors: constructors,
            Steps: stepFields.ToEquatableArray(),
            BuildMethods: buildMethods,
            TypeParameters: typeParameters,
            TypeParameterConstraints: typeParameterConstraints);
    }

    private static KnownSymbols ResolveKnownSymbols(Compilation compilation)
    {
        return new KnownSymbols(
            BuildAttribute: compilation.GetTypeByMetadataName("TypedStateBuilder.BuildAttribute"),
            StepForValueAttribute: compilation.GetTypeByMetadataName("TypedStateBuilder.StepForValueAttribute"),
            StepOverloadAttribute: compilation.GetTypeByMetadataName("TypedStateBuilder.StepOverloadAttribute"),
            StepBranchAttribute: compilation.GetTypeByMetadataName("TypedStateBuilder.StepBranchAttribute"),
            ValidateValueAttribute: compilation.GetTypeByMetadataName("TypedStateBuilder.ValidateValueAttribute"),
            TaskType: compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"));
    }

    private static AttributeData? GetStepAttribute(IFieldSymbol field, KnownSymbols symbols)
    {
        foreach (var attribute in field.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (symbols.StepForValueAttribute is not null &&
                SymbolEqualityComparer.Default.Equals(attributeClass, symbols.StepForValueAttribute))
            {
                return attribute;
            }
        }

        return null;
    }

    private static AttributeData? GetStepBranchAttribute(IFieldSymbol field, KnownSymbols symbols)
    {
        foreach (var attribute in field.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (symbols.StepBranchAttribute is not null &&
                SymbolEqualityComparer.Default.Equals(attributeClass, symbols.StepBranchAttribute))
            {
                return attribute;
            }
        }

        return null;
    }

    private static IEnumerable<AttributeData> GetValidationAttributes(IFieldSymbol field, KnownSymbols symbols)
    {
        foreach (var attribute in field.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (symbols.ValidateValueAttribute is not null &&
                SymbolEqualityComparer.Default.Equals(attributeClass, symbols.ValidateValueAttribute))
            {
                yield return attribute;
            }
        }
    }

    private static IEnumerable<AttributeData> GetStepOverloadAttributes(IFieldSymbol field, KnownSymbols symbols)
    {
        foreach (var attribute in field.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (symbols.StepOverloadAttribute is not null &&
                SymbolEqualityComparer.Default.Equals(attributeClass, symbols.StepOverloadAttribute))
            {
                yield return attribute;
            }
        }
    }

    private static EquatableArray<BuildMethodModel> GetBuildMethods(
        INamedTypeSymbol builder,
        INamedTypeSymbol? buildAttributeSymbol,
        List<Diagnostic> diagnostics)
    {
        var methods = new List<BuildMethodModel>();

        foreach (var methodSymbol in builder.GetMembers().OfType<IMethodSymbol>())
        {
            if (methodSymbol.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, builder))
            {
                continue;
            }

            foreach (var attribute in methodSymbol.GetAttributes())
            {
                if (attribute.AttributeClass is null ||
                    buildAttributeSymbol is null ||
                    !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, buildAttributeSymbol))
                {
                    continue;
                }

                string? targetBranchPath = null;

                if (attribute.ConstructorArguments.Length == 0)
                {
                    targetBranchPath = null;
                }
                else if (attribute.ConstructorArguments.Length == 1 &&
                         attribute.ConstructorArguments[0].Value is string rawTarget)
                {
                    if (!TryNormalizeBranchPath(rawTarget, out targetBranchPath, out var error))
                    {
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.InvalidBuildBranchTarget,
                            methodSymbol.Locations.FirstOrDefault(),
                            methodSymbol.Name,
                            builder.Name,
                            error));
                        continue;
                    }
                }
                else
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.InvalidBuildBranchTarget,
                        methodSymbol.Locations.FirstOrDefault(),
                        methodSymbol.Name,
                        builder.Name,
                        "build target must be omitted or be a single non-empty string path"));
                    continue;
                }

                methods.Add(new BuildMethodModel(
                    Name: methodSymbol.Name,
                    ReturnTypeName: methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsStatic: methodSymbol.IsStatic,
                    IsGenericMethod: methodSymbol.IsGenericMethod,
                    TargetBranchPath: targetBranchPath,
                    TypeParameters: CreateTypeParameterModels(methodSymbol.TypeParameters),
                    Parameters: methodSymbol.Parameters.Select(CreateParameterModel).ToEquatableArray()));
            }
        }

        return methods
            .Distinct()
            .ToEquatableArray();
    }

    private static IEnumerable<ConstructorModel> GetConstructors(INamedTypeSymbol builder)
    {
        foreach (var ctor in builder.InstanceConstructors
                     .Where(static c => !c.IsImplicitlyDeclared || c.Parameters.Length == 0)
                     .OrderBy(static c => c.Parameters.Length))
        {
            var parameters = ctor.Parameters
                .Select(CreateParameterModel)
                .ToEquatableArray();

            yield return new ConstructorModel(
                Parameters: parameters,
                SignatureKey: CreateConstructorSignatureKey(ctor),
                DisplayName: ctor.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                AccessorSuffix: GetAccessorConstructorSuffix(parameters));
        }
    }

    private static ParameterModel CreateParameterModel(IParameterSymbol parameter)
    {
        return new ParameterModel(
            Name: parameter.Name,
            TypeName: parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            RefKind: parameter.RefKind switch
            {
                RefKind.Ref => ParameterRefKind.Ref,
                RefKind.Out => ParameterRefKind.Out,
                RefKind.In => ParameterRefKind.In,
                _ => ParameterRefKind.None
            },
            HasExplicitDefaultValue: parameter.HasExplicitDefaultValue,
            DefaultValueExpression: parameter.HasExplicitDefaultValue
                ? GetOptionalParameterDefault(parameter)
                : null);
    }

    private static EquatableArray<TypeParameterModel> CreateTypeParameterModels(
        ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        return typeParameters
            .Select(static tp => new TypeParameterModel(
                Name: tp.Name,
                Constraints: BuildSingleTypeParameterConstraintParts(tp)))
            .ToEquatableArray();
    }

    private static EquatableArray<string> BuildSingleTypeParameterConstraintParts(ITypeParameterSymbol typeParameter)
    {
        var parts = new List<string>();

        if (typeParameter.HasUnmanagedTypeConstraint)
        {
            parts.Add("unmanaged");
        }
        else if (typeParameter.HasValueTypeConstraint)
        {
            parts.Add("struct");
        }
        else if (typeParameter.HasReferenceTypeConstraint)
        {
            parts.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                ? "class?"
                : "class");
        }
        else if (typeParameter.HasNotNullConstraint)
        {
            parts.Add("notnull");
        }

        var baseTypeConstraints = new List<string>();
        var interfaceConstraints = new List<string>();

        foreach (var constraintType in typeParameter.ConstraintTypes)
        {
            var display = constraintType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (constraintType.TypeKind == TypeKind.Interface)
            {
                interfaceConstraints.Add(display);
            }
            else
            {
                baseTypeConstraints.Add(display);
            }
        }

        parts.AddRange(baseTypeConstraints);
        parts.AddRange(interfaceConstraints);

        if (typeParameter.HasConstructorConstraint)
        {
            parts.Add("new()");
        }

        return parts.ToEquatableArray();
    }

    private static EquatableArray<string> BuildTypeParameterConstraints(
        EquatableArray<TypeParameterModel> typeParameters)
    {
        return typeParameters
            .Where(static tp => tp.Constraints.Count > 0)
            .Select(static tp => $"where {tp.Name} : {string.Join(", ", tp.Constraints)}")
            .ToEquatableArray();
    }

    private static string? ReadBranchPath(
        IFieldSymbol field,
        AttributeData attribute,
        List<Diagnostic> diagnostics)
    {
        if (attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is not string rawPath)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepBranchPath,
                field.Locations.FirstOrDefault(),
                field.Name,
                field.ContainingType.Name,
                "branch path must be a single non-empty string"));
            return null;
        }

        if (!TryNormalizeBranchPath(rawPath, out var branchPath, out var error))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepBranchPath,
                field.Locations.FirstOrDefault(),
                field.Name,
                field.ContainingType.Name,
                error));
            return null;
        }

        return branchPath;
    }

    private static DefaultModel? ReadDefaultModel(
        Compilation compilation,
        IFieldSymbol field,
        AttributeData attribute,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (attribute.ConstructorArguments.Length == 0)
        {
            return null;
        }

        if (attribute.ConstructorArguments.Length != 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                field.ContainingType.Name,
                string.Empty,
                "provider form must specify exactly one nameof(...) argument"));
            return null;
        }

        if (!TryGetSingleNameofArgumentSyntax(attribute, out var nameofArgumentExpression))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderSyntax,
                field.Locations.FirstOrDefault(),
                field.Name,
                field.ContainingType.Name));
            return null;
        }

        var providerName = attribute.ConstructorArguments[0].Value as string;
        if (string.IsNullOrWhiteSpace(providerName))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                field.ContainingType.Name,
                string.Empty,
                "provider member name is empty"));
            return null;
        }

        var providerMethod = ResolveProviderMethod(
            compilation,
            field,
            providerName,
            nameofArgumentExpression,
            diagnostics,
            cancellationToken);

        if (providerMethod is null)
        {
            return null;
        }

        return new DefaultModel(
            ProviderMethod: new ProviderMethodModel(
                Name: providerMethod.Name,
                IsStatic: providerMethod.IsStatic));
    }

    private static EquatableArray<ValidatorModel> ReadValidators(
        Compilation compilation,
        IFieldSymbol field,
        IEnumerable<AttributeData> attributes,
        KnownSymbols symbols,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var validators = new List<ValidatorModel>();

        foreach (var attribute in attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attribute.ConstructorArguments.Length != 1)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidValidatorMember,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    field.ContainingType.Name,
                    string.Empty,
                    "validator form must specify exactly one nameof(...) argument"));
                continue;
            }

            if (!TryGetSingleNameofArgumentSyntax(attribute, out var nameofArgumentExpression))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidValidatorSyntax,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    field.ContainingType.Name));
                continue;
            }

            var validatorName = attribute.ConstructorArguments[0].Value as string;
            if (string.IsNullOrWhiteSpace(validatorName))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidValidatorMember,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    field.ContainingType.Name,
                    string.Empty,
                    "validator member name is empty"));
                continue;
            }

            var validatorMethod = ResolveValidatorMethod(
                compilation,
                field,
                validatorName,
                nameofArgumentExpression,
                symbols,
                diagnostics,
                cancellationToken);

            if (validatorMethod is null)
            {
                continue;
            }

            validators.Add(new ValidatorModel(
                Name: validatorMethod.Name,
                IsStatic: validatorMethod.IsStatic,
                ReturnKind: GetValidatorReturnKind(validatorMethod)));
        }

        return validators.ToEquatableArray();
    }

    private static EquatableArray<StepOverloadModel> ReadStepOverloads(
        Compilation compilation,
        IFieldSymbol field,
        IEnumerable<AttributeData> attributes,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var overloads = new List<StepOverloadModel>();

        foreach (var attribute in attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attribute.ConstructorArguments.Length != 1)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidStepOverloadMember,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    field.ContainingType.Name,
                    string.Empty,
                    "step overload form must specify exactly one nameof(...) argument"));
                continue;
            }

            if (!TryGetSingleNameofArgumentSyntax(attribute, out var nameofArgumentExpression))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidStepOverloadSyntax,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    field.ContainingType.Name));
                continue;
            }

            var overloadName = attribute.ConstructorArguments[0].Value as string;
            if (string.IsNullOrWhiteSpace(overloadName))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidStepOverloadMember,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    field.ContainingType.Name,
                    string.Empty,
                    "step overload member name is empty"));
                continue;
            }

            var overloadMethod = ResolveStepOverloadMethod(
                compilation,
                field,
                overloadName,
                nameofArgumentExpression,
                diagnostics,
                cancellationToken);

            if (overloadMethod is null)
            {
                continue;
            }

            overloads.Add(new StepOverloadModel(
                Name: overloadMethod.Name,
                IsStatic: overloadMethod.IsStatic,
                Parameters: overloadMethod.Parameters.Select(CreateParameterModel).ToEquatableArray(),
                SignatureKey: CreateMethodSignatureKey(overloadMethod),
                DisplayName: overloadMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        return overloads
            .Distinct()
            .ToEquatableArray();
    }

    private static void ValidateStepMethodSignatureCollisions(
        IFieldSymbol field,
        INamedTypeSymbol builder,
        string stepMethodName,
        ITypeSymbol fieldType,
        EquatableArray<StepOverloadModel> overloads,
        List<Diagnostic> diagnostics)
    {
        var parameterlessOverloads = overloads
            .Where(static o => o.Parameters.Count == 0)
            .ToArray();

        if (parameterlessOverloads.Length > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.DuplicateParameterlessStepOverloadMethod,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                stepMethodName,
                string.Join(", ", parameterlessOverloads.Select(static o => o.DisplayName))));
        }

        var signatures = new HashSet<string>(StringComparer.Ordinal);

        var baseSignature = CreateMethodSignatureKey(
            stepMethodName,
            new[]
            {
                new ParameterModel(
                    Name: "value",
                    TypeName: fieldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RefKind: ParameterRefKind.None,
                    HasExplicitDefaultValue: false,
                    DefaultValueExpression: null)
            }.ToEquatableArray());

        signatures.Add(baseSignature);

        foreach (var overload in overloads)
        {
            var overloadSignature = CreateMethodSignatureKey(stepMethodName, overload.Parameters);
            if (!signatures.Add(overloadSignature))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DuplicateStepOverloadMethod,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    builder.Name,
                    stepMethodName,
                    overload.DisplayName));
            }
        }
    }

    private static bool TryGetSingleNameofArgumentSyntax(
        AttributeData attribute,
        out ExpressionSyntax? nameofArgumentExpression)
    {
        nameofArgumentExpression = null;

        if (attribute.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax attributeSyntax)
        {
            return false;
        }

        if (attributeSyntax.ArgumentList is null || attributeSyntax.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        var expression = attributeSyntax.ArgumentList.Arguments[0].Expression;
        if (expression is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (invocation.Expression is not IdentifierNameSyntax identifier || identifier.Identifier.ValueText != "nameof")
        {
            return false;
        }

        if (invocation.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        nameofArgumentExpression = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    private static IMethodSymbol? ResolveProviderMethod(
        Compilation compilation,
        IFieldSymbol field,
        string providerName,
        ExpressionSyntax nameofArgumentExpression,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builder = field.ContainingType;
        var semanticModel = compilation.GetSemanticModel(nameofArgumentExpression.SyntaxTree);
        var symbolInfo = semanticModel.GetSymbolInfo(nameofArgumentExpression, cancellationToken);

        var referencedMethods = new List<IMethodSymbol>();

        if (symbolInfo.Symbol is IMethodSymbol directMethod && directMethod.MethodKind == MethodKind.Ordinary)
        {
            referencedMethods.Add(directMethod);
        }

        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
        {
            if (candidate.MethodKind == MethodKind.Ordinary)
            {
                referencedMethods.Add(candidate);
            }
        }

        referencedMethods = referencedMethods
            .GroupBy(static m => (ISymbol)m, SymbolEqualityComparer.Default)
            .Select(static g => (IMethodSymbol)g.Key)
            .ToList();

        if (referencedMethods.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                providerName,
                "nameof(...) must reference a method declared on the builder class"));
            return null;
        }

        if (referencedMethods.Any(m => !SymbolEqualityComparer.Default.Equals(m.ContainingType, builder)))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                providerName,
                "provider method must be declared on the builder class itself"));
            return null;
        }

        var methodsByName = builder.GetMembers(providerName)
            .OfType<IMethodSymbol>()
            .Where(static m => m.MethodKind == MethodKind.Ordinary)
            .Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, builder))
            .ToArray();

        var validMethods = methodsByName
            .Where(static m => m.Parameters.Length == 0)
            .Where(static m => m.TypeParameters.Length == 0)
            .ToArray();

        if (validMethods.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                providerName,
                "method must be parameterless and non-generic"));
            return null;
        }

        if (validMethods.Length > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                providerName,
                "multiple matching methods with this name were found on the builder class"));
            return null;
        }

        var method = validMethods[0];
        if (!referencedMethods.Any(m =>
                SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method.OriginalDefinition) ||
                SymbolEqualityComparer.Default.Equals(m, method)))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                providerName,
                "nameof(...) must reference the exact provider method used for the default value"));
            return null;
        }

        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, field.Type))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepDefaultProviderMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                providerName,
                $"method return type '{method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' does not match field type '{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'"));
            return null;
        }

        return method;
    }

    private static IMethodSymbol? ResolveValidatorMethod(
        Compilation compilation,
        IFieldSymbol field,
        string validatorName,
        ExpressionSyntax nameofArgumentExpression,
        KnownSymbols symbols,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builder = field.ContainingType;
        var semanticModel = compilation.GetSemanticModel(nameofArgumentExpression.SyntaxTree);
        var symbolInfo = semanticModel.GetSymbolInfo(nameofArgumentExpression, cancellationToken);

        var referencedMethods = new List<IMethodSymbol>();

        if (symbolInfo.Symbol is IMethodSymbol directMethod && directMethod.MethodKind == MethodKind.Ordinary)
        {
            referencedMethods.Add(directMethod);
        }

        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
        {
            if (candidate.MethodKind == MethodKind.Ordinary)
            {
                referencedMethods.Add(candidate);
            }
        }

        referencedMethods = referencedMethods
            .GroupBy(static m => (ISymbol)m, SymbolEqualityComparer.Default)
            .Select(static g => (IMethodSymbol)g.Key)
            .ToList();

        if (referencedMethods.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidValidatorMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                validatorName,
                "nameof(...) must reference a method declared on the builder class"));
            return null;
        }

        if (referencedMethods.Any(m => !SymbolEqualityComparer.Default.Equals(m.ContainingType, builder)))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidValidatorMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                validatorName,
                "validator method must be declared on the builder class itself"));
            return null;
        }

        var methodsByName = builder.GetMembers(validatorName)
            .OfType<IMethodSymbol>()
            .Where(static m => m.MethodKind == MethodKind.Ordinary)
            .Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, builder))
            .ToArray();

        var validMethods = methodsByName
            .Where(static m => m.TypeParameters.Length == 0)
            .Where(static m => m.Parameters.Length == 1)
            .Where(m => SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, field.Type))
            .Where(m => IsSupportedValidatorReturnType(m.ReturnType, symbols))
            .ToArray();

        if (validMethods.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidValidatorMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                validatorName,
                $"method must be non-generic, accept exactly one parameter of type '{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}', and return void or Task"));
            return null;
        }

        if (validMethods.Length > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidValidatorMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                validatorName,
                "multiple matching validator methods with this name were found on the builder class"));
            return null;
        }

        var method = validMethods[0];
        if (!referencedMethods.Any(m =>
                SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method.OriginalDefinition) ||
                SymbolEqualityComparer.Default.Equals(m, method)))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidValidatorMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                validatorName,
                "nameof(...) must reference the exact validator method used for validation"));
            return null;
        }

        return method;
    }

    private static IMethodSymbol? ResolveStepOverloadMethod(
        Compilation compilation,
        IFieldSymbol field,
        string overloadName,
        ExpressionSyntax nameofArgumentExpression,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builder = field.ContainingType;
        var semanticModel = compilation.GetSemanticModel(nameofArgumentExpression.SyntaxTree);
        var symbolInfo = semanticModel.GetSymbolInfo(nameofArgumentExpression, cancellationToken);

        var referencedMethods = new List<IMethodSymbol>();

        if (symbolInfo.Symbol is IMethodSymbol directMethod && directMethod.MethodKind == MethodKind.Ordinary)
        {
            referencedMethods.Add(directMethod);
        }

        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
        {
            if (candidate.MethodKind == MethodKind.Ordinary)
            {
                referencedMethods.Add(candidate);
            }
        }

        referencedMethods = referencedMethods
            .GroupBy(static m => (ISymbol)m, SymbolEqualityComparer.Default)
            .Select(static g => (IMethodSymbol)g.Key)
            .ToList();

        if (referencedMethods.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepOverloadMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                overloadName,
                "nameof(...) must reference a method declared on the builder class"));
            return null;
        }

        if (referencedMethods.Any(m => !SymbolEqualityComparer.Default.Equals(m.ContainingType, builder)))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepOverloadMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                overloadName,
                "step overload method must be declared on the builder class itself"));
            return null;
        }

        var methodsByName = builder.GetMembers(overloadName)
            .OfType<IMethodSymbol>()
            .Where(static m => m.MethodKind == MethodKind.Ordinary)
            .Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, builder))
            .ToArray();

        var validMethods = methodsByName
            .Where(static m => m.TypeParameters.Length == 0)
            .Where(m => SymbolEqualityComparer.Default.Equals(m.ReturnType, field.Type))
            .ToArray();

        if (validMethods.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepOverloadMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                overloadName,
                $"method must be non-generic and return '{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'"));
            return null;
        }

        var matchingReferencedMethods = validMethods
            .Where(method =>
                referencedMethods.Any(m =>
                    SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method.OriginalDefinition) ||
                    SymbolEqualityComparer.Default.Equals(m, method)))
            .ToArray();

        if (matchingReferencedMethods.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepOverloadMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                overloadName,
                "nameof(...) must reference the exact overload method used for step generation"));
            return null;
        }

        if (matchingReferencedMethods.Length > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidStepOverloadMember,
                field.Locations.FirstOrDefault(),
                field.Name,
                builder.Name,
                overloadName,
                "multiple matching overload methods with this name were found on the builder class"));
            return null;
        }

        return matchingReferencedMethods[0];
    }

    private static bool IsSupportedValidatorReturnType(ITypeSymbol returnType, KnownSymbols symbols)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return true;
        }

        return symbols.TaskType is not null &&
               SymbolEqualityComparer.Default.Equals(returnType, symbols.TaskType);
    }

    private static ValidatorReturnKind GetValidatorReturnKind(IMethodSymbol method)
        => method.ReturnType.SpecialType == SpecialType.System_Void
            ? ValidatorReturnKind.Void
            : ValidatorReturnKind.Task;

    private static string GenerateBuilderSource(BuilderModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();

        if (model.Namespace is not null)
        {
            sb.Append("namespace ").Append(model.Namespace).AppendLine();
            sb.AppendLine("{");
            sb.AppendLine();
        }

        EmitAccessorClass(sb, model);
        sb.AppendLine();
        EmitWrapperClass(sb, model);
        sb.AppendLine();
        EmitTypedExtensions(sb, model);
        sb.AppendLine();

        if (model.Namespace is not null)
        {
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("namespace TypedStateBuilder");
        sb.AppendLine("{");
        sb.AppendLine();
        EmitCreateExtensions(sb, model);
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void EmitAccessorClass(StringBuilder sb, BuilderModel model)
    {
        if (model.TypeParameters.Count == 0)
        {
            sb.AppendLine("file static class " + model.AccessorClassName);
        }
        else
        {
            sb.Append("file sealed class ")
                .Append(model.AccessorClassName)
                .Append('<')
                .Append(string.Join(", ", model.TypeParameters.Select(static tp => tp.Name)))
                .AppendLine(">");

            foreach (var clause in model.TypeParameterConstraints)
            {
                sb.Append("    ").AppendLine(clause);
            }
        }

        sb.AppendLine("{");

        foreach (var ctor in model.Constructors)
        {
            sb.AppendLine("    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]");
            sb.Append("    internal static extern ")
                .Append(model.BuilderFullyQualifiedName)
                .Append(" Create")
                .Append(ctor.AccessorSuffix)
                .Append('(')
                .Append(ParameterListWithTypes(ctor.Parameters, includeDefaultValues: false))
                .AppendLine(");");
            sb.AppendLine();
        }

        foreach (var step in model.Steps)
        {
            sb.Append("    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = ")
                .Append(SymbolDisplay.FormatLiteral(step.FieldName, quote: true))
                .AppendLine(")]");
            sb.Append("    internal static extern ref ")
                .Append(step.FieldTypeName)
                .Append(' ')
                .Append(step.MethodName)
                .Append("Field(")
                .Append(model.BuilderFullyQualifiedName)
                .AppendLine(" builder);");
            sb.AppendLine();
        }

        foreach (var step in model.Steps.Where(static s => s.Default is not null))
        {
            var provider = step.Default!.Value.ProviderMethod;

            sb.Append("    [UnsafeAccessor(UnsafeAccessorKind.")
                .Append(provider.IsStatic ? "StaticMethod" : "Method")
                .Append(", Name = ")
                .Append(SymbolDisplay.FormatLiteral(provider.Name, quote: true))
                .AppendLine(")]");

            sb.Append("    internal static extern ")
                .Append(step.FieldTypeName)
                .Append(' ')
                .Append(GetProviderAccessorName(step, provider))
                .Append('(')
                .Append(model.BuilderFullyQualifiedName)
                .AppendLine(" owner);");
            sb.AppendLine();
        }

        foreach (var step in model.Steps)
        {
            for (var i = 0; i < step.Validators.Count; i++)
            {
                var validator = step.Validators.AsSpan()[i];

                sb.Append("    [UnsafeAccessor(UnsafeAccessorKind.")
                    .Append(validator.IsStatic ? "StaticMethod" : "Method")
                    .Append(", Name = ")
                    .Append(SymbolDisplay.FormatLiteral(validator.Name, quote: true))
                    .AppendLine(")]");

                sb.Append("    internal static extern ")
                    .Append(validator.ReturnKind == ValidatorReturnKind.Void
                        ? "void"
                        : "global::System.Threading.Tasks.Task")
                    .Append(' ')
                    .Append(GetValidatorAccessorName(step, validator, i))
                    .Append('(')
                    .Append(model.BuilderFullyQualifiedName)
                    .Append(" owner, ")
                    .Append(step.FieldTypeName)
                    .AppendLine(" value);");

                sb.AppendLine();
            }
        }

        foreach (var step in model.Steps)
        {
            for (var i = 0; i < step.Overloads.Count; i++)
            {
                var overload = step.Overloads.AsSpan()[i];

                sb.Append("    [UnsafeAccessor(UnsafeAccessorKind.")
                    .Append(overload.IsStatic ? "StaticMethod" : "Method")
                    .Append(", Name = ")
                    .Append(SymbolDisplay.FormatLiteral(overload.Name, quote: true))
                    .AppendLine(")]");

                sb.Append("    internal static extern ")
                    .Append(step.FieldTypeName)
                    .Append(' ')
                    .Append(GetStepOverloadAccessorName(step, overload, i))
                    .Append('(')
                    .Append(model.BuilderFullyQualifiedName)
                    .Append(" owner");

                foreach (var parameter in overload.Parameters)
                {
                    sb.Append(", ").Append(ParameterDeclaration(parameter, includeDefaultValues: false));
                }

                sb.AppendLine(");");
                sb.AppendLine();
            }
        }

        foreach (var buildMethod in model.BuildMethods)
        {
            sb.AppendLine("    [UnsafeAccessor(UnsafeAccessorKind.Method)]");
            sb.Append("    internal static extern ")
                .Append(buildMethod.ReturnTypeName)
                .Append(' ')
                .Append(buildMethod.Name);

            if (buildMethod.IsGenericMethod)
            {
                sb.Append('<')
                    .Append(string.Join(", ", buildMethod.TypeParameters.Select(static tp => tp.Name)))
                    .Append('>');
            }

            sb.Append('(')
                .Append(model.BuilderFullyQualifiedName)
                .Append(" builder");

            foreach (var parameter in buildMethod.Parameters)
            {
                sb.Append(", ").Append(ParameterDeclaration(parameter, includeDefaultValues: false));
            }

            sb.AppendLine(");");
            sb.AppendLine();
        }

        sb.AppendLine("}");
    }

    private static void EmitWrapperClass(StringBuilder sb, BuilderModel model)
    {
        sb.Append(model.GeneratedAccessibility == GeneratedAccessibility.Public ? "public" : "internal")
            .Append(" sealed class ")
            .Append(model.WrapperName)
            .Append('<')
            .Append(string.Join(", ", GetWrapperGenericParameterNames(model)))
            .AppendLine(">");

        foreach (var clause in GetWrapperWhereClauses(model))
        {
            sb.Append("    ").AppendLine(clause);
        }

        sb.AppendLine("{");
        sb.Append("    private ")
            .Append(model.BuilderFullyQualifiedName)
            .AppendLine(" Inner { get; }");
        sb.AppendLine();
        sb.Append("    internal ")
            .Append(model.WrapperName)
            .Append('(')
            .Append(model.BuilderFullyQualifiedName)
            .AppendLine(" inner)");
        sb.AppendLine("    {");
        sb.AppendLine("        Inner = inner;");
        sb.AppendLine("    }");
        sb.AppendLine();

        EmitStepCoreMethods(sb, model);
        EmitBuildCoreMethods(sb, model);

        sb.AppendLine("}");
    }

    private static void EmitStepCoreMethods(StringBuilder sb, BuilderModel model)
    {
        for (var i = 0; i < model.Steps.Count; i++)
        {
            var step = model.Steps.AsSpan()[i];
            var inputStateArgs = GetStepMethodInputStateArgs(model, i);
            var outputStateArgs = GetStepMethodOutputStateArgs(model, i);
            var stateTypeParameters = GetStepMethodTypeParameters(model, i);

            sb.Append("    internal static ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, outputStateArgs)))
                .Append("> ")
                .Append(step.MethodName)
                .Append("Core");

            if (stateTypeParameters.Length > 0)
            {
                sb.Append('<').Append(string.Join(", ", stateTypeParameters)).Append('>');
            }

            sb.Append('(')
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, inputStateArgs)))
                .Append("> builder, ")
                .Append(step.FieldTypeName)
                .Append(" value)")
                .AppendLine();

            foreach (var stateName in stateTypeParameters)
            {
                sb.Append("    where ").Append(stateName).Append(" : ").Append(QualifiedIValueState).AppendLine();
            }

            sb.AppendLine("    {");
            sb.Append("        ")
                .Append(GetAccessorTypeReference(model))
                .Append('.')
                .Append(step.MethodName)
                .AppendLine("Field(builder.Inner) = value;");
            sb.Append("        return new ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, outputStateArgs)))
                .AppendLine(">(builder.Inner);");
            sb.AppendLine("    }");
            sb.AppendLine();

            for (var overloadIndex = 0; overloadIndex < step.Overloads.Count; overloadIndex++)
            {
                var overload = step.Overloads.AsSpan()[overloadIndex];

                sb.Append("    internal static ")
                    .Append(model.WrapperName)
                    .Append('<')
                    .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, outputStateArgs)))
                    .Append("> ")
                    .Append(GetStepOverloadCoreName(step, overload, overloadIndex));

                if (stateTypeParameters.Length > 0)
                {
                    sb.Append('<').Append(string.Join(", ", stateTypeParameters)).Append('>');
                }

                sb.Append('(')
                    .Append(model.WrapperName)
                    .Append('<')
                    .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, inputStateArgs)))
                    .Append("> builder");

                foreach (var parameter in overload.Parameters)
                {
                    sb.Append(", ").Append(ParameterDeclaration(parameter, includeDefaultValues: true));
                }

                sb.AppendLine(")");

                foreach (var stateName in stateTypeParameters)
                {
                    sb.Append("    where ").Append(stateName).Append(" : ").Append(QualifiedIValueState).AppendLine();
                }

                sb.AppendLine("    {");
                sb.Append("        var value = ")
                    .Append(GetAccessorTypeReference(model))
                    .Append('.')
                    .Append(GetStepOverloadAccessorName(step, overload, overloadIndex))
                    .Append("(builder.Inner");

                foreach (var parameter in overload.Parameters)
                {
                    sb.Append(", ").Append(ParameterInvocation(parameter));
                }

                sb.AppendLine(");");
                sb.Append("        return ")
                    .Append(step.MethodName)
                    .Append("Core");

                if (stateTypeParameters.Length > 0)
                {
                    sb.Append('<').Append(string.Join(", ", stateTypeParameters)).Append('>');
                }

                sb.AppendLine("(builder, value);");
                sb.AppendLine("    }");
                sb.AppendLine();
            }
        }
    }

    private static void EmitBuildCoreMethods(StringBuilder sb, BuilderModel model)
    {
        foreach (var buildMethod in model.BuildMethods)
        {
            var buildShape = GetBuildShape(model, buildMethod);
            var genericMethodTypeParameters = buildMethod.TypeParameters.Select(static tp => tp.Name).ToArray();

            var allMethodTypeParameters = new List<string>();
            allMethodTypeParameters.AddRange(buildShape.OptionalApplicableStateTypeParameters);
            allMethodTypeParameters.AddRange(genericMethodTypeParameters);

            sb.Append("    internal static ")
                .Append(buildMethod.ReturnTypeName)
                .Append(' ')
                .Append(buildMethod.Name)
                .Append("Core");

            if (allMethodTypeParameters.Count > 0)
            {
                sb.Append('<').Append(string.Join(", ", allMethodTypeParameters)).Append('>');
            }

            sb.Append('(')
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, buildShape.StateArgs)))
                .Append("> builder");

            foreach (var parameter in buildMethod.Parameters)
            {
                sb.Append(", ").Append(ParameterDeclaration(parameter, includeDefaultValues: true));
            }

            sb.AppendLine(")");

            foreach (var optionalState in buildShape.OptionalApplicableStateTypeParameters)
            {
                sb.Append("    where ").Append(optionalState).Append(" : ").Append(QualifiedIValueState).AppendLine();
            }

            foreach (var methodConstraint in BuildTypeParameterConstraints(buildMethod.TypeParameters))
            {
                sb.Append("    ").AppendLine(methodConstraint);
            }

            sb.AppendLine("    {");

            foreach (var optionalStep in buildShape.OptionalApplicableSteps)
            {
                if (optionalStep.Default is null)
                {
                    continue;
                }

                sb.Append("        if (typeof(")
                    .Append(optionalStep.StateName)
                    .Append(") == typeof(")
                    .Append(QualifiedValueUnset)
                    .AppendLine("))");
                sb.AppendLine("        {");
                sb.Append("            ")
                    .Append(GetAccessorTypeReference(model))
                    .Append('.')
                    .Append(optionalStep.MethodName)
                    .Append("Field(builder.Inner) = ")
                    .Append(GetProviderInvocation(model, optionalStep, optionalStep.Default.Value.ProviderMethod,
                        "builder.Inner"))
                    .AppendLine(";");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("        List<Exception>? exceptions = null;");
            sb.AppendLine();

            foreach (var step in buildShape.ApplicableSteps)
            {
                for (var i = 0; i < step.Validators.Count; i++)
                {
                    var validator = step.Validators.AsSpan()[i];
                    var accessorType = GetAccessorTypeReference(model);
                    var valueExpression = $"{accessorType}.{step.MethodName}Field(builder.Inner)";
                    var invocation = GetValidatorInvocation(
                        model,
                        step,
                        validator,
                        i,
                        "builder.Inner",
                        valueExpression);

                    sb.AppendLine("        try");
                    sb.AppendLine("        {");

                    if (validator.ReturnKind == ValidatorReturnKind.Task)
                    {
                        sb.Append("            var task = ")
                            .Append(invocation)
                            .AppendLine(";");
                        sb.AppendLine("            task.GetAwaiter().GetResult();");
                    }
                    else
                    {
                        sb.Append("            ")
                            .Append(invocation)
                            .AppendLine(";");
                    }

                    sb.AppendLine("        }");
                    sb.AppendLine("        catch (Exception ex)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            (exceptions ??= new List<Exception>()).Add(ex);");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("        if (exceptions is not null)");
            sb.AppendLine("        {");
            sb.AppendLine("            throw new AggregateException(exceptions);");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.Append("        return ")
                .Append(GetAccessorTypeReference(model))
                .Append('.')
                .Append(buildMethod.Name);

            if (buildMethod.IsGenericMethod)
            {
                sb.Append('<').Append(string.Join(", ", genericMethodTypeParameters)).Append('>');
            }

            sb.Append("(builder.Inner");
            foreach (var parameter in buildMethod.Parameters)
            {
                sb.Append(", ").Append(ParameterInvocation(parameter));
            }

            sb.AppendLine(");");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
    }

    private static void EmitCreateExtensions(StringBuilder sb, BuilderModel model)
    {
        sb.Append("public static partial class ").AppendLine(model.CreateClassName);
        sb.AppendLine("{");
        EmitCreateMethods(sb, model);
        sb.AppendLine("}");
    }

    private static void EmitCreateMethods(StringBuilder sb, BuilderModel model)
    {
        foreach (var ctor in model.Constructors)
        {
            EmitCreateMethodDocumentation(sb, model, ctor);
            sb.Append("    ")
                .Append(model.GeneratedAccessibility == GeneratedAccessibility.Public ? "public" : "internal")
                .Append(" static ")
                .Append(model.FullyQualifiedWrapperName)
                .Append('<')
                .Append(string.Join(", ", GetCreateReturnGenericArguments(model)))
                .Append("> ")
                .Append(model.CreateMethodName);

            var createMethodTypeParameters = model.TypeParameters.Select(static tp => tp.Name).ToArray();
            if (createMethodTypeParameters.Length > 0)
            {
                sb.Append('<').Append(string.Join(", ", createMethodTypeParameters)).Append('>');
            }

            sb.Append('(')
                .Append(ParameterListWithTypes(ctor.Parameters, includeDefaultValues: true))
                .AppendLine(")");

            foreach (var builderTypeConstraint in model.TypeParameterConstraints)
            {
                sb.Append("    ").AppendLine(builderTypeConstraint);
            }

            sb.AppendLine("    {");
            sb.Append("        var inner = ")
                .Append(GetFullyQualifiedAccessorTypeReference(model))
                .Append(".Create")
                .Append(ctor.AccessorSuffix)
                .Append('(')
                .Append(string.Join(", ", ctor.Parameters.Select(ParameterInvocation)))
                .AppendLine(");");

            sb.Append("        return new ")
                .Append(model.FullyQualifiedWrapperName)
                .Append('<')
                .Append(string.Join(", ", GetCreateReturnGenericArguments(model)))
                .AppendLine(">(inner);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
    }

    private static string GetProviderInvocation(
        BuilderModel model,
        StepModel step,
        ProviderMethodModel providerMethod,
        string builderExpression)
    {
        return
            $"{GetFullyQualifiedAccessorTypeReference(model)}.{GetProviderAccessorName(step, providerMethod)}({builderExpression})";
    }

    private static string GetValidatorInvocation(
        BuilderModel model,
        StepModel step,
        ValidatorModel validator,
        int validatorIndex,
        string builderExpression,
        string valueExpression)
    {
        return
            $"{GetAccessorTypeReference(model)}.{GetValidatorAccessorName(step, validator, validatorIndex)}({builderExpression}, {valueExpression})";
    }

    private static string GetAccessorTypeReference(BuilderModel model)
        => model.TypeParameters.Count == 0
            ? model.AccessorClassName
            : $"{model.AccessorClassName}<{string.Join(", ", model.TypeParameters.Select(static tp => tp.Name))}>";

    private static string GetFullyQualifiedAccessorTypeReference(BuilderModel model)
        => model.TypeParameters.Count == 0
            ? model.FullyQualifiedAccessorClassName
            : $"{model.FullyQualifiedAccessorClassName}<{string.Join(", ", model.TypeParameters.Select(static tp => tp.Name))}>";

    private static void EmitTypedExtensions(StringBuilder sb, BuilderModel model)
    {
        sb.Append(model.GeneratedAccessibility == GeneratedAccessibility.Public ? "public" : "internal")
            .Append(" static partial class ")
            .AppendLine(model.ExtensionClassName);
        sb.AppendLine("{");

        EmitStepExtensions(sb, model);
        EmitBuildExtensions(sb, model);

        sb.AppendLine("}");
    }

    private static void EmitStepExtensions(StringBuilder sb, BuilderModel model)
    {
        for (var i = 0; i < model.Steps.Count; i++)
        {
            var step = model.Steps.AsSpan()[i];
            var inputStateArgs = GetStepMethodInputStateArgs(model, i);
            var outputStateArgs = GetStepMethodOutputStateArgs(model, i);
            var stateTypeParameters = GetStepMethodTypeParameters(model, i);

            EmitDirectStepDocumentation(sb, step);
            sb.Append("    ")
                .Append(model.GeneratedAccessibility == GeneratedAccessibility.Public ? "public" : "internal")
                .Append(" static ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, outputStateArgs)))
                .Append("> ")
                .Append(step.MethodName);

            var extensionMethodTypeParameters = new List<string>();
            extensionMethodTypeParameters.AddRange(model.TypeParameters.Select(static tp => tp.Name));
            extensionMethodTypeParameters.AddRange(stateTypeParameters);

            if (extensionMethodTypeParameters.Count > 0)
            {
                sb.Append('<').Append(string.Join(", ", extensionMethodTypeParameters)).Append('>');
            }

            sb.Append('(')
                .Append("this ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, inputStateArgs)))
                .Append("> builder, ")
                .Append(step.FieldTypeName)
                .Append(" value)")
                .AppendLine();

            foreach (var builderTypeConstraint in model.TypeParameterConstraints)
            {
                sb.Append("    ").AppendLine(builderTypeConstraint);
            }

            foreach (var stateName in stateTypeParameters)
            {
                sb.Append("    where ").Append(stateName).Append(" : ").Append(QualifiedIValueState).AppendLine();
            }

            sb.Append("        => ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, inputStateArgs)))
                .Append(">")
                .Append('.')
                .Append(step.MethodName)
                .Append("Core");

            if (stateTypeParameters.Length > 0)
            {
                sb.Append('<').Append(string.Join(", ", stateTypeParameters)).Append('>');
            }

            sb.AppendLine("(builder, value);");
            sb.AppendLine();

            for (var overloadIndex = 0; overloadIndex < step.Overloads.Count; overloadIndex++)
            {
                var overload = step.Overloads.AsSpan()[overloadIndex];

                EmitOverloadStepDocumentation(sb, step, overload);
                sb.Append("    ")
                    .Append(model.GeneratedAccessibility == GeneratedAccessibility.Public ? "public" : "internal")
                    .Append(" static ")
                    .Append(model.WrapperName)
                    .Append('<')
                    .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, outputStateArgs)))
                    .Append("> ")
                    .Append(step.MethodName);

                if (extensionMethodTypeParameters.Count > 0)
                {
                    sb.Append('<').Append(string.Join(", ", extensionMethodTypeParameters)).Append('>');
                }

                sb.Append('(')
                    .Append("this ")
                    .Append(model.WrapperName)
                    .Append('<')
                    .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, inputStateArgs)))
                    .Append("> builder");

                foreach (var parameter in overload.Parameters)
                {
                    sb.Append(", ").Append(ParameterDeclaration(parameter, includeDefaultValues: true));
                }

                sb.AppendLine(")");

                foreach (var builderTypeConstraint in model.TypeParameterConstraints)
                {
                    sb.Append("    ").AppendLine(builderTypeConstraint);
                }

                foreach (var stateName in stateTypeParameters)
                {
                    sb.Append("    where ").Append(stateName).Append(" : ").Append(QualifiedIValueState).AppendLine();
                }

                sb.Append("        => ")
                    .Append(model.WrapperName)
                    .Append('<')
                    .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, inputStateArgs)))
                    .Append(">")
                    .Append('.')
                    .Append(GetStepOverloadCoreName(step, overload, overloadIndex));

                if (stateTypeParameters.Length > 0)
                {
                    sb.Append('<').Append(string.Join(", ", stateTypeParameters)).Append('>');
                }

                sb.Append("(builder");
                foreach (var parameter in overload.Parameters)
                {
                    sb.Append(", ").Append(ParameterInvocation(parameter));
                }

                sb.AppendLine(");");
                sb.AppendLine();
            }
        }
    }

    private static void EmitBuildExtensions(StringBuilder sb, BuilderModel model)
    {
        foreach (var buildMethod in model.BuildMethods)
        {
            var buildShape = GetBuildShape(model, buildMethod);
            var genericMethodTypeParameters = buildMethod.TypeParameters.Select(static tp => tp.Name).ToArray();

            var allMethodTypeParameters = new List<string>();
            allMethodTypeParameters.AddRange(model.TypeParameters.Select(static tp => tp.Name));
            allMethodTypeParameters.AddRange(buildShape.OptionalApplicableStateTypeParameters);
            allMethodTypeParameters.AddRange(genericMethodTypeParameters);

            EmitBuildDocumentation(sb, buildMethod, buildShape);
            sb.Append("    ")
                .Append(model.GeneratedAccessibility == GeneratedAccessibility.Public ? "public" : "internal")
                .Append(" static ")
                .Append(buildMethod.ReturnTypeName)
                .Append(' ')
                .Append(buildMethod.Name);

            if (allMethodTypeParameters.Count > 0)
            {
                sb.Append('<').Append(string.Join(", ", allMethodTypeParameters)).Append('>');
            }

            sb.Append('(')
                .Append("this ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, buildShape.StateArgs)))
                .Append("> builder");

            foreach (var parameter in buildMethod.Parameters)
            {
                sb.Append(", ").Append(ParameterDeclaration(parameter, includeDefaultValues: true));
            }

            sb.AppendLine(")");

            foreach (var builderTypeConstraint in model.TypeParameterConstraints)
            {
                sb.Append("    ").AppendLine(builderTypeConstraint);
            }

            foreach (var optionalState in buildShape.OptionalApplicableStateTypeParameters)
            {
                sb.Append("    where ").Append(optionalState).Append(" : ").Append(QualifiedIValueState).AppendLine();
            }

            foreach (var methodConstraint in BuildTypeParameterConstraints(buildMethod.TypeParameters))
            {
                sb.Append("    ").AppendLine(methodConstraint);
            }

            sb.Append("        => ")
                .Append(model.WrapperName)
                .Append('<')
                .Append(string.Join(", ", GetBuilderTypeArgsThenStates(model, buildShape.StateArgs)))
                .Append(">")
                .Append('.')
                .Append(buildMethod.Name)
                .Append("Core");

            var coreInvocationTypeParameters = new List<string>();
            coreInvocationTypeParameters.AddRange(buildShape.OptionalApplicableStateTypeParameters);
            coreInvocationTypeParameters.AddRange(genericMethodTypeParameters);

            if (coreInvocationTypeParameters.Count > 0)
            {
                sb.Append('<').Append(string.Join(", ", coreInvocationTypeParameters)).Append('>');
            }

            sb.Append("(builder");
            foreach (var parameter in buildMethod.Parameters)
            {
                sb.Append(", ").Append(ParameterInvocation(parameter));
            }

            sb.AppendLine(");");
            sb.AppendLine();
        }
    }

    private static void EmitCreateMethodDocumentation(StringBuilder sb, BuilderModel model, ConstructorModel ctor)
    {
        AppendXmlSummary(sb, $"Creates a new <see cref=\"{FormatCref(model.FullyQualifiedWrapperName)}\"/>.");

        foreach (var parameter in ctor.Parameters)
        {
            AppendXmlParam(sb, parameter.Name, $"The value for {FormatParameterDisplay(parameter.Name)}.");
        }

        AppendXmlReturns(sb, "A new builder instance.");
    }

    private static void EmitDirectStepDocumentation(StringBuilder sb, StepModel step)
    {
        var summary = step.BranchPath is null
            ? $"Sets the {GetFriendlyStepDisplayName(step)}."
            : $"Sets the {GetFriendlyStepDisplayName(step)} for the <c>{EscapeXml(step.BranchPath)}</c> branch.";

        AppendXmlSummary(sb, summary);
        AppendXmlParam(sb, "builder", "The builder.");
        AppendXmlParam(sb, "value", $"The value for <c>{EscapeXml(step.FieldName)}</c>.");
        AppendXmlReturns(sb, "The updated builder.");

        if (step.Default is not null)
        {
            var remarks = step.BranchPath is null
                ? $"If this step is not set explicitly, <c>{EscapeXml(step.Default.Value.ProviderMethod.Name)}</c> is used to provide <c>{EscapeXml(step.FieldName)}</c> during build."
                : $"If this step is not set explicitly for the <c>{EscapeXml(step.BranchPath)}</c> branch, <c>{EscapeXml(step.Default.Value.ProviderMethod.Name)}</c> is used to provide <c>{EscapeXml(step.FieldName)}</c> during build.";

            AppendXmlRemarks(sb, remarks);
        }
    }

    private static void EmitOverloadStepDocumentation(StringBuilder sb, StepModel step, StepOverloadModel overload)
    {
        AppendXmlSummary(sb, $"Sets the {GetFriendlyStepDisplayName(step)}.");
        AppendXmlParam(sb, "builder", "The builder.");

        foreach (var parameter in overload.Parameters)
        {
            var description = overload.Parameters.Count == 1
                ? $"The value used to produce <c>{EscapeXml(step.FieldName)}</c>."
                : $"The value used when producing <c>{EscapeXml(step.FieldName)}</c>.";
            AppendXmlParam(sb, parameter.Name, description);
        }

        AppendXmlReturns(sb, "The updated builder.");

        if (step.Default is not null)
        {
            var remarks = step.BranchPath is null
                ? $"If this step is not set explicitly, <c>{EscapeXml(step.Default.Value.ProviderMethod.Name)}</c> is used to provide <c>{EscapeXml(step.FieldName)}</c> during build."
                : $"If this step is not set explicitly for the <c>{EscapeXml(step.BranchPath)}</c> branch, <c>{EscapeXml(step.Default.Value.ProviderMethod.Name)}</c> is used to provide <c>{EscapeXml(step.FieldName)}</c> during build.";

            AppendXmlRemarks(sb, remarks);
        }
    }

    private static void EmitBuildDocumentation(StringBuilder sb, BuildMethodModel buildMethod, BuildShape buildShape)
    {
        var summary = buildMethod.TargetBranchPath is null
            ? "Builds the result."
            : $"Builds the result for the <c>{EscapeXml(buildMethod.TargetBranchPath)}</c> branch.";

        AppendXmlSummary(sb, summary);
        AppendXmlParam(sb, "builder", "The builder.");

        foreach (var parameter in buildMethod.Parameters)
        {
            AppendXmlParam(sb, parameter.Name, $"The value for {FormatParameterDisplay(parameter.Name)}.");
        }

        AppendXmlReturns(sb, "The built result.");
        AppendXmlException(sb, "System.AggregateException", "Thrown if validation fails.");

        var defaults = buildShape.OptionalApplicableSteps
            .Where(static step => step.Default is not null)
            .Select(static step => $"<c>{EscapeXml(step.FieldName)}</c> using <c>{EscapeXml(step.Default!.Value.ProviderMethod.Name)}</c>")
            .ToArray();

        if (defaults.Length > 0)
        {
            AppendXmlRemarks(sb,
                $"If not set explicitly, this method provides default values for {JoinHumanReadable(defaults)}");
        }
    }

    private static void AppendXmlSummary(StringBuilder sb, string text)
    {
        sb.AppendLine("    /// <summary>");
        AppendXmlTextLines(sb, text);
        sb.AppendLine("    /// </summary>");
    }

    private static void AppendXmlParam(StringBuilder sb, string name, string text)
    {
        sb.Append("    /// <param name=\"").Append(name).Append("\">");
        sb.Append(text);
        sb.AppendLine("</param>");
    }

    private static void AppendXmlReturns(StringBuilder sb, string text)
    {
        sb.AppendLine("    /// <returns>");
        AppendXmlTextLines(sb, text);
        sb.AppendLine("    /// </returns>");
    }

    private static void AppendXmlException(StringBuilder sb, string cref, string text)
    {
        sb.Append("    /// <exception cref=\"").Append(cref).Append("\">");
        sb.Append(text);
        sb.AppendLine("</exception>");
    }

    private static void AppendXmlRemarks(StringBuilder sb, string text)
    {
        sb.AppendLine("    /// <remarks>");
        AppendXmlTextLines(sb, text);
        sb.AppendLine("    /// </remarks>");
    }

    private static void AppendXmlTextLines(StringBuilder sb, string text)
    {
        foreach (var line in WrapXmlText(text))
        {
            sb.Append("    /// ").AppendLine(line);
        }
    }

    private static IEnumerable<string> WrapXmlText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            yield return rawLine;
        }
    }

    private static string FormatCref(string fullyQualifiedTypeName)
        => EscapeXml(fullyQualifiedTypeName.Replace("global::", string.Empty));

    private static string GetFriendlyStepDisplayName(StepModel step)
        => ToSentenceCase(step.MethodName.StartsWith("Set", StringComparison.Ordinal)
            ? step.MethodName.Substring(3)
            : step.MethodName);

    private static string ToSentenceCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length == 1)
        {
            return value.ToLowerInvariant();
        }

        return char.ToLowerInvariant(value[0]) + value.Substring(1);
    }

    private static string FormatParameterDisplay(string parameterName)
        => "<c>" + EscapeXml(parameterName) + "</c>";

    private static string JoinHumanReadable(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (items.Count == 1)
        {
            return items[0] + ".";
        }

        if (items.Count == 2)
        {
            return items[0] + " and " + items[1] + ".";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(i == items.Count - 1 ? ", and " : ", ");
            }

            sb.Append(items[i]);
        }

        sb.Append('.');
        return sb.ToString();
    }

    private static string EscapeXml(string text)
        => text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    private static string GetOptionalParameterDefault(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
        {
            return string.Empty;
        }

        return GetConstantExpression(parameter.Type, parameter.ExplicitDefaultValue);
    }

    private static string GetConstantExpression(ITypeSymbol type, object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return SymbolDisplay.FormatLiteral((string)value, quote: true);
        }

        if (type.SpecialType == SpecialType.System_Char)
        {
            return SymbolDisplay.FormatLiteral((char)value, quote: true);
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return (bool)value ? "true" : "false";
        }

        if (type.SpecialType == SpecialType.System_Single)
        {
            var floatValue = (float)value;
            if (float.IsNaN(floatValue))
            {
                return "float.NaN";
            }

            if (float.IsPositiveInfinity(floatValue))
            {
                return "float.PositiveInfinity";
            }

            if (float.IsNegativeInfinity(floatValue))
            {
                return "float.NegativeInfinity";
            }

            return floatValue.ToString("R", CultureInfo.InvariantCulture) + "F";
        }

        if (type.SpecialType == SpecialType.System_Double)
        {
            var doubleValue = (double)value;
            if (double.IsNaN(doubleValue))
            {
                return "double.NaN";
            }

            if (double.IsPositiveInfinity(doubleValue))
            {
                return "double.PositiveInfinity";
            }

            if (double.IsNegativeInfinity(doubleValue))
            {
                return "double.NegativeInfinity";
            }

            return doubleValue.ToString("R", CultureInfo.InvariantCulture) + "D";
        }

        if (type.SpecialType == SpecialType.System_Decimal)
        {
            return ((decimal)value).ToString(CultureInfo.InvariantCulture) + "M";
        }

        if (type.SpecialType == SpecialType.System_Int64)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture) + "L";
        }

        if (type.SpecialType == SpecialType.System_UInt32)
        {
            return ((uint)value).ToString(CultureInfo.InvariantCulture) + "U";
        }

        if (type.SpecialType == SpecialType.System_UInt64)
        {
            return ((ulong)value).ToString(CultureInfo.InvariantCulture) + "UL";
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return
                $"({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){Convert.ToString(value, CultureInfo.InvariantCulture)}";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
    }

    private static IEnumerable<string> GetWrapperGenericParameterNames(
        BuilderModel model,
        IReadOnlyList<string>? stateOverride = null)
    {
        foreach (var tp in model.TypeParameters)
        {
            yield return tp.Name;
        }

        if (stateOverride is not null)
        {
            foreach (var s in stateOverride)
            {
                yield return s;
            }
        }
        else
        {
            foreach (var step in model.Steps)
            {
                yield return step.StateName;
            }
        }
    }

    private static string[] GetCreateReturnGenericArguments(BuilderModel model)
        => GetBuilderTypeArgsThenStates(model, Enumerable.Repeat(QualifiedValueUnset, model.Steps.Count).ToArray());

    private static string[] GetBuilderTypeArgsThenStates(BuilderModel model, IReadOnlyList<string> stateArgs)
    {
        var result = new List<string>(model.TypeParameters.Count + stateArgs.Count);
        result.AddRange(model.TypeParameters.Select(static tp => tp.Name));
        result.AddRange(stateArgs);
        return result.ToArray();
    }

    private static string[] GetStateArgumentList(BuilderModel model, int changedIndex, string replacement)
    {
        var args = new string[model.Steps.Count];
        for (var i = 0; i < model.Steps.Count; i++)
        {
            args[i] = i == changedIndex ? replacement : model.Steps.AsSpan()[i].StateName;
        }

        return args;
    }

    private static IEnumerable<string> GetWrapperWhereClauses(
        BuilderModel model,
        IReadOnlyList<string>? stateOverride = null)
    {
        foreach (var constraint in model.TypeParameterConstraints)
        {
            yield return constraint;
        }

        var states = stateOverride ?? model.Steps.Select(static s => s.StateName).ToArray();
        foreach (var state in states.Distinct(StringComparer.Ordinal))
        {
            if (state == QualifiedValueSet || state == QualifiedValueUnset)
            {
                continue;
            }

            yield return $"where {state} : {QualifiedIValueState}";
        }
    }

    private static string ParameterPrefix(ParameterModel parameter)
        => parameter.RefKind switch
        {
            ParameterRefKind.Ref => "ref ",
            ParameterRefKind.Out => "out ",
            ParameterRefKind.In => "in ",
            _ => string.Empty,
        };

    private static string ParameterDeclaration(ParameterModel parameter, bool includeDefaultValues)
    {
        var sb = new StringBuilder();

        sb.Append(ParameterPrefix(parameter))
            .Append(parameter.TypeName)
            .Append(' ')
            .Append(parameter.Name);

        if (includeDefaultValues && parameter.HasExplicitDefaultValue)
        {
            sb.Append(" = ").Append(parameter.DefaultValueExpression);
        }

        return sb.ToString();
    }

    private static string ParameterInvocation(ParameterModel parameter)
        => ParameterPrefix(parameter) + parameter.Name;

    private static string ParameterListWithTypes(
        EquatableArray<ParameterModel> parameters,
        bool includeDefaultValues)
        => string.Join(", ", parameters.Select(p => ParameterDeclaration(p, includeDefaultValues)));

    private static string CreateConstructorSignatureKey(IMethodSymbol ctor)
    {
        var sb = new StringBuilder();
        sb.Append(ctor.Name).Append('(');

        for (var i = 0; i < ctor.Parameters.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var parameter = ctor.Parameters[i];
            sb.Append((int)parameter.RefKind)
                .Append(':')
                .Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string CreateMethodSignatureKey(IMethodSymbol method)
        => CreateMethodSignatureKey(
            method.Name,
            method.Parameters.Select(CreateParameterModel).ToEquatableArray());

    private static string CreateMethodSignatureKey(string methodName, EquatableArray<ParameterModel> parameters)
    {
        var sb = new StringBuilder();
        sb.Append(methodName).Append('(');

        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var parameter = parameters.AsSpan()[i];
            sb.Append((int)parameter.RefKind)
                .Append(':')
                .Append(parameter.TypeName);
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string GetAccessorConstructorSuffix(EquatableArray<ParameterModel> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("__");
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("__");
            }

            var parameter = parameters.AsSpan()[i];
            sb.Append(SanitizeIdentifier(GetAccessorParameterKey(parameter)));
        }

        return sb.ToString();
    }

    private static string GetAccessorParameterKey(ParameterModel parameter)
    {
        var refPart = parameter.RefKind switch
        {
            ParameterRefKind.Ref => "ref_",
            ParameterRefKind.Out => "out_",
            ParameterRefKind.In => "in_",
            _ => string.Empty
        };

        return refPart + parameter.TypeName;
    }

    private static string GetProviderAccessorName(StepModel step, ProviderMethodModel providerMethod)
        => "Provide_" + SanitizeIdentifier(step.MethodName + "_" + providerMethod.Name);

    private static string GetValidatorAccessorName(StepModel step, ValidatorModel validator, int index)
        => "Validate_" + SanitizeIdentifier(step.MethodName + "_" + validator.Name + "_" +
                                            index.ToString(CultureInfo.InvariantCulture));

    private static string GetStepOverloadAccessorName(StepModel step, StepOverloadModel overload, int index)
        => "Overload_" + SanitizeIdentifier(step.MethodName + "_" + overload.Name + "_" +
                                            index.ToString(CultureInfo.InvariantCulture));

    private static string GetStepOverloadCoreName(StepModel step, StepOverloadModel overload, int index)
        => "SetFrom_" +
           SanitizeIdentifier(
               step.MethodName + "_" + overload.Name + "_" + index.ToString(CultureInfo.InvariantCulture)) + "Core";

    private static string GetStepMethodName(string fieldName)
    {
        var trimmed = fieldName.TrimStart('_');
        if (trimmed.Length == 0)
        {
            trimmed = fieldName;
        }

        return "Set" + ToPascalCase(trimmed);
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var parts = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                sb.Append(part.Substring(1));
            }
        }

        return sb.ToString();
    }

    private static string GetUniqueStateParameterName(string fieldName, HashSet<string> used)
    {
        var baseName = "T" + ToPascalCase(fieldName.TrimStart('_')) + "State";
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        var i = 2;
        while (used.Contains(baseName + i.ToString(CultureInfo.InvariantCulture)))
        {
            i++;
        }

        return baseName + i.ToString(CultureInfo.InvariantCulture);
    }

    private static string SanitizeHintName(string text)
    {
        var sb = new StringBuilder(text.Length + 9);
        foreach (var ch in text)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        sb.Append('_').Append(GetStableHashHex(text));
        return sb.ToString();
    }

    private static string GetStableHashHex(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private static string SanitizeIdentifier(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (sb.Length == 0 || (!char.IsLetter(sb[0]) && sb[0] != '_'))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private static bool TryNormalizeBranchPath(string rawPath, out string? normalizedPath, out string error)
    {
        normalizedPath = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "branch path cannot be empty";
            return false;
        }

        var parts = rawPath.Split('/');
        var normalizedParts = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                error = "branch path must not contain empty segments";
                return false;
            }

            normalizedParts.Add(trimmed);
        }

        normalizedPath = string.Join("/", normalizedParts);
        return true;
    }

    private static bool IsBranchPrefixOrEqual(string prefix, string target)
        => prefix == target || target.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static bool IsStepApplicableToBuild(string? stepBranchPath, string? targetBranchPath)
    {
        if (stepBranchPath is null)
        {
            return true;
        }

        if (targetBranchPath is null)
        {
            return false;
        }

        return IsBranchPrefixOrEqual(stepBranchPath, targetBranchPath);
    }

    private static bool AreBranchPathsCompatible(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return true;
        }

        return IsBranchPrefixOrEqual(left, right) || IsBranchPrefixOrEqual(right, left);
    }

    private static string GetStepMethodOtherStateConstraint(BuilderModel model, int currentStepIndex,
        string otherStateName)
    {
        var currentStep = model.Steps.AsSpan()[currentStepIndex];
        var otherStep = model.Steps.First(s => s.StateName == otherStateName);

        return AreBranchPathsCompatible(currentStep.BranchPath, otherStep.BranchPath)
            ? QualifiedIValueState
            : QualifiedValueUnset;
    }

    private static BuildShape GetBuildShape(BuilderModel model, BuildMethodModel buildMethod)
    {
        var stateArgs = new string[model.Steps.Count];
        var applicableSteps = new List<StepModel>();
        var optionalApplicableSteps = new List<StepModel>();
        var optionalApplicableStateTypeParameters = new List<string>();

        for (var i = 0; i < model.Steps.Count; i++)
        {
            var step = model.Steps.AsSpan()[i];
            var applicable = IsStepApplicableToBuild(step.BranchPath, buildMethod.TargetBranchPath);

            if (!applicable)
            {
                stateArgs[i] = QualifiedValueUnset;
                continue;
            }

            applicableSteps.Add(step);

            if (step.IsRequired)
            {
                stateArgs[i] = QualifiedValueSet;
            }
            else
            {
                stateArgs[i] = step.StateName;
                optionalApplicableSteps.Add(step);
                optionalApplicableStateTypeParameters.Add(step.StateName);
            }
        }

        return new BuildShape(
            StateArgs: stateArgs,
            ApplicableSteps: applicableSteps.ToEquatableArray(),
            OptionalApplicableSteps: optionalApplicableSteps.ToEquatableArray(),
            OptionalApplicableStateTypeParameters: optionalApplicableStateTypeParameters.ToEquatableArray());
    }

    private static string[] GetStepMethodInputStateArgs(BuilderModel model, int currentStepIndex)
    {
        var args = new string[model.Steps.Count];
        var current = model.Steps.AsSpan()[currentStepIndex];

        for (var i = 0; i < model.Steps.Count; i++)
        {
            var other = model.Steps.AsSpan()[i];

            if (i == currentStepIndex)
            {
                args[i] = QualifiedValueUnset;
            }
            else if (!AreBranchPathsCompatible(current.BranchPath, other.BranchPath))
            {
                args[i] = QualifiedValueUnset;
            }
            else if (IsStrictBranchAncestor(other.BranchPath, current.BranchPath))
            {
                args[i] = QualifiedValueSet;
            }
            else
            {
                args[i] = other.StateName;
            }
        }

        return args;
    }

    private static string[] GetStepMethodOutputStateArgs(BuilderModel model, int currentStepIndex)
    {
        var args = new string[model.Steps.Count];
        var current = model.Steps.AsSpan()[currentStepIndex];

        for (var i = 0; i < model.Steps.Count; i++)
        {
            var other = model.Steps.AsSpan()[i];

            if (i == currentStepIndex)
            {
                args[i] = QualifiedValueSet;
            }
            else if (!AreBranchPathsCompatible(current.BranchPath, other.BranchPath))
            {
                args[i] = QualifiedValueUnset;
            }
            else if (IsStrictBranchAncestor(other.BranchPath, current.BranchPath))
            {
                args[i] = QualifiedValueSet;
            }
            else
            {
                args[i] = other.StateName;
            }
        }

        return args;
    }

    private static string[] GetStepMethodTypeParameters(BuilderModel model, int currentStepIndex)
    {
        var list = new List<string>();
        var current = model.Steps.AsSpan()[currentStepIndex];

        for (var i = 0; i < model.Steps.Count; i++)
        {
            if (i == currentStepIndex)
            {
                continue;
            }

            var other = model.Steps.AsSpan()[i];

            if (!AreBranchPathsCompatible(current.BranchPath, other.BranchPath))
            {
                continue;
            }

            if (IsStrictBranchAncestor(other.BranchPath, current.BranchPath))
            {
                continue;
            }

            list.Add(other.StateName);
        }

        return list.ToArray();
    }

    private static bool IsStrictBranchAncestor(string? ancestor, string? descendant)
    {
        if (ancestor is null || descendant is null)
        {
            return false;
        }

        return descendant.StartsWith(ancestor + "/", StringComparison.Ordinal);
    }

    private static readonly string AttributeSources = @"// <auto-generated />
#nullable enable
using System;

namespace TypedStateBuilder;

/// <summary>
/// Represents a builder step state.
/// </summary>
public interface IValueState { }

/// <summary>
/// Indicates that a builder step has been set.
/// </summary>
public sealed class ValueSet : IValueState { }

/// <summary>
/// Indicates that a builder step has not been set.
/// </summary>
public sealed class ValueUnset : IValueState { }

/// <summary>
/// Marks a method as a build method.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BuildAttribute : Attribute
{
    /// <summary>
    /// Marks a method as a build method.
    /// </summary>
    public BuildAttribute()
    {
    }

    /// <summary>
    /// Marks a method as a build method for a specific branch.
    /// </summary>
    /// <param name=""targetBranch"">The branch path.</param>
    public BuildAttribute(string targetBranch)
    {
    }
}

/// <summary>
/// Marks a field as a builder step.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public class StepForValueAttribute : Attribute
{
    /// <summary>
    /// Marks a required builder step.
    /// </summary>
    public StepForValueAttribute()
    {
    }

    /// <summary>
    /// Marks an optional builder step with a default value provider.
    /// </summary>
    /// <param name=""providerMemberName"">The provider member name.</param>
    public StepForValueAttribute(string providerMemberName)
    {
    }
}

/// <summary>
/// Assigns a step to a branch.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class StepBranchAttribute : Attribute
{
    /// <summary>
    /// Assigns a step to a branch.
    /// </summary>
    /// <param name=""branchPath"">The branch path.</param>
    public StepBranchAttribute(string branchPath)
    {
    }
}

/// <summary>
/// Adds another way to set a builder step.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class StepOverloadAttribute : Attribute
{
    /// <summary>
    /// Adds another way to set a builder step.
    /// </summary>
    /// <param name=""overloadMemberName"">The overload member name.</param>
    public StepOverloadAttribute(string overloadMemberName)
    {
    }
}

/// <summary>
/// Adds validation for a builder step.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class ValidateValueAttribute : Attribute
{
    /// <summary>
    /// Adds validation for a builder step.
    /// </summary>
    /// <param name=""validatorMemberName"">The validator member name.</param>
    public ValidateValueAttribute(string validatorMemberName)
    {
    }
}

/// <summary>
/// Enables typed builder generation for a builder class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TypedStateBuilderAttribute : Attribute
{
}
";

    private readonly record struct KnownSymbols(
        INamedTypeSymbol? BuildAttribute,
        INamedTypeSymbol? StepForValueAttribute,
        INamedTypeSymbol? StepOverloadAttribute,
        INamedTypeSymbol? StepBranchAttribute,
        INamedTypeSymbol? ValidateValueAttribute,
        INamedTypeSymbol? TaskType)
    {
        public INamedTypeSymbol? BuildAttribute { get; } = BuildAttribute;
        public INamedTypeSymbol? StepForValueAttribute { get; } = StepForValueAttribute;
        public INamedTypeSymbol? StepOverloadAttribute { get; } = StepOverloadAttribute;
        public INamedTypeSymbol? StepBranchAttribute { get; } = StepBranchAttribute;
        public INamedTypeSymbol? ValidateValueAttribute { get; } = ValidateValueAttribute;
        public INamedTypeSymbol? TaskType { get; } = TaskType;
    }

    private readonly record struct BuilderAnalysisResult(
        BuilderModel? Model,
        EquatableArray<Diagnostic> Diagnostics)
    {
        public BuilderModel? Model { get; } = Model;
        public EquatableArray<Diagnostic> Diagnostics { get; } = Diagnostics;
    }

    private readonly record struct BuilderModel(
        string BuilderName,
        string BuilderFullyQualifiedName,
        string? Namespace,
        GeneratedAccessibility GeneratedAccessibility,
        string WrapperName,
        string FullyQualifiedWrapperName,
        string ExtensionClassName,
        string CreateClassName,
        string AccessorClassName,
        string FullyQualifiedAccessorClassName,
        string CreateMethodName,
        EquatableArray<ConstructorModel> Constructors,
        EquatableArray<StepModel> Steps,
        EquatableArray<BuildMethodModel> BuildMethods,
        EquatableArray<TypeParameterModel> TypeParameters,
        EquatableArray<string> TypeParameterConstraints)
    {
        public string BuilderName { get; } = BuilderName;
        public string BuilderFullyQualifiedName { get; } = BuilderFullyQualifiedName;
        public string? Namespace { get; } = Namespace;
        public GeneratedAccessibility GeneratedAccessibility { get; } = GeneratedAccessibility;
        public string WrapperName { get; } = WrapperName;
        public string FullyQualifiedWrapperName { get; } = FullyQualifiedWrapperName;
        public string ExtensionClassName { get; } = ExtensionClassName;
        public string CreateClassName { get; } = CreateClassName;
        public string AccessorClassName { get; } = AccessorClassName;
        public string FullyQualifiedAccessorClassName { get; } = FullyQualifiedAccessorClassName;
        public string CreateMethodName { get; } = CreateMethodName;
        public EquatableArray<ConstructorModel> Constructors { get; } = Constructors;
        public EquatableArray<StepModel> Steps { get; } = Steps;
        public EquatableArray<BuildMethodModel> BuildMethods { get; } = BuildMethods;
        public EquatableArray<TypeParameterModel> TypeParameters { get; } = TypeParameters;
        public EquatableArray<string> TypeParameterConstraints { get; } = TypeParameterConstraints;
    }

    private readonly record struct ConstructorModel(
        EquatableArray<ParameterModel> Parameters,
        string SignatureKey,
        string DisplayName,
        string AccessorSuffix)
    {
        public EquatableArray<ParameterModel> Parameters { get; } = Parameters;
        public string SignatureKey { get; } = SignatureKey;
        public string DisplayName { get; } = DisplayName;
        public string AccessorSuffix { get; } = AccessorSuffix;
    }

    private readonly record struct BuildMethodModel(
        string Name,
        string ReturnTypeName,
        bool IsStatic,
        bool IsGenericMethod,
        string? TargetBranchPath,
        EquatableArray<TypeParameterModel> TypeParameters,
        EquatableArray<ParameterModel> Parameters)
    {
        public string Name { get; } = Name;
        public string ReturnTypeName { get; } = ReturnTypeName;
        public bool IsStatic { get; } = IsStatic;
        public bool IsGenericMethod { get; } = IsGenericMethod;
        public string? TargetBranchPath { get; } = TargetBranchPath;
        public EquatableArray<TypeParameterModel> TypeParameters { get; } = TypeParameters;
        public EquatableArray<ParameterModel> Parameters { get; } = Parameters;
    }

    private readonly record struct TypeParameterModel(
        string Name,
        EquatableArray<string> Constraints)
    {
        public string Name { get; } = Name;
        public EquatableArray<string> Constraints { get; } = Constraints;
    }

    private readonly record struct ParameterModel(
        string Name,
        string TypeName,
        ParameterRefKind RefKind,
        bool HasExplicitDefaultValue,
        string? DefaultValueExpression)
    {
        public string Name { get; } = Name;
        public string TypeName { get; } = TypeName;
        public ParameterRefKind RefKind { get; } = RefKind;
        public bool HasExplicitDefaultValue { get; } = HasExplicitDefaultValue;
        public string? DefaultValueExpression { get; } = DefaultValueExpression;
    }

    private readonly record struct StepModel(
        string FieldName,
        string FieldTypeName,
        string StateName,
        string MethodName,
        string? BranchPath,
        DefaultModel? Default,
        bool IsRequired,
        EquatableArray<ValidatorModel> Validators,
        EquatableArray<StepOverloadModel> Overloads)
    {
        public string FieldName { get; } = FieldName;
        public string FieldTypeName { get; } = FieldTypeName;
        public string StateName { get; } = StateName;
        public string MethodName { get; } = MethodName;
        public string? BranchPath { get; } = BranchPath;
        public DefaultModel? Default { get; } = Default;
        public bool IsRequired { get; } = IsRequired;
        public EquatableArray<ValidatorModel> Validators { get; } = Validators;
        public EquatableArray<StepOverloadModel> Overloads { get; } = Overloads;
    }

    private readonly record struct DefaultModel(ProviderMethodModel ProviderMethod)
    {
        public ProviderMethodModel ProviderMethod { get; } = ProviderMethod;
    }

    private readonly record struct ProviderMethodModel(string Name, bool IsStatic)
    {
        public string Name { get; } = Name;
        public bool IsStatic { get; } = IsStatic;
    }

    private readonly record struct ValidatorModel(string Name, bool IsStatic, ValidatorReturnKind ReturnKind)
    {
        public string Name { get; } = Name;
        public bool IsStatic { get; } = IsStatic;
        public ValidatorReturnKind ReturnKind { get; } = ReturnKind;
    }

    private readonly record struct StepOverloadModel(
        string Name,
        bool IsStatic,
        EquatableArray<ParameterModel> Parameters,
        string SignatureKey,
        string DisplayName)
    {
        public string Name { get; } = Name;
        public bool IsStatic { get; } = IsStatic;
        public EquatableArray<ParameterModel> Parameters { get; } = Parameters;
        public string SignatureKey { get; } = SignatureKey;
        public string DisplayName { get; } = DisplayName;
    }

    private readonly record struct BuildShape(
        IReadOnlyList<string> StateArgs,
        EquatableArray<StepModel> ApplicableSteps,
        EquatableArray<StepModel> OptionalApplicableSteps,
        EquatableArray<string> OptionalApplicableStateTypeParameters)
    {
        public IReadOnlyList<string> StateArgs { get; } = StateArgs;
        public EquatableArray<StepModel> ApplicableSteps { get; } = ApplicableSteps;
        public EquatableArray<StepModel> OptionalApplicableSteps { get; } = OptionalApplicableSteps;

        public EquatableArray<string> OptionalApplicableStateTypeParameters { get; } =
            OptionalApplicableStateTypeParameters;
    }

    private enum ParameterRefKind
    {
        None,
        Ref,
        Out,
        In
    }

    private enum GeneratedAccessibility
    {
        Invalid,
        Public,
        Internal,
    }

    private enum ValidatorReturnKind
    {
        Void,
        Task
    }

    private static class Diagnostics
    {
        public static readonly DiagnosticDescriptor InvalidBuilderShape = new(
            id: "TSB001",
            title: "Invalid builder shape",
            messageFormat: "Type-state builder '{0}' is invalid: {1}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor StaticStepField = new(
            id: "TSB002",
            title: "Static step field is not supported",
            messageFormat:
            "Field '{0}' on builder '{1}' is marked with [StepForValue] but is static. Static step fields are not supported.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ReadonlyStepField = new(
            id: "TSB003",
            title: "Readonly step field is not supported",
            messageFormat:
            "Field '{0}' on builder '{1}' is marked with [StepForValue] but is readonly. Readonly step fields are not supported.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidBuildMethod = new(
            id: "TSB005",
            title: "Invalid build method",
            messageFormat: "Build method '{0}' on builder '{1}' is invalid: {2}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidStepDefaultProviderSyntax = new(
            id: "TSB006",
            title: "Invalid step default provider syntax",
            messageFormat:
            "Field '{0}' on builder '{1}' must use nameof(...) when specifying a default provider method.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidStepDefaultProviderMember = new(
            id: "TSB007",
            title: "Invalid step default provider member",
            messageFormat: "Field '{0}' on builder '{1}' specifies provider member '{2}', but it is invalid: {3}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateStepMethod = new(
            id: "TSB008",
            title: "Duplicate generated step method",
            messageFormat: "Field '{0}' on builder '{1}' would generate duplicate step method '{2}'.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NoStepFields = new(
            id: "TSB009",
            title: "Builder has no steps",
            messageFormat: "Builder '{0}' must declare at least one field marked with [StepForValue].",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NoBuildMethods = new(
            id: "TSB010",
            title: "Builder has no build methods",
            messageFormat: "Builder '{0}' must declare at least one method marked with [Build].",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidValidatorSyntax = new(
            id: "TSB011",
            title: "Invalid validator syntax",
            messageFormat:
            "Field '{0}' on builder '{1}' must use nameof(...) when specifying a validator method.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidValidatorMember = new(
            id: "TSB012",
            title: "Invalid validator member",
            messageFormat: "Field '{0}' on builder '{1}' specifies validator member '{2}', but it is invalid: {3}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidStepOverloadSyntax = new(
            id: "TSB013",
            title: "Invalid step overload syntax",
            messageFormat:
            "Field '{0}' on builder '{1}' must use nameof(...) when specifying a step overload method.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidStepOverloadMember = new(
            id: "TSB014",
            title: "Invalid step overload member",
            messageFormat: "Field '{0}' on builder '{1}' specifies step overload member '{2}', but it is invalid: {3}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateStepOverloadMethod = new(
            id: "TSB015",
            title: "Duplicate generated step overload method",
            messageFormat:
            "Field '{0}' on builder '{1}' would generate duplicate step overload method '{2}' from overload '{3}'.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateParameterlessStepOverloadMethod = new(
            id: "TSB016",
            title: "Multiple parameterless step overloads are not supported",
            messageFormat:
            "Field '{0}' on builder '{1}' generates multiple parameterless overloads for step method '{2}': {3}. Only one parameterless [StepOverload] is allowed per step.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidStepBranchPath = new(
            id: "TSB017",
            title: "Invalid step branch path",
            messageFormat: "Field '{0}' on builder '{1}' has an invalid [StepBranch] path: {2}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidBuildBranchTarget = new(
            id: "TSB018",
            title: "Invalid build branch target",
            messageFormat: "Build method '{0}' on builder '{1}' has an invalid target branch: {2}.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor BranchedBuilderRequiresBuildTarget = new(
            id: "TSB019",
            title: "Branched builder requires explicit build target",
            messageFormat:
            "Build method '{0}' on branched builder '{1}' must specify an explicit [Build(\"branch/path\")] target.",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor StepBranchRequiresStepForValue = new(
            id: "TSB020",
            title: "StepBranch requires StepForValue",
            messageFormat: "Field '{0}' on builder '{1}' uses [StepBranch] but is not marked with [StepForValue].",
            category: "TypedStateBuilder",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
