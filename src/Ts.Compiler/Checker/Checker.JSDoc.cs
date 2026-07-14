// Ported from tsgo internal/checker/jsdoc.go, plan/03 §4.2.
// Faithful per-function translation: same function order as Go, same branch structure; Go comments
// and `// !!!` markers preserved.
//
// §1.5: diagnostics via `using Diag = global::Ts.Diagnostics.Diagnostics;`; the no-collision hosts
// (NodeTests/AstUtilities) are used directly. `collections.Set` -> Ts.Collections.Set.
//
// Reachability: for .ts inputs without `@param` JSDoc, getAllJSDocTags returns empty and
// checkUnmatchedJSDocParameters early-returns. The JSDoc-parameter processing branches only run for
// JS files / `.ts` with `@param` tags. containsArgumentsReference is REAL in
// Checker.Declarations.Deps.cs (checker.go:31875); entityNameToString/errorOrSuggestion land with
// their owning waves.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using static Ts.Ast.NodeTests;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // jsdoc.go:9
    internal void CheckUnmatchedJSDocParameters(Node node)
    {
        List<Node>? jsdocParameters = null;
        foreach (var tag in GetAllJSDocTags(node).OrEmpty())
        {
            if (tag.Kind == SyntaxKind.JSDocParameterTag)
            {
                var tagName = tag.AsJSDocParameterOrPropertyTag().Name();
                if (IsIdentifier(tagName!) && tagName!.Text().Length == 0)
                {
                    continue;
                }
                (jsdocParameters ??= new List<Node>()).Add(tag);
            }
        }

        if (jsdocParameters is null || jsdocParameters.Count == 0)
        {
            return;
        }

        bool isJs = AstUtilities.IsInJSFile(node);
        var parameters = new Set<string>();
        var excludedParameters = new Set<int>();

        for (int i = 0; i < node.Parameters().Length; i++)
        {
            var param = node.Parameters()[i];
            var name = param.AsParameterDeclaration().Name();
            if (IsIdentifier(name!))
            {
                parameters.Add(name!.Text());
            }
            if (AstUtilities.IsBindingPattern(name))
            {
                excludedParameters.Add(i);
            }
        }
        if (ContainsArgumentsReference(node))
        {
            if (isJs)
            {
                int lastJSDocParamIndex = jsdocParameters.Count - 1;
                var lastJSDocParam = jsdocParameters[lastJSDocParamIndex].AsJSDocParameterOrPropertyTag();
                if (lastJSDocParam is null || !IsIdentifier(lastJSDocParam.Name()!))
                {
                    return;
                }
                if (excludedParameters.Has(lastJSDocParamIndex) || parameters.Has(lastJSDocParam.Name()!.Text()))
                {
                    return;
                }
                if (lastJSDocParam.TypeExpression is null || lastJSDocParam.TypeExpression.Type() is null)
                {
                    return;
                }
                if (IsArrayType(GetTypeFromTypeNode(lastJSDocParam.TypeExpression.Type()!)))
                {
                    return;
                }
                Error(lastJSDocParam.Name(), Diag.JSDoc_param_tag_has_name_0_but_there_is_no_parameter_with_that_name_It_would_match_arguments_if_it_had_an_array_type, lastJSDocParam.Name()!.Text());
            }
        }
        else
        {
            for (int index = 0; index < jsdocParameters.Count; index++)
            {
                var tag = jsdocParameters[index];
                var name = tag.AsJSDocParameterOrPropertyTag().Name();
                bool isNameFirst = tag.AsJSDocParameterOrPropertyTag().IsNameFirst;

                if (excludedParameters.Has(index) || (IsIdentifier(name!) && parameters.Has(name!.Text())))
                {
                    continue;
                }

                if (IsQualifiedName(name!))
                {
                    if (isJs)
                    {
                        Error(
                            name, Diag.Qualified_name_0_is_not_allowed_without_a_leading_param_object_1,
                            EntityNameToString(name!),
                            EntityNameToString(name!.AsQualifiedName().Left!));
                    }
                }
                else
                {
                    if (!isNameFirst)
                    {
                        ErrorOrSuggestion(
                            isJs, name,
                            Diag.JSDoc_param_tag_has_name_0_but_there_is_no_parameter_with_that_name,
                            name!.Text());
                    }
                }
            }
        }
    }

    // jsdoc.go:85
    private static Node[]? GetAllJSDocTags(Node node)
    {
        if ((node.Flags & NodeFlags.JSDoc) == 0)
        {
            for (var current = node; current != null; current = AstUtilities.GetNextJSDocCommentLocation(current))
            {
                var jsdocs = current.JSDoc(null);
                if (jsdocs is null || jsdocs.Length == 0)
                {
                    continue;
                }
                var lastJSDoc = jsdocs[^1].AsJSDoc();
                if (lastJSDoc.Tags != null)
                {
                    return lastJSDoc.Tags.Nodes;
                }
            }
        }
        return null;
    }
}
