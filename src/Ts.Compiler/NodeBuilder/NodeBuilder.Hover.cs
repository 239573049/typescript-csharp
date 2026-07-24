// Ported from tsgo internal/checker/nodebuilder_hover.go — the hover-expansion declaration
// synthesis (class / interface / enum / module) behind expandable hover (VerbosityContext).

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ts.Ast;

namespace Ts.Checker;

public sealed partial class NodeBuilder
{
    // nodebuilder_hover.go:19-48 — expandSymbolForHover produces declaration nodes (class,
    // interface, enum, module) for a symbol for expandable hover. This is a focused alternative to
    // the full symbolTableToDeclarationStatements machinery used by declaration emit — it directly
    // builds the declaration nodes hover needs without the declaration-emit scaffolding (deferred
    // privates, symbol name remapping, export modifier computation, alias resolution, visited
    // symbols tracking).
    private Node[] ExpandSymbolForHoverImpl(Symbol symbol)
    {
        List<Node>? results = null;
        if ((symbol.Flags & SymbolFlags.Enum) != 0)
        {
            if (ExpandEnumDecl(symbol) is { } node)
            {
                (results ??= new List<Node>()).Add(node);
            }
        }
        if ((symbol.Flags & SymbolFlags.Class) != 0)
        {
            if (ExpandClassDecl(symbol) is { } node)
            {
                (results ??= new List<Node>()).Add(node);
            }
        }
        // Module/namespace before interface (matching Strada ordering for merged declarations)
        if ((symbol.Flags & (SymbolFlags.ValueModule | SymbolFlags.NamespaceModule)) != 0)
        {
            if (ExpandModuleDecl(symbol) is { } node)
            {
                (results ??= new List<Node>()).Add(node);
            }
        }
        if ((symbol.Flags & SymbolFlags.Interface) != 0 && (symbol.Flags & SymbolFlags.Class) == 0)
        {
            if (ExpandInterfaceDecl(symbol) is { } node)
            {
                (results ??= new List<Node>()).Add(node);
            }
        }
        return results?.ToArray() ?? [];
    }

    // nodebuilder_hover.go:50-89 — expandEnumDecl produces an EnumDeclaration node with all members.
    private Node? ExpandEnumDecl(Symbol symbol)
    {
        var name = AstUtilities.SymbolName(symbol);
        ctx!.approximateLength += 9 + name.Length;
        var memberProps = ch.GetPropertiesOfType(ch.GetTypeOfSymbol(symbol))
            .Where(p => (p.Flags & SymbolFlags.EnumMember) != 0).ToArray();
        var members = new List<Node>();
        for (var i = 0; i < memberProps.Length; i++)
        {
            var p = memberProps[i];
            if (CheckTruncationLengthIfExpanding() && i + 3 < memberProps.Length - 1)
            {
                ctx.expansionTruncated = true;
                members.Add(f.NewEnumMember(f.NewStringLiteral($" ... {memberProps.Length - i - 1} more ... ", TokenFlags.None), null));
                var last = memberProps[^1];
                members.Add(f.NewEnumMember(f.NewIdentifier(last.Name), EnumMemberInitializer(last)));
                break;
            }
            var memberDecl = (p.Declarations ?? []).FirstOrDefault(NodeTests.IsEnumMember);
            Node? initializer;
            if (memberDecl != null && memberDecl.AsEnumMember().Initializer != null)
            {
                initializer = f.DeepCloneNode(memberDecl.AsEnumMember().Initializer!);
            }
            else
            {
                initializer = EnumMemberInitializer(p);
            }
            ctx.approximateLength += 4 + p.Name.Length;
            if (initializer != null)
            {
                ctx.approximateLength += 5; // " = " + value estimate
            }
            members.Add(f.NewEnumMember(f.NewIdentifier(p.Name), initializer));
        }

        var constModifier = ModifierFlags.None;
        if (Checker.IsConstEnumSymbol(symbol))
        {
            constModifier = ModifierFlags.Const;
        }
        ModifierList? mods = null;
        if (constModifier != 0)
        {
            mods = f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(constModifier, f.NewModifier));
        }
        return f.NewEnumDeclaration(mods, f.NewIdentifier(name), f.NewNodeList(members.ToArray()));
    }

    // nodebuilder_hover.go:91-107
    private Node? EnumMemberInitializer(Symbol p)
    {
        var memberDecl = (p.Declarations ?? []).FirstOrDefault(NodeTests.IsEnumMember);
        if (memberDecl == null)
        {
            return null;
        }
        var val = ch.GetConstantValue(memberDecl);
        switch (val)
        {
            case string s:
                return f.NewStringLiteral(s, TokenFlags.None);
            case global::Ts.JsNum.Number n:
                return f.NewNumericLiteral(n.ToString(), TokenFlags.None);
        }
        return null;
    }

    // nodebuilder_hover.go:109-182 — expandClassDecl produces a ClassDeclaration node with heritage
    // clauses and members.
    private Node? ExpandClassDecl(Symbol symbol)
    {
        var name = AstUtilities.SymbolName(symbol);
        ctx!.approximateLength += 9 + name.Length;

        var classLikeDeclarations = (symbol.Declarations ?? []).Where(AstUtilities.IsClassLike).ToArray();
        var originalDecl = classLikeDeclarations.FirstOrDefault();
        var oldEnclosing = ctx.enclosingDeclaration;
        if (originalDecl != null)
        {
            ctx.enclosingDeclaration = originalDecl;
        }
        try
        {
            var localParams = ch.GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(symbol);
            var typeParamDecls = (localParams ?? []).Select(TypeParameterToDeclaration).ToArray();

            var declaredType = ch.GetDeclaredTypeOfClassOrInterface(symbol);
            var classType = ch.GetTypeWithThisArgument(declaredType, null, false);
            var baseTypes = ch.GetBaseTypes(Checker.GetTargetType(classType));
            var staticType = ch.GetTypeOfSymbol(symbol);
            var isClass = staticType.symbol?.ValueDeclaration != null && AstUtilities.IsClassLike(staticType.symbol.ValueDeclaration);
            var staticBaseType = isClass ? ch.GetBaseConstructorTypeOfClass(declaredType) : ch.anyType;

            // Heritage clauses
            var heritageClauses = HoverHeritageClauses(classLikeDeclarations);

            // Instance members via addPropertyToElementList (reusing existing serialization),
            // then convert TypeElements to ClassElements and add class-specific modifiers
            var allProps = ch.GetPropertiesOfType(classType);
            var symbolProps = FilterInheritedProperties(classType, baseTypes, allProps ?? []);
            var publicProps = symbolProps.Where(s => !IsHashPrivate(s)).ToArray();
            var hasPrivate = symbolProps.Any(IsHashPrivate);

            var instanceMembers = new List<Node>();
            SerializePropertiesWithTruncation(publicProps, instanceMembers);
            TypeElementsToClassElements(f, instanceMembers);
            AddClassModifiers(instanceMembers, false);

            // Static members
            var staticProps = ch.GetPropertiesOfType(staticType)
                .Where(p => (p.Flags & SymbolFlags.Prototype) == 0 && p.Name != "prototype" && !IsNamespaceMember(p)).ToArray();
            var staticMembers = new List<Node>();
            SerializePropertiesWithTruncation(staticProps, staticMembers);
            TypeElementsToClassElements(f, staticMembers);
            AddClassModifiers(staticMembers, true);

            // Hash-private members
            var privateMembers = new List<Node>();
            if (hasPrivate)
            {
                SerializePropertiesWithTruncation(symbolProps.Where(IsHashPrivate).ToArray(), privateMembers);
                TypeElementsToClassElements(f, privateMembers);
            }

            // Constructors
            var constructors = SerializeConstructors(staticType, staticBaseType, isClass, symbol);

            // Index signatures
            var indexSigs = SerializeIndexSignaturesOfType(classType, baseTypes.FirstOrDefault());

            var allMembers = new List<Node>(indexSigs.Count + staticMembers.Count + constructors.Count + instanceMembers.Count + privateMembers.Count);
            allMembers.AddRange(indexSigs);
            allMembers.AddRange(staticMembers);
            allMembers.AddRange(constructors);
            allMembers.AddRange(instanceMembers);
            allMembers.AddRange(privateMembers);

            return f.NewClassDeclaration(null, f.NewIdentifier(name), f.NewNodeList(typeParamDecls), f.NewNodeList(heritageClauses.ToArray()), f.NewNodeList(allMembers.ToArray()));
        }
        finally
        {
            ctx.enclosingDeclaration = oldEnclosing;
        }
    }

    // nodebuilder_hover.go:184-211 — addClassModifiers post-processes class member nodes to add
    // class-specific modifiers (private, protected, public, abstract, static) based on the original
    // symbol declarations.
    private void AddClassModifiers(List<Node> members, bool isStatic)
    {
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            // Find the symbol for this member by matching the property name
            Symbol? memberSymbol = null;
            var memberName = m.Name();
            if (memberName != null && idToSymbol.TryGetValue(memberName, out var sym))
            {
                memberSymbol = sym;
            }
            if (memberSymbol == null)
            {
                continue;
            }
            var modFlags = Checker.GetDeclarationModifierFlagsFromSymbol(memberSymbol) & ~ModifierFlags.Async;
            if (isStatic)
            {
                modFlags |= ModifierFlags.Static;
            }
            if (modFlags != 0 && Checker.CanHaveModifiers(m))
            {
                var existing = m.ModifierFlags();
                if (modFlags != existing)
                {
                    members[i] = AstUtilities.ReplaceModifiers(f, m, f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modFlags | existing, f.NewModifier)));
                }
            }
        }
    }

    // nodebuilder_hover.go:213-228 — typeElementsToClassElements converts TypeElement nodes
    // (PropertySignature, MethodSignature) to their ClassElement equivalents (PropertyDeclaration,
    // MethodDeclaration) so they can be used as members of a ClassDeclaration. Nodes that are
    // already ClassElements pass through unchanged.
    private static void TypeElementsToClassElements(NodeFactory f, List<Node> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            switch (m.Kind)
            {
                case SyntaxKind.PropertySignature:
                {
                    var ps = m.AsPropertySignatureDeclaration();
                    members[i] = f.NewPropertyDeclaration(m.Modifiers(), ps.Name()!, ps.QuestionToken(), ps.Type, null);
                    break;
                }
                case SyntaxKind.MethodSignature:
                {
                    var ms = m.AsMethodSignatureDeclaration();
                    members[i] = f.NewMethodDeclaration(m.Modifiers(), null, ms.Name()!, ms.QuestionToken(), ms.TypeParameters, ms.Parameters, ms.Type, null, null);
                    break;
                }
            }
        }
    }

    // nodebuilder_hover.go:230-272 — expandInterfaceDecl produces an InterfaceDeclaration with
    // members. Reuses addPropertyToElementList for property serialization and
    // signatureToSignatureDeclarationHelper for signatures.
    private Node? ExpandInterfaceDecl(Symbol symbol)
    {
        var name = AstUtilities.SymbolName(symbol);
        ctx!.approximateLength += 14 + name.Length;

        var interfaceType = ch.GetDeclaredTypeOfClassOrInterface(symbol);
        var interfaceDeclarations = (symbol.Declarations ?? []).Where(NodeTests.IsInterfaceDeclaration).ToArray();
        var localParams = ch.GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(symbol);
        var typeParamDecls = (localParams ?? []).Select(TypeParameterToDeclaration).ToArray();
        var baseTypes = ch.GetBaseTypes(interfaceType);
        Type? baseType = null;
        if (baseTypes.Length > 0)
        {
            baseType = ch.GetIntersectionType(baseTypes);
        }

        // Members: reuse existing serialization functions
        var resolved = ch.ResolveStructuredTypeMembers(interfaceType);
        var members = new List<Node>();

        // Index signatures, filtering those identical to base
        members.AddRange(SerializeIndexSignaturesOfType(interfaceType, baseType));
        // Construct signatures (skip abstract)
        foreach (var sig in resolved.ConstructSignatures())
        {
            if ((sig.flags & SignatureFlags.Abstract) != 0)
            {
                continue;
            }
            members.Add(SignatureToSignatureDeclarationHelper(sig!, SyntaxKind.ConstructSignature, null));
        }
        // Call signatures
        foreach (var sig in resolved.CallSignatures())
        {
            members.Add(SignatureToSignatureDeclarationHelper(sig, SyntaxKind.CallSignature, null));
        }
        // Properties, filtering inherited
        var filteredProps = FilterInheritedProperties(interfaceType, baseTypes, resolved.properties ?? []);
        SerializePropertiesWithTruncation(filteredProps, members);

        // Heritage clauses
        var heritageClauses = HoverHeritageClauses(interfaceDeclarations);

        return f.NewInterfaceDeclaration(null, f.NewIdentifier(name), f.NewNodeList(typeParamDecls), f.NewNodeList(heritageClauses.ToArray()), f.NewNodeList(members.ToArray()));
    }

    // nodebuilder_hover.go:274-294
    private List<Node> HoverHeritageClauses(Node[] declarations)
    {
        List<Node>? extendsTypes = null;
        List<Node>? implementsTypes = null;
        foreach (var declaration in declarations)
        {
            foreach (var heritageElement in AstUtilities.GetExtendsHeritageClauseElements(declaration) ?? [])
            {
                (extendsTypes ??= new List<Node>()).Add(f.DeepCloneNode(heritageElement)!);
            }
            foreach (var heritageElement in AstUtilities.GetImplementsHeritageClauseElements(declaration) ?? [])
            {
                (implementsTypes ??= new List<Node>()).Add(f.DeepCloneNode(heritageElement)!);
            }
        }

        var heritageClauses = new List<Node>();
        if (extendsTypes is { Count: > 0 })
        {
            heritageClauses.Add(f.NewHeritageClause(SyntaxKind.ExtendsKeyword, f.NewNodeList(extendsTypes.ToArray())));
        }
        if (implementsTypes is { Count: > 0 })
        {
            heritageClauses.Add(f.NewHeritageClause(SyntaxKind.ImplementsKeyword, f.NewNodeList(implementsTypes.ToArray())));
        }
        return heritageClauses;
    }

    // nodebuilder_hover.go:296-313 — serializePropertiesWithTruncation iterates properties using
    // addPropertyToElementList, with truncation checks matching Strada's
    // createTypeNodesFromResolvedType behavior.
    private void SerializePropertiesWithTruncation(Symbol[] properties, List<Node> elements)
    {
        properties = properties.Where(p => (p.Flags & SymbolFlags.Prototype) == 0).ToArray();
        for (var i = 0; i < properties.Length; i++)
        {
            var p = properties[i];
            if (CheckTruncationLengthIfExpanding() && (i + 3 < properties.Length - 1))
            {
                ctx!.expansionTruncated = true;
                var text = string.Format(CultureInfo.InvariantCulture, "... {0} more ...", properties.Length - i - 1);
                elements.Add(f.NewPropertySignatureDeclaration(null, f.NewIdentifier(text), null, null, null));
                AddPropertyToElementList(properties[^1], elements);
                break;
            }
            AddPropertyToElementList(p, elements);
        }
    }

    // nodebuilder_hover.go:315-365 — serializeConstructors builds constructor signature(s) for a
    // class, with base type filtering.
    private List<Node> SerializeConstructors(Type staticType, Type? staticBaseType, bool isClass, Symbol symbol)
    {
        var isNonConstructable = !isClass &&
            symbol.ValueDeclaration != null &&
            AstUtilities.IsInJSFile(symbol.ValueDeclaration) &&
            ch.GetSignaturesOfType(staticType, SignatureKind.Construct).Length == 0;
        if (isNonConstructable)
        {
            ctx!.approximateLength += 21;
            var modifiers = AstUtilities.CreateModifiersFromModifierFlags(ModifierFlags.Private, f.NewModifier);
            return new List<Node> { f.NewConstructorDeclaration(f.NewModifierList(modifiers), null, f.NewNodeList([]), null, null, null) };
        }
        var signatures = ch.GetSignaturesOfType(staticType, SignatureKind.Construct);
        if (staticBaseType != null)
        {
            var baseSigs = ch.GetSignaturesOfType(staticBaseType, SignatureKind.Construct);
            if (baseSigs.Length == 0 && signatures.All(sig => sig.parameters!.Length == 0))
            {
                return new List<Node>();
            }
            if (baseSigs.Length == signatures.Length)
            {
                var allMatch = true;
                for (var i = 0; i < baseSigs.Length; i++)
                {
                    if (ch.CompareSignaturesIdentical(signatures[i], baseSigs[i], false, false, true, ch.CompareTypesIdentical) != Ternary.True)
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch)
                {
                    return new List<Node>();
                }
            }
            var privateProtected = ModifierFlags.None;
            foreach (var sig in signatures)
            {
                if (sig.declaration != null)
                {
                    privateProtected |= sig.declaration.ModifierFlags() & (ModifierFlags.Private | ModifierFlags.Protected);
                }
            }
            if (privateProtected != 0)
            {
                return new List<Node>
                {
                    f.NewConstructorDeclaration(
                        f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(privateProtected, f.NewModifier)),
                        null, f.NewNodeList([]), null, null, null),
                };
            }
        }
        else if (signatures.All(sig => sig.parameters!.Length == 0))
        {
            return new List<Node>();
        }
        var result = new List<Node>();
        foreach (var sig in signatures)
        {
            ctx!.approximateLength++;
            result.Add(SignatureToSignatureDeclarationHelper(sig, SyntaxKind.Constructor, null));
        }
        return result;
    }

    // nodebuilder_hover.go:367-380 — serializeIndexSignaturesOfType builds index signature
    // declarations, filtering those identical to baseType.
    private List<Node> SerializeIndexSignaturesOfType(Type input, Type? baseType)
    {
        var result = new List<Node>();
        foreach (var info in ch.GetIndexInfosOfType(input))
        {
            if (baseType != null)
            {
                var baseInfo = ch.GetIndexInfoOfType(baseType, info.keyType);
                if (baseInfo != null && ch.IsTypeIdenticalTo(info.valueType, baseInfo.valueType))
                {
                    continue;
                }
            }
            result.Add(IndexInfoToIndexSignatureDeclarationHelper(info, null));
        }
        return result;
    }

    // nodebuilder_hover.go:382-409 — serializeNamespaceMember produces the appropriate declaration
    // node for a namespace member based on its symbol flags (type alias, enum, class, interface,
    // nested namespace, or variable).
    private Node? SerializeNamespaceMember(Symbol resolved, string name)
    {
        if ((resolved.Flags & SymbolFlags.TypeAlias) != 0)
        {
            return SerializeTypeAliasForNamespace(resolved, name);
        }
        if ((resolved.Flags & SymbolFlags.Enum) != 0)
        {
            return ExpandEnumDecl(resolved);
        }
        if ((resolved.Flags & SymbolFlags.Class) != 0)
        {
            return ExpandClassDecl(resolved);
        }
        if ((resolved.Flags & SymbolFlags.Interface) != 0)
        {
            return ExpandInterfaceDecl(resolved);
        }
        if ((resolved.Flags & (SymbolFlags.ValueModule | SymbolFlags.NamespaceModule)) != 0)
        {
            return ExpandModuleDecl(resolved);
        }
        var t = ch.GetWidenedType(ch.GetTypeOfSymbol(resolved));
        ctx!.approximateLength += name.Length + 5;
        return f.NewVariableStatement(
            null,
            f.NewVariableDeclarationList(
                f.NewNodeList([
                    f.NewVariableDeclaration(f.NewIdentifier(name), null, SerializeTypeForDeclarationImpl(null, t, resolved, true), null),
                ]),
                NodeFlags.Let));
    }

    // nodebuilder_hover.go:411-547 — expandModuleDecl produces a ModuleDeclaration with exported
    // members.
    private Node? ExpandModuleDecl(Symbol symbol)
    {
        var exports = ch.GetExportsOfSymbol(symbol);
        var members = new List<Symbol>();
        foreach (var (_, sym) in exports!) // map-iter: 已排序 (SortSymbols below)
        {
            // Filter to namespace-relevant members
            if (!IsNamespaceMember(sym))
            {
                continue;
            }
            if (!global::Ts.Scanner.Scanner.IsIdentifierText(sym.Name, global::Ts.Core.LanguageVariant.Standard))
            {
                continue;
            }
            members.Add(sym);
        }
        ch.SortSymbols(members, 0, members.Count);
        ctx!.approximateLength += 14;

        // Use the same name as symbol display.
        var oldFlags = ctx.flags;
        Node? localName;
        try
        {
            ctx.flags |= global::Ts.NodeBuilder.Flags.WriteTypeParametersInQualifiedName | (global::Ts.NodeBuilder.Flags)SymbolFormatFlags.UseOnlyExternalAliasing;
            localName = SymbolToNodeImpl(symbol, SymbolFlags.All);
        }
        finally
        {
            ctx.flags = oldFlags;
        }
        if (localName == null)
        {
            return null;
        }

        var bodyStmts = new List<(Node Node, bool IsLocal)>(); // isLocal: alias targets should not get export modifier
        var emittedLocals = new HashSet<Symbol>();
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (CheckTruncationLengthIfExpanding() && i + 3 < members.Count - 1)
            {
                ctx.expansionTruncated = true;
                bodyStmts.Add((f.NewExpressionStatement(f.NewIdentifier(string.Format(CultureInfo.InvariantCulture, "... ({0} more) ...", members.Count - i - 1))), false));
                i = members.Count - 2; // skip to last member after i++ at end of iteration
                continue;
            }

            // Handle alias/re-export symbols
            if ((m.Flags & SymbolFlags.Alias) != 0)
            {
                var aliasDecl = ch.GetDeclarationOfAliasSymbol(m);
                var target = ch.GetMergedSymbol(ch.GetTargetOfAliasDeclaration(aliasDecl));
                if (target != null)
                {
                    // If the alias target is a local symbol (not itself an export), emit its declaration first
                    if ((target.Flags & (SymbolFlags.BlockScopedVariable | SymbolFlags.FunctionScopedVariable | SymbolFlags.Property)) != 0)
                    {
                        if (emittedLocals.Add(target))
                        {
                            var localType = ch.GetWidenedType(ch.GetTypeOfSymbol(target));
                            ctx.approximateLength += target.Name.Length + 5;
                            var localStmt = f.NewVariableStatement(null,
                                f.NewVariableDeclarationList(f.NewNodeList([
                                    f.NewVariableDeclaration(f.NewIdentifier(target.Name), null, SerializeTypeForDeclarationImpl(null, localType, target, true), null),
                                ]), NodeFlags.Let));
                            bodyStmts.Add((localStmt, true));
                        }
                    }
                    var targetName = target.Name;
                    ctx.approximateLength += 16 + m.Name.Length;
                    Node? propertyName = null;
                    if (m.Name != targetName)
                    {
                        propertyName = f.NewIdentifier(targetName);
                    }
                    var stmt = f.NewExportDeclaration(
                        null, false,
                        f.NewNamedExports(f.NewNodeList([
                            f.NewExportSpecifier(false, propertyName, f.NewIdentifier(m.Name)),
                        ])),
                        null, null);
                    bodyStmts.Add((stmt, false));
                    continue;
                }
            }

            var resolved = ch.ResolveSymbol(m)!;

            // Handle functions as function declarations
            if ((resolved.Flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0)
            {
                var t = ch.GetTypeOfSymbol(resolved);
                var sigs = ch.GetSignaturesOfType(t, SignatureKind.Call);
                foreach (var sig in sigs)
                {
                    ctx.approximateLength++;
                    var decl = SignatureToSignatureDeclarationHelper(sig, SyntaxKind.FunctionDeclaration, new SignatureToSignatureDeclarationOptions
                    {
                        name = f.NewIdentifier(m.Name),
                    });
                    bodyStmts.Add((decl, false));
                }
                // If the function also has namespace characteristics, emit an empty namespace.
                var merged = ch.GetMergedSymbol(resolved)!;
                var hasModuleExports = (merged.Flags & (SymbolFlags.ValueModule | SymbolFlags.NamespaceModule)) != 0 && merged.Exports is { Count: > 0 };
                if (!hasModuleExports)
                {
                    bodyStmts.Add((f.NewModuleDeclaration(null, SyntaxKind.NamespaceKeyword, f.NewIdentifier(m.Name), f.NewModuleBlock(f.NewNodeList([]))), false));
                }
                continue;
            }

            // Handle remaining member kinds (type alias, enum, class, interface, namespace, variable)
            if (SerializeNamespaceMember(resolved, m.Name) is { } node)
            {
                bodyStmts.Add((node, false));
            }
        }

        // Add export modifier to exported statements (skip local declarations and ExportDeclarations).
        for (var i = 0; i < bodyStmts.Count; i++)
        {
            var s = bodyStmts[i];
            if (s.IsLocal || NodeTests.IsExportDeclaration(s.Node))
            {
                continue;
            }
            if (Checker.CanHaveModifiers(s.Node))
            {
                var mf = s.Node.ModifierFlags() | ModifierFlags.Export;
                bodyStmts[i] = (AstUtilities.ReplaceModifiers(f, s.Node, f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(mf, f.NewModifier))), s.IsLocal);
            }
        }

        // Collect nodes, stripping export if all statements are exported.
        var bodyStatements = bodyStmts.Select(s => s.Node).ToArray();
        var allExported = bodyStatements.Length > 0 && bodyStatements.All(d => AstUtilities.HasSyntacticModifier(d, ModifierFlags.Export));
        if (allExported)
        {
            for (var i = 0; i < bodyStatements.Length; i++)
            {
                var stmt = bodyStatements[i];
                if (Checker.CanHaveModifiers(stmt))
                {
                    var mf = stmt.ModifierFlags() & ~ModifierFlags.Export;
                    bodyStatements[i] = AstUtilities.ReplaceModifiers(f, stmt, f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(mf, f.NewModifier)));
                }
            }
        }

        var keyword = SyntaxKind.NamespaceKeyword;
        if (!NodeTests.IsIdentifier(localName))
        {
            keyword = SyntaxKind.ModuleKeyword;
        }
        return f.NewModuleDeclaration(null, keyword, localName, f.NewModuleBlock(f.NewNodeList(bodyStatements)));
    }

    // nodebuilder_hover.go:549-560 — serializeTypeAliasForNamespace produces a TypeAliasDeclaration
    // for a type alias inside a namespace body.
    private Node SerializeTypeAliasForNamespace(Symbol symbol, string name)
    {
        var aliasType = ch.GetDeclaredTypeOfTypeAlias(symbol);
        var typeParams = ch.GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(symbol);
        var typeParamDecls = (typeParams ?? []).Select(TypeParameterToDeclaration).ToArray();
        var restoreFlags = SaveRestoreFlags();
        ctx!.flags |= global::Ts.NodeBuilder.Flags.InTypeAlias;
        var typeNode = TypeToTypeNodeCore(aliasType);
        restoreFlags();
        ctx.approximateLength += 8 + name.Length;
        return f.NewTypeAliasDeclaration(null, f.NewIdentifier(name), f.NewNodeList(typeParamDecls), typeNode);
    }

    // nodebuilder_hover.go:562-588 — filterInheritedProperties removes properties already present in
    // base types.
    private Symbol[] FilterInheritedProperties(Type t, Type[] baseTypes, Symbol[] properties)
    {
        if (baseTypes.Length == 0)
        {
            return properties;
        }
        // Build a lookup from property name to symbol for parent-identity comparison.
        var propsByName = new Dictionary<string, Symbol>(properties.Length);
        foreach (var p in properties)
        {
            propsByName[p.Name] = p;
        }
        // Collect names of properties inherited unchanged from base types.
        var inherited = new HashSet<string>();
        foreach (var baseT in baseTypes)
        {
            var baseWithThis = ch.GetTypeWithThisArgument(baseT, Checker.GetTargetType(t).AsInterfaceType()!.thisType, false);
            foreach (var prop in ch.GetPropertiesOfType(baseWithThis))
            {
                if (propsByName.TryGetValue(prop.Name, out var existing) && prop.Parent == existing.Parent)
                {
                    inherited.Add(prop.Name);
                }
            }
        }
        if (inherited.Count == 0)
        {
            return properties;
        }
        return properties.Where(p => !inherited.Contains(p.Name)).ToArray();
    }

    // nodebuilder_hover.go:590-593
    private static bool IsNamespaceMember(Symbol p)
    {
        return (p.Flags & (SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias)) != 0 ||
            !((p.Flags & SymbolFlags.Prototype) != 0 || p.Name == "prototype" ||
              (p.ValueDeclaration != null && AstUtilities.HasStaticModifier(p.ValueDeclaration) && AstUtilities.IsClassLike(p.ValueDeclaration.Parent!)));
    }

    // nodebuilder_hover.go:595-597
    private static bool IsHashPrivate(Symbol s)
    {
        return s.ValueDeclaration != null && s.ValueDeclaration.Name() != null && NodeTests.IsPrivateIdentifier(s.ValueDeclaration.Name()!);
    }

    // nodebuilder.go:150-186 — ExpandSymbolForHover produces declaration nodes for a symbol with
    // verbosity level support.
    public Node[] ExpandSymbolForHover(Symbol symbol, SymbolFlags meaning)
    {
        EnterContext(null, global::Ts.NodeBuilder.Flags.IgnoreErrors | global::Ts.NodeBuilder.Flags.MultilineObjectLiterals | global::Ts.NodeBuilder.Flags.UseAliasDefinedOutsideCurrentScope, global::Ts.NodeBuilder.InternalFlags.None, null);

        // Push the declared type onto the type stack to prevent re-expansion. We push a nil sentinel
        // after the real type so that isTypeOnStack (which skips the last element) still checks
        // declaredType.
        var declaredType = ch.GetDeclaredTypeOfSymbol(symbol);
        ctx!.typeStack.Add(declaredType);
        ctx.typeStack.Add(null);

        var nodes = ExpandSymbolForHoverImpl(symbol);

        ctx.typeStack.RemoveRange(ctx.typeStack.Count - 2, 2);

        PropagateVerbosityOut();

        // Simplify declarations by applying original modifiers
        var result = new List<Node>(nodes.Length);
        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case SyntaxKind.ClassDeclaration:
                    result.Add(SimplifyClassDeclaration(f, node, symbol));
                    break;
                case SyntaxKind.EnumDeclaration:
                    result.Add(SimplifyModifiers(f, node, NodeTests.IsEnumDeclaration, symbol));
                    break;
                case SyntaxKind.InterfaceDeclaration:
                    if ((meaning & SymbolFlags.Interface) != 0)
                    {
                        result.Add(SimplifyModifiers(f, node, NodeTests.IsInterfaceDeclaration, symbol));
                    }
                    break;
                case SyntaxKind.ModuleDeclaration:
                    result.Add(SimplifyModifiers(f, node, NodeTests.IsModuleDeclaration, symbol));
                    break;
            }
        }

        return ExitContextSlice(result.ToArray());
    }

    // nodebuilder.go:188-210
    private static Node SimplifyClassDeclaration(NodeFactory f, Node classDecl, Symbol symbol)
    {
        var classDeclarations = (symbol.Declarations ?? []).Where(AstUtilities.IsClassLike).ToArray();
        var originalClassDecl = classDeclarations.Length > 0 ? classDeclarations[0] : classDecl;
        var modifiers = originalClassDecl.ModifierFlags() & ~(ModifierFlags.Export | ModifierFlags.Ambient);
        var isAnonymous = NodeTests.IsClassExpression(originalClassDecl);
        if (isAnonymous)
        {
            var cd = classDecl.AsClassDeclaration();
            classDecl = f.UpdateClassDeclaration(
                cd,
                classDecl.Modifiers(),
                null,
                cd.TypeParameters,
                cd.HeritageClauses,
                cd.Members);
        }
        return AstUtilities.ReplaceModifiers(f, classDecl, f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modifiers, f.NewModifier)));
    }

    // nodebuilder.go:212-222
    private static Node SimplifyModifiers(NodeFactory f, Node newDecl, System.Func<Node, bool> isDeclKind, Symbol symbol)
    {
        var decls = (symbol.Declarations ?? []).Where(isDeclKind).ToArray();
        var declWithModifiers = decls.Length > 0 ? decls[0] : newDecl;
        var modifiers = declWithModifiers.ModifierFlags() & ~(ModifierFlags.Export | ModifierFlags.Ambient);
        return AstUtilities.ReplaceModifiers(f, newDecl, f.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modifiers, f.NewModifier)));
    }
}
